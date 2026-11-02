using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FMLauncher.UpdateShared;

/// <summary>客户端、临时更新器和发布工具共用的签名协议。</summary>
public static class UpdateProduct
{
    public const string Id = "FMLauncher";
    public const string Repository = "Voltucit/FocusMinecraft-Launcher";
    public const string Executable = "FocusMinecraftLauncher.exe";
    public const string Runtime = "win-x64";
    public const string ManifestName = "FMLauncher-win-x64.update.json";
    public const string SignatureName = ManifestName + ".sig";
    public const string KeyResource = "FMLauncher.UpdatePublicKey";
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static string Version(Assembly assembly) => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public static string PublicKey(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream(KeyResource);
        if (stream == null) return "";
        using var reader = new StreamReader(stream); var text = reader.ReadToEnd();
        if (!text.TrimStart().StartsWith("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal) || text.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new InvalidDataException("更新公钥配置无效。");
        return text;
    }
}

public sealed record UpdateFile(string Path, long Size, string Sha256);
public sealed class UpdateManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Product { get; set; } = UpdateProduct.Id;
    public string Version { get; set; } = "";
    public string Runtime { get; set; } = UpdateProduct.Runtime;
    public string PackageName { get; set; } = "";
    public long PackageSize { get; set; }
    public string PackageSha256 { get; set; } = "";
    public List<UpdateFile> Files { get; set; } = [];

    /// <summary>先验证原始字节的发布签名，再解释清单；校验值不替代来源签名。</summary>
    public static UpdateManifest Verify(byte[] data, byte[] signature, string publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey)) throw new InvalidOperationException("此版本尚未配置在线更新，请从发布页面下载。");
        if (data.Length > 4 * 1024 * 1024 || signature.Length != 64) throw new InvalidDataException("更新签名格式无效。");
        using var key = ECDsa.Create(); key.ImportFromPem(publicKey);
        if (key.KeySize != 256 || !key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidDataException("更新签名验证失败，已停止更新。");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(data, UpdateProduct.Json) ?? throw new InvalidDataException("更新清单为空。");
        manifest.Validate(); return manifest;
    }
    public void Validate()
    {
        if (SchemaVersion != 1 || Product != UpdateProduct.Id || Runtime != UpdateProduct.Runtime || !ReleaseVersion.TryParse(Version, out _))
            throw new InvalidDataException("更新包不适用于此启动器或系统架构。");
        if (System.IO.Path.GetFileName(PackageName) != PackageName || !PackageName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || PackageSize <= 0 || PackageSize > 2L * 1024 * 1024 * 1024 || !IsHash(PackageSha256)) throw new InvalidDataException("更新包声明无效。");
        if (Files is not { Count: > 0 and <= 10000 } || Files.Sum(f => (decimal)f.Size) > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("更新文件清单无效。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            UpdatePaths.ValidateProgramPath(file.Path);
            if (file.Size < 0 || !IsHash(file.Sha256) || !paths.Add(file.Path)) throw new InvalidDataException("更新文件声明重复或无效。");
        }
        if (!paths.Contains(UpdateProduct.Executable)) throw new InvalidDataException("更新包缺少启动器程序。");
    }
    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    public static async Task<string> HashAsync(string path, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
    public static async Task VerifyFileAsync(string path, long size, string hash, CancellationToken token = default)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size || !string.Equals(await HashAsync(path, token), hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新文件校验失败：" + System.IO.Path.GetFileName(path));
    }
}

/// <summary>比较三段式版本及预发布标识，避免按文本顺序比较版本号。</summary>
public sealed record ReleaseVersion(int Major, int Minor, int Patch, string Prerelease) : IComparable<ReleaseVersion>
{
    public bool IsPrerelease => Prerelease.Length > 0;
    public static bool TryParse(string text, out ReleaseVersion version)
    {
        version = new(0, 0, 0, "");
        var match = Regex.Match(text, @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) || !int.TryParse(match.Groups[2].Value, out var minor) || !int.TryParse(match.Groups[3].Value, out var patch)) return false;
        version = new(major, minor, patch, match.Groups[4].Value); return true;
    }
    public int CompareTo(ReleaseVersion? other)
    {
        if (other == null) return 1;
        var result = Major.CompareTo(other.Major); if (result != 0) return result;
        result = Minor.CompareTo(other.Minor); if (result != 0) return result;
        result = Patch.CompareTo(other.Patch); if (result != 0) return result;
        if (!IsPrerelease || !other.IsPrerelease) return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        var left = Prerelease.Split('.'); var right = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var a = long.TryParse(left[i], out var an); var b = long.TryParse(right[i], out var bn);
            result = a && b ? an.CompareTo(bn) : a != b ? a ? -1 : 1 : string.CompareOrdinal(left[i], right[i]);
            if (result != 0) return result;
        }
        return left.Length.CompareTo(right.Length);
    }
}
