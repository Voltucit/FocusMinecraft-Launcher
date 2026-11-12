using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FocusMinecraftLauncher.Features.Downloads.Models;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>CurseForge 公开接口的只读客户端，走 mcimirror 镜像转发，不携带官方 API Key。</summary>
public sealed partial class CurseForgeClient
{
    private const string Root = "https://mod.mcimirror.top/curseforge/v1/";
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri(Root),
        Timeout = TimeSpan.FromSeconds(40)
    };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string UserAgent => CommunitySource.UserAgent;

    /// <summary>下载地址只把 edge.forgecdn.net 换成镜像；mediafilez 等其它 CDN 官方明确不镜像。</summary>
    public static string Rewrite(string address)
    {
        if (CommunitySource.Current == CommunitySource.Official) return address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return address;
        if (!uri.Host.Equals("edge.forgecdn.net", StringComparison.OrdinalIgnoreCase)) return address;
        return "https://mod.mcimirror.top" + uri.PathAndQuery;
    }

    /// <summary>所有 CurseForge 文件共用候选来源；只在允许镜像时加入 MCIM。</summary>
    public static string[] DownloadUrls(long fileId, string fileName, string? direct)
    {
        var official = new List<string>();
        var mirrors = new List<string>();
        void Add(string? address)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
            if (uri.Host.Equals("mod.mcimirror.top", StringComparison.OrdinalIgnoreCase)) mirrors.Add(uri.AbsoluteUri);
            else official.Add(uri.AbsoluteUri);
        }
        Add(direct?.Replace("-service.overwolf.wtf", ".forgecdn.net", StringComparison.OrdinalIgnoreCase));
        if (fileId > 0)
        {
            var path = "/files/" + (fileId / 1000).ToString(CultureInfo.InvariantCulture) + "/"
                + (fileId % 1000).ToString(CultureInfo.InvariantCulture) + "/" + Uri.EscapeDataString(fileName);
            Add("https://mediafilez.forgecdn.net" + path);
            Add("https://edge.forgecdn.net" + path);
            Add("https://media.forgecdn.net" + path);
        }
        if (CommunitySource.Current == CommunitySource.Official) return official.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var address in official)
        {
            var mapped = Rewrite(address);
            if (mapped != address) mirrors.Add(mapped);
        }
        return mirrors.Concat(official).Distinct(StringComparer.Ordinal).ToArray();
    }

    public static int ClassId(DownloadSection section) => section switch
    {
        DownloadSection.Modpacks => 4471,
        DownloadSection.Resourcepacks => 12,
        DownloadSection.Shaders => 6552,
        DownloadSection.Datapacks => 6945,
        _ => 6
    };

    /// <summary>CurseForge 的加载器编号：Forge 1、Fabric 4、Quilt 5、NeoForge 6。</summary>
    public static int? LoaderType(string? loader) => loader?.ToLowerInvariant() switch
    {
        "forge" => 1,
        "fabric" => 4,
        "quilt" => 5,
        "neoforge" => 6,
        _ => null
    };

    public static string? LoaderKey(string token) => token.ToLowerInvariant() switch
    {
        "forge" => "forge",
        "fabric" => "fabric",
        "quilt" => "quilt",
        "neoforge" => "neoforge",
        "liteloader" => "liteloader",
        _ => null
    };

    public async Task<CurseForgePage> SearchAsync(DownloadSection section, string? query, string? gameVersion, string? loader, int offset, int limit, CancellationToken token, int? categoryId = null, bool background = false)
    {
        var builder = new StringBuilder($"mods/search?gameId=432&classId={ClassId(section)}&sortField=2&sortOrder=desc&index={offset}&pageSize={Math.Clamp(limit, 1, 50)}");
        if (!string.IsNullOrWhiteSpace(query)) builder.Append("&searchFilter=").Append(Uri.EscapeDataString(query.Trim()));
        if (!string.IsNullOrWhiteSpace(gameVersion)) builder.Append("&gameVersion=").Append(Uri.EscapeDataString(gameVersion));
        if (categoryId is { } category) builder.Append("&categoryId=").Append(category.ToString(CultureInfo.InvariantCulture));
        if (LoaderType(loader) is { } type && section is DownloadSection.Mods or DownloadSection.Modpacks)
            builder.Append("&modLoaderType=").Append(type);
        using var document = await GetAsync(builder.ToString(), token, background);
        var data = document.RootElement.GetProperty("data");
        var mods = new List<CurseForgeMod>();
        foreach (var item in data.EnumerateArray()) mods.Add(ReadMod(item));
        var total = document.RootElement.TryGetProperty("pagination", out var pagination) && pagination.TryGetProperty("totalCount", out var count) ? count.GetInt64() : mods.Count;
        return new(mods, total);
    }

    /// <summary>分类列表用于把界面的标签名换成接口需要的 categoryId。</summary>
    public async Task<IReadOnlyList<CurseForgeCategory>> GetCategoriesAsync(DownloadSection section, CancellationToken token, bool background = false)
    {
        using var document = await GetAsync($"categories?gameId=432&classId={ClassId(section)}", token, background);
        var items = new List<CurseForgeCategory>();
        foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var id)) continue;
            items.Add(new(id.GetInt32(), Text(item, "name") ?? "未命名分类"));
        }
        return items;
    }

    /// <summary>镜像没有旧文件时，从 CurseForge 网页接口补文件名和大小；哈希需要下载后本地计算。</summary>
    public async Task<CurseForgeFallbackFile> GetFallbackFileAsync(long projectId, long fileId, CancellationToken token)
    {
        using var client = new HttpClient { BaseAddress = new Uri("https://www.curseforge.com/api/v1/") };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"mods/{projectId.ToString(CultureInfo.InvariantCulture)}/files/{fileId.ToString(CultureInfo.InvariantCulture)}");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var response = await client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("id", out var id) || !id.TryGetInt64(out var actualId)
            || actualId != fileId
            || !data.TryGetProperty("projectId", out var project) || !project.TryGetInt64(out var actualProject)
            || actualProject != projectId
            || Text(data, "fileName") is not { Length: > 0 } fileName
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !data.TryGetProperty("fileLength", out var length) || !length.TryGetInt64(out var size) || size <= 0)
            throw new InvalidDataException("CurseForge 网页接口没有返回有效的文件信息。");
        string path = $"{fileId / 1000}/{fileId % 1000}/{Uri.EscapeDataString(fileName)}";
        return new(fileId, projectId, fileName, size, "https://edge.forgecdn.net/files/" + path);
    }

    /// <summary>把官方兜底文件下载到本地并计算 SHA-1；返回本次实际校验值。</summary>
    public async Task<string> DownloadFallbackAsync(CurseForgeFallbackFile fallback, string destination, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, fallback.DownloadUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = File.Create(temporary))
            {
                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    written += read;
                }
                if (written != fallback.FileLength) throw new InvalidDataException("CurseForge 兜底文件大小与网页接口不一致。");
            }
            await using (var stream = File.OpenRead(temporary))
            {
                var hash = Convert.ToHexString(await SHA1.HashDataAsync(stream, token));
                token.ThrowIfCancellationRequested();
                File.Move(temporary, destination, true);
                return hash;
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<CurseForgeMod> GetModAsync(long id, CancellationToken token)
    {
        using var document = await GetAsync("mods/" + id.ToString(CultureInfo.InvariantCulture), token);
        return ReadMod(document.RootElement.GetProperty("data"));
    }

    /// <summary>整合包的 manifest.json 只记录文件编号，需要按编号取回下载地址与校验值。</summary>
    public async Task<CurseForgeFile> GetFileAsync(long modId, long fileId, CancellationToken token)
    {
        using var document = await GetAsync($"mods/{modId.ToString(CultureInfo.InvariantCulture)}/files/{fileId.ToString(CultureInfo.InvariantCulture)}", token);
        return ReadFile(document.RootElement.GetProperty("data")) ?? throw new InvalidDataException("CurseForge 没有返回这个文件的下载信息。");
    }

    public async Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(long id, string? gameVersion, string? loader, CancellationToken token)
    {
        var builder = new StringBuilder($"mods/{id}/files?pageSize=50");
        if (!string.IsNullOrWhiteSpace(gameVersion)) builder.Append("&gameVersion=").Append(Uri.EscapeDataString(gameVersion));
        if (LoaderType(loader) is { } type) builder.Append("&modLoaderType=").Append(type);
        using var document = await GetAsync(builder.ToString(), token);
        var files = new List<CurseForgeFile>();
        foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
            if (ReadFile(item) is { } file) files.Add(file);
        return files;
    }

    /// <summary>用文件指纹反查 CurseForge 文件；批量上限 1000，需要明确反馈失败的详情跳转可启用 requireSuccess。</summary>
    public async Task<IReadOnlyDictionary<uint, long>> MatchFingerprintsAsync(IReadOnlyList<uint> fingerprints, CancellationToken token, bool requireSuccess = false)
        => (await MatchFingerprintFilesAsync(fingerprints,token,requireSuccess)).ToDictionary(pair=>pair.Key,pair=>pair.Value.ModId);

    /// <summary>以响应中的文件指纹关联本地文件，保留已安装文件的发布时间；不依赖响应数组的位置。</summary>
    internal async Task<IReadOnlyDictionary<uint, CurseForgeFingerprintMatch>> MatchFingerprintFilesAsync(IReadOnlyList<uint> fingerprints, CancellationToken token, bool requireSuccess = false)
    {
        var result = new Dictionary<uint, CurseForgeFingerprintMatch>();
        if (fingerprints.Count == 0) return result;
        using var response = await MirrorGate.SendAsync(Http,()=>
        {
            var request=new HttpRequestMessage(HttpMethod.Post,"fingerprints")
            {Content=JsonContent.Create(new{fingerprints=fingerprints.Take(1000).ToArray()})};
            request.Headers.TryAddWithoutValidation("User-Agent",UserAgent);
            return request;
        },token);
        if (!response.IsSuccessStatusCode)
        { if(requireSuccess)response.EnsureSuccessStatusCode();return result; }
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind!=JsonValueKind.Object
            || !data.TryGetProperty("exactMatches", out var matches) || matches.ValueKind!=JsonValueKind.Array)
        { if(requireSuccess)throw new InvalidDataException("CurseForge 返回了无法读取的文件匹配结果。");return result; }
        foreach (var match in matches.EnumerateArray())
        {
            if(match.ValueKind!=JsonValueKind.Object)continue;
            if (!match.TryGetProperty("file", out var file) || file.ValueKind!=JsonValueKind.Object || ReadFile(file) is not {} parsed) continue;
            var modId=file.TryGetProperty("modId",out var owner)&&owner.TryGetInt64(out var ownerId)?ownerId
                :match.TryGetProperty("id",out var id)&&id.TryGetInt64(out var matchId)?matchId:0;
            if(modId>0&&fingerprints.Contains(parsed.Fingerprint))result[parsed.Fingerprint]=new(modId,parsed);
        }
        return result;
    }

    /// <summary>下载到指定路径并按 SHA-1 校验；文件大小与哈希都由接口给出。</summary>
    public async Task DownloadAsync(CurseForgeFile file, string destination, Action<double>? progress, CancellationToken token)
    {
        if (file.Sha1 is { Length: 40 })
        {
            var urls = DownloadUrls(file.Id, file.FileName, file.DownloadUrl);
            await FMCore.Downloads.FileDownloader.DownloadAsync(new FMCore.Downloads.DownloadRequest
            {
                Url = urls[0], FallbackUrls = urls.Skip(1).ToArray(), FilePath = destination,
                Size = file.FileLength, Sha1 = file.Sha1, UseSourcePerformance = false
            }, value => progress?.Invoke(value.Percentage), token);
            return;
        }
        var address = Rewrite(file.DownloadUrl ?? throw new InvalidDataException("该文件没有可用的下载地址。"));
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? file.FileLength;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using (var input = await response.Content.ReadAsStreamAsync(token))
        await using (var output = File.Create(destination))
        {
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                written += read;
                if (total > 0) progress?.Invoke(Math.Clamp(written * 100d / total, 0, 100));
            }
        }
        if (file.Sha1 is { Length: > 0 } expected)
        {
            await using var stream = File.OpenRead(destination);
            var actual = Convert.ToHexString(await SHA1.HashDataAsync(stream, token));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(destination);
                throw new InvalidDataException("下载的文件校验失败，请重试。");
            }
        }
    }

    private static async Task<JsonDocument> GetAsync(string path, CancellationToken token, bool background = false)
    {
        using var response = await MirrorGate.SendAsync(Http, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            return request;
        }, token, background);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidDataException("CurseForge 镜像暂时没有这个资源，请稍后再试。");
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
    }

    private static CurseForgeMod ReadMod(JsonElement item) => new(
        item.GetProperty("id").GetInt64(),
        Text(item, "name") ?? "未命名资源",
        Text(item, "slug") ?? "",
        Text(item, "summary") ?? "",
        Text(item, "links", "websiteUrl") ?? "",
        ReadLogo(item),
        item.TryGetProperty("authors", out var authors)
            ? string.Join(" / ", authors.EnumerateArray().Select(author => Text(author, "name")).Where(name => !string.IsNullOrWhiteSpace(name)))
            : null,
        item.TryGetProperty("downloadCount", out var downloads) ? downloads.GetInt64() : 0,
        item.TryGetProperty("dateModified", out var modified) && DateTimeOffset.TryParse(modified.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var updated) ? DateOnly.FromDateTime(updated.Date) : null,
        item.TryGetProperty("categories", out var categories)
            ? categories.EnumerateArray().Select(category => Text(category, "name")).OfType<string>().ToArray()
            : [],
        item.TryGetProperty("latestFiles", out var latest) ? latest.EnumerateArray().Select(ReadFile).OfType<CurseForgeFile>().ToArray() : [],
        item.TryGetProperty("classId", out var classId) ? classId.GetInt32() : 6);

    private static string? ReadLogo(JsonElement item)
    {
        if (!item.TryGetProperty("logo", out var logo)) return null;
        foreach (var key in new[] { "thumbnailUrl", "url" })
            if (Text(logo, key) is { Length: > 0 } value) return value;
        return null;
    }

    private static CurseForgeFile? ReadFile(JsonElement item)
    {
        if (!item.TryGetProperty("id", out var id) || !item.TryGetProperty("fileName", out var name)) return null;
        DateTimeOffset? publishedAt=DateTimeOffset.TryParse(Text(item,"fileDate"),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var published)?published:null;
        var tokens = item.TryGetProperty("gameVersions", out var versions)
            ? versions.EnumerateArray().Select(version => version.GetString() ?? "").ToArray()
            : [];
        var gameVersions = tokens.Where(token => IsGameVersion(token)).ToArray();
        var loaders = tokens.Select(LoaderKey).Where(key => key != null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(
            id.GetInt64(),
            name.GetString() ?? "",
            Text(item, "displayName") ?? "",
            Text(item, "downloadUrl") ?? "",
            item.TryGetProperty("fileLength", out var length) ? length.GetInt64() : 0,
            FileHash(item, 1),
            publishedAt is {} date?DateOnly.FromDateTime(date.Date):DateOnly.FromDateTime(DateTime.Today),
            item.TryGetProperty("downloadCount", out var downloads) ? downloads.GetInt64() : 0,
            item.TryGetProperty("fileFingerprint", out var fingerprint) ? (uint)fingerprint.GetInt64() : 0u,
            gameVersions,
            loaders,
            item.TryGetProperty("dependencies", out var dependencies)
                ? dependencies.EnumerateArray().Select(dependency => new CurseForgeDependency(
                    dependency.TryGetProperty("modId", out var mod) ? mod.GetInt64() : 0,
                    dependency.TryGetProperty("relationType", out var relation) ? relation.GetInt32() : 3)).Where(dependency => dependency.ModId > 0).ToArray()
                : []) {PublishedAt=publishedAt};
    }

    private static string? FileHash(JsonElement item, int algorithm)
    {
        if (!item.TryGetProperty("hashes", out var hashes)) return null;
        foreach (var hash in hashes.EnumerateArray())
            if (hash.TryGetProperty("algo", out var algo) && algo.GetInt32() == algorithm) return Text(hash, "value");
        return null;
    }

    private static bool IsGameVersion(string token) =>
        token.Length > 0 && char.IsDigit(token[0]) && token.Count(c => c == '.') >= 1 && token.All(c => char.IsDigit(c) || c == '.');

    private static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? value.GetString() : null;
    private static string? Text(JsonElement item, string parent, string name) =>
        item.TryGetProperty(parent, out var node) ? Text(node, name) : null;
}

public sealed record CurseForgePage(IReadOnlyList<CurseForgeMod> Mods, long Total);

public sealed record CurseForgeCategory(int Id, string Name);
public sealed record CurseForgeFallbackFile(long Id, long ProjectId, string FileName, long FileLength, string DownloadUrl);

public sealed record CurseForgeMod(long Id, string Name, string Slug, string Summary, string Page, string? Icon,
    string? Author, long Downloads, DateOnly? Updated, string[] Categories, IReadOnlyList<CurseForgeFile> LatestFiles, int ClassId);

public sealed record CurseForgeFile(long Id, string FileName, string DisplayName, string DownloadUrl, long FileLength,
    string? Sha1, DateOnly Date, long Downloads, uint Fingerprint, string[] GameVersions, string[] Loaders,
    IReadOnlyList<CurseForgeDependency> Dependencies)
{
    /// <summary>保留完整发布时刻用于更新排序，缺失时不能假定文件更新。</summary>
    public DateTimeOffset? PublishedAt {get;init;}
}

internal sealed record CurseForgeFingerprintMatch(long ModId,CurseForgeFile File);

public sealed record CurseForgeDependency(long ModId, int RelationType)
{
    public bool Required => RelationType == 3;
    public bool Optional => RelationType == 2;
    public bool Incompatible => RelationType == 1;
}
