using System.Collections.ObjectModel;
using System.Windows.Input;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Features.Downloads.Models;

namespace FocusMinecraftLauncher.Features.Downloads.ViewModels;

/// <summary>五类资源共享远程查询和服务端分页，每个分类保留独立筛选及滚动状态。</summary>
public sealed class CommunityCatalogViewModel : ObservableObject
{
    private readonly ResourceCatalog _catalog;
    private readonly Action<string,bool> _open;
    private readonly Dictionary<string,ResourceEntryViewModel> _entries;
    private readonly Dictionary<string,string> _tagIds=[];
    private string _query="",_source="全部",_tag="全部",_environment="全部",_summary="全部条件",_notice="";
    private bool _searchExpanded=true,_busy,_loaded;
    private int _page=1,_revision;
    private long _total;
    private double _nameFieldWidth=300;
    public DownloadSection Category { get; }
    public ObservableCollection<ResourceEntryViewModel> PageItems { get; }=[];
    public DownloadScrollPosition Scroll { get; }=new();
    public GameVersionFilterViewModel VersionFilter { get; private set; }
    public string[] Sources { get; }=["全部","Modrinth"];
    public string[] Tags { get; private set; }=["全部"];
    public string[] Environments { get; }
    public bool UsesEnvironment=>Category is DownloadSection.Mods or DownloadSection.Modpacks or DownloadSection.Shaders;
    public string EnvironmentTitle=>Category==DownloadSection.Shaders?"运行环境":"加载器";
    public string Query { get=>_query;set=>Set(ref _query,value); }
    public string Source { get=>_source;set=>Set(ref _source,value??"全部"); }
    public string Tag { get=>_tag;set=>Set(ref _tag,value??"全部"); }
    public string Environment { get=>_environment;set=>Set(ref _environment,value??"全部"); }
    public bool IsSearchExpanded { get=>_searchExpanded;set {if(Set(ref _searchExpanded,value))Changed(nameof(FilterToggleText));} }
    public string FilterToggleText=>IsSearchExpanded?"收起筛选":"展开筛选";
    public string SearchSummary=>_summary;
    public string Notice { get=>_notice;private set=>Set(ref _notice,value); }
    public double NameFieldWidth { get=>_nameFieldWidth;set=>Set(ref _nameFieldWidth,value); }
    public int PageCount=>(int)Math.Max(1,(_total+19)/20);
    public int Page=>_page;
    public bool IsEmpty=>!_busy&&PageItems.Count==0;
    public string PageText=>$"第 {_page} / {PageCount} 页，共 {_total} 项";
    public UiCommand Search { get; }
    public UiCommand Reset { get; }
    public UiCommand ToggleFilters { get; }
    public UiCommand PreviousPage { get; }
    public UiCommand NextPage { get; }
    public CommunityCatalogViewModel(DownloadSection category,ResourceCatalog catalog,Dictionary<string,ResourceEntryViewModel> entries,Action<string,bool> open)
    {
        Category=category;_catalog=catalog;_entries=entries;_open=open;
        Environments=category==DownloadSection.Shaders?["全部","iris","optifine"]:["全部","fabric","forge","neoforge","quilt"];
        VersionFilter=new([],()=>{_page=1;_ = QueryAsync();});
        Search=new(async _=>{_page=1;await QueryAsync();},_=>!_busy);
        Reset=new(async _=>{Query="";Source=Tag=Environment="全部";_page=1;if(!VersionFilter.IsAllSelected)VersionFilter.Select("全部");else await QueryAsync();},_=>!_busy);
        ToggleFilters=new(_=>IsSearchExpanded=!IsSearchExpanded);
        PreviousPage=new(async _=>{_page--;await QueryAsync();},_=>!_busy&&_page>1);
        NextPage=new(async _=>{_page++;await QueryAsync();},_=>!_busy&&_page<PageCount);
    }
    public async Task EnsureLoadedAsync()
    {
        if(_loaded)return;
        _loaded=true;
        try
        {
            var categories=await _catalog.Client.GetCategoriesAsync(ResourceCatalog.Type(Category));
            foreach(var tag in categories)_tagIds[tag.DisplayName]=tag.Name;
            Tags=["全部",.._tagIds.Keys];Changed(nameof(Tags));
            var versions=await FMCore.Installer.MinecraftInstaller.GetVersionsAsync();
            VersionFilter=new(versions.Where(v=>v.Type=="release").Select(v=>v.Id),()=>{_page=1;_ = QueryAsync();});Changed(nameof(VersionFilter));
        }
        catch(Exception error){_loaded=false;Notice=FocusMinecraftLauncher.Infrastructure.Runtime.RuntimeSupport.Error(error);}
        await QueryAsync();
    }
    private async Task QueryAsync()
    {
        var revision=++_revision;_busy=true;Notice="正在加载资源";Refresh();CommandManager.InvalidateRequerySuggested();
        var version=VersionFilter.SelectedVersion;
        _summary=string.Join(" / ",new[]{Query,version=="全部"?"":version,Tag=="全部"?"":Tag,Environment=="全部"?"":Environment}.Where(s=>s.Length>0));
        if(_summary.Length==0)_summary="全部条件";Changed(nameof(SearchSummary));
        try
        {
            var result=await _catalog.Client.SearchAsync(new(){Query=Query.Trim(),ResourceType=ResourceCatalog.Type(Category),GameVersion=version=="全部"?null:version,Loader=!UsesEnvironment||Environment=="全部"?null:Environment,Categories=_tagIds.TryGetValue(Tag,out var tag)?[tag]:[],Offset=(_page-1)*20,Limit=20});
            if(revision!=_revision)return;
            _total=result.TotalHits;PageItems.Clear();
            foreach(var hit in result.Hits)
            {
                if(!_entries.TryGetValue(hit.ProjectId,out var entry))_entries[hit.ProjectId]=entry=new(_catalog.FromHit(hit,Category),_catalog,_open);
                PageItems.Add(entry);_ = LoadImageAsync(entry,hit.IconUrl);
            }
            Notice="";Scroll.Reset();
        }
        catch(Exception error){if(revision==_revision){Notice=FocusMinecraftLauncher.Infrastructure.Runtime.RuntimeSupport.Error(error);}}
        finally{if(revision==_revision){_busy=false;Refresh();CommandManager.InvalidateRequerySuggested();}}
    }
    private static async Task LoadImageAsync(ResourceEntryViewModel entry,string? url)
    {entry.Project.LoadedImage=await RemoteArtwork.LoadAsync(url);entry.Refresh();}
}

/// <summary>摘要与详情共用文件选择、前置资源和展开状态，避免两处显示互不一致的依赖。</summary>
public sealed class ResourceEntryViewModel : ObservableObject
{
    private readonly ResourceCatalog _catalog;
    private readonly Action<string, bool> _open;
    private bool _expanded, _dependenciesExpanded, _loadingDetails;
    private string _dependencyError="";
    private ResourceFileItem? _selectedFile;
    public ResourceProject Project { get; private set; }
    public bool IsExpanded { get => _expanded; set {if(Set(ref _expanded,value)&&value)_ = EnsureExpandedAsync();} }
    public bool IsDependenciesExpanded { get => _dependenciesExpanded; set => Set(ref _dependenciesExpanded, value); }
    public bool HasFiles => Project.Files.Count > 0;
    public ObservableCollection<DependencyViewModel> Dependencies { get; } = [];
    public bool HasDependencies => Dependencies.Count > 0;
    public ResourceFileItem? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (ReferenceEquals(_selectedFile, value)) return;
            _selectedFile = value; RefreshDependencies();
            Changed(nameof(SelectedFile)); Changed(nameof(DependencySummary));
        }
    }
    public string DependencyStatus => _loadingDetails?"正在读取资源详情":_dependencyError.Length>0?_dependencyError:SelectedFile?.DependencyText ?? "前置信息未提供";
    public string DependencySummary => SelectedFile == null ? DependencyStatus : SelectedFile.ResourceVersion + "：" + DependencyStatus;
    public UiCommand OpenDetails { get; }
    public UiCommand ChooseVersion { get; }
    public ResourceEntryViewModel(ResourceProject project, ResourceCatalog catalog, Action<string, bool> open)
    {
        Project = project; _catalog = catalog; _open = open; _selectedFile = project.Files.FirstOrDefault();
        OpenDetails = new(_ => open(project.Id, false)); ChooseVersion = new(_ => open(project.Id, true));
        RefreshDependencies(); _dependenciesExpanded = Dependencies.Count <= 2;
    }
    public void UpdateProject(ResourceProject project) {var wanted=SelectedFile?.Id;Project=project;SelectedFile=project.Files.FirstOrDefault(file=>file.Id==wanted)??project.Files.FirstOrDefault();Refresh(); }
    private async Task EnsureExpandedAsync()
    {
        if(_loadingDetails||HasFiles)return;
        _loadingDetails=true;Changed(nameof(DependencyStatus));Changed(nameof(DependencySummary));
        try { UpdateProject(await _catalog.LoadAsync(Project.Id)); }
        catch(Exception error){_dependencyError=FocusMinecraftLauncher.Infrastructure.Runtime.RuntimeSupport.Error(error);}
        finally{_loadingDetails=false;Changed(nameof(DependencyStatus));Changed(nameof(DependencySummary));}
    }
    private async void RefreshDependencies()
    {
        var selected=SelectedFile;_dependencyError="";
        try { if(selected!=null) await _catalog.EnsureDependenciesAsync(selected); }
        catch(Exception error) { _dependencyError="前置资料加载失败，请重新选择文件重试。"; System.Diagnostics.Trace.TraceWarning("前置资料读取失败：{0}", error.GetType().Name); }
        if(!ReferenceEquals(selected,SelectedFile))return;
        Dependencies.Clear();
        foreach (var dependency in SelectedFile?.EffectiveDependencies ?? [])
            if (_catalog.Find(dependency.ProjectId) is { } project) Dependencies.Add(new(project, dependency, _open));
        Changed(nameof(HasDependencies)); Changed(nameof(DependencyStatus));Changed(nameof(DependencySummary));
    }
}
