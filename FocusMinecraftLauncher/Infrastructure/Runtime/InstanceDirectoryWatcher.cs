using System.IO;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>监听当前游戏目录的 versions；用户从资源管理器拖入版本后自动刷新实例列表。</summary>
public sealed class InstanceDirectoryWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly Action _changed;
    private readonly string _root;
    private int _pending;

    public InstanceDirectoryWatcher(string root, Action changed)
    {
        _root = Path.GetFullPath(root);
        _changed = changed;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024
        };
        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsVersionPath(e.FullPath)) return;
        Schedule();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsVersionPath(e.FullPath) && !IsVersionPath(e.OldFullPath)) return;
        Schedule();
    }

    private bool IsVersionPath(string path)
    {
        try
        {
            string relative = Path.GetRelativePath(_root, path);
            return relative.StartsWith("versions" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(relative, "versions", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Schedule()
    {
        Interlocked.Exchange(ref _pending, 1);
        // 复制一个版本会连续产生大量事件；等目录安静 800ms 后再扫描，避免读到半截 JSON。
        try { _timer.Change(800, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private void Flush()
    {
        if (Interlocked.Exchange(ref _pending, 0) == 0) return;
        try { _changed(); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _timer.Dispose();
    }
}
