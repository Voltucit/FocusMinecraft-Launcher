using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using FMCore.Downloads;
using FMCore.Installer;
using FMCore.Launch;
using FMCore.Logger;
using FMCore.Modsloader;
using FocusMinecraftLauncher.Infrastructure.Configuration;
using FocusMinecraftLauncher.Infrastructure.Runtime;

namespace FocusMinecraftLauncher.Preview;

public sealed partial class PreviewSession
{
    private readonly Dictionary<PreviewTask, CancellationTokenSource> _operations = [];
    private readonly Dictionary<PreviewTask,Task> _executions = [];
    private readonly Dictionary<PreviewTask, Func<PreviewTask, CancellationToken, Task>> _retryOperations = [];
    private readonly Dictionary<string, SemaphoreSlim> _rootWriters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _incomplete = new(StringComparer.OrdinalIgnoreCase);
    private int _refreshRevision;
    private string _selectedRoot = "", _restoreId = "";
    public string SelectedRoot => _selectedRoot;
    private bool _applyingIsolation;
    public bool IsApplyingIsolation => _applyingIsolation;
    public event Action<string, bool>? TaskNotification;
    /// <summary>实例文件发生变化后通知已打开的管理页；目录和实例标识共同确定通知范围。</summary>
    public event Action<string, string>? InstanceContentChanged;
    public void NotifyInstanceContentChanged(string root, string id)
    {
        InstanceContentChanged?.Invoke(root, id);
        Signal();
    }
    private Features.Downloads.Models.DownloadNotification? _globalNotification;
    public Features.Downloads.Models.DownloadNotification? GlobalNotification { get=>_globalNotification;private set=>Set(ref _globalNotification,value); }
    public void SetGlobalNotification(string message,bool error)=>GlobalNotification=new(message,error);
    public void NotifyTask(string message,bool error){SetGlobalNotification(message,error);TaskNotification?.Invoke(message,error);}
    public IReadOnlyList<string> GameFolders => (_configuration.ReadLauncher()["gameFolders"] as JsonArray)?.Select(n => LauncherConfiguration.Text(n)).Where(s => s.Length > 0).ToArray() ?? [];
    public DownloadSource FileSource => IsMirrorSource(LauncherConfiguration.Text(_configuration.ReadLauncher()["downloads"]?["fileDownloadSource"])) ? DownloadSource.BmclApi : DownloadSource.Official;
    /// <summary>仅用于支持选源的加载器列表查询；原版列表暂沿用核心的官方源入口。</summary>
    public DownloadSource MetadataSource => IsMirrorSource(LauncherConfiguration.Text(_configuration.ReadLauncher()["downloads"]?["versionListSource"])) ? DownloadSource.BmclApi : DownloadSource.Official;
    private static bool IsMirrorSource(string value)=>value.Contains("BMCLAPI")||value.StartsWith("优先使用镜像");
    public int DownloadConcurrency => Math.Clamp((int)LauncherConfiguration.Number(_configuration.ReadLauncher()["downloads"]?["maximumThreads"], 16), 1, 64);

    private void RestoreRuntimeConfiguration(JsonObject root)
    {
        _selectedRoot = LauncherConfiguration.Text(root["selection"]?["root"], SavedSettings.GameDirectory);
        _restoreId = LauncherConfiguration.Text(root["selection"]?["instanceId"]);
        foreach (var item in root["incompleteInstallations"] as JsonArray ?? []) _incomplete.Add(LauncherConfiguration.Text(item));
    }
    private void SaveInstanceSelection(JsonObject root)
    {
        var selection = LauncherConfiguration.Object(root, "selection");
        selection["instanceId"] = CurrentInstance?.Id;
        selection["root"] = SelectedRoot;
    }
    public async Task SelectRootAsync(string root)
    {
        root = Path.GetFullPath(root);
        // 不在这里清空实例列表：先保留旧条目，等新目录扫描完成后由刷新逻辑逐条替换，
        // 避免切换文件夹时列表瞬间清空、页面出现空白帧。
        if (!string.Equals(root, SelectedRoot, StringComparison.OrdinalIgnoreCase))
        { _selectedRoot = root; _restoreId = ""; CurrentInstance = null; Changed(nameof(SelectedRoot)); SaveSelectedInstance(); }
        await RefreshInstancesAsync();
    }
    public void RememberFolder(string path)
    {
        path = Path.GetFullPath(path);
        _configuration.ChangeLauncher(root =>
        {
            var list = root["gameFolders"] as JsonArray ?? new JsonArray();
            if (!list.Any(item => string.Equals(LauncherConfiguration.Text(item), path, StringComparison.OrdinalIgnoreCase))) list.Add(path);
            if (list.Parent == null) root["gameFolders"] = list;
        });
    }
    /// <summary>忘记用户添加过的父目录；只移除记录，不触碰磁盘上的文件。</summary>
    public void ForgetFolder(string path)
    {
        var full = Path.GetFullPath(path);
        _configuration.ChangeLauncher(root =>
        {
            if (root["gameFolders"] is not JsonArray list) return;
            foreach (var item in list.Where(item => string.Equals(LauncherConfiguration.Text(item), full, StringComparison.OrdinalIgnoreCase)).ToArray()) list.Remove(item);
        });
    }
    /// <summary>重新查询同一份实例目录；修订号阻止较早查询覆盖用户刚切换的目录，安装中的实例不提前登记。</summary>
    public async Task RefreshInstancesAsync()
    {
        var revision = ++_refreshRevision;
        var root = SelectedRoot;
        IsLoading = true; InstanceError = ""; Signal();
        try
        {
            var found = await Task.Run(() => InstanceStore.ScanAsync(root, _lifetime.Token), _lifetime.Token);
            if (revision != _refreshRevision || _lifetime.IsCancellationRequested) return;
            var wanted = CurrentInstance?.Id ?? _restoreId;
            var items = found.Where(i => !_incomplete.Contains(InstanceStore.Key(root, i.Id))).ToArray();
            foreach (var item in items) item.IsolationMode = SavedSettings.VersionIsolation;
            // 尽量保留原对象，避免安装刷新打断当前编辑页及选择。
            bool SameInstance(PreviewInstance left, PreviewInstance right) =>
                string.Equals(InstanceStore.Key(left.Root, left.Id), InstanceStore.Key(right.Root, right.Id), StringComparison.OrdinalIgnoreCase)
                && left.Version == right.Version && left.Loader == right.Loader && left.LoaderVersion == right.LoaderVersion;
            foreach (var old in Instances.ToArray()) if (!items.Any(i => SameInstance(i, old))) Instances.Remove(old);
            foreach (var item in items)
            {
                var existing = Instances.FirstOrDefault(i => SameInstance(i, item));
                if (existing == null) Instances.Add(item);
                else
                {
                    existing.Name = item.Name; existing.Description = item.Description; existing.Category = item.Category;
                    existing.RequiredJava = item.RequiredJava; existing.MetadataIssue = item.MetadataIssue;
                    existing.IsolationMode = SavedSettings.VersionIsolation;
                    existing.IsFavorite = item.IsFavorite; existing.Options = item.Options; existing.IconImage = item.IconImage;existing.FallbackIconImage=item.FallbackIconImage;
                    existing.RefreshOptiFineState();
                }
            }
            CurrentInstance = Instances.FirstOrDefault(i => i.Id == wanted) ?? Instances.FirstOrDefault();
            _restoreId = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (revision == _refreshRevision) { InstanceError = RuntimeSupport.Error(error); Log("读取游戏：" + InstanceError); } }
        finally { if (revision == _refreshRevision) { IsLoading = false; Signal(); } }
    }
    public async Task InitializeRuntimeAsync() => await RefreshInstancesAsync();

    /// <summary>把 SDK 的实际下载数据调度至 UI；进度百分比不改变任务终态。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<PreviewTask,long> _progressTimes = new();
    public void Report(PreviewTask task, string stage, DownloadBatchProgress? batch = null, DownloadProgress? file = null)
    {
        var now=Environment.TickCount64;
        if(_progressTimes.TryGetValue(task,out var last)&&now-last<100)return;
        _progressTimes[task]=now;
        PostUi(() =>
        {
            if (!task.IsActive) return;
            task.Stage = stage;
            task.Bytes = batch?.ProcessedBytes ?? file?.DownloadedBytes ?? 0;
            task.Total = batch?.TotalBytes ?? file?.TotalBytes ?? 0;
            task.BytesPerSecond = batch?.BytesPerSecond ?? file?.BytesPerSecond ?? 0;
            task.Refresh(); Signal();
        });
    }
    public PreviewTask Enqueue(PreviewTaskKind kind, string name, string version, string root, string instanceId, Func<PreviewTask, CancellationToken, Task> run)
    {
        if (IsInstallingLauncherUpdate) throw new InvalidOperationException("正在安装启动器更新，请稍后重试。");
        if (IsRemovingInstance) throw new InvalidOperationException("正在删除游戏，请稍后重试。");
        if (IsApplyingIsolation) throw new InvalidOperationException("正在应用版本隔离设置，请稍后重试。");
        var item = new PreviewTask(kind, name, version) { Root = root, InstanceId = instanceId };
        _retryOperations[item] = run; Tasks.Insert(0, item); _active = item;
        _executions[item] = ExecuteAsync(item, run); Signal(); return item;
    }
    /// <summary>等待共享任务的执行和清理结束；调用方读取任务终态，不把进度事件当作完成。</summary>
    public Task WaitForTaskAsync(PreviewTask task) => _executions.TryGetValue(task,out var execution) ? execution : Task.CompletedTask;
    private async Task ExecuteAsync(PreviewTask task, Func<PreviewTask, CancellationToken, Task> run)
    {
        if (_operations.ContainsKey(task)) return;
        using var cancellation = task.Kind == PreviewTaskKind.Launch ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operations[task] = cancellation;
        task.State = "进行中"; task.Error = ""; task.Stage = "正在准备"; task.Bytes = task.Total = 0;task.CompletedFiles=task.TotalFiles=0; task.Refresh(); Signal();
        try
        {
            await run(task, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            task.State = "已完成";
            if (!task.IsRunningGame) task.Stage = "已完成";
        }
        catch (OperationCanceledException) when(cancellation.IsCancellationRequested) { task.State = "已取消"; task.Stage = "已取消"; }
        catch (Exception error)
        {
            task.State = "失败"; task.Error = error is OperationCanceledException ? "服务响应超时，请检查网络后重试。" : RuntimeSupport.Error(error); task.Stage = task.Kind switch { PreviewTaskKind.Launch=>"启动失败", PreviewTaskKind.Repair=>"检查或补全失败", PreviewTaskKind.Import=>"导入失败", PreviewTaskKind.Export=>"导出失败", _=>"下载失败" };
            Log(task.Name + "：" + error); NotifyTask(task.Stage, true);
        }
        finally
        {
            _operations.Remove(task); _progressTimes.TryRemove(task,out _); task.IsRunningGame = false; task.BytesPerSecond = 0;
            task.Refresh(); Log(task.Name + "：" + task.State); Signal();
            if (task.Kind is PreviewTaskKind.Resource or PreviewTaskKind.Repair or PreviewTaskKind.Import or PreviewTaskKind.Install)
                NotifyInstanceContentChanged(task.Root, task.InstanceId);
        }
    }
    public bool HasPendingWrites=>IsRemovingInstance || _operations.Any(pair=>!pair.Key.IsRunningGame);
    /// <summary>退出时等待下载和安装取消清理、回收站操作完成；已运行的 Java 进程继续运行。</summary>
    public async Task StopPendingWritesAsync()
    {
        var removal = _instanceRemoval;
        var pending=_operations.Where(pair=>!pair.Key.IsRunningGame).ToArray();
        foreach(var pair in pending){pair.Value.Cancel();pair.Key.Stage="正在取消";pair.Key.Refresh();}
        await Task.WhenAll(pending.Where(pair=>_executions.ContainsKey(pair.Key)).Select(pair=>_executions[pair.Key]));
        // 回收站操作开始后不强制中断；退出前等待它完成，失败由删除入口反馈。
        if (removal != null)
            try { await removal; }
            catch (Exception error) { Log("等待删除游戏结束：" + RuntimeSupport.Error(error)); }
    }
    private void Cancel(PreviewTask? task)
    {
        if (task == null || !_operations.TryGetValue(task, out var cancellation)) return;
        if (task.IsRunningGame && !Presentation.Dialogs.MessageDialog.Confirm("关闭游戏",
            $"将强制关闭“{task.Name}”，未保存的进度可能丢失。", "关闭游戏", "继续游戏")) return;
        // 模态确认期间游戏可能自行退出；重新取得仍存活的操作，避免取消已释放的会话。
        if (!task.IsActive || !_operations.TryGetValue(task, out cancellation) || cancellation.IsCancellationRequested) return;
        cancellation.Cancel(); task.Stage = task.IsRunningGame ? "正在关闭游戏" : "正在取消"; task.Refresh();
    }
    private void Retry(PreviewTask? task)
    {
        if (task is not { CanRetry: true } || !_retryOperations.TryGetValue(task, out var run)) return;
        if (Tasks.Any(other => other != task && other.IsActive && string.Equals(InstanceStore.Key(other.Root, other.InstanceId), InstanceStore.Key(task.Root, task.InstanceId), StringComparison.OrdinalIgnoreCase))) return;
        if(task.Kind==PreviewTaskKind.Launch)
        {
            if(!CanLaunch||CurrentInstance==null||CurrentAccount==null)
            {NotifyTask("请先选择可用账号与游戏版本后重试。",true);return;}
            if(!string.Equals(InstanceStore.Key(task.Root,task.InstanceId),InstanceStore.Key(CurrentInstance.Root,CurrentInstance.Id),StringComparison.OrdinalIgnoreCase))
            {NotifyTask("请先选择这个任务对应的游戏版本后重试。",true);return;}
            var instance=CurrentInstance;var account=CurrentAccount;
            run=(item,token)=>RunLaunchAsync(instance,account,item,token);
            _retryOperations[task]=run;_launch=task;
        }
        task.State = "排队中"; _executions[task] = ExecuteAsync(task, run);
    }
    private async Task SetIncompleteAsync(string root, string id, bool incomplete)
    {
        var key = InstanceStore.Key(root, id);
        if (incomplete) _incomplete.Add(key); else _incomplete.Remove(key);
        _configuration.ChangeLauncher(document => document["incompleteInstallations"] = new JsonArray(_incomplete.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()));
        if(!await _configuration.FlushAsync())throw new IOException("无法保存安装状态，请检查启动器配置目录的写入权限。");
    }
    /// <summary>同根目录安装串行，其他目录可以同时安装；只在核心安装成功并保存来源后刷新对应目录。</summary>
    public PreviewTask InstallGame(string name, string game, string loader, string loaderVersion, string optifine)
    {
        var root = SelectedRoot;
        if (Tasks.Any(t => t.IsActive && string.Equals(InstanceStore.Key(t.Root, t.InstanceId), InstanceStore.Key(root, name), StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("这个版本正在安装。");
        var target = Path.Combine(root, "versions", name);
        // 隐藏的原版父版本可以原地安装成正式的纯原版实例，不必要求用户换名或手动删目录。
        var reuseBase = (loader is "" or "原版") && string.Equals(name, game, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(target) && !File.Exists(Path.Combine(target, InstanceStore.FileName));
        if (Directory.Exists(target) && !_incomplete.Contains(InstanceStore.Key(root, name)) && !reuseBase) throw new IOException("这个安装名称已存在，请使用其他名称。");
        return Enqueue(PreviewTaskKind.Install, name, game, root, name, async (task, token) =>
        {
            if (!_rootWriters.TryGetValue(root, out var writer)) _rootWriters[root] = writer = new(1, 1);
            await writer.WaitAsync(token);
            var baseExisted = File.Exists(Path.Combine(root, "versions", game, game + ".json"));
            try
            {
                await SetIncompleteAsync(root, name, true);
                await InstallComponentsAsync(root, name, game, loader, loaderVersion, optifine, task, token);
                await RemoveAutoInstalledBaseAsync(root, game, name, baseExisted);
                if (!baseExisted && !string.Equals(name, game, StringComparison.OrdinalIgnoreCase)) MarkDependencyVersion(root, game);
                token.ThrowIfCancellationRequested();
                var instance = new PreviewInstance(name, name, game, loader) { Root = root, IsolationMode = SavedSettings.VersionIsolation };
                var options = InstanceStore.Read(instance); options.Name = name; options.Source = "Minecraft";
                await InstanceStore.SaveAsync(instance, options);
                await SetIncompleteAsync(root, name, false);
                if (string.Equals(root, SelectedRoot, StringComparison.OrdinalIgnoreCase)) await RefreshInstancesAsync();
                NotifyTask(name + "安装完成", false);
            }
            finally
            {
                if (!baseExisted && !string.Equals(name, game, StringComparison.OrdinalIgnoreCase)) MarkDependencyVersion(root, game);
                writer.Release();
            }
        });
    }
    /// <summary>加载器安装器自动补装的原版父版本只作为依赖保留，不在游戏列表中显示。</summary>
    private static void MarkDependencyVersion(string root, string id)
    {
        try
        {
            var directory = Path.Combine(root, "versions", id);
            if (!File.Exists(Path.Combine(directory, id + ".json"))) return;
            Directory.CreateDirectory(directory);
            var marker = Path.Combine(directory, InstanceStore.DependencyMarker);
            if (!File.Exists(marker)) File.WriteAllText(marker, "");
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning("标记原版依赖失败：{0}", error.GetType().Name);
        }
    }
    /// <summary>确认没有其他版本继承后，删除本次自动补装的原版父版本；失败时保留并标记为依赖。</summary>
    private static async Task RemoveAutoInstalledBaseAsync(string root, string game, string name, bool baseExisted)
    {
        if (string.Equals(name, game, StringComparison.OrdinalIgnoreCase)) return;
        var versionsRoot = Path.GetFullPath(Path.Combine(root, "versions"));
        var directory = Path.GetFullPath(Path.Combine(versionsRoot, game));
        if (!Directory.Exists(directory)) return;
        if (!string.Equals(Path.GetDirectoryName(directory), versionsRoot, StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(Path.Combine(directory, InstanceStore.FileName))) return;
        // 旧版本留下的标记也可以清理；没有标记且非本次安装创建的原版目录不碰。
        if (baseExisted && !File.Exists(Path.Combine(directory, InstanceStore.DependencyMarker))) return;
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
            // 版本目录里出现用户数据时不删除，退化为隐藏，避免把玩过的原版目录清掉。
            foreach (var folder in new[] { "mods", "saves", "config", "resourcepacks", "shaderpacks", "logs", "crash-reports" })
                if (Directory.Exists(Path.Combine(directory, folder))) return;
            if (File.Exists(Path.Combine(directory, "options.txt"))) return;
            var reader = new FMCore.Versions.MinecraftVersionReader();
            var versions = await reader.GetVersionsAsync(root, CancellationToken.None);
            foreach (var version in versions)
            {
                if (string.Equals(version.Id, game, StringComparison.Ordinal)) continue;
                var chain = reader.GetInheritanceChain(version, versions);
                if (chain.Any(item => string.Equals(item.Id, game, StringComparison.Ordinal))) return;
            }
            Directory.Delete(directory, true);
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning("清理自动补装的原版失败：{0}", error.GetType().Name);
            MarkDependencyVersion(root, game);
        }
    }
    private async Task InstallComponentsAsync(string root, string name, string game, string loader, string loaderVersion, string optifine, PreviewTask task, CancellationToken token)
    {
                var source = FileSource; var threads = DownloadConcurrency;
                OptiFineInstallVersion? optiFineMod=null;
                if(loader=="Forge"&&optifine.Length>0)
                {
                    var versions=await OptiFineOnlineInstaller.GetVersionsAsync(game,token);
                    optiFineMod=versions.SingleOrDefault(v=>v.Edition==optifine)??throw new InvalidOperationException("未找到所选 OptiFine 版本。");
                    await OptiFineForgeIntegration.CheckCombinationAsync(optiFineMod,loaderVersion,token);
                }
                string? java = null;
                if (loader is "Forge" or "NeoForge" || optifine.Length > 0)
                {
                    if(string.IsNullOrWhiteSpace(SavedSettings.JavaPath))
                    {
                        await new MinecraftInstaller(game,root,threads){Source=source}.InstallAsync(p=>Report(task,p.Message,p.Download),token);
                        var baseInstance=new PreviewInstance(game,game,game,"原版"){Root=root,IsolationMode=SavedSettings.VersionIsolation};
                        var selected=await RuntimeSupport.SelectCheckedLauncherAsync(baseInstance,FMCore.Accounts.OfflineAccount.Create("FMLauncher"),new InstanceOptions(),SavedSettings,token);
                        java=selected.JavaPath;
                    }
                    else java=await RuntimeSupport.SelectJavaAsync(SavedSettings.JavaPath,null,token);
                }
                if (loader == "Fabric")
                {
                    await new FabricInstaller(game, loaderVersion, root, name, threads) { Source = source }.InstallAsync(p => Report(task, p.Message, p.Download), token);
                    try { await InstallFabricApiAsync(root, name, game, task, token); }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        Log("Fabric API 自动安装失败：" + RuntimeSupport.Error(error));
                        Report(task, "Fabric API 自动安装失败，可稍后从下载页安装");
                    }
                }
                else if (loader == "Forge")
                {
                    await new ForgeInstaller(game,loaderVersion,root,java!,name,threads){Source=source}.InstallAsync(p=>Report(task,p.Message,p.Download,p.InstallerDownload),token);
                    if(optiFineMod!=null)
                    {
                        token.ThrowIfCancellationRequested();
                        var instance=new PreviewInstance(name,name,game,"Forge"){Root=root,IsolationMode=SavedSettings.VersionIsolation};
                        ModpackArchive.EnsureInstanceWritePath(instance);
                        var component=await OptiFineForgeIntegration.InstallAsync(optiFineMod,loaderVersion,instance.GameDirectory,p=>Report(task,"下载 OptiFine（BMCLAPI）",file:p),token);
                        var options=InstanceStore.Read(instance);options.OptiFineMod=component;
                        await InstanceStore.SaveAsync(instance,options);
                        Log("Forge 与 OptiFine 接入完成："+game+" / "+loaderVersion+" / "+component.Edition);
                    }
                }
                else if (loader == "NeoForge") await new NeoForgeInstaller(game, loaderVersion, root, java!, name, threads) { Source = source }.InstallAsync(p => Report(task, p.Message, p.Download, p.InstallerDownload), token);
                else if (optifine.Length > 0) await new OptiFineOnlineInstaller(game, optifine, root, java!, name, threads).InstallAsync(p => Report(task, p.Message, p.Download, p.InstallerDownload), token);
                else
                {
                    await new MinecraftInstaller(game, root, threads) { Source = source }.InstallAsync(p => Report(task, p.Message, p.Download), token);
                    if (name != game)
                    {
                        var directory = Path.Combine(root, "versions", name); Directory.CreateDirectory(directory);
                        // 创建安装声明后交给核心展开为独立版本，不在启动器内合并版本配置。
                        var version=await new FMCore.Versions.MinecraftVersionReader().GetVersionAsync(game,root,token);
                        var temporary=Path.Combine(directory,".fmlauncher-"+Guid.NewGuid().ToString("N")+".tmp");
                        try
                        {
                            await File.WriteAllTextAsync(temporary,new JsonObject{["id"]=name,["inheritsFrom"]=game,["type"]=version.VersionType??"release"}.ToJsonString(),token);
                            token.ThrowIfCancellationRequested();
                            var profile=Path.Combine(directory,name+".json");
                            if(File.Exists(profile))
                            {
                                var existing=System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(profile,token));
                                var recorded=InstanceStore.Read(new PreviewInstance(name,name,game,"原版"){Root=root});
                                if(existing?["id"]?.GetValue<string>()!=name ||
                                    (existing?["inheritsFrom"]?.GetValue<string>()!=game &&
                                     !(existing?["inheritsFrom"]==null && recorded.GameVersion==game)))
                                    throw new IOException("已有版本声明与本次安装不同，未覆盖。");
                            }
                            else File.Move(temporary,profile,false);
                        }
                        finally{if(File.Exists(temporary))File.Delete(temporary);}
                    }
                }
                var installed=new PreviewInstance(name,name,game,loader){Root=root,IsolationMode=SavedSettings.VersionIsolation};
                await InstanceStore.UpdateAsync(installed,options=>options.GameVersion=game);
                if(loader is "" or "原版" && optifine.Length==0 && name!=game)
                {
                    Report(task,"生成独立游戏版本");
                    await new FMCore.Versions.MinecraftVersionConverter(name,root).MakeIndependentAsync(token);
                }
                // 核心安装器默认使用独立工作目录；共享模式还需将旧版资源映射到实际启动目录。
                if(!installed.UsesGameIsolation)await CheckAndCompleteFilesAsync(installed,task,token);
    }
    /// <summary>Fabric 安装完成后自动装 Fabric API；只取与当前游戏和 fabric 加载器匹配的最新版本。</summary>
    private async Task InstallFabricApiAsync(string root, string name, string game, PreviewTask task, CancellationToken token)
    {
        var instance = new PreviewInstance(name, name, game, "Fabric") { Root = root, IsolationMode = SavedSettings.VersionIsolation };
        ModpackArchive.EnsureInstanceWritePath(instance);
        var client = CommunitySource.NewClient();
        Report(task, "查询 Fabric API");
        var versions = await client.GetVersionsAsync("fabric-api", game, "fabric", token);
        var version = versions.FirstOrDefault(item => item.Files.Count > 0);
        var file = version?.PrimaryFile ?? version?.Files.FirstOrDefault();
        if (version is null || file is null)
        {
            Report(task, "当前游戏版本没有兼容的 Fabric API");
            return;
        }
        Report(task, "安装 Fabric API " + version.VersionNumber);
        var plan = await ResourceFileInstaller.PlanAsync(instance,
            [new ResourceInstallFile("mods/" + file.Filename, "P7dR8mSH", version.Id, "Fabric API", file)], token);
        await ResourceFileInstaller.ApplyAsync(instance, plan, client, _configuration.ConfigDirectory,
            (stage, progress) => Report(task, stage, file: progress), token);
    }
    private Task BeginLaunchAsync()
    {
        if (!CanLaunch || CurrentInstance is not { } instance || CurrentAccount is not { } account) return Task.CompletedTask;
        _launch = Enqueue(PreviewTaskKind.Launch, instance.Name, instance.Version, instance.Root, instance.Id,
            (task,token)=>RunLaunchAsync(instance,account,task,token));
        Signal();return Task.CompletedTask;
    }
    /// <summary>开始及重试都明确传入当前确认的账户与实例，不复用失败任务捕获的旧账户。</summary>
    private async Task RunLaunchAsync(PreviewInstance instance,PreviewAccount account,PreviewTask task,CancellationToken token)
    {
            if (account.IsMicrosoft) { await AuthenticateMicrosoftAsync(account, false); if (!account.IsValid) throw new InvalidOperationException("登录已过期，请重新登录。"); }
            if (account.RuntimeAccount == null) throw new InvalidOperationException("账号不可用，请重新登录。");
            var settings = InstanceStore.Read(instance);
            var global = SavedSettings.Copy();
            Report(task, "检测 Java");
            var requiredJava = instance.RequiredJava;
            var javaCandidates = await RuntimeSupport.ReadLaunchJavaCandidatesAsync(instance, settings, global, token);
            await EnsureFilesAsync(instance, task, token);
            // 恢复缺少的父版本后才能得到完整 Java 声明，此时重新按核心元数据选择。
            if (requiredJava != instance.RequiredJava)
                javaCandidates = await RuntimeSupport.ReadLaunchJavaCandidatesAsync(instance, settings, global, token);
            // 缺少所需版本时先问用户：下载官方运行时，还是改用已安装的更高版本。
            javaCandidates = await ResolveJavaAsync(instance, settings, global, javaCandidates, task, token);
            // 缺少匹配版本时使用更高版本的 Java 兜底；这类兜底启动失败要额外弹框说明原因。
            var fallbackJava = instance.RequiredJava is { } required && javaCandidates.Count > 0 && javaCandidates[0].MajorVersion > required;
            if (javaCandidates.Count == 0)
                throw new InvalidOperationException(instance.RequiredJava is { } missing ? $"未找到 Java {missing} 或更高版本，请安装并选择可用的 Java。" : "未找到可用的 Java，请先安装并选择 Java。");
            // 启动前自动备份：失败只写日志，不阻断启动。
            if (GameBackupService.ShouldBackup(instance))
            {
                Report(task, "自动备份");
                try
                {
                    await GameBackupService.CreateAsync(instance, "启动前自动备份", true, new Progress<string>(text => Report(task, text)), token);
                    GameBackupService.Prune(instance, settings.BackupRetention);
                }
                catch (Exception error) when (error is not OperationCanceledException) { Log("自动备份失败：" + RuntimeSupport.Error(error)); }
            }
            Report(task, "检查启动配置");
            try
            {
            // 自动内存按“可选用的内存”计算，先释放启动器占用再读取，读数才反映真实可用量。
            if (settings.GlobalMemory ? global.OptimizeMemory : settings.OptimizeMemory) RuntimeSupport.ReleaseLauncherMemory();
            var selectedLauncher = await RuntimeSupport.SelectCheckedLauncherAsync(instance,account.RuntimeAccount,settings,global,token,javaCandidates);
            var launcher = selectedLauncher.Launcher;
            token.ThrowIfCancellationRequested();
            var code = await launcher.LaunchAsync(p => PostUi(() =>
            {
                if (!task.IsActive) return;
                task.Stage = p.Stage == LaunchStage.JavaStarted ? "已启动" : p.Stage == LaunchStage.GameExited ? "游戏已退出" : "启动中";
                task.IsRunningGame = p.Stage == LaunchStage.JavaStarted || task.IsRunningGame;
                task.Total = task.Bytes = 0; task.BytesPerSecond = 0; task.CompletedFiles = task.TotalFiles = 0;
                task.Refresh(); Signal();
            }), line => Log(line), line => Log(line), token);
            if (code != 0) throw new InvalidOperationException("游戏异常退出，退出码：" + code + "。请在任务记录中查看日志。");
            task.Stage = "游戏已退出";
            }
            catch (Exception error) when (fallbackJava && error is not OperationCanceledException)
            {
                var used = javaCandidates[0].MajorVersion;
                Presentation.Dialogs.MessageDialog.Show("游戏启动失败",
                    $"没有找到与游戏匹配的 Java {instance.RequiredJava}，已改用 Java {used} 启动，但这次启动没有成功。\n\n{RuntimeSupport.Error(error)}");
                throw;
            }
    }
    /// <summary>缺少实例所需的 Java 时让用户选择：下载官方运行时、改用已安装的更高版本，或取消启动。</summary>
    private async Task<IReadOnlyList<FMCore.Java.JavaInfo>> ResolveJavaAsync(PreviewInstance instance, InstanceOptions settings, PreviewSettings global, IReadOnlyList<FMCore.Java.JavaInfo> candidates, PreviewTask task, CancellationToken token)
    {
        // 手动指定过 Java 就尊重用户选择；不会因为版本不匹配就擅自下载。
        var manual = settings.GlobalJava ? global.JavaPath : settings.JavaPath;
        if (!string.IsNullOrWhiteSpace(manual) || instance.RequiredJava is not { } required) return candidates;
        if (candidates.Any(java => java.MajorVersion == required)) return candidates;

        var higher = candidates.Where(java => java.MajorVersion > required).OrderBy(java => java.MajorVersion).FirstOrDefault();
        var canDownload = JavaBootstrap.CanInstall(required);
        if (higher is null && !canDownload)
            throw new InvalidOperationException($"未找到 Java {required} 或更高版本，请安装并选择可用的 Java。");

        var choice = Presentation.Dialogs.JavaRuntimeDialog.Ask(required, higher?.MajorVersion, canDownload);
        if (choice == Presentation.Dialogs.JavaRuntimeChoice.Cancel) throw new OperationCanceledException("已取消启动。");
        if (choice == Presentation.Dialogs.JavaRuntimeChoice.Download)
        {
            Report(task, $"下载 Java {required}");
            var installed = await JavaBootstrap.InstallAsync(required, FileSource == DownloadSource.BmclApi, progress => ReportJavaInstall(task, progress), token);
            return [installed, .. candidates];
        }
        return [higher!, .. candidates.Where(java => !ReferenceEquals(java, higher))];
    }
    /// <summary>把核心的 Java 下载进度映射到启动任务上，让任务记录显示百分比与速度。</summary>
    private void ReportJavaInstall(PreviewTask task, FMCore.Java.JavaInstallProgress progress)
    {
        PostUi(() =>
        {
            if (!task.IsActive) return;
            task.Stage = progress.Message;
            if (progress.Download is { } download)
            {
                task.Total = download.TotalBytes;
                task.Bytes = download.ProcessedBytes;
                task.BytesPerSecond = download.BytesPerSecond;
                task.CompletedFiles = download.CompletedFiles;
                task.TotalFiles = download.TotalFiles;
            }
            task.Refresh(); Signal();
        });
    }
}
