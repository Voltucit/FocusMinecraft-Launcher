using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FMCore.ModsResources;

namespace FocusMinecraftLauncher.Features.Downloads.Models;

/// <summary>FMCore Modrinth 公开接口适配；模型仅包含真实服务响应，不注入演示条目。</summary>
public sealed class ResourceCatalog : IDisposable
{
    public ModrinthClient Client { get; } = new("FocusMinecraftLauncher/2.0 (+https://github.com/Voltucit/FocusMinecraft-Launcher)");
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
            foreach (var file in version.Files)
                files.Add(new(version.Id + ":" + file.Filename, version.VersionNumber, file.Filename, version.GameVersions.ToArray(), version.Loaders.Count > 0 ? string.Join(" / ", version.Loaders) : null,
                    DateOnly.FromDateTime(version.PublishedAt.Date), file.Size, null, dependencies)
                    { DownloadFile = file, CoreVersion = version, ProjectId = detail.Id, Loaders = version.Loaders.ToArray() });
        }
        result.Files = files;_loaded.Add(id);
        result.LoadedImage = await RemoteArtwork.LoadAsync(detail.IconUrl, token);
        return result;
    }
    public async Task EnsureDependenciesAsync(ResourceFileItem file)
    {
        if(file.ResolvedDependencies!=null)return;
        if(!_dependencyLoads.TryGetValue(file.Id,out var pending))_dependencyLoads[file.Id]=pending=ResolveDependenciesAsync(file);
        try { await pending; }
        finally { if(pending.IsCompleted)_dependencyLoads.Remove(file.Id); }
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
            resolved.Add(new(id,version?.VersionNumber??"按目标实例选择兼容版本",dependency.DependencyType=="required",
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
    /// <summary>只接受 HTTPS 图片，后台解码并冻结；失败使用统一图标，不阻断资源操作。</summary>
    public static async Task<ImageSource?> LoadAsync(string? address, CancellationToken token = default)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https") return null;
        if (Cache.TryGetValue(address!, out var cached)) return cached;
        try
        {
            var bytes = await Client.GetByteArrayAsync(uri, token);
            var image = await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes); var bitmap = new BitmapImage(); bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 960; bitmap.StreamSource = stream;
                bitmap.EndInit(); bitmap.Freeze(); return bitmap;
            }, token);
            if (Cache.Count >= 150) Cache.Clear(); Cache[address!] = image; return image;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or NotSupportedException or OperationCanceledException) { return null; }
    }
}
