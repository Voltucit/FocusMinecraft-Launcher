using System.IO;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

internal sealed record StorageLocation(string Name, string Path, bool IsGameRoot = false);
internal sealed record StorageRow(string Name, string Path, string Size, string Reclaimable);
internal sealed record CachedFile(string Path, long Length, DateTime LastWriteUtc);
internal sealed record StorageSnapshot(IReadOnlyList<StorageRow> Rows, IReadOnlyList<StorageRow> Drives,
    IReadOnlyList<CachedFile> Cleanup, bool IsPartial, bool ArchivesInUse);

internal sealed class LauncherStorage(string configDirectory, IReadOnlyList<StorageLocation> games,
    HashSet<string> pendingArchives, bool archivesInUse, string? affectedRoot)
{
    private readonly string _downloads = Path.Combine(configDirectory, "Downloads");
    private static readonly string[] GameDirectories =
        ["versions", "assets", "libraries", "saves", "mods", "resourcepacks", "shaderpacks", "config", "defaultconfigs",
         "natives", "logs", "crash-reports", "screenshots", "datapacks", "server-resource-packs"];

    public StorageSnapshot Scan(CancellationToken token)
    {
        var locations = games.Concat([
            new StorageLocation("启动器管理的 Java", Path.Combine(configDirectory, "Java")),
            new StorageLocation("游戏备份", Path.Combine(configDirectory, "Backups")),
            new StorageLocation("整合包下载缓存", _downloads),
            new StorageLocation("社区资源缓存", CommunityCache.Root)
        ]).ToArray();
        var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 小计共享资源的实际目录时，同一路径只计算一次。
        foreach (var path in locations.Where(location => !location.IsGameRoot).Select(location => location.Path))
            scanned.Add(path);
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<StorageRow>();
        var cleanup = new List<CachedFile>();
        var partial = false;
        foreach (var location in locations)
        {
            token.ThrowIfCancellationRequested();
            if (!completed.Add(location.Path)) continue;
            string[] folders = location.IsGameRoot
                ? GameDirectories.Select(name => Path.Combine(location.Path, name)).Where(path => scanned.Add(path)).ToArray()
                : [location.Path];
            long bytes = 0, reclaimable = 0;
            foreach (var folder in folders)
            {
                Visit(folder, token, file =>
                {
                    bytes += file.Length;
                    if (!CanClean(file.FullName)) return;
                    reclaimable += file.Length;
                    cleanup.Add(new(file.FullName, file.Length, file.LastWriteTimeUtc));
                }, () => partial = true);
            }
            var isCache = SamePath(location.Path, _downloads) || SamePath(location.Path, CommunityCache.Root);
            rows.Add(new(location.Name, location.Path, RuntimeSupport.FormatSize(bytes),
                isCache ? RuntimeSupport.FormatSize(reclaimable) : "—"));
        }
        var drives = new List<StorageRow>();
        foreach (var root in locations.Select(location => location.Path).Append(configDirectory).Append(affectedRoot)
                     .Append(Path.GetTempPath()).Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(path => Path.GetPathRoot(path!)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var drive = new DriveInfo(root);
                drives.Add(new(root, "", drive.IsReady ? "可用 " + RuntimeSupport.FormatSize(drive.AvailableFreeSpace) : "空间未知", ""));
            }
            catch (Exception error) when (Unreadable(error) || error is ArgumentException)
            {
                drives.Add(new(root, "", "空间未知", ""));
            }
        }
        return new(rows, drives, cleanup, partial, archivesInUse);
    }

    public (int Count, long Bytes, int Skipped) Clean(IReadOnlyList<CachedFile> preview)
    {
        var count = 0;
        var skipped = 0;
        long bytes = 0;
        foreach (var candidate in preview)
        {
            if (CanClean(candidate.Path) &&
                (IsCommunityCache(candidate.Path) ? CommunityCache.ClearIfUnchanged(candidate) : TryDelete(candidate)))
            {
                count++;
                bytes += candidate.Length;
            }
            else skipped++;
        }
        return (count, bytes, skipped);
    }

    private bool CanClean(string path)
    {
        if (pendingArchives.Contains(path)) return false;
        if (IsCommunityCache(path)) return true;
        if (archivesInUse) return false;
        var pack = Path.GetRelativePath(_downloads, path).Split(Path.DirectorySeparatorChar);
        if (pack.Length == 5 && SamePath(pack[0], "InstallQueue"))
            return (SamePath(pack[1], "Modrinth") && HasExtension(path, ".mrpack")) ||
                   (SamePath(pack[1], "CurseForge") && HasExtension(path, ".zip"));
        // 旧版使用 Downloads/<Modrinth SHA-1 或版本 ID> 和 Downloads/curseforge。
        if (pack.Length != 2) return false;
        return (SamePath(pack[0], "curseforge") && HasExtension(path, ".zip")) ||
               (HasExtension(path, ".mrpack") && (IsHex(pack[0], 40)
                    || (pack[0].Length == 8 && pack[0].All(char.IsAsciiLetterOrDigit))));
    }

    private static bool IsCommunityCache(string path)
    {
        var cache = Path.GetRelativePath(CommunityCache.Root, path).Split(Path.DirectorySeparatorChar);
        return cache.Length == 2 && IsHex(cache[0], 2) && IsHex(Path.GetFileNameWithoutExtension(cache[1]), 64)
            && cache[1].StartsWith(cache[0], StringComparison.OrdinalIgnoreCase)
            && Path.GetExtension(cache[1]) is ".txt" or ".bin";
    }

    internal static bool TryDelete(CachedFile file)
    {
        try
        {
            if (!IsOrdinaryPath(file.Path)) return false;
            using var handle = File.Open(file.Path, FileMode.Open, FileAccess.Read, FileShare.Delete);
            if (handle.Length != file.Length || File.GetLastWriteTimeUtc(file.Path) != file.LastWriteUtc) return false;
            File.Delete(file.Path);
            return true;
        }
        catch (Exception error) when (Unreadable(error)) { return false; }
    }

    private static void Visit(string folder, CancellationToken token, Action<FileInfo> fileFound, Action skipped)
    {
        try
        {
            if (!IsOrdinaryPath(folder)) { skipped(); return; }
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return; }
        catch (Exception error) when (Unreadable(error)) { skipped(); return; }

        var folders = new Stack<DirectoryInfo>();
        folders.Push(new(folder));
        while (folders.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            skipped();
                            continue;
                        }
                        if (entry is FileInfo file) fileFound(file);
                        else if (entry is DirectoryInfo child) folders.Push(child);
                    }
                    catch (Exception error) when (Unreadable(error)) { skipped(); }
                }
            }
            catch (Exception error) when (Unreadable(error)) { skipped(); }
        }
    }

    private static bool IsOrdinaryPath(string path)
    {
        // 从盘根开始核对，避免穿过缓存上层目录中的联接点。
        var ancestors = new Stack<string>();
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
            ancestors.Push(current);
        return ancestors.All(entry => !File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint));
    }

    private static bool Unreadable(Exception error) => error is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    private static bool IsHex(string text, int length) => text.Length == length && text.All(Uri.IsHexDigit);
    private static bool SamePath(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool HasExtension(string path, string extension) => SamePath(Path.GetExtension(path), extension);
}
