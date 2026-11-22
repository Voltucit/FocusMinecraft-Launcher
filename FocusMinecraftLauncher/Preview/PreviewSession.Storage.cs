using System.IO;
using FocusMinecraftLauncher.Infrastructure.Runtime;

namespace FocusMinecraftLauncher.Preview;

public sealed partial class PreviewSession
{
    private bool _cleaningStorage;

    internal LauncherStorage GetStorage(string? affectedRoot)
    {
        var games = GameFolders.Append(SelectedRoot).Append(SavedSettings.GameDirectory).Append(affectedRoot)
            .Select(FullStoragePath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var locations = new List<StorageLocation>();
        foreach (var root in games)
        {
            locations.Add(new("游戏文件", root, IsGameRoot: true));
            if (FullStoragePath(ReadResourceLink(root, "assets")) is { } assets) locations.Add(new("共享游戏资源", assets));
            if (FullStoragePath(ReadResourceLink(root, "libraries")) is { } libraries) locations.Add(new("共享游戏库", libraries));
        }

        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in Tasks)
        {
            if (task.Installation is not { } plan) continue;
            var path = plan.Origin switch
            {
                InstallationOrigin.LocalPack => FullStoragePath(plan.ArchivePath),
                InstallationOrigin.Modrinth or InstallationOrigin.CurseForge => FullStoragePath(OnlineArchivePath(plan)),
                _ => null
            };
            if (path != null) pending.Add(path);
        }
        return new(_configuration.ConfigDirectory, locations, pending, HasPendingWrites, FullStoragePath(affectedRoot));
    }

    internal async Task<(int Count, long Bytes, int Skipped)> CleanStorageAsync(StorageSnapshot preview, string? affectedRoot)
    {
        var storage = GetStorage(affectedRoot);
        _cleaningStorage = true;
        try { return await Task.Run(() => storage.Clean(preview.Cleanup)); }
        finally { _cleaningStorage = false; }
    }

    private static string? FullStoragePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return null; }
    }
}
