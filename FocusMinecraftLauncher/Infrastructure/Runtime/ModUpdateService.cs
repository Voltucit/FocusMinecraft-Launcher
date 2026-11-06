using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FocusMinecraftLauncher.Preview;
namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>
/// 用文件 SHA-1 或指纹识别 Modrinth、CurseForge 项目，并按当前游戏版本和加载器检查更新。
/// </summary>
public static class ModUpdateService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
    static ModUpdateService() => Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", CommunitySource.UserAgent);
    private static readonly CurseForgeClient CurseForge = new();
    /// <summary>一次检查的结论；下载字段用于直接替换旧文件，不需要用户再打开网页。</summary>
    public sealed record ModUpdate(
        string FileName,
        string ProjectId,
        string CurrentVersion,
        string LatestVersion,
        string DownloadUrl,
        string LatestFileName,
        string LatestSha1,
        string LatestVersionId,
        string InstalledVersionId)
    {
        public DateTimeOffset? InstalledPublishedAt {get;init;}
        public DateTimeOffset? LatestPublishedAt {get;init;}
        public bool HasPublicationDates=>InstalledPublishedAt.HasValue&&LatestPublishedAt.HasValue;
        /// <summary>不同 ID 只表示另一个文件；候选还必须有可比较的发布时间，并且确实发布得更晚。</summary>
        public bool HasUpdate => LatestVersionId.Length > 0
            && !string.Equals(LatestVersionId, InstalledVersionId, StringComparison.Ordinal)
            && InstalledPublishedAt is {} installed&&LatestPublishedAt is {} latest&&latest>installed;
    }

    private const int BatchSize = 100;
    private const int ProjectParallelism = 4;

    /// <summary>按本地文件内容精确匹配资源项目；优先 Modrinth，再匹配 CurseForge 指纹，不以名称猜测项目。</summary>
    public static async Task<string?> FindProjectAsync(string path,CancellationToken token)
    {
        var bytes=await File.ReadAllBytesAsync(path,token).ConfigureAwait(false);
        var hash=await Task.Run(()=>Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(),token).ConfigureAwait(false);
        Exception? modrinthError=null;
        try
        {
            var matches=await MatchInstalledAsync(new(StringComparer.OrdinalIgnoreCase){{hash,path}},null,token).ConfigureAwait(false);
            if(matches.FirstOrDefault() is {ProjectId.Length:>0} match)return match.ProjectId;
        }
        catch(Exception error) when(!token.IsCancellationRequested&&error is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
        { modrinthError=error; }
        token.ThrowIfCancellationRequested();
        var fingerprint=await Task.Run(()=>Murmur2(bytes),token).ConfigureAwait(false);
        var projects=await CurseForge.MatchFingerprintsAsync([fingerprint],token,requireSuccess:true).ConfigureAwait(false);
        if(projects.TryGetValue(fingerprint,out var projectId)&&projectId>0)return "cf-"+projectId;
        if(modrinthError!=null)throw new IOException("Modrinth 暂时无法查询，请稍后重试。",modrinthError);
        return null;
    }

    /// <summary>检查指定内容目录下的文件；只有 Mod 需要匹配加载器，资源包与光影包只按游戏版本匹配。</summary>
    public static async Task<IReadOnlyList<ModUpdate>> CheckAsync(PreviewInstance instance, string folder, string extension, string label, IProgress<string>? progress, CancellationToken token)
    {
        var directory = Path.Combine(instance.GameDirectory, folder);
        if (!Directory.Exists(directory)) return [];
        var filterLoader = extension == ".jar";

        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fingerprints = new Dictionary<uint, string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*" + extension))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var bytes = await File.ReadAllBytesAsync(file, token);
                hashes[Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant()] = file;
                fingerprints[Murmur2(bytes)] = file;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        if (hashes.Count == 0) return [];

        var results = new List<ModUpdate>();
        var installed = await MatchInstalledAsync(hashes, progress, token);
        if (installed.Count > 0) results.AddRange(await CheckModrinthAsync(instance, installed, filterLoader, progress, token));

        var matched = installed.Select(item => item.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = fingerprints.Where(pair => !matched.Contains(Path.GetFileName(pair.Value))).ToDictionary(pair => pair.Key, pair => pair.Value);
        if (remaining.Count > 0) results.AddRange(await CheckCurseForgeAsync(instance, remaining, filterLoader, progress, token));

        progress?.Report($"检查完成：{results.Count(update=>update.HasUpdate)} 个{label}有可用更新。");
        return results.OrderBy(update => update.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<IReadOnlyList<ModUpdate>> CheckModrinthAsync(PreviewInstance instance, List<(string FileName, string ProjectId, string VersionId, string Version, DateTimeOffset? PublishedAt)> installed, bool filterLoader, IProgress<string>? progress, CancellationToken token)
    {
        var results = new List<ModUpdate>();
        using var gate = new SemaphoreSlim(ProjectParallelism);
        var projects = installed.GroupBy(item => item.ProjectId, StringComparer.OrdinalIgnoreCase).ToArray();
        var finished = 0;
        var tasks = projects.Select(async group =>
        {
            await gate.WaitAsync(token);
            try
            {
                var latest = await FindLatestAsync(group.Key, instance, filterLoader, token);
                lock (results)
                {
                    foreach (var item in group)
                    {
                        var found=latest??(item.VersionId,item.Version,"",item.FileName,"",item.PublishedAt);
                        results.Add(new(item.FileName, group.Key, item.Version, found.Version, found.Url, found.FileName, found.Sha1, found.VersionId, item.VersionId)
                        {InstalledPublishedAt=item.PublishedAt,LatestPublishedAt=found.PublishedAt});
                    }
                }
            }
            finally
            {
                gate.Release();
                progress?.Report($"已检查 {Interlocked.Increment(ref finished)}/{projects.Length} 个项目");
            }
        }).ToArray();
        await Task.WhenAll(tasks);
        return results;
    }

    /// <summary>Modrinth 查不到的 jar 用 CurseForge 指纹接口再匹配一次；匹配结果只认“同一游戏版本 + 加载器下最新的文件”。</summary>
    private static async Task<IReadOnlyList<ModUpdate>> CheckCurseForgeAsync(PreviewInstance instance, Dictionary<uint, string> remaining, bool filterLoader, IProgress<string>? progress, CancellationToken token)
    {
        var results = new List<ModUpdate>();
        var prints = remaining.Keys.ToArray();
        for (var offset = 0; offset < prints.Length; offset += BatchSize)
        {
            token.ThrowIfCancellationRequested();
            var batch = prints.Skip(offset).Take(BatchSize).ToArray();
            progress?.Report($"正在核对 CurseForge 文件 {offset + 1}-{offset + batch.Length}/{prints.Length}");
            var matches=await CurseForge.MatchFingerprintFilesAsync(batch,token,requireSuccess:true);
            foreach (var pair in matches)
            {
                if(!remaining.TryGetValue(pair.Key,out var local))continue;
                var current=pair.Value.File;
                var candidates=await CurseForge.GetFilesAsync(pair.Value.ModId,instance.Version,filterLoader?instance.Loader:null,token);
                var loader=filterLoader?LoaderKey(instance.Loader):"";
                // 镜像可能返回未筛选的缓存；在本地再次核对精确游戏版本、加载器和文件类型。
                var latest=candidates.Where(item=>!string.IsNullOrWhiteSpace(item.DownloadUrl)
                    &&item.GameVersions.Contains(instance.Version,StringComparer.OrdinalIgnoreCase)
                    &&(!filterLoader||loader.Length>0&&item.Loaders.Contains(loader,StringComparer.OrdinalIgnoreCase))
                    &&InstallableFile(item.FileName,filterLoader))
                    .OrderByDescending(item=>item.PublishedAt).FirstOrDefault()??current;
                results.Add(new(Path.GetFileName(local),"cf-"+pair.Value.ModId,current.DisplayName,latest.DisplayName,
                    CurseForgeClient.Rewrite(latest.DownloadUrl),latest.FileName,latest.Sha1??"",latest.Id.ToString(),current.Id.ToString())
                    {InstalledPublishedAt=current.PublishedAt,LatestPublishedAt=latest.PublishedAt});
            }
        }
        return results;
    }

    /// <summary>CurseForge 指纹：忽略制表、换行、回车和空格字节后计算 seed 为 1 的 MurmurHash2。</summary>
    private static uint Murmur2(byte[] data)
    {
        const uint m = 0x5bd1e995;
        const int r = 24;
        var length = 0;
        foreach(var value in data)if(value is not (9 or 10 or 13 or 32))length++;
        var hash = 1u ^ (uint)length;
        uint chunk=0;
        var shift=0;
        foreach(var value in data)
        {
            if(value is 9 or 10 or 13 or 32)continue;
            chunk|=(uint)value<<shift;shift+=8;
            if(shift<32)continue;
            chunk *= m; chunk ^= chunk >> r; chunk *= m;
            hash *= m; hash ^= chunk;
            chunk=0;shift=0;
        }
        if(shift>0){hash^=chunk;hash*=m;}
        hash ^= hash >> 13; hash *= m; hash ^= hash >> 15;
        return hash;
    }

    /// <summary>批量把 SHA-1 换成本地文件对应的版本信息（项目 id、版本 id、版本号）。</summary>
    private static async Task<List<(string FileName, string ProjectId, string VersionId, string Version, DateTimeOffset? PublishedAt)>> MatchInstalledAsync(
        Dictionary<string, string> hashes, IProgress<string>? progress, CancellationToken token)
    {
        var found = new List<(string, string, string, string, DateTimeOffset?)>();
        var keys = hashes.Keys.ToArray();
        for (var offset = 0; offset < keys.Length; offset += BatchSize)
        {
            var batch = keys.Skip(offset).Take(BatchSize).ToArray();
            progress?.Report($"正在核对本地文件 {offset + 1}-{offset + batch.Length}/{keys.Length}");
            var body = JsonSerializer.Serialize(new { hashes = batch, algorithm = "sha1" });
            using var document = await LookupVersionsAsync(body, token);
            if (document is null) continue;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!hashes.TryGetValue(property.Name, out var file)) continue;
                var version = property.Value;
                var projectId = Text(version, "project_id");
                var versionId = Text(version, "id");
                if (projectId is null || versionId is null) continue;
                found.Add((Path.GetFileName(file), projectId, versionId, Text(version, "version_number") ?? versionId,PublishedAt(version,"date_published")));
            }
        }
        return found;
    }

    private const string VersionFilesUrl = "https://api.modrinth.com/v2/version_files";
    /// <summary>按设置里的来源发出请求；整批哈希都没有记录时接口回 404，镜像是转发上游的，此时去官方再确认一次。</summary>
    private static async Task<JsonDocument?> LookupVersionsAsync(string body, CancellationToken token)
    {
        using var response = await SendVersionsAsync(CommunitySource.Current.GetUrl(VersionFilesUrl), body, token);
        if (response.StatusCode != HttpStatusCode.NotFound) return await ReadVersionsAsync(response, token);
        if (CommunitySource.Current.Name == CommunitySource.OfficialName) return null;
        // 镜像可能还没收录这批文件，回官方确认，避免把已上架的包当成“没有记录”。
        using var official = await SendVersionsAsync(VersionFilesUrl, body, token);
        return official.StatusCode == HttpStatusCode.NotFound ? null : await ReadVersionsAsync(official, token);
    }

    private static async Task<HttpResponseMessage> SendVersionsAsync(string url, string body, CancellationToken token)
    {
        return await MirrorGate.SendAsync(Client, () =>
        {
            var message = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            AddHeaders(message);
            return message;
        }, token);
    }

    private static async Task<JsonDocument> ReadVersionsAsync(HttpResponseMessage response, CancellationToken token)
    {
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token);
    }

    /// <summary>按文件内容找到 Modrinth 项目并取项目图标地址；用于压缩包内没有预览图的资源包与光影包。</summary>
    public static async Task<Dictionary<string, string>> FindIconsAsync(IEnumerable<string> paths, CancellationToken token)
    {
        var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var bytes = await File.ReadAllBytesAsync(path, token);
                hashes[Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant()] = path;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        if (hashes.Count == 0) return icons;

        List<(string FileName, string ProjectId, string VersionId, string Version, DateTimeOffset? PublishedAt)> matches;
        try { matches = await MatchInstalledAsync(hashes, null, token); }
        catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException) { return icons; }
        if (matches.Count == 0) return icons;

        var ids = matches.Select(match => match.ProjectId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var query = new StringBuilder("https://api.modrinth.com/v2/projects?ids=")
            .Append(Uri.EscapeDataString(JsonSerializer.Serialize(ids))).ToString();
        using var response = await MirrorGate.SendAsync(Client, () =>
        {
            var message = new HttpRequestMessage(HttpMethod.Get, CommunitySource.Current.GetUrl(query));
            AddHeaders(message);
            return message;
        }, token);
        if (!response.IsSuccessStatusCode) return icons;
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return icons;

        var byProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in document.RootElement.EnumerateArray())
        {
            var id = Text(project, "id");
            var icon = Text(project, "icon_url");
            if (id is null || string.IsNullOrWhiteSpace(icon)) continue;
            byProject[id] = CommunitySource.Current.GetUrl(icon);
        }
        foreach (var match in matches)
            if (byProject.TryGetValue(match.ProjectId, out var icon)) icons[match.FileName] = icon;
        return icons;
    }

    /// <summary>查项目在当前游戏版本与加载器下的最新版本；没有可用版本时返回 null。</summary>
    private static async Task<(string VersionId, string Version, string Url, string FileName, string Sha1, DateTimeOffset? PublishedAt)?> FindLatestAsync(
        string projectId, PreviewInstance instance, bool filterLoader, CancellationToken token)
    {
        var loaders = filterLoader ? LoaderKey(instance.Loader) : "";
        var query = new StringBuilder("https://api.modrinth.com/v2/project/")
            .Append(Uri.EscapeDataString(projectId)).Append("/version?");
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(instance.Version))
            filters.Add("game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { instance.Version })));
        if (loaders.Length > 0)
            filters.Add("loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loaders })));
        query.Append(string.Join('&', filters));

        using var response = await MirrorGate.SendAsync(Client, () =>
        {
            var message = new HttpRequestMessage(HttpMethod.Get, CommunitySource.Current.GetUrl(query.ToString()));
            AddHeaders(message);
            return message;
        }, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        if(document.RootElement.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("资源服务返回了无法读取的版本列表。");
        var compatible=document.RootElement.EnumerateArray().Where(version=>version.ValueKind==JsonValueKind.Object
            &&Contains(version,"game_versions",instance.Version)
            &&(!filterLoader||loaders.Length>0&&Contains(version,"loaders",loaders)))
            .OrderByDescending(version=>PublishedAt(version,"date_published"));
        foreach(var version in compatible)
        {
            if(!version.TryGetProperty("files",out var files)||files.ValueKind!=JsonValueKind.Array)continue;
            var primary=files.EnumerateArray().Where(file=>file.ValueKind==JsonValueKind.Object&&InstallableFile(Text(file,"filename")??"",filterLoader))
                .OrderByDescending(file=>file.TryGetProperty("primary",out var main)&&main.ValueKind==JsonValueKind.True).FirstOrDefault();
            if(primary.ValueKind!=JsonValueKind.Object)continue;
            var url=Text(primary,"url");var fileName=Text(primary,"filename");
            if(url is null||fileName is null)continue;
            var sha1=primary.TryGetProperty("hashes",out var hashes)?Text(hashes,"sha1")??"":"";
            return(Text(version,"id")??"",Text(version,"version_number")??"",CommunitySource.Current.GetUrl(url),fileName,sha1,PublishedAt(version,"date_published"));
        }
        return null;
    }

    private static bool Contains(JsonElement item,string field,string value)=>item.TryGetProperty(field,out var items)
        &&items.ValueKind==JsonValueKind.Array&&items.EnumerateArray().Any(entry=>entry.ValueKind==JsonValueKind.String&&string.Equals(entry.GetString(),value,StringComparison.OrdinalIgnoreCase));
    private static bool InstallableFile(string file,bool mod)=>file.EndsWith(mod?".jar":".zip",StringComparison.OrdinalIgnoreCase)
        &&!file.EndsWith("-sources.jar",StringComparison.OrdinalIgnoreCase)&&!file.EndsWith("-javadoc.jar",StringComparison.OrdinalIgnoreCase);
    private static DateTimeOffset? PublishedAt(JsonElement item,string field)=>DateTimeOffset.TryParse(Text(item,field),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var value)?value:null;

    /// <summary>下载并替换所选文件；返回成功替换的数量。旧文件按删除按钮同样的方式移入回收站。</summary>
    public static async Task<int> ApplyAsync(PreviewInstance instance, string folder, IReadOnlyList<ModUpdate> updates, IProgress<string>? progress, CancellationToken token)
    {
        if(updates.Any(update=>!update.HasUpdate))throw new InvalidOperationException("候选文件未被确认为较新的兼容版本，已停止更新。");
        var directory = Path.Combine(instance.GameDirectory, folder);
        Directory.CreateDirectory(directory);
        var applied = 0;
        foreach (var update in updates)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report($"正在下载 {update.LatestFileName}");
            var target = Path.Combine(directory, update.LatestFileName);
            var temporary = target + ".download";
            try
            {
                using (var response = await MirrorGate.SendAsync(Client, () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, update.DownloadUrl);
                    AddHeaders(request);
                    return request;
                }, token))
                {
                    response.EnsureSuccessStatusCode();
                    await using var input = await response.Content.ReadAsStreamAsync(token);
                    await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None);
                    await input.CopyToAsync(output, token);
                }
                if (update.LatestSha1.Length > 0)
                {
                    await using var stream = File.OpenRead(temporary);
                    var actual = Convert.ToHexString(await SHA1.HashDataAsync(stream, token)).ToLowerInvariant();
                    if (!string.Equals(actual, update.LatestSha1, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"{update.LatestFileName} 校验失败，已放弃这次更新。");
                }
                var old = Path.Combine(directory, update.FileName);
                if (!string.Equals(old, target, StringComparison.OrdinalIgnoreCase) && File.Exists(old))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(old, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                File.Move(temporary, target, true);
                applied++;
            }
            finally
            {
                if (File.Exists(temporary)) { try { File.Delete(temporary); } catch (IOException) { } }
            }
        }
        return applied;
    }

    /// <summary>实例的加载器名映射为 Modrinth 的 loader 键；原版或未知加载器返回空串。</summary>
    private static string LoaderKey(string loader) => loader switch
    {
        "Fabric" => "fabric", "Forge" => "forge", "NeoForge" => "neoforge", "Quilt" => "quilt", _ => ""
    };

    private static void AddHeaders(HttpRequestMessage request) =>
        request.Headers.TryAddWithoutValidation("User-Agent", CommunitySource.UserAgent);

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
