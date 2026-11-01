using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>查询加载器版本的发布日期，只用于安装页展示；查询失败时返回空表，界面继续显示版本号。</summary>
public static class LoaderReleaseDates
{
    private const string NeoForgeMaven = "https://maven.neoforged.net/releases/net/neoforged/neoforge/";
    private static readonly HttpClient Metadata = new() { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 16 * 1024 * 1024 };
    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static readonly Dictionary<string, (IReadOnlyDictionary<string, string> Dates, DateTimeOffset Until)> Cache = new(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    /// <summary>按加载器名称取版本号到发布日期的映射；同一次启动内相同查询复用 10 分钟。</summary>
    public static async Task<IReadOnlyDictionary<string, string>> GetAsync(string kind, string game, IEnumerable<string> versions, CancellationToken token = default)
    {
        var key = kind + "|" + game;
        await CacheLock.WaitAsync(token).ConfigureAwait(false);
        try { if (Cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow < cached.Until) return cached.Dates; }
        finally { CacheLock.Release(); }
        IReadOnlyDictionary<string, string> dates;
        try
        {
            dates = kind switch
            {
                "Fabric" => ParseDirectoryListing(await Metadata.GetStringAsync("https://maven.fabricmc.net/net/fabricmc/fabric-loader/", token).ConfigureAwait(false)),
                "Forge" => ReadBuilds(await GetJsonAsync("https://bmclapi2.bangbang93.com/forge/minecraft/" + Uri.EscapeDataString(game), token).ConfigureAwait(false)),
                "NeoForge" => await NeoForgeAsync(versions, token).ConfigureAwait(false),
                _ => Empty
            };
        }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested) { return Empty; }
        await CacheLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Cache.Count >= 64) Cache.Clear();
            Cache[key] = (dates, DateTimeOffset.UtcNow.AddMinutes(10));
        }
        finally { CacheLock.Release(); }
        return dates;
    }

    /// <summary>Forge 用 BMCLAPI 的构建列表，modified 即构建时间。</summary>
    private static IReadOnlyDictionary<string, string> ReadBuilds(JsonElement root)
    {
        var dates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in root.EnumerateArray())
        {
            if (ReadString(item, "version") is not { Length: > 0 } version || ReadString(item, "modified") is not { Length: > 0 } text) continue;
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var published))
                dates[version] = published.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        return dates;
    }

    /// <summary>NeoForge 的仓库页面没有日期列，用安装包响应头里的最后修改时间；批量并行查询，超时只丢日期。</summary>
    private static async Task<IReadOnlyDictionary<string, string>> NeoForgeAsync(IEnumerable<string> versions, CancellationToken token)
    {
        var dates = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        using var throttle = new SemaphoreSlim(32);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        var queries = versions.Distinct(StringComparer.Ordinal).Select(async version =>
        {
            try { await throttle.WaitAsync(budget.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try
            {
                var encoded = Uri.EscapeDataString(version);
                using var request = new HttpRequestMessage(HttpMethod.Head, NeoForgeMaven + encoded + "/neoforge-" + encoded + "-installer.jar");
                using var response = await Metadata.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode && response.Content.Headers.LastModified is { } published)
                    dates[version] = published.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch (Exception) { }
            finally { throttle.Release(); }
        }).ToArray();
        await Task.WhenAll(queries).ConfigureAwait(false);
        return dates;
    }

    private static async Task<JsonElement> GetJsonAsync(string url, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await Metadata.GetStringAsync(url, token).ConfigureAwait(false));
        return document.RootElement.Clone();
    }

    private static string? ReadString(JsonElement item, string name)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>解析 Maven 目录页：nginx 用 pre 行、Jetty 用表格行，两种都取链接之后那段文字里的日期。</summary>
    private static IReadOnlyDictionary<string, string> ParseDirectoryListing(string html)
    {
        var dates = new Dictionary<string, string>(StringComparer.Ordinal);
        var anchors = Regex.Matches(html, @"<a\b[^>]*href\s*=\s*[""']([^""'/?#][^""']*)[""'][^>]*>", RegexOptions.IgnoreCase);
        for (var index = 0; index < anchors.Count; index++)
        {
            var name = WebUtility.UrlDecode(anchors[index].Groups[1].Value).TrimEnd('/');
            if (name.Length == 0 || name.Contains('/') || name is "." or "..") continue;
            var start = anchors[index].Index + anchors[index].Length;
            var stop = index + 1 < anchors.Count ? anchors[index + 1].Index : html.Length;
            var tail = html.Substring(start, Math.Max(0, Math.Min(stop, start + 240) - start));
            var text = WebUtility.HtmlDecode(Regex.Replace(tail, "<[^>]+>", " "));
            if (TryReadDate(text, out var date)) dates[name] = date;
        }
        return dates;
    }

    private static bool TryReadDate(string text, out string date)
    {
        var iso = Regex.Match(text, @"\b\d{4}-\d{2}-\d{2}\b");
        if (iso.Success && DateTime.TryParseExact(iso.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            date = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }
        var nginx = Regex.Match(text, @"\b\d{1,2}-[A-Za-z]{3}-\d{4}\b");
        if (nginx.Success && DateTime.TryParseExact(nginx.Value, "dd-MMM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dashed))
        {
            date = dashed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }
        var jetty = Regex.Match(text, @"\b[A-Za-z]{3}\s+[A-Za-z]{3}\s+\d{1,2}\s+\d{2}:\d{2}:\d{2}\s+\S+\s+\d{4}\b");
        if (jetty.Success && DateTime.TryParse(jetty.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timed))
        {
            date = timed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }
        date = "";
        return false;
    }
}
