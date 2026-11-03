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
    public ModrinthClient Client { get; } = new("FocusMinecraftLauncher/2.0 (+https://github.com/Voltucit/FocusMinecraft-Launcher)");
    public CurseForgeClient CurseForge { get; } = new();
    private readonly Dictionary<string, ResourceProject> _projects = [];
    private readonly HashSet<string> _loaded=[];
    private readonly Dictionary<string,Task> _dependencyLoads=[];
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
            hit.Categories.Where(c => c is "fabric" or "forge" or "neoforge" or "quilt" or "iris" or "optifine").ToArray(), hit.Author, "Modrinth", hit.Downloads, DateOnly.FromDateTime(hit.UpdatedAt.Date), null);
        _projects[project.Id] = project; return project;
    }
    public async Task<ResourceProject> LoadAsync(string id, CancellationToken token = default)
    {
        if (id.StartsWith("cf-", StringComparison.Ordinal))
            return await LoadCurseForgeAsync(long.Parse(id[3..]), null, token);
        if(_projects.TryGetValue(id,out var alias))id=alias.Id;
        if (_projects.TryGetValue(id, out var cached) && _loaded.Contains(id)) return cached;
        var detail = await Client.GetProjectAsync(id, token);
        var versions = await Client.GetVersionsAsync(id, cancellationToken: token);
        var result = new ResourceProject(detail.Id, detail.Loaders.Contains("datapack")?DownloadSection.Datapacks:Section(detail.ProjectType), detail.Title, [], detail.Description, detail.Tags.Select(t => t.DisplayName).ToArray(), detail.GameVersions.ToArray(), detail.Loaders.ToArray(),
            string.Join(" / ", detail.Authors.Select(a => a.DisplayName)), "Modrinth", detail.Downloads, DateOnly.FromDateTime(detail.UpdatedAt.Date), null)
        { Body = detail.Body, Gallery = detail.Gallery.Select(g => g.Url).ToArray() };
        _projects[id] = result;
        var files = new List<ResourceFileItem>();
        foreach (var version in versions)
        {
            IReadOnlyList<ResourceDependency>? dependencies = version.Dependencies.Any(d => d.ProjectId == null && d.VersionId == null) ? null : version.Dependencies.Where(d => d.DependencyType is "required" or "optional").Select(d => new ResourceDependency(d.ProjectId ?? d.VersionId!, d.VersionId ?? "由兼容版本决定", d.DependencyType == "required", version.GameVersions.ToArray(), string.Join(" / ", version.Loaders), "")).ToArray();
            foreach (var file in SelectInstallable(version.Files))
                files.Add(new(version.Id + ":" + file.Filename, version.VersionNumber, file.Filename, version.GameVersions.ToArray(), version.Loaders.Count > 0 ? string.Join(" / ", version.Loaders) : null,
                    DateOnly.FromDateTime(version.PublishedAt.Date), file.Size, null, dependencies)
                    { DownloadFile = file, CoreVersion = version, ProjectId = detail.Id, Loaders = version.Loaders.ToArray() });
        }
        result.Files = files;_loaded.Add(id);
        result.LoadedImage = await RemoteArtwork.LoadAsync(detail.IconUrl, token);
        return result;
    }

    // ---------------- CurseForge（经 mcimirror 转发，无需官方 Key） ----------------
    public async Task<(IReadOnlyList<ResourceProject> Items, long Total)> SearchCurseForgeAsync(DownloadSection section, string query, string? version, string? loader, int offset, int limit, CancellationToken token, int? categoryId = null)
    {
        var page = await CurseForge.SearchAsync(section, query, version, loader, offset, limit, token, categoryId);
        return (page.Mods.Select(mod => FromCurseForge(mod, section)).ToArray(), page.Total);
    }

    public ResourceProject FromCurseForge(CurseForgeMod mod, DownloadSection section)
    {
        var key = "cf-" + mod.Id;
        if (_projects.TryGetValue(key, out var existing) && _loaded.Contains(key)) return existing;
        var project = new ResourceProject(key, section, mod.Name, [], mod.Summary, mod.Categories, null, null,
            mod.Author, "CurseForge", mod.Downloads, mod.Updated, null)
        { Files = mod.LatestFiles.Where(Installable).Select(file => FileItem(mod, file, section)).ToArray(), IconUrl = mod.Icon };
        _projects[key] = project;
        return project;
    }

    /// <summary>详情页需要完整文件列表，搜索响应里的 latestFiles 只够列表展示。</summary>
    public async Task<ResourceProject> LoadCurseForgeAsync(long id, DownloadSection? section, CancellationToken token)
    {
        var key = "cf-" + id;
        var mod = await CurseForge.GetModAsync(id, token);
        var category = section ?? _projects.GetValueOrDefault(key)?.Category ?? DownloadSection.Mods;
        var files = new List<CurseForgeFile>(mod.LatestFiles);
        foreach (var file in await CurseForge.GetFilesAsync(id, null, null, token))
            if (files.All(existing => existing.Id != file.Id)) files.Add(file);
        var project = FromCurseForge(mod, category);
        project.Files = files.Where(Installable).OrderByDescending(file => file.Date).Select(file => FileItem(mod, file, category)).ToArray();
        _projects[key] = project; _loaded.Add(key);
        project.LoadedImage = await RemoteArtwork.LoadAsync(mod.Icon, token);
        return project;
    }

    private static ResourceFileItem FileItem(CurseForgeMod mod, CurseForgeFile file, DownloadSection section)
    {
        var dependencies = file.Dependencies.Where(item => item.Required || item.Optional)
            .Select(item => new ResourceDependency("cf-" + item.ModId, "按目标游戏选择兼容版本", item.Required, file.GameVersions, null, "")).ToArray();
        return new ResourceFileItem("cf:" + file.Id, file.DisplayName.Length > 0 ? file.DisplayName : file.FileName, file.FileName,
            file.GameVersions, file.Loaders.Length > 0 ? string.Join(" / ", file.Loaders) : null, file.Date, file.FileLength,
            file.Downloads, dependencies.Length > 0 ? dependencies : null)
        {
            ProjectId = "cf-" + mod.Id,
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
                var project = await LoadCurseForgeAsync(dependency.ModId, DownloadSection.Mods, CancellationToken.None);
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
            ModrinthVersion? version = dependency.VersionId == null ? null : await Client.GetVersionAsync(dependency.VersionId);
            var id = dependency.ProjectId;
            if(id == null) id = version?.ProjectId;
            if(id == null)continue;
            var project = await Client.GetProjectAsync(id);
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
    private static readonly Dictionary<string, ImageSource> Cache = [];
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
            var bytes = await Client.GetByteArrayAsync(uri, token);
            var image = await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes); var bitmap = new BitmapImage(); bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = width; bitmap.StreamSource = stream;
                bitmap.EndInit(); bitmap.Freeze(); return bitmap;
            }, token);
            if (Cache.Count >= 150) Cache.Clear(); Cache[key] = image; return image;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or NotSupportedException or OperationCanceledException) { return null; }
    }
}
