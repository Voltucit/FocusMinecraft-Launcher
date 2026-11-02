using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using FocusMinecraftLauncher.Preview;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>把游戏文件打成 zip 快照；备份只含游戏内容，账号、日志与启动器配置不进入归档。</summary>
public static class GameBackupService
{
    public const string ManifestName = "fmlauncher.backup.json";
    private const string VersionPrefix = "version/", SharedPrefix = "shared/";
    // 版本目录里由启动器或 Minecraft 自己生成、备份价值低且体积大的内容。
    private static readonly string[] SkippedDirectories = ["logs", "crash-reports", "screenshots", "cache", ".cache", ".fmlauncher", "downloads"];
    private static readonly string[] SharedEntries = ["mods", "config", "defaultconfigs", "saves", "resourcepacks", "shaderpacks", "scripts", "options.txt", "servers.dat"];
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static string BackupDirectory(PreviewInstance instance) =>
        Path.Combine(AppContext.BaseDirectory, "FMLauncher", "Backups", Key(instance));
    private static string Key(PreviewInstance instance)
    {
        var text = instance.Root + "|" + instance.Id;
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToLowerInvariant()));
        var name = string.Join("", instance.Name.Split(Path.GetInvalidFileNameChars())).Trim();
        return (name.Length > 24 ? name[..24] : name) + "-" + Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }
    public static IReadOnlyList<GameBackup> List(PreviewInstance instance)
    {
        var directory = BackupDirectory(instance);
        if (!Directory.Exists(directory)) return [];
        return new DirectoryInfo(directory).EnumerateFiles("*.zip").OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(Read).OfType<GameBackup>().ToArray();
    }
    private static GameBackup? Read(FileInfo file)
    {
        var automatic = file.Name.Contains("-auto", StringComparison.OrdinalIgnoreCase);
        var label = automatic ? "启动前自动备份" : "手动备份";
        try
        {
            using var archive = ZipFile.OpenRead(file.FullName);
            if (archive.GetEntry(ManifestName) is { } manifest)
            {
                using var reader = new StreamReader(manifest.Open());
                using var document = JsonDocument.Parse(reader.ReadToEnd());
                if (document.RootElement.TryGetProperty("label", out var text) && text.GetString() is { Length: > 0 } value) label = value;
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { }
        return new(file.FullName, file.Name, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero), file.Length, label, automatic);
    }
    /// <summary>生成快照；同一时刻只跑一个，避免启动与手动备份互相覆盖。</summary>
    public static async Task<GameBackup> CreateAsync(PreviewInstance instance, string label, bool automatic, IProgress<string>? progress, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            var directory = BackupDirectory(instance);
            Directory.CreateDirectory(directory);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var path = Path.Combine(directory, stamp + (automatic ? "-auto.zip" : "-manual.zip"));
            for (var index = 2; File.Exists(path); index++) path = Path.Combine(directory, $"{stamp}-{index}{(automatic ? "-auto" : "-manual")}.zip");
            var entries = Collect(instance);
            var total = entries.Sum(entry => Length(entry.SourcePath));
            progress?.Report($"正在打包 {entries.Count} 个文件（{RuntimeSupport.FormatSize(total)}）");
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            var manifest = archive.CreateEntry(ManifestName, CompressionLevel.NoCompression);
            await using (var writer = new StreamWriter(manifest.Open()))
                await writer.WriteAsync(JsonSerializer.Serialize(new
                {
                    name = instance.Name,
                    gameVersion = instance.Version,
                    loader = instance.Loader,
                    loaderVersion = instance.LoaderVersion,
                    label,
                    automatic,
                    created = DateTimeOffset.Now
                }, Options));
            long written = 0;
            var count = 0;
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var item = archive.CreateEntry(entry.RelativePath.Replace('\\', '/'), CompressionLevel.Fastest);
                item.LastWriteTime = File.GetLastWriteTime(entry.SourcePath);
                await using (var source = File.OpenRead(entry.SourcePath))
                await using (var target = item.Open())
                    await source.CopyToAsync(target, 128 * 1024, token);
                written += Length(entry.SourcePath);
                if (++count % 32 == 0 || count == entries.Count) progress?.Report($"已打包 {count}/{entries.Count} 个文件（{RuntimeSupport.FormatSize(written)}）");
            }
            archive.Dispose();
            var size = new FileInfo(path).Length;
            progress?.Report("备份完成：" + RuntimeSupport.FormatSize(size));
            return new(path, Path.GetFileName(path), DateTimeOffset.Now, size, label, automatic);
        }
        finally { Gate.Release(); }
    }
    /// <summary>还原前先做一次自动备份，恢复失败或恢复错了还能回到原状态。</summary>
    public static async Task RestoreAsync(PreviewInstance instance, GameBackup backup, IProgress<string>? progress, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            progress?.Report("正在读取备份");
            var staging = Path.Combine(Path.GetTempPath(), "fmlauncher-restore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                ZipFile.ExtractToDirectory(backup.FilePath, staging, true);
                var files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                    .Where(file => !file.EndsWith(ManifestName, StringComparison.OrdinalIgnoreCase)).ToArray();
                var index = 0;
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(staging, file);
                    var target = Target(instance, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.Copy(file, temporary, true);
                    File.Move(temporary, target, true);
                    if (++index % 32 == 0 || index == files.Length) progress?.Report($"已还原 {index}/{files.Length} 个文件");
                }
                progress?.Report("还原完成");
            }
            finally { try { Directory.Delete(staging, true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
        }
        finally { Gate.Release(); }
    }
    private static string Target(PreviewInstance instance, string relative)
    {
        var normalized = relative.Replace('\\', '/');
        if (normalized.StartsWith(VersionPrefix, StringComparison.Ordinal))
            return ModpackArchive.SafePath(instance.Folder, normalized[VersionPrefix.Length..]);
        if (normalized.StartsWith(SharedPrefix, StringComparison.Ordinal))
            return ModpackArchive.SafePath(instance.Root, normalized[SharedPrefix.Length..]);
        throw new InvalidDataException("备份条目不在可还原的目录中：" + normalized);
    }
    public static void Delete(GameBackup backup)
    {
        if (!File.Exists(backup.FilePath)) return;
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(backup.FilePath,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }
    /// <summary>只保留最新的若干份，自动备份不会无限堆积。</summary>
    public static void Prune(PreviewInstance instance, int keep)
    {
        if (keep < 1) keep = 1;
        foreach (var backup in List(instance).Skip(keep).ToArray())
            try { Delete(backup); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    /// <summary>尚未备份过、或上次备份早于一小时，才在启动前生成快照。</summary>
    public static bool ShouldBackup(PreviewInstance instance)
    {
        if (!instance.Options.AutoBackup) return false;
        var newest = List(instance).FirstOrDefault();
        return newest == null || DateTimeOffset.Now - newest.Created > TimeSpan.FromHours(1);
    }
    private static IReadOnlyList<BackupEntry> Collect(PreviewInstance instance)
    {
        var entries = new List<BackupEntry>();
        Add(instance, instance.Folder, VersionPrefix, entries, []);
        if (!instance.UsesGameIsolation)
            foreach (var name in SharedEntries)
            {
                var path = Path.Combine(instance.Root, name);
                if (File.Exists(path)) entries.Add(new(path, SharedPrefix + name));
                else if (Directory.Exists(path)) Add(instance, path, SharedPrefix, entries, []);
            }
        return entries.Where(entry => entry.RelativePath != ManifestName).ToArray();
    }
    private static void Add(PreviewInstance instance, string directory, string prefix, List<BackupEntry> entries, HashSet<string> visited)
    {
        var full = Path.GetFullPath(directory);
        if (!visited.Add(full)) return;
        foreach (var file in new DirectoryInfo(full).EnumerateFiles())
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            var relative = Path.GetRelativePath(instance.Folder, file.FullName);
            if (prefix == SharedPrefix) relative = Path.GetRelativePath(instance.Root, file.FullName);
            if (relative.StartsWith("..", StringComparison.Ordinal)) relative = file.Name;
            entries.Add(new(file.FullName, prefix + relative.Replace('\\', '/')));
        }
        foreach (var child in new DirectoryInfo(full).EnumerateDirectories())
        {
            if (child.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (SkippedDirectories.Contains(child.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (child.Name.StartsWith("natives-", StringComparison.OrdinalIgnoreCase)) continue;
            Add(instance, child.FullName, prefix, entries, visited);
        }
    }
    private static long Length(string path) { try { return new FileInfo(path).Length; } catch (IOException) { return 0; } }
    private readonly record struct BackupEntry(string SourcePath, string RelativePath);
}

public sealed record GameBackup(string FilePath, string Name, DateTimeOffset Created, long Size, string Label, bool Automatic);
