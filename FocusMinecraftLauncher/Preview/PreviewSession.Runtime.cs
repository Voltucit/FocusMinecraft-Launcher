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
        if (!string.Equals(root, SelectedRoot, StringComparison.OrdinalIgnoreCase))
        { _selectedRoot = root; _restoreId = ""; CurrentInstance = null; Instances.Clear(); Changed(nameof(SelectedRoot)); SaveSelectedInstance(); }
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
                    existing.IsFavorite = item.IsFavorite; existing.Options = item.Options; existing.IconImage = item.IconImage;existing.FallbackIconImage=item.FallbackIconImage;
                    existing.RefreshOptiFineState();
                }
            }
            CurrentInstance = Instances.FirstOrDefault(i => i.Id == wanted) ?? Instances.FirstOrDefault();
            _restoreId = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (revision == _refreshRevision) { InstanceError = RuntimeSupport.Error(error); Log("读取实例：" + InstanceError); } }
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
    public bool HasPendingWrites=>_operations.Any(pair=>!pair.Key.IsRunningGame);
    /// <summary>退出时等待下载和安装取消清理完成；已运行的 Java 进程继续运行。</summary>
    public async Task StopPendingWritesAsync()
    {
        var pending=_operations.Where(pair=>!pair.Key.IsRunningGame).ToArray();
        foreach(var pair in pending){pair.Value.Cancel();pair.Key.Stage="正在取消";pair.Key.Refresh();}
        await Task.WhenAll(pending.Where(pair=>_executions.ContainsKey(pair.Key)).Select(pair=>_executions[pair.Key]));
    }
    private void Cancel(PreviewTask? task)
    {
        if (task == null || !_operations.TryGetValue(task, out var cancellation)) return;
        if (task.IsRunningGame && MessageBox.Show("强制结束游戏可能丢失未保存的进度。是否继续？", "结束游戏", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        cancellation.Cancel(); task.Stage = "正在取消"; task.Refresh();
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
        if (Directory.Exists(Path.Combine(root, "versions", name)) && !_incomplete.Contains(InstanceStore.Key(root, name))) throw new IOException("这个安装名称已存在，请使用其他名称。");
        return Enqueue(PreviewTaskKind.Install, name, game, root, name, async (task, token) =>
        {
            if (!_rootWriters.TryGetValue(root, out var writer)) _rootWriters[root] = writer = new(1, 1);
            await writer.WaitAsync(token);
            try
            {
                await SetIncompleteAsync(root, name, true);
                await InstallComponentsAsync(root, name, game, loader, loaderVersion, optifine, task, token);
                token.ThrowIfCancellationRequested();
                var instance = new PreviewInstance(name, name, game, loader) { Root = root };
                var options = InstanceStore.Read(instance); options.Name = name; options.Source = "Minecraft";
                await InstanceStore.SaveAsync(instance, options);
                await SetIncompleteAsync(root, name, false);
                if (string.Equals(root, SelectedRoot, StringComparison.OrdinalIgnoreCase)) await RefreshInstancesAsync();
                NotifyTask(name + "安装完成", false);
            }
            finally { writer.Release(); }
        });
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
                        var baseInstance=new PreviewInstance(game,game,game,"原版"){Root=root};
                        var selected=await RuntimeSupport.SelectCheckedLauncherAsync(baseInstance,FMCore.Accounts.OfflineAccount.Create("FMLauncher"),new InstanceOptions(),SavedSettings,token);
                        java=selected.JavaPath;
                    }
                    else java=await RuntimeSupport.SelectJavaAsync(SavedSettings.JavaPath,null,token);
                }
                if (loader == "Fabric") await new FabricInstaller(game, loaderVersion, root, name, threads) { Source = source }.InstallAsync(p => Report(task, p.Message, p.Download), token);
                else if (loader == "Forge")
                {
                    await new ForgeInstaller(game,loaderVersion,root,java!,name,threads){Source=source}.InstallAsync(p=>Report(task,p.Message,p.Download,p.InstallerDownload),token);
                    if(optiFineMod!=null)
                    {
                        token.ThrowIfCancellationRequested();
                        var instance=new PreviewInstance(name,name,game,"Forge"){Root=root};
                        ModpackArchive.EnsureInstanceWritePath(instance);
                        var component=await OptiFineForgeIntegration.InstallAsync(optiFineMod,loaderVersion,instance.Folder,p=>Report(task,"下载 OptiFine（BMCLAPI）",file:p),token);
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
                        // 仅创建继承原版的实例声明；继承、参数和依赖仍由核心解释。
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
                                if(existing?["id"]?.GetValue<string>()!=name||existing?["inheritsFrom"]?.GetValue<string>()!=game)throw new IOException("已有版本声明与本次安装不同，未覆盖。");
                            }
                            else File.Move(temporary,profile,false);
                        }
                        finally{if(File.Exists(temporary))File.Delete(temporary);}
                    }
                }
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
            var javaCandidates = await RuntimeSupport.ReadLaunchJavaCandidatesAsync(instance, settings, global, token);
            await EnsureFilesAsync(instance, task, token);
            Report(task, "检查启动配置");
            var selectedLauncher = await RuntimeSupport.SelectCheckedLauncherAsync(instance,account.RuntimeAccount,settings,global,token,javaCandidates);
            var launcher = selectedLauncher.Launcher;
            token.ThrowIfCancellationRequested();
            if (settings.GlobalMemory ? global.OptimizeMemory : settings.OptimizeMemory) RuntimeSupport.ReleaseLauncherMemory();
            var code = await launcher.LaunchAsync(p => PostUi(() =>
            {
                if (!task.IsActive) return;
                task.Stage = p.Stage == LaunchStage.JavaStarted ? "已启动" : p.Stage == LaunchStage.GameExited ? "游戏已退出" : "启动中";
                task.IsRunningGame = p.Stage == LaunchStage.JavaStarted || task.IsRunningGame;
                task.Total = task.Bytes = 0; task.Refresh(); Signal();
            }), line => Log(line), line => Log(line), token);
            if (code != 0) throw new InvalidOperationException("游戏异常退出，退出码：" + code + "。请在任务记录中查看日志。");
            task.Stage = "游戏已退出";
    }
    /// <summary>离线校验通过时直接返回，否则交由核心联网补全并校验；仅正常完成后允许进入启动配置检查。</summary>
    public async Task EnsureFilesAsync(PreviewInstance instance, PreviewTask task, CancellationToken token)
    {
        Report(task, "检查完整性");
        var checker = new GameFileChecker(instance.Id, instance.Root);
        var check = await checker.CheckAsync(p => Report(task, $"检查完整性 {p.CheckedFiles}/{p.TotalKnownFiles}"), token);
        token.ThrowIfCancellationRequested();
        if (check.FileContentVerified) return;

        Log(instance.Id + $"：文件检查发现缺失 {check.MissingFiles} 项，内容不符 {check.InvalidFiles} 项，校验声明不足 {check.UnverifiableFiles} 项，交由核心补全并校验。");
        // MetadataMissing 表示本地声明不足，不等于文件损坏。补全器会在线解析缺少的声明并核验文件；
        // 正常返回即完成本轮校验，不再用读取同一份原始声明的离线检查器重复拦截。
        var completed = await new GameFileCompleter(instance.Id, instance.Root, DownloadConcurrency) { Source = FileSource }.CompleteAsync(p =>
        {
            var stage = p.Stage is GameFileCompletionStage.CompletingClientAndLibraries or GameFileCompletionStage.CompletingResources
                ? "补全并校验文件" : "检查完整性";
            Report(task, stage + "：" + p.Message, p.Download);
        }, token);
        token.ThrowIfCancellationRequested();
        Log(instance.Id + $"：核心文件校验完成，共 {completed.TotalFiles} 个文件，下载 {completed.DownloadedFiles} 个，复用 {completed.ReusedFiles} 个。");
        Report(task, "检查完整性完成");
    }
}
