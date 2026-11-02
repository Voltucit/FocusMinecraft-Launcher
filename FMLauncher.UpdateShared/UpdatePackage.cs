using System.IO.Compression;

namespace FMLauncher.UpdateShared;

public static class UpdatePackage
{
    /// <summary>完整校验包与每个文件后再准备替换，不直接将压缩包解到程序目录。</summary>
    public static async Task<UpdateManifest> PrepareAsync(string root, string id, string key, CancellationToken token = default)
    {
        var job = UpdatePaths.Job(root, id);
        var manifest = UpdateManifest.Verify(await File.ReadAllBytesAsync(Path.Combine(job, "manifest.json"), token),
            await File.ReadAllBytesAsync(Path.Combine(job, "manifest.sig"), token), key);
        var package = Path.Combine(job, "package.zip");
        await UpdateManifest.VerifyFileAsync(package, manifest.PackageSize, manifest.PackageSha256, token);
        var destination = Path.Combine(job, "new");
        UpdatePaths.RejectLinks(root, destination);
        if (Directory.Exists(destination)) Directory.Delete(destination, true);
        Directory.CreateDirectory(destination);
        var expected = manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(package);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var relative = entry.FullName.TrimEnd('/');
            UpdatePaths.ValidateProgramPath(relative);
            if (entry.FullName.EndsWith('/')) continue;
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || !seen.Add(relative)
                || !expected.TryGetValue(relative, out var file) || entry.Length != file.Size)
                throw new InvalidDataException("更新压缩包包含未声明或无效的文件。");
            var output = UpdatePaths.ProgramFile(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using (var source = entry.Open())
            await using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                await source.CopyToAsync(target, token);
            await UpdateManifest.VerifyFileAsync(output, file.Size, file.Sha256, token);
        }
        if (seen.Count != expected.Count) throw new InvalidDataException("更新压缩包缺少文件。");
        return manifest;
    }
}
