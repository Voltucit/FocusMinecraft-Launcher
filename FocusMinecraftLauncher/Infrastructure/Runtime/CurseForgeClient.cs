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
public sealed class CurseForgeClient
{
    private const string Root = "https://mod.mcimirror.top/curseforge/v1/";
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri(Root),
        Timeout = TimeSpan.FromSeconds(40)
    };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const string UserAgent = "FocusMinecraftLauncher/2.0.5 (+https://github.com/Voltucit/FocusMinecraft-Launcher)";

    /// <summary>下载地址只把 edge.forgecdn.net 换成镜像；mediafilez 等其它 CDN 官方明确不镜像。</summary>
    public static string Rewrite(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return address;
        if (!uri.Host.Equals("edge.forgecdn.net", StringComparison.OrdinalIgnoreCase)) return address;
        return "https://mod.mcimirror.top" + uri.PathAndQuery;
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

    public async Task<CurseForgePage> SearchAsync(DownloadSection section, string? query, string? gameVersion, string? loader, int offset, int limit, CancellationToken token, int? categoryId = null)
    {
        var builder = new StringBuilder($"mods/search?gameId=432&classId={ClassId(section)}&sortField=2&sortOrder=desc&index={offset}&pageSize={Math.Clamp(limit, 1, 50)}");
        if (!string.IsNullOrWhiteSpace(query)) builder.Append("&searchFilter=").Append(Uri.EscapeDataString(query.Trim()));
        if (!string.IsNullOrWhiteSpace(gameVersion)) builder.Append("&gameVersion=").Append(Uri.EscapeDataString(gameVersion));
        if (categoryId is { } category) builder.Append("&categoryId=").Append(category.ToString(CultureInfo.InvariantCulture));
        if (LoaderType(loader) is { } type && section is DownloadSection.Mods or DownloadSection.Modpacks)
            builder.Append("&modLoaderType=").Append(type);
        using var document = await GetAsync(builder.ToString(), token);
        var data = document.RootElement.GetProperty("data");
        var mods = new List<CurseForgeMod>();
        foreach (var item in data.EnumerateArray()) mods.Add(ReadMod(item));
        var total = document.RootElement.TryGetProperty("pagination", out var pagination) && pagination.TryGetProperty("totalCount", out var count) ? count.GetInt64() : mods.Count;
        return new(mods, total);
    }

    /// <summary>分类列表用于把界面的标签名换成接口需要的 categoryId。</summary>
    public async Task<IReadOnlyList<CurseForgeCategory>> GetCategoriesAsync(DownloadSection section, CancellationToken token)
    {
        using var document = await GetAsync($"categories?gameId=432&classId={ClassId(section)}", token);
        var items = new List<CurseForgeCategory>();
        foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var id)) continue;
            items.Add(new(id.GetInt32(), Text(item, "name") ?? "未命名分类"));
        }
        return items;
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

    /// <summary>用文件指纹反查 CurseForge 文件；批量上限 1000，失败返回空表而不影响其它来源的检查。</summary>
    public async Task<IReadOnlyDictionary<uint, long>> MatchFingerprintsAsync(IReadOnlyList<uint> fingerprints, CancellationToken token)
    {
        var result = new Dictionary<uint, long>();
        if (fingerprints.Count == 0) return result;
        using var request = new HttpRequestMessage(HttpMethod.Post, "fingerprints")
        {
            Content = JsonContent.Create(new { fingerprints = fingerprints.Take(1000).ToArray() })
        };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var response = await Http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) return result;
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        if (!document.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("exactMatches", out var matches)) return result;
        foreach (var match in matches.EnumerateArray())
        {
            if (!match.TryGetProperty("file", out var file) || !file.TryGetProperty("id", out var fileId)) continue;
            if (!match.TryGetProperty("id", out var modId)) continue;
            if (ReadFile(file) is { } parsed && file.TryGetProperty("modId", out var owner)) result[parsed.Fingerprint] = owner.GetInt64();
            else _ = fileId;
        }
        return result;
    }

    /// <summary>下载到指定路径并按 SHA-1 校验；文件大小与哈希都由接口给出。</summary>
    public async Task DownloadAsync(CurseForgeFile file, string destination, Action<double>? progress, CancellationToken token)
    {
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

    private static async Task<JsonDocument> GetAsync(string path, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var response = await Http.SendAsync(request, token);
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
            item.TryGetProperty("fileDate", out var date) && DateTimeOffset.TryParse(date.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published) ? DateOnly.FromDateTime(published.Date) : DateOnly.FromDateTime(DateTime.Today),
            item.TryGetProperty("downloadCount", out var downloads) ? downloads.GetInt64() : 0,
            item.TryGetProperty("fileFingerprint", out var fingerprint) ? (uint)fingerprint.GetInt64() : 0u,
            gameVersions,
            loaders,
            item.TryGetProperty("dependencies", out var dependencies)
                ? dependencies.EnumerateArray().Select(dependency => new CurseForgeDependency(
                    dependency.TryGetProperty("modId", out var mod) ? mod.GetInt64() : 0,
                    dependency.TryGetProperty("relationType", out var relation) ? relation.GetInt32() : 3)).Where(dependency => dependency.ModId > 0).ToArray()
                : []);
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

public sealed record CurseForgeMod(long Id, string Name, string Slug, string Summary, string Page, string? Icon,
    string? Author, long Downloads, DateOnly? Updated, string[] Categories, IReadOnlyList<CurseForgeFile> LatestFiles, int ClassId);

public sealed record CurseForgeFile(long Id, string FileName, string DisplayName, string DownloadUrl, long FileLength,
    string? Sha1, DateOnly Date, long Downloads, uint Fingerprint, string[] GameVersions, string[] Loaders,
    IReadOnlyList<CurseForgeDependency> Dependencies);

public sealed record CurseForgeDependency(long ModId, int RelationType)
{
    public bool Required => RelationType == 3;
    public bool Optional => RelationType == 2;
    public bool Incompatible => RelationType == 1;
}
