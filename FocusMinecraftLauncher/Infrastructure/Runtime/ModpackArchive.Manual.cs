using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using FocusMinecraftLauncher.Preview;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

public static partial class ModpackArchive
{
    private static string CurseForgeFilePage(string? projectPage, long fileId)
        => IsCurseForgePage(projectPage)
            ? projectPage!.TrimEnd('/') + "/files/" + fileId.ToString(CultureInfo.InvariantCulture) : "";

    public static bool IsCurseForgePage(string? address)
        => Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host is "www.curseforge.com" or "curseforge.com" && uri.UserInfo.Length == 0 && uri.IsDefaultPort;

    private static async Task<bool> MatchesEntryAsync(PackFile file, ZipArchiveEntry entry, CancellationToken token)
    {
        if (entry.Length != file.Size) return false;
        await using var stream = entry.Open();
        return Convert.ToHexString(await SHA1.HashDataAsync(stream, token)).Equals(file.Sha1, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>手动选择与重试复用都按清单的真实大小、SHA-1（及已有 SHA-512）校验。</summary>
    public static async Task<bool> MatchesFileAsync(PackFile file, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return false;
        await using var stream = File.OpenRead(path);
        if (stream.Length != file.Size) return false;
        if (!Convert.ToHexString(await SHA1.HashDataAsync(stream, token)).Equals(file.Sha1, StringComparison.OrdinalIgnoreCase)) return false;
        if (file.Sha512.Length == 0) return true;
        stream.Position = 0;
        return Convert.ToHexString(await SHA512.HashDataAsync(stream, token)).Equals(file.Sha512, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>用户提供的 Mod 文件由启动器导入；写入临时文件并再次校验，保护现有游戏文件。</summary>
    private static async Task CopyManualFileAsync(PackFile file, string destination, CancellationToken token)
    {
        if (await MatchesFileAsync(file, destination, token)) return;
        if (File.Exists(destination)) throw new IOException("已有文件与指定版本不同，未覆盖：" + file.FileName);
        if (string.IsNullOrWhiteSpace(file.SourcePath))
            throw new InvalidDataException("请先手动补齐整合包资源：" + file.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = File.OpenRead(file.SourcePath))
            await using (var output = File.Create(temporary))
                await input.CopyToAsync(output, token);
            if (!await MatchesFileAsync(file, temporary, token))
                throw new InvalidDataException("所选文件已变化或与指定版本不符，请重新选择：" + file.FileName);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RecordPackOrigin(PackFile file, PreviewInstance instance, InstanceOptions options)
    {
        var url = file.Urls.FirstOrDefault() ?? "";
        if (!AllowedDownload(url) && file.CurseForgeProjectId == 0) return;
        options.ResourcesFor(instance)[file.Path.Replace('\\', '/')] = new(
            file.CurseForgeProjectId > 0 ? "cf-" + file.CurseForgeProjectId.ToString(CultureInfo.InvariantCulture) : "",
            file.CurseForgeFileId > 0 ? file.CurseForgeFileId.ToString(CultureInfo.InvariantCulture) : "",
            url, file.Sha1, file.Sha512, file.Size, file.FileName);
    }
}
