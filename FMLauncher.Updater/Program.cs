using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using FMLauncher.UpdateShared;

return await Runner.RunAsync(args);

internal static class Runner
{
    /// <summary>只接受本机更新会话，独立验证签名和文件；主进程退出后才接触程序文件。</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("--apply" or "--recover")) return 2;
        UpdateJournal? journal = null;
        var journalPath = Path.GetFullPath(args[1]);
        var key = UpdateProduct.PublicKey(Assembly.GetExecutingAssembly());
        bool parentExited = false;
        FileStream? updateLock = null;
        Process? child = null;
        try
        {
            journal = UpdatePaths.ReadJournal(journalPath);
            // 独占文件句柄跨 await 保持有效，进程意外退出时由系统释放。
            try { updateLock = new FileStream(Path.Combine(UpdatePaths.Cache(journal.Root), "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return 3; }
            var job = UpdatePaths.Job(journal.Root, journal.Id);
            // 原始签名仍独立验证；不能仅信任主程序已经完成的检查。
            var manifest = UpdateManifest.Verify(await File.ReadAllBytesAsync(Path.Combine(job, "manifest.json")), await File.ReadAllBytesAsync(Path.Combine(job, "manifest.sig")), key);
            if (journal.Version != manifest.Version) throw new InvalidDataException("更新会话与签名版本不一致。");
            if (args[0] == "--recover")
            {
                if (journal.Phase is not ("Applying" or "AwaitingStartup" or "RollingBack" or "RollbackFailed")) return 0;
                await WaitRecoveryParentAsync(journal);
                parentExited = true;
                await RollbackAsync(journalPath, journal);
                StartLauncher(journal, "--update-rollback"); return 0;
            }
            if (journal.Phase != "Prepared") throw new InvalidDataException("更新会话状态无效。");
            using (var parent = Process.GetProcessById(journal.ParentPid))
            {
                if (parent.StartTime.ToUniversalTime().Ticks != journal.ParentStartedUtcTicks
                    || !string.Equals(parent.MainModule?.FileName, journal.ParentExecutable, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(Path.GetDirectoryName(journal.ParentExecutable), Path.GetFullPath(journal.Root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新发起程序不匹配。");
                journal.Phase = "WaitingForExit"; UpdatePaths.Save(journalPath, journal);
                UpdatePaths.AtomicBytes(Path.Combine(job, "ready"), []);
                await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
            }
            parentExited = true;
            await UpdatePackage.PrepareAsync(journal.Root, journal.Id, key);
            var previous = ReadPrevious(journal.Root, key);
            var next = manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var changes = manifest.Files.Select(f => new UpdateChange { Path = f.Path }).ToList();
            if (previous != null)
                foreach (var old in previous.Files.Where(f => !next.Contains(f.Path)))
                {
                    var target = UpdatePaths.ProgramFile(journal.Root, old.Path, journal.ProtectedDirectories);
                    // 用户自行修改过的旧文件保留，不把它当作可清理的发布文件。
                    if (File.Exists(target) && new FileInfo(target).Length == old.Size && string.Equals(await UpdateManifest.HashAsync(target), old.Sha256, StringComparison.OrdinalIgnoreCase))
                        changes.Add(new UpdateChange { Path = old.Path, RemoveOnly = true });
                }
            foreach (var change in changes)
            {
                var target = UpdatePaths.ProgramFile(journal.Root, change.Path, journal.ProtectedDirectories);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target)) using (File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                var probe = Path.Combine(Path.GetDirectoryName(target)!, ".fml-update-" + Guid.NewGuid().ToString("N"));
                try { using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
                finally { if (File.Exists(probe)) File.Delete(probe); }
            }
            journal.Changes = changes; journal.Phase = "Applying"; UpdatePaths.Save(journalPath, journal);
            foreach (var change in changes)
            {
                var target = UpdatePaths.ProgramFile(journal.Root, change.Path, journal.ProtectedDirectories);
                var backup = UpdatePaths.ProgramFile(Path.Combine(job, "backup"), change.Path);
                change.HadOriginal = File.Exists(target);
                if (change.HadOriginal) { Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(target, backup, false); }
                // 先保存待修改状态，中断后可根据已有备份恢复。
                change.Touched = true; UpdatePaths.Save(journalPath, journal);
                if (change.RemoveOnly) File.Delete(target);
                else ReplaceFrom(UpdatePaths.ProgramFile(Path.Combine(job, "new"), change.Path), target);
            }
            journal.Phase = "AwaitingStartup"; UpdatePaths.Save(journalPath, journal);
            child = StartLauncher(journal, "--update-session");
            var healthy = Path.Combine(job, "healthy");
            var deadline = DateTime.UtcNow.AddMinutes(1);
            bool started = false;
            while (DateTime.UtcNow < deadline && !child.HasExited)
            {
                if (File.Exists(healthy) && (await File.ReadAllTextAsync(healthy)).Trim() == child.Id.ToString()) { started = true; break; }
                await Task.Delay(200);
            }
            if (!started)
            {
                if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); }
                throw new IOException("新版启动器未能正常打开，准备恢复旧版本。");
            }
            var cache = UpdatePaths.Cache(journal.Root);
            var index = Path.Combine(cache, "installed-release.json");
            journal.HadInstalledIndex = File.Exists(index);
            if (journal.HadInstalledIndex) File.Copy(index, Path.Combine(job, "previous-installed.json"), false);
            journal.InstalledIndexTouched = true; UpdatePaths.Save(journalPath, journal);
            UpdatePaths.AtomicBytes(index, JsonSerializer.SerializeToUtf8Bytes(new InstalledRelease(
                Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Combine(job, "manifest.json"))),
                Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Combine(job, "manifest.sig")))), UpdateProduct.Json));
            journal.Phase = "Completed"; UpdatePaths.Save(journalPath, journal); return 0;
        }
        catch (Exception error)
        {
            if (journal != null)
            {
                journal.Error = error.Message;
                try
                {
                    File.WriteAllText(Path.Combine(UpdatePaths.Job(journal.Root, journal.Id), "error.log"), error.ToString());
                    if (parentExited)
                    {
                        if (child is { HasExited: false }) { child.Kill(true); await child.WaitForExitAsync(); }
                        await RollbackAsync(journalPath, journal);
                        StartLauncher(journal, "--update-rollback").Dispose();
                    }
                    else { journal.Phase = args[0] == "--recover" ? "RollbackFailed" : "Aborted"; UpdatePaths.Save(journalPath, journal); }
                }
                catch (Exception rollbackError)
                {
                    journal.Phase = "RollbackFailed"; journal.Error += "\n恢复失败：" + rollbackError.Message;
                    try { UpdatePaths.Save(journalPath, journal); } catch (IOException) { }
                    MessageBox(IntPtr.Zero, "启动器更新或恢复失败。请保留 FMLauncher/Updates 中的记录，并从发布页面重新下载完整程序。账号和游戏目录不属于更新范围。", "FMLauncher 更新未完成", 0x10);
                }
            }
            return 1;
        }
        finally { child?.Dispose(); updateLock?.Dispose(); }
    }
    private static UpdateManifest? ReadPrevious(string root, string key)
    {
        var path = Path.Combine(UpdatePaths.Cache(root), "installed-release.json");
        if (!File.Exists(path)) return null;
        var index = JsonSerializer.Deserialize<InstalledRelease>(File.ReadAllBytes(path), UpdateProduct.Json) ?? throw new InvalidDataException("已安装程序清单无效。");
        return UpdateManifest.Verify(Convert.FromBase64String(index.Manifest), Convert.FromBase64String(index.Signature), key);
    }
    private static async Task WaitRecoveryParentAsync(UpdateJournal journal)
    {
        if (journal.RecoveryPid <= 0) return;
        Process parent;
        try { parent = Process.GetProcessById(journal.RecoveryPid); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            if (parent.HasExited) return;
            if (parent.StartTime.ToUniversalTime().Ticks != journal.RecoveryStartedUtcTicks
                || !string.Equals(Path.GetDirectoryName(parent.MainModule?.FileName), Path.GetFullPath(journal.Root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("恢复更新的程序身份不匹配。");
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        }
    }
    private static void ReplaceFrom(string source, string target)
    {
        var temporary = target + ".fml-update-" + Guid.NewGuid().ToString("N");
        try { File.Copy(source, temporary, false); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    /// <summary>只恢复本会话备份的程序文件，账号和游戏数据不参与回滚。</summary>
    private static Task RollbackAsync(string path, UpdateJournal journal)
    {
        journal.Phase = "RollingBack"; UpdatePaths.Save(path, journal);
        foreach (var change in journal.Changes.AsEnumerable().Reverse().Where(c => c.Touched))
        {
            var target = UpdatePaths.ProgramFile(journal.Root, change.Path, journal.ProtectedDirectories);
            if (change.HadOriginal)
            {
                var backup = UpdatePaths.ProgramFile(Path.Combine(UpdatePaths.Job(journal.Root, journal.Id), "backup"), change.Path);
                if (!File.Exists(backup)) throw new IOException("缺少恢复文件：" + change.Path);
                ReplaceFrom(backup, target);
            }
            else if (File.Exists(target)) File.Delete(target);
        }
        if (journal.InstalledIndexTouched)
        {
            var index = Path.Combine(UpdatePaths.Cache(journal.Root), "installed-release.json");
            if (journal.HadInstalledIndex) UpdatePaths.AtomicBytes(index, File.ReadAllBytes(Path.Combine(UpdatePaths.Job(journal.Root, journal.Id), "previous-installed.json")));
            else if (File.Exists(index)) File.Delete(index);
        }
        journal.Phase = "RolledBack"; UpdatePaths.Save(path, journal); return Task.CompletedTask;
    }
    private static Process StartLauncher(UpdateJournal journal, string argument)
    {
        var start = new ProcessStartInfo(Path.Combine(journal.Root, UpdateProduct.Executable)) { UseShellExecute = false, WorkingDirectory = journal.Root };
        start.ArgumentList.Add(argument); start.ArgumentList.Add(journal.Id);
        return Process.Start(start) ?? throw new IOException("无法重新打开启动器。");
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
