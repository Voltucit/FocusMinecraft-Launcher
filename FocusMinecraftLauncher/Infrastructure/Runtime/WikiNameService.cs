using System.Net.Http;
using System.Text.RegularExpressions;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>
/// 从 MC 百科的搜索页取回资源的中文名。百科页面是服务端渲染的 HTML，这里只做轻量解析。
/// 默认开启，可在设置里关闭；请求串行且间隔不低于 2 秒，结果写进 FMLauncher/Cache。
/// </summary>
public static partial class WikiNameService
{
    public static bool Enabled { get; set; } = true;

    private const string SearchUrl = "https://search.mcmod.cn/s";
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";
    private const int BatchLimit = 8;
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HitLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan MissLifetime = TimeSpan.FromDays(3);
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, (DateTime At, string? Name)> Memory = [];
    private static DateTime _lastRequest = DateTime.MinValue;
    private static DateTime _blockedUntil = DateTime.MinValue;

    static WikiNameService()
    {
        Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        Client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        Client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    }

    /// <summary>批量取中文名，返回「调用方给的键 → 中文名」；取不到的项目不会出现在结果里。</summary>
    public static async Task<Dictionary<string, string>> LookupAsync(IReadOnlyList<(string Key, string Title)> items, CancellationToken token = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (items.Count == 0) return result;
        var pending = new List<(string Key, string Title)>();
        foreach (var item in items)
        {
            var title = item.Title.Trim();
            if (title.Length == 0) continue;
            var key = Normalize(title);
            if (key.Length == 0) continue;
            if (Memory.TryGetValue(key, out var cached))
            {
                if (DateTime.UtcNow - cached.At < (cached.Name is null ? MissLifetime : HitLifetime))
                {
                    if (cached.Name is { Length: > 0 } name) result[item.Key] = name;
                    continue;
                }
            }
            pending.Add((item.Key, title));
        }
        if (pending.Count == 0) return result;
        foreach (var (key, title) in pending.Take(BatchLimit))
        {
            if (token.IsCancellationRequested) break;
            var normalized = Normalize(title);
            if (TryReadDisk(normalized, title) is { } cachedName)
            {
                if (cachedName.Length > 0) result[key] = cachedName;
                continue;
            }
            if (!Enabled || DateTime.UtcNow < _blockedUntil) continue;
            var found = await SearchAsync(title, token).ConfigureAwait(false);
            if (found is null) continue; // 请求失败或被限流：不写缓存，留给下次
            Memory[normalized] = (DateTime.UtcNow, found);
            WriteDisk(normalized, found);
            if (found.Length > 0) result[key] = found;
        }
        return result;
    }

    private static async Task<string?> SearchAsync(string title, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var wait = Gap - (DateTime.UtcNow - _lastRequest);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
            _lastRequest = DateTime.UtcNow;
            var keyword = SearchKeyword(title);
            var url = SearchUrl + "?key=" + Uri.EscapeDataString(keyword);
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var html = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            // 访问过快时百科会返回提示页，这时不能把它当成“没有中文名”记下来。
            if (html.Contains("频繁") || html.Contains("请稍后再试"))
            {
                _blockedUntil = DateTime.UtcNow.AddMinutes(10);
                System.Diagnostics.Trace.TraceWarning("MC 百科限制了访问频率，暂停查询 10 分钟。");
                return null;
            }
            return Pick(html, title) ?? "";
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning("查询中文名失败：{0}", error.GetType().Name);
            return null;
        }
        finally { Gate.Release(); }
    }

    private static string? TryReadDisk(string normalized, string title)
    {
        var hit = CommunityCache.ReadText("wiki-name:" + normalized, HitLifetime);
        if (hit is not null) return hit.Trim();
        var miss = CommunityCache.ReadText("wiki-miss:" + normalized, MissLifetime);
        return miss is null ? null : "";
    }

    private static void WriteDisk(string normalized, string name)
    {
        if (name.Length > 0) CommunityCache.WriteText("wiki-name:" + normalized, name);
        else CommunityCache.WriteText("wiki-miss:" + normalized, "miss");
    }

    /// <summary>从搜索结果里挑英文名与项目名相符的条目，取它的中文名。</summary>
    private static string? Pick(string html, string title)
    {
        var target = Normalize(title);
        if (target.Length == 0) return null;
        var best = 0;
        string? name = null;
        foreach (Match match in EntryPattern().Matches(html))
        {
            var id = match.Groups[1].Value;
            if (!id.All(char.IsAsciiDigit)) continue;
            var text = StripTags(match.Groups[2].Value);
            var (chinese, english) = SplitEntry(text);
            if (chinese.Length == 0) continue;
            var normalized = Normalize(english);
            if (normalized.Length == 0) continue;
            var score = normalized == target ? 100
                : normalized.Length >= 4 && target.Length >= 4
                    && (normalized.StartsWith(target, StringComparison.Ordinal) || target.StartsWith(normalized, StringComparison.Ordinal))
                    ? 60 : 0;
            if (score <= best) continue;
            best = score;
            name = chinese;
        }
        return name;
    }

    /// <summary>把「[JEI] JEI物品管理器 (Just Enough Items)」拆成（中文名, 英文名）。</summary>
    private static (string Chinese, string English) SplitEntry(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith('['))
        {
            var close = trimmed.IndexOf(']');
            if (close >= 0) trimmed = trimmed[(close + 1)..].Trim();
        }
        var open = Math.Max(trimmed.LastIndexOf('('), trimmed.LastIndexOf('（'));
        var end = Math.Max(trimmed.LastIndexOf(')'), trimmed.LastIndexOf('）'));
        if (open < 0 || end <= open) return ("", trimmed);
        var english = trimmed[(open + 1)..end].Trim();
        var chinese = trimmed[..open].Trim();
        return HasChinese(chinese) && !HasChinese(english) && chinese.Length > 0 ? (chinese, english) : ("", trimmed);
    }

    private static bool HasChinese(string text) => text.Any(ch => ch is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff');

    private static string StripTags(string html) => System.Net.WebUtility.HtmlDecode(
        System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ").Trim());

    /// <summary>项目名常带「(JEI)」「[Forge]」后缀，检索前去掉更容易命中。</summary>
    private static string SearchKeyword(string title)
    {
        var text = title.Trim();
        foreach (var (open, close) in new[] { ('(', ')'), ('[', ']'), ('（', '）'), ('【', '】') })
        {
            var start = text.LastIndexOf(open);
            var end = text.LastIndexOf(close);
            if (end > start && start > 0) text = text[..start].Trim();
        }
        return text.Length == 0 ? title.Trim() : text;
    }

    private static string Normalize(string text) =>
        new(text.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    [GeneratedRegex("href=\"[^\"]*mcmod\\.cn/class/(\\d+)\\.html\"[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex EntryPattern();
}
