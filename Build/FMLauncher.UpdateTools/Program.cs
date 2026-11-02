using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FMLauncher.UpdateShared;

try
{
    if (args.Length == 0) { Usage(); return 0; }
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 1; i < args.Length; i += 2)
    { if (i + 1 >= args.Length || !args[i].StartsWith("--") || !options.TryAdd(args[i], args[i + 1])) throw new ArgumentException("参数格式无效。"); }
    string Required(string key) => options.TryGetValue(key, out var value) ? value : throw new ArgumentException("缺少参数 " + key);
    string PrivateKey() => Environment.GetEnvironmentVariable("FMLAUNCHER_UPDATE_PRIVATE_KEY") is { Length: > 0 } value ? value : throw new InvalidOperationException("缺少发布侧环境变量 FMLAUNCHER_UPDATE_PRIVATE_KEY。");
    if (args[0] == "keygen")
    {
        var publicPath = Path.GetFullPath(options.GetValueOrDefault("--public-key", Path.Combine("Build", "Updates", "update-public.pem")));
        var privatePath = Path.GetFullPath(options.GetValueOrDefault("--private-key", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fmlauncher-signing", "update-private.pem")));
        var repository = new DirectoryInfo(Path.GetDirectoryName(publicPath)!);
        while (repository.Parent != null && !File.Exists(Path.Combine(repository.FullName, "FocusMinecraftLauncher.slnx"))) repository = repository.Parent;
        if (!File.Exists(Path.Combine(repository.FullName, "FocusMinecraftLauncher.slnx"))) throw new InvalidOperationException("请在启动器项目目录运行生成命令。");
        if (privatePath.StartsWith(repository.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("私钥必须保存在项目目录之外，不能写入源码仓库。");
        if (File.Exists(privatePath) || File.Exists(publicPath)) throw new IOException("密钥文件已存在，未覆盖；请保管原密钥。");
        UpdatePaths.RejectLinks(Path.GetPathRoot(privatePath)!, privatePath);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!); Directory.CreateDirectory(Path.GetDirectoryName(publicPath)!);
        using (var file = new FileStream(privatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(file, new UTF8Encoding(false))) writer.Write(key.ExportPkcs8PrivateKeyPem());
        using (var file = new FileStream(publicPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(file, new UTF8Encoding(false))) writer.Write(key.ExportSubjectPublicKeyInfoPem());
        Console.WriteLine("公钥：" + publicPath); Console.WriteLine("私钥已保存到项目外：" + privatePath);
        Console.WriteLine("请将私钥作为 GitHub Actions Secret：FMLAUNCHER_UPDATE_PRIVATE_KEY 保存。不要将私钥发到聊天或提交到仓库。");
        Console.WriteLine("公钥指纹 SHA-256：" + Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))); return 0;
    }
    if (args[0] == "verify-key")
    {
        using var privateKey = ECDsa.Create(); privateKey.ImportFromPem(PrivateKey());
        var publicText = File.ReadAllText(Required("--public-key"));
        if (!publicText.TrimStart().StartsWith("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal) || publicText.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new CryptographicException("公钥文件不能包含私钥。");
        using var publicKey = ECDsa.Create(); publicKey.ImportFromPem(publicText);
        if (privateKey.KeySize != 256 || !privateKey.ExportSubjectPublicKeyInfo().SequenceEqual(publicKey.ExportSubjectPublicKeyInfo())) throw new CryptographicException("发布私钥与项目公钥不匹配。");
        Console.WriteLine("发布密钥与客户端公钥匹配。"); return 0;
    }
    if (args[0] == "pack")
    {
        var input = Path.GetFullPath(Required("--input")); var output = Path.GetFullPath(Required("--output")); var version = Required("--version");
        if (!ReleaseVersion.TryParse(version, out _) || version.StartsWith('v')) throw new ArgumentException("版本号无效，不要带 v 前缀。");
        using var key = ECDsa.Create(); key.ImportFromPem(PrivateKey()); if (key.KeySize != 256) throw new CryptographicException("发布密钥必须使用 ECDSA P-256。");
        if (output.Equals(input, StringComparison.OrdinalIgnoreCase) || output.StartsWith(input.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("更新包输出目录不能位于程序输入目录内。");
        var manifest = new UpdateManifest { Version = version, PackageName = $"FMLauncher-{version}-{UpdateProduct.Runtime}.zip" };
        foreach (var path in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(input, path).Replace('\\', '/');
            UpdatePaths.ProgramFile(input, relative);
            manifest.Files.Add(new(relative, new FileInfo(path).Length, await UpdateManifest.HashAsync(path)));
        }
        if (!manifest.Files.Any(f => f.Path.Equals(UpdateProduct.Executable, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("输入目录缺少正式启动器程序。");
        Directory.CreateDirectory(output); var package = Path.Combine(output, manifest.PackageName);
        if (File.Exists(package)) throw new IOException("更新包已存在，未覆盖。");
        ZipFile.CreateFromDirectory(input, package, CompressionLevel.Optimal, false);
        manifest.PackageSize = new FileInfo(package).Length; manifest.PackageSha256 = await UpdateManifest.HashAsync(package); manifest.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, UpdateProduct.Json);
        var signature = key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        // 发布前回读签名，避免把损坏或不匹配的附件交给客户端。
        _ = UpdateManifest.Verify(bytes, signature, key.ExportSubjectPublicKeyInfoPem());
        File.WriteAllBytes(Path.Combine(output, UpdateProduct.ManifestName), bytes);
        File.WriteAllBytes(Path.Combine(output, UpdateProduct.SignatureName), signature);
        Console.WriteLine("更新包与签名清单已生成：" + output); return 0;
    }
    Usage(); return 2;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message); return 1;
}
static void Usage()
{
    Console.WriteLine("keygen [--public-key Build/Updates/update-public.pem] [--private-key 项目外路径]");
    Console.WriteLine("verify-key --public-key 公钥路径（发布私钥从 FMLAUNCHER_UPDATE_PRIVATE_KEY 环境变量读取）");
    Console.WriteLine("pack --input 发布目录 --output 包目录 --version 1.2.3（同一发布私钥环境变量）");
}
