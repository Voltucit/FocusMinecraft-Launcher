using System.ComponentModel;
using System.Windows.Media;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Features.Downloads.Models;
using FocusMinecraftLauncher.Preview;
namespace FocusMinecraftLauncher.Features.Downloads.ViewModels;

/// <summary>下载导航、真实资源及任务状态的页面协调。</summary>
public sealed class DownloadsViewModel : ObservableObject, IDisposable
{
    private readonly ResourceCatalog _catalog = new();
    private readonly Dictionary<DownloadSection, CommunityCatalogViewModel> _categories;
    private readonly Dictionary<string, ResourceEntryViewModel> _entries;
    private readonly Dictionary<string, ResourceDetailViewModel> _details = [];
    private readonly Dictionary<string, VanillaInstallViewModel> _installations = [];
    private readonly Stack<ResourceDetailViewModel> _detailTrail = [];
    private DownloadSection _section, _lastBrowse;
    private ResourceDetailViewModel? _detail;
    private VanillaInstallViewModel? _installation;
    private DownloadPreviewViewModel? _preview;
    private DownloadNotification? _notification;
    private ImageSource? _image;
    private bool _compact, _syncing,_disposed;
    private ResourceFilter? _filter;
    private bool _filterApplied;
    private int _navigationRevision;
    private readonly CancellationTokenSource _lifetime = new();
    private string _state = "正常";
    public PreviewSession Session { get; }
    public VanillaCatalogViewModel Vanilla { get; }
    public TaskHistoryViewModel Tasks { get; }
    public DownloadNotification? Notification { get => _notification; private set => Set(ref _notification, value); }
    public IReadOnlyList<DownloadNavigation> Navigation { get; } =
    [new(DownloadSection.Vanilla,"原版游戏"),new(null,"社区资源"),new(DownloadSection.Mods,"Mod"),new(DownloadSection.Modpacks,"整合包"),new(DownloadSection.Datapacks,"数据包"),new(DownloadSection.Resourcepacks,"资源包"),new(DownloadSection.Shaders,"光影包")];
    public IEnumerable<DownloadNavigation> NavigationChoices => Navigation.Where(item => !item.IsHeading);
    public DownloadNavigation? SelectedNavigation { get => Navigation.FirstOrDefault(item => item.Section == _section); set { if (value?.Section is { } section) SetSection(section); } }
    public object ActivePage => (object?)_detail ?? (object?)_installation ?? (_section == DownloadSection.Vanilla ? (object)Vanilla : _section == DownloadSection.Tasks ? Tasks : _categories[_section]);
    public bool IsCompact { get => _compact; set => Set(ref _compact, value); }
    public string DisplayState { get => _state; set { if (Set(ref _state,value)) RefreshRoute(); } }
    public bool CanChangeDisplayState => _detail == null && _installation == null && _section != DownloadSection.Tasks;
    public bool ShowPage => !CanChangeDisplayState || DisplayState == "正常";
    public bool ShowState => !ShowPage;
    public string StateTitle => DisplayState switch { "加载中" => "正在加载版本列表", "空结果" => "没有匹配的内容", _ => "版本列表读取失败" };
    public string StateDescription => _loadError;
    private string _loadError = "";
    public bool IsOverlayOpen => Preview != null || LargeImage != null;
    public bool IsPreviewOpen => Preview != null;
    public bool IsImageOpen => LargeImage != null;
    public bool CanInteract => !IsOverlayOpen;
    public DownloadPreviewViewModel? Preview { get => _preview; private set { if(Set(ref _preview,value)) OverlayChanged(); } }
    public ImageSource? LargeImage { get => _image; private set { if(Set(ref _image,value)) OverlayChanged(); } }
    public UiCommand ShowTasks { get; }
    public UiCommand CloseOverlay { get; }
    public UiCommand Escape { get; }
    public UiCommand NormalState { get; }
    public DownloadsViewModel(PreviewSession session)
    {
        Session = session;
        _entries = [];
        _categories = Enum.GetValues<DownloadSection>().Where(section => (int)section >= 2).ToDictionary(section => section,section => new CommunityCatalogViewModel(section,_catalog,_entries,OpenResource));
        Vanilla = new(OpenInstallation); Tasks = new(session,() => SetSection(_lastBrowse),ShowNotification);
        _section = Enum.IsDefined((DownloadSection)session.DownloadTab) ? (DownloadSection)session.DownloadTab : DownloadSection.Vanilla;
        _lastBrowse = _section == DownloadSection.Tasks ? DownloadSection.Vanilla : _section;
        ShowTasks = new(_ => SetSection(DownloadSection.Tasks)); NormalState = new(async _ => await LoadVanillaAsync());
        CloseOverlay = new(_ => { LargeImage = null; Preview = null; });
        Escape = new(_ => { if(IsOverlayOpen) CloseOverlay.Execute(null); else if(_detail != null) BackFromDetail(); else if(_installation != null) ReturnToVanilla(); });
        session.PropertyChanged += SessionChanged;
        session.TaskNotification += ShowNotification;
        _ = LoadVanillaAsync();
        if(_categories.TryGetValue(_section,out var currentCategory))_ = EnterCategoryAsync(currentCategory);
    }
    private void SessionChanged(object? sender,PropertyChangedEventArgs e)
    { if(!_syncing && e.PropertyName == nameof(PreviewSession.DownloadTab)) SetSection(Enum.IsDefined((DownloadSection)Session.DownloadTab) ? (DownloadSection)Session.DownloadTab : DownloadSection.Vanilla); }
    private void SetSection(DownloadSection section)
    {
        _navigationRevision++;
        if(section != DownloadSection.Tasks) _lastBrowse = section;
        _section = section; _detail = null; _installation = null; _detailTrail.Clear(); _state = "正常";
        _syncing = true; Session.DownloadTab = (int)section; _syncing = false;
        Changed(nameof(SelectedNavigation)); RefreshRoute();
        if(_categories.TryGetValue(section,out var category)) _ = EnterCategoryAsync(category);
    }
    /// <summary>首次进入分类时接管实例带来的筛选上下文：自动落到版本与加载器，之后详情页沿用同一范围。</summary>
    private async Task EnterCategoryAsync(CommunityCatalogViewModel category)
    {
        if(_filter==null&&Session.PendingResourceFilter is { } pending){_filter=pending;Session.PendingResourceFilter=null;}
        await category.EnsureLoadedAsync();
        if(_filter is not { } filter||_filterApplied||category.Category!=DownloadSection.Mods)return;
        _filterApplied=true;
        category.ApplyFilter(filter.GameVersion,filter.Loader);
    }
    private void OpenInstallation(GameVersionItem game)
    {
        _navigationRevision++;
        if(!_installations.TryGetValue(game.Id,out var vm)) _installations[game.Id] = vm = new(game,ReturnToVanilla,EnqueueInstallation,Session);
        _installation = vm; _ = vm.RetryFailedQueriesAsync(Session); RefreshRoute();
    }
    private void ReturnToVanilla() { _navigationRevision++; _installation = null; _state = "正常"; RefreshRoute(); }
    /// <summary>捕获安装选择并加入真实任务，返回列表后提示；失败通过全局通知显示。</summary>
    private void EnqueueInstallation(VanillaInstallViewModel configuration)
    {
        try { Tasks.Enqueue(configuration); ReturnToVanilla(); ShowNotification(configuration.Name + "已加入下载列表", false); }
        catch(Exception error){ShowNotification(Infrastructure.Runtime.RuntimeSupport.Error(error),true);}
    }
    private void ShowNotification(string message, bool isError) { Notification = new(message, isError); Session.SetGlobalNotification(message,isError); }
    private async void OpenResource(string id,bool versions)
    {
        var revision=++_navigationRevision;
        try
        {
            var project=await _catalog.LoadAsync(id,_lifetime.Token);
            if(_disposed||revision!=_navigationRevision)return;
            if(!_entries.TryGetValue(id,out var entry))_entries[id]=entry=new(project,_catalog,OpenResource);else if(!ReferenceEquals(entry.Project,project))entry.UpdateProject(project);
            Preview=null;
            if(_detail!=null&&_detail.Project.Id!=id)_detailTrail.Push(_detail);
            if(!_details.TryGetValue(id,out var vm))_details[id]=vm=new ResourceDetailViewModel(entry,BackFromDetail,async file=>await OpenDownloadConfirmationAsync(entry,file),async key=>LargeImage=await RemoteArtwork.LoadAsync(key),_filter);_detail=vm;if(versions)vm.SelectedTab=2;RefreshRoute();
        }
        catch(Exception error){if(!_disposed)ShowNotification(Infrastructure.Runtime.RuntimeSupport.Error(error),true);}
    }
    /// <summary>确认框等待当前文件的前置资料读取结束，不使用上一个版本或尚未完成的资料快照。</summary>
    private async Task OpenDownloadConfirmationAsync(ResourceEntryViewModel entry,ResourceFileItem file)
    {
        var revision=_navigationRevision;
        try
        {
            await _catalog.EnsureDependenciesAsync(file);
            if(_disposed||revision!=_navigationRevision||!ReferenceEquals(entry.SelectedFile,file))return;
            Preview=DownloadPreviewViewModel.Resource(entry.Project,file,Session,_catalog,OpenResource);
        }
        catch(Exception error){if(!_disposed&&revision==_navigationRevision)ShowNotification(Infrastructure.Runtime.RuntimeSupport.Error(error),true);}
    }
    private async Task LoadVanillaAsync()
    {
        if(_section==DownloadSection.Vanilla)DisplayState="加载中";
        try { await Vanilla.LoadAsync(_lifetime.Token);if(_section==DownloadSection.Vanilla)DisplayState="正常"; }
        catch(Exception error){_loadError=Infrastructure.Runtime.RuntimeSupport.Error(error);if(_section==DownloadSection.Vanilla)DisplayState="失败";}
    }
    private void BackFromDetail() { _navigationRevision++; _detail = _detailTrail.TryPop(out var previous) ? previous : null; RefreshRoute(); }
    private void RefreshRoute() { Changed(nameof(ActivePage)); Changed(nameof(CanChangeDisplayState)); Changed(nameof(ShowPage)); Changed(nameof(ShowState)); Changed(nameof(StateTitle)); Changed(nameof(StateDescription)); Changed(nameof(DisplayState)); }
    private void OverlayChanged() { Changed(nameof(IsOverlayOpen)); Changed(nameof(IsPreviewOpen)); Changed(nameof(IsImageOpen)); Changed(nameof(CanInteract)); }
    public void Dispose() { _disposed=true;_navigationRevision++;_lifetime.Cancel(); Session.PropertyChanged -= SessionChanged; Session.TaskNotification -= ShowNotification; _catalog.Dispose(); Tasks.Dispose(); foreach(var detail in _details.Values) detail.Dispose(); }
}
/// <summary>用户确认目标后才加入资源下载任务，前置资源与所选文件绑定。</summary>
public sealed class DownloadPreviewViewModel : ObservableObject
{
    private PreviewInstance? _instance;
    public PreviewSession Session { get; }
    public string Title { get; }
    public string Summary { get; }
    public string Message { get; private set; }
    private bool _submitted;
    public UiCommand Confirm { get; }
    public string DependencyStatus { get; }
    public bool SelectInstance { get; }
    public bool HasInstances => Session.Instances.Count > 0;
    public bool HasDependencies => Dependencies.Count > 0;
    public IReadOnlyList<DependencyViewModel> Dependencies { get; }
    public PreviewInstance? SelectedInstance { get => _instance; set { if(Set(ref _instance,value)) Changed(nameof(InstanceText)); } }
    public string InstanceText => SelectedInstance?.Name ?? "未选择游戏";
    private DownloadPreviewViewModel(string title,string summary,string message,PreviewSession session,ResourceFileItem? file,ResourceCatalog? catalog,Action<string,bool>? open)
    {
        Title = title; Summary = summary; Message = message; Session = session; SelectInstance = file != null && file.DownloadFile?.Filename.EndsWith(".mrpack",StringComparison.OrdinalIgnoreCase) != true; _instance = session.CurrentInstance; DependencyStatus = file?.DependencyText ?? "";
        Dependencies = file?.EffectiveDependencies?.Select(dependency => catalog?.Find(dependency.ProjectId) is { } project ? new DependencyViewModel(project,dependency,open!) : null).OfType<DependencyViewModel>().ToArray() ?? [];
        Confirm=new(_=>
        {
            try { if(file!=null) { session.DownloadResource(file, SelectedInstance); _submitted=true; System.Windows.Input.CommandManager.InvalidateRequerySuggested(); Message="已加入下载列表";Changed(nameof(Message)); } }
            catch(Exception error){Message=Infrastructure.Runtime.RuntimeSupport.Error(error);Changed(nameof(Message));}
        },_=>!_submitted&&file!=null&&(!SelectInstance||SelectedInstance!=null));
    }
    public static DownloadPreviewViewModel Resource(ResourceProject project,ResourceFileItem file,PreviewSession session,ResourceCatalog catalog,Action<string,bool> open) => new("确认下载",project.Name+"\n"+file.FileName+"\n"+file.CompatibilityText+(file.Environment == null ? "" : " / "+file.Environment)+"\n"+file.SizeText,"必需前置资源会一同下载，可选资源请按需另行选择。",session,file,catalog,open);
}
