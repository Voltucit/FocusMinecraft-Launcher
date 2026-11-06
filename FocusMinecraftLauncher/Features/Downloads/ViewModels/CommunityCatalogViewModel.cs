using System.Collections.ObjectModel;
using System.Threading;
using System.Windows.Input;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Features.Downloads.Models;
using FocusMinecraftLauncher.Infrastructure.Runtime;

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
    private bool _searchExpanded=true,_busy,_loaded,_versionsLoaded,_tagsLoaded,_queryFailed,_partialFailure,_updatingFilters;
    private Task? _loadTask,_filterTask,_curseTagsTask;
    private int _page=1,_revision,_pageCount=1;
    private long _total;
    private double _nameFieldWidth=300;
    public const string AllSources="全部";
    private const int PageSize=20;
    public DownloadSection Category { get; }
    public ObservableCollection<ResourceEntryViewModel> PageItems { get; }=[];
    public DownloadScrollPosition Scroll { get; }=new();
    public GameVersionFilterViewModel VersionFilter { get; }
    public string[] Sources { get; }=[AllSources,"Modrinth","CurseForge"];
    public string[] Tags { get; private set; }=["全部"];
    public string[] Environments { get; }
    public bool UsesEnvironment=>Category is DownloadSection.Mods or DownloadSection.Modpacks or DownloadSection.Shaders;
    public string EnvironmentTitle=>Category==DownloadSection.Shaders?"运行环境":"加载器";
    public string Query { get=>_query;set=>Set(ref _query,value); }
    public string Source { get=>_source;set {if(!Set(ref _source,string.IsNullOrWhiteSpace(value)?AllSources:value)||_updatingFilters)return;_page=1;_ = SwitchSourceAsync();} }
    public string Tag { get=>_tag;set=>Set(ref _tag,value??"全部"); }
    public string Environment { get=>_environment;set=>Set(ref _environment,value??"全部"); }
    public bool IsSearchExpanded { get=>_searchExpanded;set {if(Set(ref _searchExpanded,value))Changed(nameof(FilterToggleText));} }
    public string FilterToggleText=>IsSearchExpanded?"收起筛选":"展开筛选";
    public string SearchSummary=>_summary;
    /// <summary>版本清单只在首次进入时拉取一次，五个分类共用，避免每次切分类都等一遍官方接口。</summary>
    private static Task<IReadOnlyList<FMCore.Installer.MinecraftInstallVersion>>? _versions;
    /// <summary>分类标签与列表一样共享正在执行的请求，避免预热未结束时重复拉取。</summary>
    private static readonly Dictionary<string,(DateTime At,Task<FMCore.ModsResources.ModrinthCategory[]> Load)> _tagCache=[];
    private static readonly DownloadSection[] WarmSections=[DownloadSection.Mods,DownloadSection.Modpacks,DownloadSection.Datapacks,DownloadSection.Resourcepacks,DownloadSection.Shaders];
    /// <summary>按来源与精确条件缓存请求；页面可直接复用预热中的任务，也可独立呈现先返回的一方。</summary>
    private static readonly Dictionary<string,(DateTime At,Task<SourcePage> Load)> _queryCache=[];
    private static readonly TimeSpan CacheLifetime=TimeSpan.FromMinutes(10);
    /// <summary>列表页落盘三小时，启动后第一次打开下载页直接有内容，不必再等网络。</summary>
    private static readonly TimeSpan DiskPageLifetime=TimeSpan.FromHours(3);
    private const int CacheLimit=120;
    /// <summary>详情与前置资源预读的并发上限；给搜索、翻译等当前操作留出请求名额。</summary>
    private static readonly SemaphoreSlim DetailSlots=new(4,4);
    private readonly HashSet<string> _detailPrefetching=[];
    private bool _active=true;
    private int _detailDone,_detailTotal,_detailProgressRevision;
    private sealed record SourcePage(IReadOnlyList<(ResourceProject Project,string? Icon)> Items,long Total);
    private sealed record SourceResult(string Source,SourcePage? Page,string Error);
    public string Notice { get=>_notice;private set=>Set(ref _notice,value); }
    public double NameFieldWidth { get=>_nameFieldWidth;set=>Set(ref _nameFieldWidth,value); }
    public int PageCount=>_pageCount;
    public int Page=>_page;
    public bool IsEmpty=>!_busy&&!_queryFailed&&PageItems.Count==0;
    public string PageText=>_busy?$"第 {_page} 页，正在加载"
        :$"第 {_page} / {_pageCount} 页，{(_partialFailure?"已获取":"共")} {_total} 项";
    public bool IsDetailLoading=>_detailTotal>0&&_detailDone<_detailTotal;
    public double DetailProgress=>_detailTotal==0?0:Math.Clamp(100d*_detailDone/_detailTotal,0,100);
    public string DetailProgressText=>IsDetailLoading?$"正在加载资源详情 {_detailDone} / {_detailTotal}（{DetailProgress:0}%）":"";
    public UiCommand Search { get; }
    public UiCommand Reset { get; }
    public UiCommand Reload { get; }
    public UiCommand ToggleFilters { get; }
    public UiCommand PreviousPage { get; }
    public UiCommand NextPage { get; }
    public CommunityCatalogViewModel(DownloadSection category,ResourceCatalog catalog,Dictionary<string,ResourceEntryViewModel> entries,Action<string,bool> open)
    {
        Category=category;_catalog=catalog;_entries=entries;_open=open;
        Environments=category==DownloadSection.Shaders?["全部","iris","optifine"]:["全部","fabric","forge","neoforge","quilt"];
        VersionFilter=new([],()=>{if(_updatingFilters)return;_page=1;_ = QueryAsync();});
        Search=new(async _=>{_page=1;await QueryAsync();});
        Reset=new(async _=>
        {
            _updatingFilters=true;
            try{Query="";Tag=Environment="全部";VersionFilter.Select("全部");Source=AllSources;}
            finally{_updatingFilters=false;}
            UpdateTags();_page=1;await QueryAsync();
        });
        // 手动刷新不复用旧结果；旧请求即使随后完成，也不会覆盖本次查询。
        Reload=new(async _=>{_queryCache.Clear();_page=1;await QueryAsync(true);});
        ToggleFilters=new(_=>IsSearchExpanded=!IsSearchExpanded);
        PreviousPage=new(async _=>{_page--;await QueryAsync();},_=>!_busy&&_page>1);
        NextPage=new(async _=>{_page++;await QueryAsync();},_=>!_busy&&_page<PageCount);
    }
    /// <summary>列表立即查询，筛选资料在后台独立补齐；再次进入时保留已有列表和滚动位置。</summary>
    public Task EnsureLoadedAsync()
    {
        if(!_loaded){_loaded=true;_loadTask=QueryAsync();}
        if(_filterTask==null||(_filterTask.IsCompleted&&(!_versionsLoaded||!_tagsLoaded)))
            _filterTask=Task.WhenAll(LoadVersionsAsync(),LoadModrinthTagsAsync());
        return _loadTask??Task.CompletedTask;
    }
    /// <summary>离开分类时停止继续预读；重新进入时补齐当前页尚未加载的详情。</summary>
    public void SetActive(bool active)
    {
        if(_active==active)return;
        _active=active;
        if(active)QueueDetailPrefetch(PageItems);
        else ResetDetailProgress();
    }
    private async Task LoadVersionsAsync()
    {
        if(_versionsLoaded)return;
        try
        {
            var versions=await VersionsAsync();
            VersionFilter.UpdateVersions(versions.Where(v=>v.Type=="release").Select(v=>v.Id));
            _versionsLoaded=true;
        }
        catch(Exception error){System.Diagnostics.Trace.TraceWarning("社区版本筛选资料加载失败：{0}",error.GetType().Name);}
    }
    private async Task LoadModrinthTagsAsync()
    {
        if(_tagsLoaded)return;
        try
        {
            foreach(var tag in await TagsAsync(_catalog.Client,Category))_tagIds[tag.DisplayName]=tag.Name;
            _tagsLoaded=true;
            if(Source!="CurseForge")UpdateTags();
        }
        catch(Exception error){System.Diagnostics.Trace.TraceWarning("社区分类资料加载失败：{0}",error.GetType().Name);}
    }
    private static Task<IReadOnlyList<FMCore.Installer.MinecraftInstallVersion>> VersionsAsync()
    {
        var pending=_versions;
        if(pending==null||pending.IsFaulted||pending.IsCanceled)_versions=pending=FMCore.Installer.MinecraftInstaller.GetVersionsAsync();
        return pending;
    }
    /// <summary>后台加载分类标签；失败请求不复用，且不占用全部前台查询名额。</summary>
    private static Task<FMCore.ModsResources.ModrinthCategory[]> TagsAsync(FMCore.ModsResources.ModrinthClient client,DownloadSection section)
    {
        var key=CommunitySource.Current.Name+"|"+section;
        if(_tagCache.TryGetValue(key,out var cached)&&!cached.Load.IsFaulted&&!cached.Load.IsCanceled
            &&(!cached.Load.IsCompleted||DateTime.UtcNow-cached.At<TimeSpan.FromMinutes(30)))return cached.Load;
        var load=MirrorGate.RunAsync(async ()=>(await client.GetCategoriesAsync(ResourceCatalog.Type(section))).ToArray(),background:true);
        _tagCache[key]=(DateTime.UtcNow,load);
        return load;
    }
    /// <summary>启动即低并发预热，优先 Mod；逐项启动请求，避免尚未浏览的分类挤满查询队列。</summary>
    public static async Task PrefetchAsync()
    {
        using var catalog=new ResourceCatalog();
        _ = Quiet(VersionsAsync());
        foreach(var section in WarmSections)
        {
            foreach(var source in new[]{"Modrinth","CurseForge"})
                await Quiet(SourcePageAsync(catalog,section,source,"",null,null,[],null,0,PageSize/2,true));
            await Quiet(TagsAsync(catalog.Client,section));
        }
    }
    /// <summary>预热里的单个请求失败不影响其他类别，失败原因只写到跟踪日志。</summary>
    private static async Task Quiet(Task task)
    {
        try{await task;}
        catch(Exception error)
        {System.Diagnostics.Trace.TraceWarning("社区资源预热失败：{0}",error.GetType().Name);}
    }
    /// <summary>切源立即查询；分类标签在后台更新，不成为显示列表的前置条件。</summary>
    private async Task SwitchSourceAsync()
    {
        Tag="全部";
        UpdateTags();
        if(Source=="CurseForge")_ = UpdateCurseTagsAsync();
        await QueryAsync();
    }
    private void UpdateTags()
    {
        Tags=Source=="CurseForge"?["全部",.._sourceTagIds.Keys]:["全部",.._tagIds.Keys];
        Changed(nameof(Tags));
    }
    private async Task UpdateCurseTagsAsync()
    {
        await EnsureCurseTagsAsync();
        if(Source=="CurseForge")UpdateTags();
    }
    private Task EnsureCurseTagsAsync()
    {
        if(_sourceTagIds.Count>0)return Task.CompletedTask;
        if(_curseTagsTask==null||_curseTagsTask.IsCompleted)_curseTagsTask=LoadCurseTagsAsync();
        return _curseTagsTask;
    }
    private async Task LoadCurseTagsAsync()
    {
        try
        {
            foreach(var category in await _catalog.CurseForge.GetCategoriesAsync(Category,CancellationToken.None,background:true))_sourceTagIds[category.Name]=category.Id;
        }
        catch(Exception error){System.Diagnostics.Trace.TraceWarning("CurseForge 分类资料加载失败：{0}",error.GetType().Name);}
    }
    /// <summary>将游戏入口的条件一次性落到可见控件；版本清单尚未加载时也准确显示所选版本。</summary>
    public void ApplyFilter(string gameVersion, string loader)
    {
        Environment=UsesEnvironment?Environments.FirstOrDefault(item=>item.Equals(loader,StringComparison.OrdinalIgnoreCase))??"全部":"全部";
        var version=string.IsNullOrWhiteSpace(gameVersion)?"全部":gameVersion;
        if(version!="全部")VersionFilter.UpdateVersions(VersionFilter.Series.SelectMany(series=>series.Versions).Select(choice=>choice.Number).Append(version));
        // 版本变化会自动查询；版本未变时补一次查询，让加载器条件也生效。
        if(!VersionFilter.SelectedVersion.Equals(version,StringComparison.OrdinalIgnoreCase)){VersionFilter.Select(version);return;}
        _page=1;_ = QueryAsync();
    }
    /// <summary>两个来源同时查询，逐个呈现返回结果；旧查询只能完成自己的请求，不能覆盖新条件。</summary>
    private async Task QueryAsync(bool fresh=false)
    {
        var revision=++_revision;
        var version=VersionFilter.SelectedVersion;
        var keyword=Query.Trim();
        var tag=Tag;
        var gameVersion=version=="全部"?null:version;
        var loader=!UsesEnvironment||Environment=="全部"?null:Environment;
        var categoryIds=_tagIds.TryGetValue(tag,out var tagName)?new[]{tagName}:Array.Empty<string>();
        var sources=Source==AllSources?new[]{"Modrinth","CurseForge"}:new[]{Source};
        var limit=sources.Length==2?PageSize/2:PageSize;
        var offset=(_page-1)*limit;
        _summary=string.Join(" / ",new[]{Query,gameVersion??"",tag=="全部"?"":tag,loader??""}.Where(value=>value.Length>0));
        if(_summary.Length==0)_summary="全部条件";
        Changed(nameof(SearchSummary));
        _busy=true;_queryFailed=false;_partialFailure=false;_total=0;_pageCount=1;
        ResetDetailProgress();
        // 翻页只显示当前页的结果：清空旧页并回到列表顶部，不再把新页追加到旧内容后面。
        PageItems.Clear();Scroll.Reset();
        Notice="正在加载资源";
        Refresh();CommandManager.InvalidateRequerySuggested();
        var pages=new List<SourcePage>();
        var errors=new List<string>();
        var finished=new HashSet<string>();
        var pending=sources.Select(source=>ReceiveAsync(source,source=="CurseForge"
            ?CursePageAsync(keyword,gameVersion,loader,tag,offset,limit,fresh)
            :SourcePageAsync(_catalog,Category,source,keyword,gameVersion,loader,categoryIds,null,offset,limit,false,fresh))).ToList();
        try
        {
            while(pending.Count>0)
            {
                var completed=await Task.WhenAny(pending);
                var result=await completed;
                pending.Remove(completed);
                if(revision!=_revision)return;
                finished.Add(result.Source);
                if(result.Page is {} page)pages.Add(page);
                else errors.Add(result.Source+" 加载失败："+result.Error);
                _total=pages.Sum(page=>page.Total);
                _pageCount=pages.Count==0?1:pages.Max(page=>Pages(page.Total,limit));
                // 后到的来源并入当前页，保留已展开条目的状态，不把两页内容混在一起。
                Present(pages.SelectMany(page=>page.Items).ToArray());
                _busy=pending.Count>0;
                _queryFailed=!_busy&&pages.Count==0;
                _partialFailure=errors.Count>0;
                var waiting=_busy?"正在加载 "+string.Join(" / ",sources.Where(source=>!finished.Contains(source))):"";
                Notice=string.Join("；",new[]{waiting,string.Join("；",errors)}.Where(text=>text.Length>0));
                Refresh();CommandManager.InvalidateRequerySuggested();
            }
        }
        catch(Exception error)
        {
            if(revision==_revision){_queryFailed=PageItems.Count==0;Notice=RuntimeSupport.Error(error);}
        }
        finally
        {
            if(revision==_revision){_busy=false;Refresh();CommandManager.InvalidateRequerySuggested();QueueDetailPrefetch(PageItems);}
        }
    }
    /// <summary>只有需要分类筛选的 CurseForge 请求才等待分类编号，其他来源不受影响。</summary>
    private async Task<SourcePage> CursePageAsync(string keyword,string? version,string? loader,string tag,int offset,int limit,bool fresh=false)
    {
        if(tag!="全部")await EnsureCurseTagsAsync();
        int? categoryId=_sourceTagIds.TryGetValue(tag,out var id)?id:null;
        return await SourcePageAsync(_catalog,Category,"CurseForge",keyword,version,loader,[],categoryId,offset,limit,false,fresh);
    }
    /// <summary>观察每个来源的异常，保留可用结果；一个来源失败不被解释成所有来源均无结果。</summary>
    private static async Task<SourceResult> ReceiveAsync(string source,Task<SourcePage> request)
    {
        try{return new(source,await request,"");}
        catch(Exception error)
        {
            System.Diagnostics.Trace.TraceWarning("社区资源 {0} 查询失败：{1}",source,error.GetType().Name);
            return new(source,null,RuntimeSupport.Error(error));
        }
    }
    private static int Pages(long total,int size)=>(int)Math.Max(1,(total+size-1)/size);
    /// <summary>成功结果与在途请求共用缓存键，覆盖来源、类别、分页及所有实际查询条件。</summary>
    private static Task<SourcePage> SourcePageAsync(ResourceCatalog catalog,DownloadSection section,string source,string keyword,
        string? version,string? loader,string[] categories,int? categoryId,int offset,int limit,bool background=false,bool fresh=false)
    {
        var key=string.Join('\x1f',CommunitySource.Current.Name,section,source,keyword,version??"",loader??"",
            string.Join(',',categories.Order(StringComparer.Ordinal)),categoryId?.ToString()??"",offset,limit);
        if(_queryCache.TryGetValue(key,out var cached)&&!cached.Load.IsFaulted&&!cached.Load.IsCanceled
            &&(!cached.Load.IsCompleted||DateTime.UtcNow-cached.At<CacheLifetime))return cached.Load;
        // 手动刷新时跳过磁盘，直接问网络；其它情况先看六小时内的落盘结果。
        if(!fresh&&ReadDisk(key) is {} disk)
        {
            var restored=Task.FromResult(disk);
            _queryCache[key]=(DateTime.UtcNow,restored);
            return restored;
        }
        // 仅淘汰已完成项，避免刷新缓存时丢掉正在被页面和预热共同等待的请求。
        if(_queryCache.Count>=CacheLimit)
            foreach(var old in _queryCache.Where(pair=>pair.Value.Load.IsCompleted).OrderBy(pair=>pair.Value.At)
                .Take(_queryCache.Count-CacheLimit+1).Select(pair=>pair.Key).ToArray())_queryCache.Remove(old);
        var load=LoadSourcePageAsync(catalog,section,source,keyword,version,loader,categories,categoryId,offset,limit,background);
        _queryCache[key]=(DateTime.UtcNow,load);
        // 成功后写盘；失败不写，避免把一次超时当成六小时的结论。
        load.ContinueWith(task =>
        {
            if(task.Status!=TaskStatus.RanToCompletion)return;
            CommunityCache.WriteText("page2:"+key,CachedPageList.Serialize(task.Result.Items,task.Result.Total));
        },TaskScheduler.Default);
        return load;
    }
    /// <summary>读回一页列表；结构不对或文件损坏时当作没有缓存。</summary>
    private static SourcePage? ReadDisk(string key)
    {
        // 旧缓存没有保存 Modrinth 图标地址，升级后按新键重新查询一次。
        var json=CommunityCache.ReadText("page2:"+key,DiskPageLifetime);
        if(json is null)return null;
        var page=CachedPageList.Deserialize(json);
        return page is null?null:new SourcePage(page.Items.Select(item=>(item.ToProject(),item.IconUrl)).ToArray(),page.Total);
    }
    private static async Task<SourcePage> LoadSourcePageAsync(ResourceCatalog catalog,DownloadSection section,string source,string keyword,
        string? version,string? loader,string[] categories,int? categoryId,int offset,int limit,bool background)
    {
        if(source=="CurseForge")
        {
            var page=await catalog.SearchCurseForgeAsync(section,keyword,version,loader,offset,limit,CancellationToken.None,categoryId,background);
            return new(page.Items.Select(project=>(project,project.IconUrl)).ToArray(),page.Total);
        }
        var client=catalog.Client;
        var result=await MirrorGate.RunAsync(()=>client.SearchAsync(new()
            {Query=keyword,ResourceType=ResourceCatalog.Type(section),GameVersion=version,Loader=loader,Categories=categories,Offset=offset,Limit=limit}),background:background);
        return new(result.Hits.Select(hit=>(catalog.FromHit(hit,section),(string?)hit.IconUrl)).ToArray(),result.TotalHits);
    }
    /// <summary>页面列出后，在后台按顺序补齐每行的文件、详情与默认版本的前置资源；用户展开时直接复用。</summary>
    private void QueueDetailPrefetch(IEnumerable<ResourceEntryViewModel> entries)
    {
        if(!_active||_busy)return;
        var revision=_revision;
        var pending=new List<ResourceEntryViewModel>();
        foreach(var entry in entries)
            if(_detailPrefetching.Add(entry.Project.Id))pending.Add(entry);
        if(pending.Count==0)return;
        var progressRevision=++_detailProgressRevision;
        _detailDone=0;_detailTotal=pending.Count;
        NotifyDetailProgress();
        foreach(var entry in pending)_ = PrefetchDetailAsync(entry,revision,progressRevision);
    }
    private void ResetDetailProgress()
    {
        _detailProgressRevision++;
        _detailDone=0;_detailTotal=0;
        NotifyDetailProgress();
    }
    private void NotifyDetailProgress()
    {
        Changed(nameof(IsDetailLoading));Changed(nameof(DetailProgress));Changed(nameof(DetailProgressText));
    }
    /// <summary>预读文件、详情与默认版本的前置资源；用户改选其它版本时仍按需查询。</summary>
    private async Task PrefetchDetailAsync(ResourceEntryViewModel entry,int revision,int progressRevision)
    {
        var id=entry.Project.Id;
        try
        {
            await DetailSlots.WaitAsync();
            try
            {
                if(!_active||revision!=_revision)return;
                var project=_catalog.IsLoaded(id)?_catalog.Find(id):await _catalog.LoadAsync(id);
                if(project is null)return;
                if(!_active||revision!=_revision)return;
                if(!ReferenceEquals(entry.Project,project))entry.UpdateProject(project);
                if(entry.SelectedFile is {} file)await _catalog.EnsureDependenciesAsync(file);
            }
            finally{DetailSlots.Release();}
        }
        catch(Exception error)
        {
            System.Diagnostics.Trace.TraceWarning("资源详情预读失败：{0}",error.GetType().Name);
        }
        finally
        {
            _detailPrefetching.Remove(id);
            if(progressRevision==_detailProgressRevision){_detailDone++;NotifyDetailProgress();}
        }
    }
    /// <summary>同一个资源在两个站各有一份时只显示一次，避免列表里出现两行一样的项目。</summary>
    private void Present(IReadOnlyList<(ResourceProject Project,string? Icon)> items)
    {
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added=new List<ResourceProject>();
        var index=0;
        foreach(var (project,icon) in items)
        {
            if(!seen.Add(project.Name.Trim()))continue;
            if(!_entries.TryGetValue(project.Id,out var entry))_entries[project.Id]=entry=new(project,_catalog,_open);
            var previous=PageItems.IndexOf(entry);
            if(previous<0)
            {
                PageItems.Insert(index,entry);
                added.Add(entry.Project);
                if(entry.Project.LoadedImage==null)_ = LoadImageAsync(entry,icon);
            }
            else if(previous!=index)PageItems.Move(previous,index);
            index++;
        }
        while(PageItems.Count>index)PageItems.RemoveAt(PageItems.Count-1);
        _ = TranslateAsync(added);
        _ = LookupNamesAsync(added);
    }
    /// <summary>中文名要按百科的节奏慢慢查，只在后台补齐，取到一条就替换一行。</summary>
    private async Task LookupNamesAsync(IReadOnlyList<ResourceProject> projects)
    {
        var wanted = projects
            .Where(project => project.ChineseName is null)
            .Select(project => (Key: project.Id, Title: project.Name))
            .DistinctBy(item => item.Key)
            .ToArray();
        if (wanted.Length == 0) return;
        var names = await WikiNameService.LookupAsync(wanted);
        foreach (var (id, name) in names)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.Project.ChineseName is not null) continue;
            entry.Project.ChineseName = name;
            entry.Refresh();
        }
    }
    /// <summary>列表先用原文显示，译文到了再逐行替换，不让翻译拖慢首页。</summary>
    private async Task TranslateAsync(IEnumerable<ResourceProject> projects)
    {
        var wanted = projects
            .Where(project => project.TranslatedDescription is null)
            .Select(project => project.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (wanted.Length == 0) return;
        var translations = await FocusMinecraftLauncher.Infrastructure.Runtime.TranslationService.TranslateAsync(wanted);
        foreach (var (id, text) in translations)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.Project.TranslatedDescription is not null) continue;
            entry.Project.TranslatedDescription = text;
            entry.Refresh();
        }
    }
    private static async Task LoadImageAsync(ResourceEntryViewModel entry,string? url)
    {
        var image=await RemoteArtwork.LoadAsync(url,160);
        if(image is null)return;
        entry.Project.LoadedImage=image;entry.Refresh();
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
    public void UpdateProject(ResourceProject project) {var wanted=SelectedFile?.Id;var translated=Project.TranslatedDescription;var chinese=Project.ChineseName;var image=Project.LoadedImage;Project=project;if(Project.TranslatedDescription is null)Project.TranslatedDescription=translated;if(Project.ChineseName is null)Project.ChineseName=chinese;if(Project.LoadedImage is null)Project.LoadedImage=image;SelectedFile=project.Files.FirstOrDefault(file=>file.Id==wanted)??project.Files.FirstOrDefault();Refresh(); }
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
