using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using FocusMinecraftLauncher.Features.Home.ViewModels.Instances;
using System.Diagnostics;
using System.Windows.Input;
using System.IO;
using Microsoft.Win32;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Navigation;
using FocusMinecraftLauncher.Preview;
using FocusMinecraftLauncher.Features.Home.Models;
using FocusMinecraftLauncher.Infrastructure.Content;

namespace FocusMinecraftLauncher.Features.Home.ViewModels;

/// <summary>组织主页的身份来源、原生菜单与实例选择；共享账号与持久化选择，皮肤、资讯和游戏流程保留现有接入边界。</summary>
public sealed class HomeViewModel : ObservableObject, IDisposable
{
    private readonly Action<bool>? _addAccount;
    private readonly Action? _renameOffline;
    private PreviewAccount[] _menuAccounts = [];
    private string[] _menuAccountNames = [];
    private bool _menuMode;
    private string _notice = "";
    private bool _selecting, _compactHeight, _infoNarrow, _narrowHome;
    private double _launchPanelWidth = 360;
    private double _panelWidth = 328;
    private bool _managing;
    private InstanceWorkspaceViewModel? _workspace;
    private readonly Dictionary<string,InstanceWorkspaceViewModel> _workspaces=[];
    public PreviewSession Session { get; }
    public InstanceSelectionViewModel InstanceSelection { get; }
    public ObservableCollection<AccountMenuEntry> AccountMenu { get; } = [];
    public ObservableCollection<HomeNewsItem> News { get; } = [];
    private readonly CancellationTokenSource _contentLifetime=new();
    public ObservableCollection<HomeNewsItem> LauncherUpdates { get; }=[];
    private bool _newsLoaded, _newsLoading, _launcherUpdatesLoaded, _launcherUpdatesLoading;
    public string LauncherUpdatesStateText {get;private set;}="正在读取启动器更新记录";
    public bool ShowHomeUpdates => Session.ShowLauncherUpdates || Session.ShowGameUpdates;
    public UiCommand OpenLauncherUpdates {get;}
    public string NewsStateText { get; private set; } = "正在加载资讯";
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public bool IsSelectingInstance { get => _selecting; set {if(Set(ref _selecting,value))HomeVisibilityChanged();} }
    public bool IsManagingInstance { get=>_managing;private set {if(Set(ref _managing,value))HomeVisibilityChanged();} }
    public bool IsHome=>!IsSelectingInstance&&!IsManagingInstance;
    public bool ShowPageSurface => !IsHome || Session.HomeLayout == HomeLayout.Classic;
    public bool IsClassicHome => IsHome && Session.HomeLayout == HomeLayout.Classic;
    public bool IsMinimalHome => IsHome && Session.HomeLayout == HomeLayout.Minimal;
    public bool IsCenteredHome => IsHome && Session.HomeLayout == HomeLayout.Centered;
    public InstanceWorkspaceViewModel? Workspace { get=>_workspace;private set=>Set(ref _workspace,value); }
    public bool IsCompactHeight { get => _compactHeight; set { if (Set(ref _compactHeight, value)) { Changed(nameof(AvatarSize)); Changed(nameof(SimpleAvatarSize)); Changed(nameof(CenteredFeedbackHeight)); } } }
    public bool IsNarrowHome { get => _narrowHome; set { if (Set(ref _narrowHome, value)) Changed(nameof(SimpleAvatarSize)); } }
    public double LaunchPanelWidth { get => _launchPanelWidth; set => Set(ref _launchPanelWidth, value); }
    public double SimpleAvatarSize => IsCompactHeight || IsNarrowHome ? 64 : 96;
    public double CenteredFeedbackHeight => IsCompactHeight ? 40 : 96;
    public bool IsInfoNarrow { get => _infoNarrow; set => Set(ref _infoNarrow, value); }
    public double PanelWidth { get => _panelWidth; set => Set(ref _panelWidth, value); }
    public double AvatarSize => IsCompactHeight ? (Session.IsLaunching ? 48 : 64) : 96;
    public bool CanChangeIdentity => !Session.IsEditingBlocked && !Session.IsLoading && !Session.IsAuthenticating;
    public bool HasCurrentAccount => Session.CurrentAccount != null;
    public bool HasInstance => Session.CurrentInstance != null;
    public bool NeedsLogin => Session.CurrentAccount is { IsMicrosoft: true, IsValid: false, IsRestoring: false };
    public string PlayerName => Session.CurrentAccount?.Name ?? (Session.MicrosoftMode ? "添加正版账号" : "创建离线账号");
    public string WelcomeText => Session.CurrentAccount is { } account ? "欢迎回来，" + account.Name : "欢迎使用";
    public string AccountState => Session.CurrentAccount?.IsMicrosoft == true && !Session.CurrentAccount.IsValid ? Session.CurrentAccount.LoginState : "";
    public string AddAccountText => Session.MicrosoftMode ? "添加正版账号" : "创建离线账号";
    public string PrimaryText => ShowTask
        ? LaunchStage=="已启动" ? "已启动" : LaunchStage.Contains("检查") ? "正在检查" : LaunchStage.Contains("补全") ? "正在补全" : LaunchStage.Contains("启动") ? "正在启动" : "正在准备"
        : Session.CurrentAccount?.IsRestoring == true ? "正在恢复登录" : !HasInstance ? "安装游戏" : !HasCurrentAccount ? "添加账号" : NeedsLogin ? "重新登录" : "开始游戏";
    public string TargetVersion => Session.CurrentInstance is not { } instance ? "未选择版本"
        : string.IsNullOrWhiteSpace(instance.Loader) || instance.Loader == "原版" ? instance.Version : instance.Version + " / " + instance.ComponentsText;
    public string LaunchSubtitle => HasInstance ? InstanceName : TargetVersion;
    public string LaunchButtonHint => HasInstance ? InstanceName + "\n" + TargetVersion : TargetVersion;
    public string LaunchButtonName => PrimaryText + "，" + LaunchSubtitle;
    public bool ShowAddAccountGuide => !HasCurrentAccount && !HasInstance && CanChangeIdentity;
    public string InstanceName => Session.CurrentInstance?.Name ?? "尚未选择游戏版本";
    public string InstanceVersion => Session.CurrentInstance?.VersionText ?? "";
    public string InstanceLoader => Session.CurrentInstance?.Loader ?? "";
    public bool ShowTask => Session.IsLaunching;
    public string LaunchHint => ShowTask ? "" : Session.IsLoading ? "正在读取游戏" : Session.InstanceError.Length > 0 ? "游戏暂不可用" : !HasCurrentAccount ? "请先添加账号" : NeedsLogin ? "请重新登录后启动" : Session.IsBusy ? "正在处理任务" : IsClassicHome && HasInstance ? "" : "";
    public string LaunchStage => Session.LaunchTask?.Stage ?? "";
    public string LaunchError => Session.LaunchTask?.Error ?? "";
    public bool CanRetryLaunch => Session.LaunchTask?.CanRetry == true;
    public string InstanceSettingsHint => HasInstance ? "管理当前游戏" : "请先选择游戏版本";
    public bool CanOpenInstanceSettings => HasInstance && CanChangeIdentity;
    public UiCommand Accounts { get; }
    public UiCommand AddAccount { get; }
    public UiCommand Relogin { get; }
    public UiCommand UseAccount { get; }
    public UiCommand OpenInstances { get; }
    public UiCommand OpenInstanceSettings { get; }
    public UiCommand ReturnHome { get; }
    public UiCommand InstallGame { get; }
    public UiCommand Primary { get; }
    public UiCommand OpenNews { get; }
    public UiCommand OpenProfile { get; }
    public UiCommand OpenPassword { get; }
    public UiCommand RenameOffline { get; }
    public UiCommand OpenNewsItem { get; }
    public UiCommand RefreshSkin { get; }
    public UiCommand SaveSkin { get; }
    public UiCommand RefreshCapes { get; }
    public UiCommand SetCape { get; }
    public UiCommand UploadLocalSkin { get; }
    public UiCommand RemoveLocalSkin { get; }
    /// <summary>离线本地皮肤按玩家名写进每个游戏目录，这里给出当前账号是否已有可用的皮肤文件。</summary>
    public string LocalSkinStatus => Session.CurrentAccount is not { } account || account.Name.Length == 0 ? ""
        : LocalSkinTargets().Any(directory => File.Exists(Infrastructure.Authentication.LocalSkinStore.FileFor(directory, account.Name)))
            ? "本地皮肤 " + account.Name + ".png"
            : account.IsMicrosoft ? "" : "本地皮肤未设置";
    public ObservableCollection<CapeChoice> CapeOptions { get; } = [];
    public bool CanRefreshSkin => Session.CurrentAccount is { IsMicrosoft: true, IsValid: true, IsSkinLoading: false };
    public bool CanSaveSkin => Session.CurrentAccount?.SkinPng != null;
    public bool CanEditLocalSkin => CanChangeIdentity && Session.CurrentAccount is { } account && account.Name.Length > 0;
    public string SkinStatus => Session.CurrentAccount?.SkinMessage ?? "";
    public string SkinRefreshHint => Session.CurrentAccount?.IsMicrosoft == true ? "重新读取官方皮肤与头像" : "离线账号没有官方皮肤";

    public HomeViewModel(PreviewSession session, Action<bool>? addAccount = null, Action? renameOffline = null)
    {
        Session = session; _addAccount = addAccount;_renameOffline=renameOffline;
        InstanceSelection = new(session, () => { IsSelectingInstance = false; IsManagingInstance = false; });
        Accounts = new(_ => session.Navigate(MainPage.Accounts));
        AddAccount = new(_ => OpenAccountEditor(), _ => CanChangeIdentity);
        Relogin = new(async _ => { session.Navigate(MainPage.Accounts); await session.LoginMicrosoftAsync(session.CurrentAccount); }, _ => CanChangeIdentity && NeedsLogin);
        UseAccount = new(p => { session.CurrentAccount = (PreviewAccount)p!; session.CurrentAccount.Refresh(); Notice = ""; }, p => p is PreviewAccount a && session.Accounts.Contains(a) && a.IsOnline == session.MicrosoftMode && CanChangeIdentity);
        OpenInstances = new(_ => { InstanceSelection.Open(); IsManagingInstance=false; IsSelectingInstance = true; }, _ => CanChangeIdentity);
        ReturnHome = new(_ => { IsSelectingInstance=false;IsManagingInstance=false; });
        OpenInstanceSettings=new(_=>OpenWorkspace(),_=>CanOpenInstanceSettings);
        InstallGame = new(_ => session.OpenVersions(), _ => CanChangeIdentity);
        Primary = new(_ =>
        {
            if (!HasInstance) session.OpenVersions();
            else if (NeedsLogin) Relogin.Execute(null);
            else if (!HasCurrentAccount) OpenAccountEditor();
            else session.Launch.Execute(null);
        }, _ => CanChangeIdentity && session.CurrentAccount?.IsRestoring != true && (!HasInstance || !HasCurrentAccount || NeedsLogin || session.CanLaunch));
        RefreshSkin = new(async _ => { if (session.CurrentAccount is { } account) await session.RefreshSkinAsync(account); }, _ => CanRefreshSkin);
        RefreshCapes = new(async _ => await RefreshCapesAsync(), _ => CanEditCapes);
        SetCape = new(async parameter =>
        {
            if (parameter is not CapeChoice choice || choice.Id.Length == 0) return;
            if (session.CurrentAccount?.RuntimeAccount is not FMCore.Accounts.MicrosoftAccount account) return;
            try
            {
                // 再次点击当前披风即取消披风，与官方档案的切换语义保持一致。
                var removing = choice.IsActive;
                await Capes().SetCapeAsync(account, removing ? null : choice.Id, CancellationToken.None);
                if (session.CurrentAccount?.Uuid != account.Uuid) return;
                session.NotifyTask(removing ? $"披风（{choice.Alias}）已取消" : $"披风（{choice.Alias}）修改成功", false);
                await LoadCapesAsync(account);
                if (session.CurrentAccount is { } selected && ReferenceEquals(selected.RuntimeAccount, account))
                    await session.RefreshSkinAsync(selected, true);
            }
            catch (Exception error) { session.NotifyTask(Infrastructure.Runtime.RuntimeSupport.Error(error), true); }
        }, parameter => parameter is CapeChoice { Id.Length: > 0 } && CanEditCapes);
        SaveSkin = new(async _ =>
        {
            if (session.CurrentAccount?.SkinPng is not { } png) return;
            var dialog = new SaveFileDialog { Title = "保存皮肤文件", Filter = "PNG 图片|*.png", FileName = "Minecraft-skin.png", DefaultExt = ".png", AddExtension = true };
            if (dialog.ShowDialog(System.Windows.Application.Current.MainWindow) != true) return;
            try { await File.WriteAllBytesAsync(dialog.FileName, png); Notice = "皮肤文件已保存。"; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Notice = "皮肤文件保存失败，请检查所选位置的写入权限。"; }
        }, _ => CanSaveSkin);
        UploadLocalSkin = new(async _ => await UploadLocalSkinAsync(), _ => CanEditLocalSkin);
        RemoveLocalSkin = new(async _ =>
        {
            if (Session.CurrentAccount is not { } account || account.Name.Length == 0) return;
            var removed = Infrastructure.Authentication.LocalSkinStore.Remove(LocalSkinTargets(), account.Name);
            await Session.RefreshLocalSkinAsync(account);
            Session.NotifyTask(removed > 0 ? "本地皮肤已移除" : "没有可移除的本地皮肤", removed == 0);
            Refresh();
        }, _ => CanEditLocalSkin);
        OpenNews = new(_ => OpenExternal("https://www.minecraft.net/en-us/updates"));
        OpenLauncherUpdates = new(_=>OpenExternal("https://github.com/Voltucit/FocusMinecraft-Launcher/releases"));
        OpenProfile = new(_ => OpenExternal("https://www.minecraft.net/en-us/msaprofile"), _ => session.CurrentAccount?.IsMicrosoft == true);
        OpenPassword = new(_ => OpenExternal("https://account.microsoft.com/security"), _ => session.CurrentAccount?.IsMicrosoft == true);
        RenameOffline = new(_=>_renameOffline?.Invoke(),_=>session.CurrentAccount is {IsMicrosoft:false}&&CanChangeIdentity);
        OpenNewsItem = new(p => { if (p is HomeNewsItem { HasTarget: true } item) OpenExternal(item.Target!.AbsoluteUri); }, p => p is HomeNewsItem { HasTarget: true });
        session.PropertyChanged += SessionChanged;
        session.Accounts.CollectionChanged += AccountsChanged;
        UpdateAccountMenu();
        LoadSelectedUpdates();
        // 提前读一次披风，外观菜单展开时才能直接列出可选项。
        EnsureCapesLoaded();
    }
    private bool CanEditCapes => Session.CurrentAccount is { IsMicrosoft: true, IsValid: true } && !Session.IsEditingBlocked;
    private static Infrastructure.Authentication.OfficialSkinService Capes() =>
        new(((App)System.Windows.Application.Current).Configuration.ConfigDirectory);
    private async Task RefreshCapesAsync()
    {
        if (Session.CurrentAccount?.RuntimeAccount is not FMCore.Accounts.MicrosoftAccount account) return;
        try { await LoadCapesAsync(account); Notice = "披风列表已刷新。"; }
        catch (Exception error)
        {
            Notice = Infrastructure.Runtime.RuntimeSupport.Error(error);
            CapeOptions.Clear();
            CapeOptions.Add(new("", "读取披风失败", false, SetCape));
        }
    }
    private async Task LoadCapesAsync(FMCore.Accounts.MicrosoftAccount account)
    {
        var capes = await Capes().GetCapesAsync(account, CancellationToken.None);
        if (Session.CurrentAccount?.Uuid != account.Uuid) return;
        CapeOptions.Clear();
        // 每项自带命令，避免子菜单弹出层里拿不到 ContextMenu 的 DataContext。
        foreach (var cape in capes) CapeOptions.Add(new(cape.Id, cape.Alias, cape.IsActive, SetCape));
        // 子菜单为空时无法展开，用一个不可点击的占位项说明当前账号没有披风。
        if (CapeOptions.Count == 0) CapeOptions.Add(new("", "没有可用披风", false, SetCape));
    }
    /// <summary>打开外观菜单时补一次披风列表，用户不必先手动刷新就能直接切换披风。</summary>
    public void EnsureCapesLoaded()
    {
        if (!RefreshCapes.CanExecute(null) || CapeOptions.Count > 0) return;
        CapeOptions.Add(new("", "正在读取披风", false, SetCape));
        RefreshCapes.Execute(null);
    }
    /// <summary>共享目录加每个实例的实际游戏目录，覆盖版本隔离与非隔离两种情况。</summary>
    private IReadOnlyList<string> LocalSkinTargets()
    {
        var targets = new List<string> { Session.SavedSettings.GameDirectory };
        foreach (var instance in Session.Instances) targets.Add(instance.GameDirectory);
        return targets;
    }
    private async Task UploadLocalSkinAsync()
    {
        if (Session.CurrentAccount is not { } account || account.Name.Length == 0) return;
        var dialog = new OpenFileDialog { Title = "选择皮肤文件", Filter = "PNG 图片|*.png", CheckFileExists = true };
        if (dialog.ShowDialog(System.Windows.Application.Current.MainWindow) != true) return;
        var targets = LocalSkinTargets();
        try
        {
            var png = await File.ReadAllBytesAsync(dialog.FileName);
            Infrastructure.Authentication.LocalSkinStore.Validate(png);
            var saved = await Infrastructure.Authentication.LocalSkinStore.SaveAsync(targets, account.Name, png, CancellationToken.None);
            await Session.RefreshLocalSkinAsync(account);
            Session.NotifyTask($"本地皮肤已保存到 {saved} 个游戏目录", false);
            if (targets.Any(directory => !Infrastructure.Authentication.LocalSkinStore.HasLoader(directory)))
                Session.NotifyTask("未检测到 CustomSkinLoader，游戏内不会显示本地皮肤，请先给游戏安装该 Mod。", true);
            Refresh();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        { Session.NotifyTask(Infrastructure.Runtime.RuntimeSupport.Error(error), true); }
    }
    /// <summary>按实例缓存子页草稿；重新载入后的新实例对象不会误用旧上下文。</summary>
    private void OpenWorkspace()
    {
        if(Session.CurrentInstance is not {} instance)return;
        if(!_workspaces.TryGetValue(Infrastructure.Runtime.InstanceStore.Key(instance.Root,instance.Id),out var workspace)||!ReferenceEquals(workspace.Instance,instance))
        {
            workspace?.Dispose();workspace=new(Session,instance,()=>ReturnHome.Execute(null));_workspaces[Infrastructure.Runtime.InstanceStore.Key(instance.Root,instance.Id)]=workspace;
        }
        Workspace=workspace;IsSelectingInstance=false;IsManagingInstance=true;
    }
    private void AccountsChanged(object? sender,NotifyCollectionChangedEventArgs e)=>UpdateAccountMenu();
    public void Dispose()
    {
        _contentLifetime.Cancel();_contentLifetime.Dispose();
        Session.PropertyChanged-=SessionChanged;Session.Accounts.CollectionChanged-=AccountsChanged;
        InstanceSelection.Dispose();
        foreach(var workspace in _workspaces.Values)workspace.Dispose();_workspaces.Clear();
    }
    private void OpenAccountEditor()
    {
        if (_addAccount != null) _addAccount(Session.MicrosoftMode);
        else { Session.Navigate(MainPage.Accounts); Notice = "请在账号页添加身份。"; }
    }
    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PreviewSession.ShowLauncherUpdates) or nameof(PreviewSession.ShowGameUpdates)) LoadSelectedUpdates();
        if(e.PropertyName==nameof(PreviewSession.CurrentAccount)){CapeOptions.Clear();EnsureCapesLoaded();}
        if(e.PropertyName==nameof(PreviewSession.ProfileRevision)){CapeOptions.Clear();EnsureCapesLoaded();}
        if (e.PropertyName == nameof(PreviewSession.HomeLayout))
        { IsSelectingInstance = false; IsManagingInstance = false; }
        UpdateAccountMenu(); Refresh();
    }
    private void HomeVisibilityChanged()
    {
        Changed(nameof(IsHome)); Changed(nameof(ShowPageSurface)); Changed(nameof(IsClassicHome)); Changed(nameof(IsMinimalHome)); Changed(nameof(IsCenteredHome));
    }
    /// <summary>只在来源或账号列表改变时重建菜单，任务进度通知不会打断菜单键盘选择。</summary>
    private void UpdateAccountMenu()
    {
        var accounts = Session.Accounts.Where(a => a.IsOnline == Session.MicrosoftMode).ToArray();
        if (AccountMenu.Count > 0 && _menuMode == Session.MicrosoftMode && _menuAccounts.SequenceEqual(accounts)&&_menuAccountNames.SequenceEqual(accounts.Select(account=>account.Name))) return;
        _menuMode = Session.MicrosoftMode; _menuAccounts = accounts;
        _menuAccountNames=accounts.Select(account=>account.Name).ToArray();
        AccountMenu.Clear();
        if (accounts.Length == 0) AccountMenu.Add(new("当前模式下没有账号", null, IsPlaceholder: true));
        foreach (var account in accounts) AccountMenu.Add(new(account.Name, UseAccount, account, account));
        AccountMenu.Add(new("", null, IsSeparator: true));
        AccountMenu.Add(new(AddAccountText, AddAccount));
        AccountMenu.Add(new("管理账号", Accounts));
    }
    /// <summary>只在对应记录被选中后读取；已读取内容保持原对象，不因切换显示而丢失展开状态。</summary>
    private void LoadSelectedUpdates()
    {
        if (Session.ShowLauncherUpdates) _ = LoadLauncherUpdatesAsync();
        if (Session.ShowGameUpdates) _ = LoadNewsAsync();
    }
    private async Task LoadNewsAsync()
    {
        if (_newsLoaded || _newsLoading || _contentLifetime.IsCancellationRequested) return;
        _newsLoading = true;
        NewsStateText = "正在加载游戏更新记录"; Changed(nameof(NewsStateText));
        try
        {
            var entries=await HomeContentService.GameUpdatesAsync(_contentLifetime.Token);
            News.Clear();foreach(var entry in entries){var item=ContentItem(entry);News.Add(item);_ = LoadCoverAsync(item,entry.Image);}
            NewsStateText=News.Count==0?"暂无游戏更新":"";
            _newsLoaded = true;
        }
        catch(OperationCanceledException) when(_contentLifetime.IsCancellationRequested){return;}
        catch(Exception error){NewsStateText="游戏更新加载失败，可查看官方更新。";Session.Log("游戏更新："+Infrastructure.Runtime.RuntimeSupport.Error(error));}
        finally { _newsLoading = false; Changed(nameof(NewsStateText)); }
    }
    private async Task LoadLauncherUpdatesAsync()
    {
        if (_launcherUpdatesLoaded || _launcherUpdatesLoading || _contentLifetime.IsCancellationRequested) return;
        _launcherUpdatesLoading = true;
        LauncherUpdatesStateText = "正在读取启动器更新记录"; Changed(nameof(LauncherUpdatesStateText));
        try
        {
            var entries=await HomeContentService.ReleasesAsync(_contentLifetime.Token);
            LauncherUpdates.Clear();foreach(var entry in entries)LauncherUpdates.Add(ContentItem(entry));
            LauncherUpdatesStateText=LauncherUpdates.Count==0?"暂无启动器更新记录":"";
            _launcherUpdatesLoaded = true;
        }
        catch(OperationCanceledException) when(_contentLifetime.IsCancellationRequested){return;}
        catch(Exception error){LauncherUpdatesStateText="启动器更新记录加载失败，可查看发布记录。";Session.Log("启动器更新记录："+Infrastructure.Runtime.RuntimeSupport.Error(error));}
        finally { _launcherUpdatesLoading = false; Changed(nameof(LauncherUpdatesStateText)); }
    }
    private HomeNewsItem ContentItem(HomeContentEntry entry)=>new()
    {Title=entry.Title,Summary=entry.Summary,Updated=entry.Date,Target=entry.Target,Detail=entry.Detail,RequestCancellation=_contentLifetime.Token};
    private async Task LoadCoverAsync(HomeNewsItem item,string? image)
    {
        var cover=await Features.Downloads.Models.RemoteArtwork.LoadAsync(image,_contentLifetime.Token);
        if(!_contentLifetime.IsCancellationRequested)item.Cover=cover;
    }
    private void OpenExternal(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        { Notice = "无法打开浏览器，请检查系统默认浏览器设置。"; }
    }
}

public sealed record AccountMenuEntry(string Title, ICommand? Command, object? Parameter = null, PreviewAccount? Account = null, bool IsSeparator = false, bool IsPlaceholder = false)
{
    public bool IsAccount => Account != null;
}

/// <summary>显示真实发布内容；官方更新详情在展开时读取，失败保留摘要供用户继续操作。</summary>
public sealed class HomeNewsItem : ObservableObject
{
    private bool _expanded,_bodyLoaded,_loading;
    private string _summary="",_bodyState="";
    private System.Windows.Media.ImageSource? _cover;
    public string Title {get;init;}="";
    public string Summary {get=>_summary;set=>Set(ref _summary,value);}
    public string Updated {get;init;}="";
    public System.Windows.Media.ImageSource? Cover {get=>_cover;set=>Set(ref _cover,value);}
    public Uri? Target {get;init;}
    public Uri? Detail {get;init;}
    public CancellationToken RequestCancellation {get;init;}
    public string BodyState {get=>_bodyState;private set=>Set(ref _bodyState,value);}
    public bool HasTarget=>Target!=null&&HomeContentService.AllowedTarget(Target.AbsoluteUri)!=null;
    public bool IsExpanded {get=>_expanded;set {if(Set(ref _expanded,value)&&value)_ = LoadBodyAsync();}}
    private async Task LoadBodyAsync()
    {
        if(Detail==null||_bodyLoaded||_loading||RequestCancellation.IsCancellationRequested)return;
        _loading=true;BodyState="正在读取更新内容";
        try
        {
            var body=await HomeContentService.UpdateBodyAsync(Detail,RequestCancellation);
            if(body.Length>0)Summary=body;
            _bodyLoaded=true;BodyState=body.Length>0?"":"官方未提供详细内容。";
        }
        catch(OperationCanceledException) when(RequestCancellation.IsCancellationRequested){ }
        catch(Exception){BodyState="更新详情读取失败，收起后可重新展开重试。";}
        finally{_loading=false;}
    }
}

/// <summary>外观菜单里的一个披风选项；自带命令，子菜单弹出后不依赖祖先的 DataContext。</summary>
public sealed record CapeChoice(string Id, string Alias, bool IsActive, Common.UiCommand Command);
