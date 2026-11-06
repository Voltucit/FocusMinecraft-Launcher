using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FocusMinecraftLauncher.Infrastructure.Runtime;
using FMCore.ModsResources;

namespace FocusMinecraftLauncher.Features.Downloads.Models;

/// <summary>FMCore Modrinth 公开接口适配，并转发 CurseForge 镜像接口；模型仅包含真实服务响应，不注入演示条目。</summary>
public sealed class ResourceCatalog : IDisposable
{
    /// <summary>每次取用都按当前设置建客户端，改完下载源不必重启启动器。</summary>
    public ModrinthClient Client => CommunitySource.NewClient();
    public CurseForgeClient CurseForge { get; } = new();
    private readonly Dictionary<string, ResourceProject> _projects = [];
    private readonly HashSet<string> _loaded=[];
    private readonly HashSet<string> _projectLoaded=[];
    private readonly Dictionary<string,Task> _dependencyLoads=[];
    private readonly ConcurrentDictionary<string,Task<ResourceProject>> _detailLoads=[];
    private readonly ConcurrentDictionary<string,Task<ResourceProject>> _projectLoads=[];
    public IReadOnlyCollection<ResourceProject> Projects => _projects.Values;
    public ResourceProject? Find(string id) => _projects.GetValueOrDefault(id);
    public static ModrinthResourceType Type(DownloadSection section) => section switch
    { DownloadSection.Modpacks => ModrinthResourceType.Modpack, DownloadSection.Datapacks => ModrinthResourceType.DataPack, DownloadSection.Resourcepacks => ModrinthResourceType.ResourcePack, DownloadSection.Shaders => ModrinthResourceType.Shader, _ => ModrinthResourceType.Mod };
    public static DownloadSection Section(string type) => type switch
    { "modpack" => DownloadSection.Modpacks, "datapack" => DownloadSection.Datapacks, "resourcepack" => DownloadSection.Resourcepacks, "shader" => DownloadSection.Shaders, _ => DownloadSection.Mods };
    public ResourceProject FromHit(ModrinthSearchHit hit, DownloadSection section)
    {
        if (_projects.TryGetValue(hit.ProjectId, out var old)) return old;
        var project = new ResourceProject(hit.ProjectId, section, hit.Title, [], hit.Description, hit.Tags.Select(t => t.DisplayName).ToArray(), hit.Versions.ToArray(),
            hit.Categories.Where(c => c is "fabric" or "forge" or "neoforge" or "quilt" or "iris" or "optifine").ToArray(), hit.Author, "Modrinth", hit.Downloads, DateOnly.FromDateTime(hit.UpdatedAt.Date), null)
        { IconUrl = hit.IconUrl };
        _projects[project.Id] = project; return project;
    }
    /// <summary>同一资源的详情请求只发一次：页面预读与用户打开详情共用结果，后到的调用直接等同一个任务。</summary>
    public Task<ResourceProject> LoadAsync(string id, CancellationToken token = default)
    {
        var key=Normalize(id);
        if(_projects.TryGetValue(key,out var alias))key=alias.Id;
        if(_projects.TryGetValue(key,out var cached)&&_loaded.Contains(key))return Task.FromResult(cached);
        var load=_detailLoads.GetOrAdd(key,LoadCoreAsync);
        return token.CanBeCanceled?load.WaitAsync(token):load;
    }
    /// <summary>只取项目正文、图库与作者，让详情页首屏先出内容；文件列表随后由 LoadAsync 补齐。</summary>
    public Task<ResourceProject> LoadProjectAsync(string id, CancellationToken token = default)
    {
        var key=Normalize(id);
        if(_projects.TryGetValue(key,out var alias))key=alias.Id;
        if(_projects.TryGetValue(key,out var cached)&&(_loaded.Contains(key)||_projectLoaded.Contains(key)))return Task.FromResult(cached);
        var load=_projectLoads.GetOrAdd(key,LoadProjectCoreAsync);
        return token.CanBeCanceled?load.WaitAsync(token):load;
    }
    private async Task<ResourceProject> LoadProjectCoreAsync(string key)
    {
        try
        {
            _projects.TryGetValue(key,out var previous);
            if(key.StartsWith("cf-",StringComparison.Ordinal))
            {
                var mod=await CurseForge.GetModAsync(long.Parse(key[3..],CultureInfo.InvariantCulture),CancellationToken.None);
                var project=FromCurseForge(mod,previous?.Category??DownloadSection.Mods);
                _projectLoaded.Add(key);
                return project;
            }
            var detail=await MirrorGate.RunAsync(()=>Client.GetProjectAsync(key,CancellationToken.None));
            var result=new ResourceProject(detail.Id, detail.Loaders.Contains("datapack")?DownloadSection.Datapacks:Section(detail.ProjectType), detail.Title, [], detail.Description, detail.Tags.Select(t => t.DisplayName).ToArray(), detail.GameVersions.ToArray(), detail.Loaders.ToArray(),
                string.Join(" / ", detail.Authors.Select(a => a.DisplayName)), "Modrinth", detail.Downloads, DateOnly.FromDateTime(detail.UpdatedAt.Date), null)
            { Body = detail.Body, Gallery = detail.Gallery.Select(g => g.Url).ToArray(), IconUrl = detail.IconUrl };
            result.Files=previous?.Files??[];
            _projects[key]=result;
            _projectLoaded.Add(key);
            return result;
        }
        finally{_projectLoads.TryRemove(key,out _);}
    }
    /// <summary>文件与详情都已就绪；列表展开和详情页据此跳过等待。</summary>
    public bool IsLoaded(string id)
    {
        var key=Normalize(id);
        if(_projects.TryGetValue(key,out var alias))key=alias.Id;
        return _loaded.Contains(key);
    }
    private static string Normalize(string id)=>
        id.StartsWith("cf-",StringComparison.Ordinal)
            ?"cf-"+long.Parse(id[3..],CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
            :id;
    private async Task<ResourceProject> LoadCoreAsync(string key)
    {
        var id=key;
        try
        {
            if(id.StartsWith("cf-",StringComparison.Ordinal))
                return await LoadCurseForgeAsync(long.Parse(id[3..],CultureInfo.InvariantCulture),null,CancellationToken.None);
            if(_projects.TryGetValue(id,out var alias))id=alias.Id;
            if(_projects.TryGetValue(id,out var cached)&&_loaded.Contains(id))return cached;
            _projects.TryGetValue(id,out var previous);
            var hasProject=_projectLoaded.Contains(id)&&previous is not null;
            Task<ModrinthProject>? detailTask=hasProject?null:MirrorGate.RunAsync(()=>Client.GetProjectAsync(id,CancellationToken.None));
            var versionsTask=MirrorGate.RunAsync(()=>Client.GetVersionsAsync(id,cancellationToken:CancellationToken.None));
            ResourceProject result;
            if(hasProject) result=previous!;
            else
            {
                var detail=await detailTask!;
                result=new ResourceProject(detail.Id, detail.Loaders.Contains("datapack")?DownloadSection.Datapacks:Section(detail.ProjectType), detail.Title, [], detail.Description, detail.Tags.Select(t => t.DisplayName).ToArray(), detail.GameVersions.ToArray(), detail.Loaders.ToArray(),
                    string.Join(" / ", detail.Authors.Select(a => a.DisplayName)), "Modrinth", detail.Downloads, DateOnly.FromDateTime(detail.UpdatedAt.Date), null)
                { Body = detail.Body, Gallery = detail.Gallery.Select(g => g.Url).ToArray(), IconUrl = detail.IconUrl };
                result.Files=previous?.Files??[];
                _projects[id]=result;
                _projectLoaded.Add(id);
            }
            AttachFiles(result,await versionsTask);
            _loaded.Add(id);
            if(await RemoteArtwork.LoadAsync(result.IconUrl) is {} image)result.LoadedImage=image;
            return result;
        }
        finally{_detailLoads.TryRemove(key,out _);}
    }
    private static void AttachFiles(ResourceProject project,IReadOnlyList<ModrinthVersion> versions)
    {
        var files=new List<ResourceFileItem>();
        foreach(var version in versions)
        {
            IReadOnlyList<ResourceDependency>? dependencies=version.Dependencies.Any(d=>d.ProjectId==null&&d.VersionId==null)?null:version.Dependencies.Where(d=>d.DependencyType is "required" or "optional").Select(d=>new ResourceDependency(d.ProjectId??d.VersionId!,d.VersionId??"由兼容版本决定",d.DependencyType=="required",version.GameVersions.ToArray(),string.Join(" / ",version.Loaders),"")).ToArray();
            foreach(var file in SelectInstallable(version.Files))
                files.Add(new(version.Id+":"+file.Filename,version.VersionNumber,file.Filename,version.GameVersions.ToArray(),version.Loaders.Count>0?string.Join(" / ",version.Loaders):null,
                    DateOnly.FromDateTime(version.PublishedAt.Date),file.Size,null,dependencies)
                    {DownloadFile=file,CoreVersion=version,ProjectId=project.Id,Loaders=version.Loaders.ToArray()});
        }
        project.Files=files;
    }

    // ---------------- CurseForge（经 mcimirror 转发，无需官方 Key） ----------------
    public async Task<(IReadOnlyList<ResourceProject> Items, long Total)> SearchCurseForgeAsync(DownloadSection section, string query, string? version, string? loader, int offset, int limit, CancellationToken token, int? categoryId = null, bool background = false)
    {
        var page = await CurseForge.SearchAsync(section, query, version, loader, offset, limit, token, categoryId, background);
        return (page.Mods.Select(mod => FromCurseForge(mod, section)).ToArray(), page.Total);
    }

    public ResourceProject FromCurseForge(CurseForgeMod mod, DownloadSection section)
    {
        var key = "cf-" + mod.Id;
        if (_projects.TryGetValue(key, out var existing) && _loaded.Contains(key)) return existing;
        var project = new ResourceProject(key, section, mod.Name, [], mod.Summary, mod.Categories, null, null,
            mod.Author, "CurseForge", mod.Downloads, mod.Updated, null)
        { Files = mod.LatestFiles.Where(Installable).Select(file => FileItem(key, file, section)).ToArray(), IconUrl = mod.Icon };
        _projects[key] = project;
        return project;
    }

    /// <summary>详情页需要完整文件列表，搜索响应里的 latestFiles 只够列表展示。</summary>
    public async Task<ResourceProject> LoadCurseForgeAsync(long id, DownloadSection? section, CancellationToken token)
    {
        var key = "cf-" + id.ToString(CultureInfo.InvariantCulture);
        if(_projects.TryGetValue(key,out var known)&&_loaded.Contains(key))return known;
        _projects.TryGetValue(key,out var project);
        var hasProject=_projectLoaded.Contains(key)&&project is not null;
        Task<CurseForgeMod>? modTask=hasProject?null:CurseForge.GetModAsync(id, token);
        var filesTask=CurseForge.GetFilesAsync(id, null, null, token);
        CurseForgeMod? mod=null;
        if(!hasProject)
        {
            mod=await modTask!;
            project=FromCurseForge(mod, section??_projects.GetValueOrDefault(key)?.Category??DownloadSection.Mods);
        }
        var category=section??project!.Category;
        var files = new List<CurseForgeFile>(mod?.LatestFiles??[]);
        foreach (var file in await filesTask)
            if (files.All(existing => existing.Id != file.Id)) files.Add(file);
        project!.Files = files.Where(Installable).OrderByDescending(file => file.Date).Select(file => FileItem(key, file, category)).ToArray();
        _projects[key] = project; _projectLoaded.Add(key); _loaded.Add(key);
        if(await RemoteArtwork.LoadAsync(project.IconUrl, token) is {} image)project.LoadedImage=image;
        return project;
    }

    private static ResourceFileItem FileItem(string projectId, CurseForgeFile file, DownloadSection section)
    {
        var dependencies = file.Dependencies.Where(item => item.Required || item.Optional)
            .Select(item => new ResourceDependency("cf-" + item.ModId, "按目标游戏选择兼容版本", item.Required, file.GameVersions, null, "")).ToArray();
        return new ResourceFileItem("cf:" + file.Id, file.DisplayName.Length > 0 ? file.DisplayName : file.FileName, file.FileName,
            file.GameVersions, file.Loaders.Length > 0 ? string.Join(" / ", file.Loaders) : null, file.Date, file.FileLength,
            file.Downloads, dependencies.Length > 0 ? dependencies : null)
        {
            ProjectId = projectId,
            Loaders = file.Loaders,
            Section = section,
            Download = new ResourceDownload(file.FileName, CurseForgeClient.Rewrite(file.DownloadUrl), file.FileLength, file.Sha1),
            CurseForgeDependencies = file.Dependencies
        };
    }

    private static bool Installable(CurseForgeFile file) =>
        !string.IsNullOrWhiteSpace(file.DownloadUrl)
        && !file.FileName.EndsWith(".pom", StringComparison.OrdinalIgnoreCase)
        && !file.FileName.EndsWith(".module", StringComparison.OrdinalIgnoreCase)
        && !file.FileName.EndsWith("-sources.jar", StringComparison.OrdinalIgnoreCase)
        && !file.FileName.EndsWith("-javadoc.jar", StringComparison.OrdinalIgnoreCase);

    /// <summary>同一版本会附带源码、文档等不可加载的附件，装进 mods 会让加载器在启动阶段直接失败。</summary>
    private static IEnumerable<ModrinthFile> SelectInstallable(IReadOnlyList<ModrinthFile> files)
    {
        var usable = files.Where(file =>
            !file.Filename.EndsWith("-sources.jar", StringComparison.OrdinalIgnoreCase)
            && !file.Filename.EndsWith("-javadoc.jar", StringComparison.OrdinalIgnoreCase)
            && !file.Filename.EndsWith(".pom", StringComparison.OrdinalIgnoreCase)
            && !file.Filename.EndsWith(".module", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (usable.Length == 0) return usable;
        // 有主文件时只保留主文件，避免用户在同版本里选到资源包之类的次要附件。
        return usable.Any(file => file.Primary) ? usable.Where(file => file.Primary) : usable;
    }
    public async Task EnsureDependenciesAsync(ResourceFileItem file)
    {
        if(file.ResolvedDependencies!=null)return;
        if(!_dependencyLoads.TryGetValue(file.Id,out var pending))
            _dependencyLoads[file.Id]=pending=file.IsCurseForge?ResolveCurseForgeAsync(file):ResolveDependenciesAsync(file);
        try { await pending; }
        finally { if(pending.IsCompleted)_dependencyLoads.Remove(file.Id); }
    }

    /// <summary>CurseForge 的前置只有项目编号，按编号补查名称，查不到时保留“按目标游戏选择”的占位说明。</summary>
    private async Task ResolveCurseForgeAsync(ResourceFileItem file)
    {
        var resolved = new List<ResourceDependency>();
        foreach (var dependency in file.CurseForgeDependencies?.Where(item => item.Required || item.Optional) ?? [])
        {
            var key = "cf-" + dependency.ModId;
            try
            {
                // 走统一入口，和页面预读共用同一个在途请求。
                await LoadAsync(key);
            }
            catch (Exception error) when (error is HttpRequestException or InvalidDataException or IOException or System.Threading.Tasks.TaskCanceledException)
            {
                System.Diagnostics.Trace.TraceWarning("CurseForge 前置信息读取失败：{0}", error.GetType().Name);
            }
            resolved.Add(new(key, "按目标游戏选择兼容版本", dependency.Required, file.GameVersions, null, ""));
        }
        file.ResolvedDependencies = resolved;
    }
    private async Task ResolveDependenciesAsync(ResourceFileItem file)
    {
        var resolved=new List<ResourceDependency>();
        foreach (var dependency in file.CoreVersion?.Dependencies.Where(d => d.DependencyType is "required" or "optional") ?? [])
        {
            ModrinthVersion? version = dependency.VersionId == null ? null : await MirrorGate.RunAsync(() => Client.GetVersionAsync(dependency.VersionId));
            var id = dependency.ProjectId;
            if(id == null) id = version?.ProjectId;
            if(id == null)continue;
            var project = await MirrorGate.RunAsync(() => Client.GetProjectAsync(id));
            var item = new ResourceProject(id, Section(project.ProjectType), project.Title, [], project.Description, project.Tags.Select(t => t.DisplayName).ToArray(), project.GameVersions.ToArray(), project.Loaders.ToArray(),
                string.Join(" / ", project.Authors.Select(a => a.DisplayName)), "Modrinth", project.Downloads, DateOnly.FromDateTime(project.UpdatedAt.Date), null);
            if(!_projects.TryGetValue(id,out var existing)||!_loaded.Contains(id))
            {item.LoadedImage=await RemoteArtwork.LoadAsync(project.IconUrl);_projects[id]=item;}
            else item=existing;
            if(dependency.ProjectId == null && dependency.VersionId != null) _projects[dependency.VersionId] = item;
            resolved.Add(new(id,version?.VersionNumber??"按目标游戏选择兼容版本",dependency.DependencyType=="required",
                version?.GameVersions.ToArray()??project.GameVersions.ToArray(),
                string.Join(" / ",version?.Loaders??project.Loaders),version==null?"安装时选择兼容的文件版本。":"要求指定的文件版本。"));
        }
        file.ResolvedDependencies=resolved;
    }
    public void Dispose() { }
}

public static class RemoteArtwork
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 16 * 1024 * 1024 };
    static RemoteArtwork() => Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Infrastructure.Runtime.CommunitySource.UserAgent);
    private static readonly Dictionary<string, ImageSource> Cache = [];
    /// <summary>一页列表会同时出现几十张缩略图，单独限流，不跟接口请求抢同一条通道。</summary>
    private static readonly SemaphoreSlim Slots = new(6, 6);
    /// <summary>只接受 HTTPS 图片，后台解码并冻结；按目标宽度缓存，列表用缩略图、详情页才解码大图。</summary>
    public static async Task<ImageSource?> LoadAsync(string? address, CancellationToken token = default) =>
        await LoadAsync(address, 480, token);
    public static async Task<ImageSource?> LoadAsync(string? address, int width, CancellationToken token = default)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https") return null;
        var key = width + "|" + address;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        try
        {
            // 图标落盘七天，第二次启动时列表直接出图，不必再走一遍网络。
            var bytes = Infrastructure.Runtime.CommunityCache.ReadBytes("icon:" + key, TimeSpan.FromDays(7));
            if (bytes is not { Length: > 0 }) bytes = await FetchAsync(uri, token);
            if (bytes.Length > 0 && bytes.Length <= 512 * 1024)
                _ = Task.Run(() => Infrastructure.Runtime.CommunityCache.WriteBytes("icon:" + key, bytes));
            var image = await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes); var bitmap = new BitmapImage(); bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = width; bitmap.StreamSource = stream;
                bitmap.EndInit(); bitmap.Freeze(); return bitmap;
            }, token);
            if (Cache.Count >= 150) Cache.Clear(); Cache[key] = image; return image;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or NotSupportedException or OperationCanceledException)
        {
            // 坏字节或损坏的磁盘缓存不能继续留七天，否则这张图会一直失败。
            Infrastructure.Runtime.CommunityCache.RemoveBytes("icon:" + key);
            return null;
        }
    }
    /// <summary>排队取一张图，最多六个请求同时在途；第一次失败稍等重试一次。</summary>
    private static async Task<byte[]> FetchAsync(Uri uri, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            await Slots.WaitAsync(token);
            try { return await Client.GetByteArrayAsync(uri, token); }
            catch (Exception error) when (attempt == 0 && !token.IsCancellationRequested
                && (error is HttpRequestException or IOException or TaskCanceledException))
            { }
            finally { Slots.Release(); }
            await Task.Delay(250, token);
        }
    }
}
