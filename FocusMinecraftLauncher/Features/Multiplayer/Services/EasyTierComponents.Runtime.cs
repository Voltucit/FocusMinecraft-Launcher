using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace FocusMinecraftLauncher.Features.Multiplayer.Services;

public static partial class EasyTierComponents
{
    private const string WorkerLockName = ".fmlauncher-runtime.lock";
    private static string WorkerRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "FMLauncher", "MultiCatch"));
    // 旧位置仅用于回收历史残留，不再创建或解压新的运行文件。
    private static string LegacyWorkerRoot => Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FMLauncher", "EasyTier-Runtime"));
    private static string[] WorkerRoots => [WorkerRoot, LegacyWorkerRoot];

    /// <summary>持有整个联机期间的占用标记，另一份启动器不能把使用中的目录当成遗留目录。</summary>
    internal sealed class WorkerDirectory : IDisposable
    {
        private FileStream? _lock;
        public string DirectoryPath { get; }
        internal WorkerDirectory(string directory)
        {
            DirectoryPath = directory;
            _lock = new FileStream(Path.Combine(directory, WorkerLockName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        }
        public void Dispose() => Interlocked.Exchange(ref _lock, null)?.Dispose();
    }

    /// <summary>只由现有已验证身份的管理员工作进程调用，不额外请求提权。</summary>
    private static async Task CleanupAbandonedWorkerDirectoriesAsync(CancellationToken token)
    {
        HashSet<string>? active;
        try { active = ActiveWorkerDirectories(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        { LogCleanupFailure(WorkerRoot, error); return; }
        // 不能确认某个进程的位置时，保留目录供下次处理，不冒险删除。
        if (active == null) return;
        foreach (var root in WorkerRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            // 读取旧目录前也验证父目录和根目录，避免跟随被替换的目录联接。
            string[] directories;
            try
            {
                if (!IsWorkerRootSafe(root)) continue;
                if (!Directory.Exists(root)) continue;
                directories = Directory.GetDirectories(root);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { LogCleanupFailure(root, error); continue; }
            foreach (var work in directories)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (!TryGetWorkerDirectory(work, out var directory) || directory == null || active.Contains(directory.FullName)) continue;
                    // 兼容旧版本没有占用标记的目录，也避开另一个工作进程刚创建、尚未取得标记的短暂窗口。
                    if (DateTime.UtcNow - directory.CreationTimeUtc < TimeSpan.FromMinutes(1)) continue;
                    if (directory.EnumerateFileSystemInfos().Any(entry =>
                        (entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)) continue;
                    using (new FileStream(Path.Combine(work, WorkerLockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
                    // 遗留目录只尝试一次，避免大量旧目录的重试拖慢创建房间；当前会话退出会做短暂重试。
                    await DeleteWorkerDirectoryAsync(work, retry: false).ConfigureAwait(false);
                }
                catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { /* 另一个会话仍持有占用标记。 */ }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { LogCleanupFailure(work, error); }
            }
        }
    }

    private static HashSet<string>? ActiveWorkerDirectories()
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "easytier-core", "easytier-cli" })
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited) continue;
                    var executable = process.MainModule?.FileName;
                    if (string.IsNullOrEmpty(executable)) return null;
                    directories.Add(Path.GetFullPath(Path.GetDirectoryName(executable)!));
                }
                catch (InvalidOperationException) { /* 进程已在枚举后退出。 */ }
                catch (Win32Exception error)
                {
                    try { if (process.HasExited) continue; } catch (InvalidOperationException) { continue; }
                    Trace.TraceWarning("无法确认 EasyTier 进程目录，跳过遗留目录清理：Windows {0}", error.NativeErrorCode);
                    return null;
                }
            }
        }
        return directories;
    }

    internal static async Task<bool> DeleteWorkerDirectoryAsync(string work, bool retry = true)
    {
        var delays = retry ? new[] { 0, 150, 350, 700, 1200 } : new[] { 0 };
        Exception? failure = null;
        foreach (var delay in delays)
        {
            if (delay > 0) await Task.Delay(delay).ConfigureAwait(false);
            try
            {
                if (!TryGetWorkerDirectory(work, out var directory)) return false;
                if (directory == null) return true;
                // 运行目录只有直接解压的文件。发现子目录或链接时保留，不递归进入不认识的内容。
                foreach (var entry in directory.EnumerateFileSystemInfos())
                    if ((entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                        throw new IOException("联机运行目录包含子目录或链接，已保留：" + entry.Name);
                Directory.Delete(directory.FullName, recursive: true);
                return true;
            }
            catch (DirectoryNotFoundException) { return true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { failure = error; }
        }
        if (failure != null) LogCleanupFailure(work, failure);
        return false;
    }

    private static bool TryGetWorkerDirectory(string work, out DirectoryInfo? directory)
    {
        directory = null;
        var target = Path.GetFullPath(work).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = WorkerRoots.FirstOrDefault(candidate => string.Equals(Path.GetDirectoryName(target), candidate, StringComparison.OrdinalIgnoreCase));
        if (root == null || !Guid.TryParseExact(Path.GetFileName(target), "N", out _) || !IsWorkerRootSafe(root)) return false;
        var found = new DirectoryInfo(target);
        if (!found.Exists) return true;
        if ((found.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        directory = found;
        return true;
    }

    private static bool IsWorkerRootSafe(string root)
    {
        foreach (var ancestor in new[] { Path.GetDirectoryName(root)!, root })
            if (Directory.Exists(ancestor) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    private static void LogCleanupFailure(string work, Exception error) =>
        Trace.TraceWarning("EasyTier 临时目录暂未清理，将在下次联机时重试：{0}；{1} / 0x{2:X8}：{3}",
            work, error.GetType().Name, error.HResult, error.Message);
}
