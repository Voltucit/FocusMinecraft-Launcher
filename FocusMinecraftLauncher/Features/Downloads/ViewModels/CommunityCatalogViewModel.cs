using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Threading;
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
    private readonly Dictionary<string,int> _sourceTagIds=[];
    private string _query="",_source="全部",_tag="全部",_environment="全部",_summary="全部条件",_notice="";
    private bool _searchExpanded=true,_busy,_loaded;
    private int _page=1,_revision,_pageCount=1;
    private long _total;
    private double _nameFieldWidth=300;
    public const string AllSources="全部";
    private const int PageSize=20;
    public DownloadSection Category { get; }
    public ObservableCollection<ResourceEntryViewModel> PageItems { get; }=[];
    public DownloadScrollPosition Scroll { get; }=new();
    public GameVersionFilterViewModel VersionFilter { get; private set; }
    public string[] Sources { get; }=[AllSources,"Modrinth","CurseForge"];
    public string[] Tags { get; private set; }=["全部"];
    public string[] Environments { get; }
    public bool UsesEnvironment=>Category is DownloadSection.Mods or DownloadSection.Modpacks or DownloadSection.Shaders;
    public string EnvironmentTitle=>Category==DownloadSection.Shaders?"运行环境":"加载器";
    public string Query { get=>_query;set=>Set(ref _query,value); }
    public string Source { get=>_source;set {if(!Set(ref _source,string.IsNullOrWhiteSpace(value)?AllSources:value))return;_page=1;_ = SwitchSourceAsync();} }
    public string Tag { get=>_tag;set=>Set(ref _tag,value??"全部"); }
    public string Environment { get=>_environment;set=>Set(ref _environment,value??"全部"); }
    public bool IsSearchExpanded { get=>_searchExpanded;set {if(Set(ref _searchExpanded,value))Changed(nameof(FilterToggleText));} }
    public string FilterToggleText=>IsSearchExpanded?"收起筛选":"展开筛选";
    public string SearchSummary=>_summary;
    /// <summary>版本清单只在首次进入时拉取一次，五个分类共用，避免每次切分类都等一遍官方接口。</summary>
    private static Task<IReadOnlyList<FMCore.Installer.MinecraftInstallVersion>>? _versions;
    public string Notice { get=>_notice;private set=>Set(ref _notice,value); }
    public double NameFieldWidth { get=>_nameFieldWidth;set=>Set(ref _nameFieldWidth,value); }
    public int PageCount=>_pageCount;
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
        Reset=new(async _=>{Query="";Tag=Environment="全部";if(!VersionFilter.IsAllSelected)VersionFilter.Select("全部");if(Source!=AllSources)Source=AllSources;else{_page=1;await QueryAsync();}},_=>!_busy);
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
            // 分类标签与版本清单互不依赖，并行拉取；版本清单全局只取一次。
            var tagsTask=_catalog.Client.GetCategoriesAsync(ResourceCatalog.Type(Category));
            var versionsTask=VersionsAsync();
            await Task.WhenAll(tagsTask,versionsTask);
            foreach(var tag in await tagsTask)_tagIds[tag.DisplayName]=tag.Name;
            Tags=["全部",.._tagIds.Keys];Changed(nameof(Tags));
            var versions=await versionsTask;
            VersionFilter=new(versions.Where(v=>v.Type=="release").Select(v=>v.Id),()=>{_page=1;_ = QueryAsync();});Changed(nameof(VersionFilter));
        }
        catch(Exception error){_loaded=false;Notice=FocusMinecraftLauncher.Infrastructure.Runtime.RuntimeSupport.Error(error);}
        await QueryAsync();
    }
    private static Task<IReadOnlyList<FMCore.Installer.MinecraftInstallVersion>> VersionsAsync()
    {
        var pending=_versions;
        if(pending==null||pending.IsFaulted||pending.IsCanceled)_versions=pending=FMCore.Installer.MinecraftInstaller.GetVersionsAsync();
        return pending;
    }
    /// <summary>切换资源来源时同步换掉标签列表：两个站的分类体系完全不同，不能互用。</summary>
    private async Task SwitchSourceAsync()
    {
        if(Source=="CurseForge")
        {
            await EnsureCurseTagsAsync();
            Tags=["全部",.._sourceTagIds.Keys];
        }
        else Tags=["全部",.._tagIds.Keys];
        Changed(nameof(Tags));
        Tag="全部";
        await QueryAsync();
    }
    private async Task EnsureCurseTagsAsync()
    {
        if(_sourceTagIds.Count>0)return;
        try
        {
            foreach(var category in await _catalog.CurseForge.GetCategoriesAsync(Category,CancellationToken.None))_sourceTagIds[category.Name]=category.Id;
        }
        catch(Exception error){Notice=FocusMinecraftLauncher.Infrastructure.Runtime.RuntimeSupport.Error(error);}
    }
    /// <summary>只有两个站都认得同一个分类名时才能把标签同时套用，否则该站的查询不加分类。</summary>
    private int? SourceTagId() => _sourceTagIds.TryGetValue(Tag,out var id)?id:null;
    /// <summary>把实例的版本与加载器落到筛选控件上；控件不认识的值会被忽略，随后照常查询。</summary>
    public void ApplyFilter(string gameVersion, string loader)
    {
        if(loader.Length>0 && UsesEnvironment && Environments.Contains(loader,StringComparer.OrdinalIgnoreCase)) Environment=loader;
        var known=VersionFilter.Series.SelectMany(series=>series.Versions).Any(choice=>choice.Number.Equals(gameVersion,StringComparison.OrdinalIgnoreCase));
        // 选中版本会自己触发查询；版本不可选或已经是当前值时补一次查询，让加载器条件也生效。
        if(known && !VersionFilter.SelectedVersion.Equals(gameVersion,StringComparison.OrdinalIgnoreCase)){VersionFilter.Select(gameVersion);return;}
        _page=1;_ = QueryAsync();
    }
    private async Task QueryAsync()
    {
        var revision=++_revision;_busy=true;Notice="正在加载资源";Refresh();CommandManager.InvalidateRequerySuggested();
        var version=VersionFilter.SelectedVersion;
        _summary=string.Join(" / ",new[]{Query,version=="全部"?"":version,Tag=="全部"?"":Tag,Environment=="全部"?"":Environment}.Where(s=>s.Length>0));
        if(_summary.Length==0)_summary="全部条件";Changed(nameof(SearchSummary));
        try
        {
            var gameVersion=version=="全部"?null:version;
            var loader=!UsesEnvironment||Environment=="全部"?null:Environment;
            var keyword=Query.Trim();
            var categoryIds=_tagIds.TryGetValue(Tag,out var tagName)?new[]{tagName}:Array.Empty<string>();
            if(Source=="CurseForge")
            {
                var page=await _catalog.SearchCurseForgeAsync(Category,keyword,gameVersion,loader,(_page-1)*PageSize,PageSize,CancellationToken.None,SourceTagId());
                if(revision!=_revision)return;
                _total=page.Total;_pageCount=Pages(_total,PageSize);
                Present(page.Items.Select(project=>(project,project.IconUrl)).ToArray());
                return;
            }
            // 全部来源：两个站并行查，Modrinth 先到就先显示，CurseForge 经镜像转发往往慢很多，不该让界面干等。
            if(Source==AllSources)
            {
                if(Tag!="全部"&&_sourceTagIds.Count==0)_ = EnsureCurseTagsAsync();
                var half=PageSize/2;var offset=(_page-1)*half;
                var modrinthTask=_catalog.Client.SearchAsync(new(){Query=keyword,ResourceType=ResourceCatalog.Type(Category),GameVersion=gameVersion,Loader=loader,Categories=categoryIds,Offset=offset,Limit=half});
                var curseForgeTask=_catalog.SearchCurseForgeAsync(Category,keyword,gameVersion,loader,offset,half,CancellationToken.None,SourceTagId());
                // 超时后不再等待它，但异常仍需观察，避免拖到进程退出时才报。
                _ = curseForgeTask.ContinueWith(task => task.Exception?.Handle(_ => true),CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
                FMCore.ModsResources.ModrinthSearchResult? modrinth=null;
                try{modrinth=await modrinthTask;}
                catch(Exception error) when (error is not OperationCanceledException)
                {System.Diagnostics.Trace.TraceWarning("Modrinth 搜索失败：{0}",error.GetType().Name);}
                if(revision!=_revision)return;
                var hits=modrinth?.TotalHits??0;
                if(modrinth!=null)
                {
                    _total=hits;_pageCount=Pages(hits,half);
                    Present(modrinth.Hits.Select(hit=>(_catalog.FromHit(hit,Category),(string?)hit.IconUrl)).ToArray());
                    Notice="正在读取 CurseForge";
                    // 先放开交互：列表已经可用，CurseForge 的结果到了再追加。
                    _busy=false;Refresh();CommandManager.InvalidateRequerySuggested();
                }
                (IReadOnlyList<ResourceProject> Items,long Total) curseForge=([],0);
                try
                {
                    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    curseForge=await curseForgeTask.WaitAsync(timeout.Token);
                }
                catch(Exception error) when (error is TimeoutException or HttpRequestException or InvalidDataException or IOException or System.Threading.Tasks.TaskCanceledException)
                {System.Diagnostics.Trace.TraceWarning("CurseForge 搜索失败：{0}",error.GetType().Name);}
                if(revision!=_revision)return;
                if(curseForge.Items.Count>0)
                {
                    Append(curseForge.Items.Select(project=>(project,project.IconUrl)).ToArray());
                    _total=hits+curseForge.Total;
                    _pageCount=Math.Max(1,Math.Max(Pages(hits,half),Pages(curseForge.Total,half)));
                    Notice="";
                }
                else if(modrinth==null) Notice="没有取到资源，请稍后重试";
                Refresh();CommandManager.InvalidateRequerySuggested();
                return;
            }
            var result=await _catalog.Client.SearchAsync(new(){Query=keyword,ResourceType=ResourceCatalog.Type(Category),GameVersion=gameVersion,Loader=loader,Categories=categoryIds,Offset=(_page-1)*PageSize,Limit=PageSize});
            if(revision!=_revision)return;
            _total=result.TotalHits;_pageCount=Pages(_total,PageSize);
            Present(result.Hits.Select(hit=>(_catalog.FromHit(hit,Category),(string?)hit.IconUrl)).ToArray());
        }
        catch(Exception error){if(revision==_revision){Notice=FocusMinecraftLauncher.Infrastructure.Runtime.RuntimeSupport.Error(error);}}
        finally{if(revision==_revision){_busy=false;Refresh();CommandManager.InvalidateRequerySuggested();}}
    }
    private static int Pages(long total,int size)=>(int)Math.Max(1,(total+size-1)/size);
    /// <summary>同一个资源在两个站各有一份时只显示一次，避免列表里出现两行一样的项目。</summary>
    private void Present(IReadOnlyList<(ResourceProject Project,string? Icon)> items)
    {
        PageItems.Clear();
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var (project,icon) in items)
        {
            if(!seen.Add(project.Name.Trim()))continue;
            if(!_entries.TryGetValue(project.Id,out var entry))_entries[project.Id]=entry=new(project,_catalog,_open);
            PageItems.Add(entry);
            if(project.LoadedImage==null)_ = LoadImageAsync(entry,icon);
        }
        Notice="";Scroll.Reset();
    }
    private static async Task LoadImageAsync(ResourceEntryViewModel entry,string? url)
    {entry.Project.LoadedImage=await RemoteArtwork.LoadAsync(url,160);entry.Refresh();}
    /// <summary>慢源的结果到达后追加到已有列表，不打断用户已经看到的条目。</summary>
    private void Append(IReadOnlyList<(ResourceProject Project,string? Icon)> items)
    {
        var seen=new HashSet<string>(PageItems.Select(entry=>entry.Project.Name.Trim()),StringComparer.OrdinalIgnoreCase);
        foreach(var (project,icon) in items)
        {
            if(!seen.Add(project.Name.Trim()))continue;
            if(!_entries.TryGetValue(project.Id,out var entry))_entries[project.Id]=entry=new(project,_catalog,_open);
            PageItems.Add(entry);
            if(project.LoadedImage==null)_ = LoadImageAsync(entry,icon);
        }
    }
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
            _selectedFile = value;
            if (_expanded) _ = ResolveDependenciesAsync();
            Changed(nameof(SelectedFile));
        }
    }
    public string DependencyStatus => _loadingDetails?"正在读取资源详情":_dependencyError.Length>0?_dependencyError:SelectedFile?.DependencyText ?? "前置信息未提供";
    public UiCommand OpenDetails { get; }
    public UiCommand ChooseVersion { get; }
    public ResourceEntryViewModel(ResourceProject project, ResourceCatalog catalog, Action<string, bool> open)
    {
        Project = project; _catalog = catalog; _open = open; _selectedFile = project.Files.FirstOrDefault();
        OpenDetails = new(_ => open(project.Id, false)); ChooseVersion = new(_ => open(project.Id, true));
        _dependenciesExpanded = false;
    }
    public void UpdateProject(ResourceProject project) {var wanted=SelectedFile?.Id;Project=project;SelectedFile=project.Files.FirstOrDefault(file=>file.Id==wanted)??project.Files.FirstOrDefault();Refresh(); }
    private async Task EnsureExpandedAsync()
    {
        if(_loadingDetails)return;
        _loadingDetails=true;Changed(nameof(DependencyStatus));
        try
        {
            // 展开时才补详情与前置，列表阶段只显示搜索返回的摘要，避免一页几十次网络请求。
            if(!HasFiles)UpdateProject(await _catalog.LoadAsync(Project.Id));
            await ResolveDependenciesAsync();
        }
        catch(Exception error){_dependencyError=FocusMinecraftLauncher.Infrastructure.Runtime.RuntimeSupport.Error(error);}
        finally{_loadingDetails=false;Changed(nameof(DependencyStatus));}
    }
    private async Task ResolveDependenciesAsync()
    {
        var selected=SelectedFile;_dependencyError="";
        try { if(selected!=null) await _catalog.EnsureDependenciesAsync(selected); }
        catch(Exception error) { _dependencyError="前置资料加载失败，请重新选择文件重试。"; System.Diagnostics.Trace.TraceWarning("前置资料读取失败：{0}", error.GetType().Name); }
        if(!ReferenceEquals(selected,SelectedFile))return;
        Dependencies.Clear();
        foreach (var dependency in SelectedFile?.EffectiveDependencies ?? [])
            if (_catalog.Find(dependency.ProjectId) is { } project) Dependencies.Add(new(project, dependency, _open));
        _dependenciesExpanded = Dependencies.Count <= 2;
        Changed(nameof(HasDependencies)); Changed(nameof(DependencyStatus));
    }
}
