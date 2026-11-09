using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;

namespace FocusMinecraftLauncher.Features.Multiplayer.Services;

/// <summary>仅在用户同意后下载独立组件；发布产物不包含 EasyTier，组件可单独卸载。</summary>
public static class EasyTierComponents
{
    public const string Version = "2.6.4";
    public static string Root => Path.Combine(AppContext.BaseDirectory, "FMLauncher", "Networking", "EasyTier");
    private static string ArchitectureName => RuntimeInformation.OSArchitecture switch
    { Architecture.X64 => "x86_64", Architecture.Arm64 => "arm64", Architecture.X86 => "i686", _ => throw new PlatformNotSupportedException("当前系统架构不支持此联机组件。") };
    public static string ArchivePath => Path.Combine(Root, Version, ArchitectureName, "easytier.zip");
    public static bool IsInstalled => File.Exists(ArchivePath);
    public static string DownloadUrl => $"https://github.com/EasyTier/EasyTier/releases/download/v{Version}/easytier-windows-{ArchitectureName}-v{Version}.zip";
    private static string ExpectedHash => ArchitectureName switch
    {
        "x86_64" => "27af91e270e554709b048bd32327fefd2dfce5062ae1e8701af7550c6f525f84",
        "arm64" => "37023f8a3451c9234b17ee2089a03dc344ce90d803b5b359cb6c46682b0549b4",
        _ => "bf557daeccc5525d95b8a230c339d75554fb52d82d1e050e9a5202c92c02e09e"
    };

    public static async Task<bool> VerifyAsync(CancellationToken token)
    {
        if (!IsInstalled) return false;
        await using var file = File.OpenRead(ArchivePath);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(ExpectedHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>调用方必须先展示下载确认；取消和失败只移除本次临时文件。</summary>
    public static async Task DownloadAsync(IProgress<(long Received, long? Total)> progress, CancellationToken token)
    {
        var destination = ArchivePath;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FMLauncher/" + typeof(App).Assembly.GetName().Version);
            using var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long received = 0;
                while (true)
                {
                    var count = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > 80L * 1024 * 1024) throw new InvalidDataException("联机组件大小异常，已停止下载。");
                    await target.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    progress.Report((received, total));
                }
            }
            await using (var downloaded = File.OpenRead(temporary))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(downloaded, token)).Equals(ExpectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("联机组件校验失败，请重新下载。");
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>只删除启动器拥有的组件目录，不触碰游戏、账号或其他软件使用的系统驱动。</summary>
    public static void Uninstall()
    {
        var root = Path.GetFullPath(Root);
        var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "FMLauncher", "Networking", "EasyTier"));
        if (!root.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("联机组件目录无效。");
        if (Directory.Exists(root))
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("联机组件目录不能是链接。");
            Directory.Delete(root, true);
        }
    }

    /// <summary>提权进程先验证完整官方压缩包，再解压到仅管理员可写的运行目录，避免执行被替换的散装文件。</summary>
    internal static async Task<string> PrepareWorkerAsync(CancellationToken token)
    {
        byte[] bytes = await ReadVerifiedArchiveAsync(token).ConfigureAwait(false);
        var applicationRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FMLauncher");
        if (Directory.Exists(applicationRoot) && (File.GetAttributes(applicationRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("联机运行目录不能是链接。");
        var parent = Path.Combine(applicationRoot, "EasyTier-Runtime");
        SecureDirectory(parent);
        var work = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        SecureDirectory(work);
        try
        {
            await ExtractAsync(bytes, work, token).ConfigureAwait(false);
            return work;
        }
        catch { DeleteWorkerDirectory(work); throw; }
    }

    /// <summary>设置页的连通性预检使用当前用户临时目录，不提权；文件仍先经过官方 SHA-256 校验。</summary>
    internal static async Task<string> PrepareProbeAsync(CancellationToken token)
    {
        byte[] bytes = await ReadVerifiedArchiveAsync(token).ConfigureAwait(false);
        string root = Path.Combine(Path.GetTempPath(), "FMLauncher", "EasyTierProbe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await ExtractAsync(bytes, root, token).ConfigureAwait(false);
            return root;
        }
        catch
        {
            DeleteProbeDirectory(root);
            throw;
        }
    }

    private static async Task<byte[]> ReadVerifiedArchiveAsync(CancellationToken token)
    {
        if (!File.Exists(ArchivePath) || new FileInfo(ArchivePath).Length > 80L * 1024 * 1024) throw new IOException("联机组件未下载，请返回后重试。");
        var bytes = await File.ReadAllBytesAsync(ArchivePath, token).ConfigureAwait(false);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(ExpectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("联机组件已损坏，请在设置中卸载后重新下载。");
        return bytes;
    }

    private static async Task ExtractAsync(byte[] bytes, string work, CancellationToken token)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        foreach (var name in new[] { "easytier-core.exe", "easytier-cli.exe", "wintun.dll", "Packet.dll", "WinDivert64.sys" })
        {
            var entry = archive.Entries.SingleOrDefault(x => Path.GetFileName(x.FullName.Replace('\\', '/')).Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("官方联机组件缺少必要文件：" + name);
            if (entry.Length > 160L * 1024 * 1024) throw new InvalidDataException("联机组件解压大小异常。");
            await using var source = entry.Open();
            await using var target = File.Create(Path.Combine(work, name));
            await source.CopyToAsync(target, token).ConfigureAwait(false);
        }
    }

    internal static void DeleteProbeDirectory(string work)
    {
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FMLauncher", "EasyTierProbe")) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(work);
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(target), "N", out _)) return;
        if (Directory.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) == 0) Directory.Delete(target, true);
    }

    private static void SecureDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("联机运行目录不能是链接。");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (!directory.Exists) FileSystemAclExtensions.Create(directory, security);
        else FileSystemAclExtensions.SetAccessControl(directory, security);
    }

    internal static void DeleteWorkerDirectory(string work)
    {
        var parent = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FMLauncher", "EasyTier-Runtime")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(work);
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(target), "N", out _)) return;
        if (Directory.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) == 0) Directory.Delete(target, true);
    }
}
