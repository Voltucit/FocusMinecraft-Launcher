using System.Diagnostics;
using System.IO;
using System.Reflection;
using FMLauncher.UpdateShared;

namespace FocusMinecraftLauncher.Infrastructure.Updates;

/// <summary>更新交接在普通配置初始化前处理，避免从临时目录启动第二份启动器配置。</summary>
public static class UpdateBootstrap
{
    public static string StartupJob { get; private set; } = "";
    public static string StartupNotice { get; private set; } = "";
    public static bool IsRollbackNotice { get; private set; }
    public static string Root => Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
    public static Process StartHelper(string root, string id, string mode)
    {
        var job = UpdatePaths.Job(root, id);
        var temporary = Path.Combine(Path.GetTempPath(), "FMLauncher.Updater", id);
        UpdatePaths.RejectLinks(Path.GetTempPath(), temporary); Directory.CreateDirectory(temporary);
        var helper = Path.Combine(temporary, "FMLauncher.Updater.exe");
        using (var resource = typeof(App).Assembly.GetManifestResourceStream("FMLauncher.EmbeddedUpdater") ?? throw new IOException("此程序未包含更新组件，请从发布页面下载完整版本。"))
        using (var file = new FileStream(helper, FileMode.Create, FileAccess.Write, FileShare.None)) resource.CopyTo(file);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = temporary };
        start.ArgumentList.Add(mode); start.ArgumentList.Add(Path.Combine(job, "job.json"));
        return Process.Start(start) ?? throw new IOException("无法启动更新组件。");
    }
    /// <returns>已交给临时恢复进程时返回 true，调用方立即退出当前主程序。</returns>
    public static bool RecoverBeforeStartup(string[] args)
    {
        var cache = UpdatePaths.Cache(Root);
        if (!Directory.Exists(cache)) return false;
        string Argument(string name)
        { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : ""; }
        var started = Argument("--update-session"); var recovered = Argument("--update-rollback");
        foreach (var directory in Directory.EnumerateDirectories(cache))
        {
            var id = Path.GetFileName(directory); if (!Guid.TryParseExact(id, "N", out _)) continue;
            var path = Path.Combine(UpdatePaths.Job(Root, id), "job.json"); if (!File.Exists(path)) continue;
            var journal = UpdatePaths.ReadJournal(path);
            if (id == started && journal.Phase == "AwaitingStartup" && journal.Version == UpdateProduct.Version(typeof(App).Assembly))
            {
                UpdateManifest.Verify(File.ReadAllBytes(Path.Combine(directory, "manifest.json")), File.ReadAllBytes(Path.Combine(directory, "manifest.sig")), UpdateProduct.PublicKey(typeof(App).Assembly));
                StartupJob = id; continue;
            }
            if (id == recovered && journal.Phase == "RolledBack")
            { StartupNotice = "更新未完成，已恢复旧版启动器。"; IsRollbackNotice = true; }
            if (journal.Phase is "Applying" or "AwaitingStartup" or "RollingBack" or "RollbackFailed")
            {
                // 有更新器正在处理时不启动第二份主程序，也不覆盖它的操作记录。
                try { using var probe = new FileStream(Path.Combine(cache, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { return true; }
                using var current = Process.GetCurrentProcess();
                journal.RecoveryPid = current.Id; journal.RecoveryStartedUtcTicks = current.StartTime.ToUniversalTime().Ticks;
                UpdatePaths.Save(path, journal);
                StartHelper(Root, id, "--recover").Dispose(); return true;
            }
        }
        return false;
    }
    /// <summary>主窗口已绘制才确认新版可打开；启动进程成功本身不代表更新成功。</summary>
    public static async Task CompleteStartupAsync(CancellationToken token)
    {
        if (StartupJob.Length == 0) return;
        var job = UpdatePaths.Job(Root, StartupJob);
        UpdatePaths.AtomicBytes(Path.Combine(job, "healthy"), System.Text.Encoding.UTF8.GetBytes(Environment.ProcessId.ToString()));
        for (var i = 0; i < 100; i++)
        {
            token.ThrowIfCancellationRequested();
            var journal = UpdatePaths.ReadJournal(Path.Combine(job, "job.json"));
            if (journal.Phase == "Completed")
            {
                StartupNotice = "启动器已更新至 " + journal.Version;
                await Task.Delay(500, token);
                CleanupCompleted(job, StartupJob); return;
            }
            if (journal.Phase is "RollingBack" or "RolledBack" or "RollbackFailed") return;
            await Task.Delay(200, token);
        }
        throw new IOException("更新完成状态尚未确认，请保留更新缓存并查看日志。");
    }
    private static void CleanupCompleted(string job, string id)
    {
        // 只清理已完成会话的新文件和压缩包；保留恢复备份、签名与操作记录。
        try
        {
            UpdatePaths.RejectLinks(Root, job);
            var payload = Path.Combine(job, "new"); if (Directory.Exists(payload)) Directory.Delete(payload, true);
            var package = Path.Combine(job, "package.zip"); if (File.Exists(package)) File.Delete(package);
            var temporary = Path.Combine(Path.GetTempPath(), "FMLauncher.Updater", id);
            UpdatePaths.RejectLinks(Path.GetTempPath(), temporary);
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Trace.TraceWarning("更新临时文件清理延期：{0}", error.Message); }
    }
}
