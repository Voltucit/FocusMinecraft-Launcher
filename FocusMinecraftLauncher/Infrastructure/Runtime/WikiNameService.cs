using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
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
    private static readonly ConcurrentDictionary<string, (DateTime At, string? Name)> Memory = new();
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
            if (TryReadDisk(normalized) is { } cachedName)
            {
                if (cachedName.Length > 0) result[key] = cachedName;
                continue;
            }
            if (!Enabled) continue;
            var html = await FetchSearchPageAsync(SearchKeyword(title), token).ConfigureAwait(false);
            if (html is null) continue; // 请求失败或被限流：不写缓存，留给下次
            var found = Pick(html, title) ?? "";
            Memory[normalized] = (DateTime.UtcNow, found);
            WriteDisk(normalized, found);
            if (found.Length > 0) result[key] = found;
        }
        return result;
    }

    internal sealed record ModSearchName(string Chinese, string English)
    {
        public bool MatchesEnglish(string title) => Normalize(English) == Normalize(title);
    }

    internal static bool CanResolveModSearch(string keyword) => Enabled && HasChinese(keyword);

    internal static bool HasLiteralTitleMatch(string title, string keyword) =>
        NormalizeSearchTitle(title).Contains(NormalizeSearchTitle(keyword), StringComparison.Ordinal);

    /// <summary>用百科的模组标题或简称找出英文项目名，供两个下载来源重试搜索。</summary>
    internal static async Task<ModSearchName?> ResolveModSearchNameAsync(string keyword)
    {
        if (!CanResolveModSearch(keyword)) return null;
        var target = NormalizeSearchTitle(keyword);
        var cacheKey = "wiki-mod-search:" + target;
        var cached = CommunityCache.ReadText(cacheKey, HitLifetime);
        if (cached?.Split('\n', 2) is [{ Length: > 0 } chinese, { Length: > 0 } english])
            return new(chinese, english);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        (ModSearchName? Name, int Cost) best = (null, int.MaxValue);
        try
        {
            var html = await FetchSearchPageAsync(keyword, timeout.Token).ConfigureAwait(false);
            if (html is not null) best = PickModSearchName(html, target);
            if (best.Cost > 1 && target.Length >= 4)
            {
                // 省略字词时，先用短关键词取候选，再用完整输入过滤候选标题。
                var prefix = target[..Math.Min((target.Length + 1) / 2, 4)];
                var candidates = await FetchSearchPageAsync(prefix, timeout.Token).ConfigureAwait(false);
                if (candidates is not null)
                {
                    var candidate = PickModSearchName(candidates, target);
                    if (candidate.Cost < best.Cost) best = candidate;
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // 扩大检索超时后，仍可使用原搜索已经找到的标题。
        }
        if (best.Name is { } match) CommunityCache.WriteText(cacheKey, match.Chinese + "\n" + match.English);
        return best.Name;
    }

    private static (ModSearchName? Name, int Cost) PickModSearchName(string html, string target)
    {
        ModSearchName? best = null;
        var bestCost = int.MaxValue;
        foreach (Match match in EntryPattern().Matches(html))
        {
            var (chinese, english, alias) = SplitEntry(StripTags(match.Groups[2].Value));
            if (chinese.Length == 0 || Normalize(english).Length == 0) continue;
            if (NormalizeSearchTitle(alias) == target) return (new(chinese, english), 0);

            var cost = TitleMatchCost(NormalizeSearchTitle(chinese), target);
            if (cost == 0) return (new(chinese, english), 0);
            if (cost >= bestCost) continue;
            best = new(chinese, english);
            bestCost = cost;
        }
        return (best, bestCost);
    }

    private static int TitleMatchCost(string title, string target)
    {
        if (title == target) return 0;
        if (target.Length >= 2 && title.Contains(target, StringComparison.Ordinal)) return 1;

        // 两段各至少匹配两个连续字符，按顺序出现。标题越接近输入，排名越高。
        for (var split = 2; split <= target.Length - 2; split++)
        {
            var start = title.IndexOf(target[..split], StringComparison.Ordinal);
            if (start >= 0 && title.IndexOf(target[split..], start + split, StringComparison.Ordinal) >= 0)
                return 2 + title.Length - target.Length;
        }
        return int.MaxValue;
    }

    private static async Task<string?> FetchSearchPageAsync(string keyword, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!Enabled || DateTime.UtcNow < _blockedUntil) return null;
            var wait = Gap - (DateTime.UtcNow - _lastRequest);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
            if (!Enabled) return null;
            _lastRequest = DateTime.UtcNow;
            var url = SearchUrl + "?key=" + Uri.EscapeDataString(keyword);
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                PauseSearches();
                return null;
            }
            if (!response.IsSuccessStatusCode) return null;
            var html = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            // 百科也会以 200 返回限流提示页。
            if ((html.Contains("搜索太频繁", StringComparison.Ordinal) || html.Contains("请稍后再试", StringComparison.Ordinal))
                && !EntryPattern().IsMatch(html))
            {
                PauseSearches();
                return null;
            }
            return html;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning("查询中文名失败：{0}", error.GetType().Name);
            return null;
        }
        finally { Gate.Release(); }
    }

    private static void PauseSearches()
    {
        _blockedUntil = DateTime.UtcNow.AddMinutes(10);
        System.Diagnostics.Trace.TraceWarning("MC 百科限制了访问频率，暂停查询 10 分钟。");
    }

    private static string? TryReadDisk(string normalized)
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
            var (chinese, english, _) = SplitEntry(text);
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

    /// <summary>解析「[别名] 中文名 (英文名)」，允许英文名带括号后缀。</summary>
    private static (string Chinese, string English, string Alias) SplitEntry(string text)
    {
        var trimmed = text.Trim();
        var alias = "";
        if (trimmed.StartsWith('[') || trimmed.StartsWith('【'))
        {
            var close = trimmed.IndexOf(trimmed[0] == '[' ? ']' : '】');
            if (close > 0)
            {
                alias = trimmed[1..close].Trim();
                trimmed = trimmed[(close + 1)..].Trim();
            }
        }
        var end = Math.Max(trimmed.LastIndexOf(')'), trimmed.LastIndexOf('）'));
        if (end < 1)
            return ("", trimmed, alias);

        var depth = 0;
        for (int index = end; index >= 0; index--)
        {
            if (trimmed[index] is ')' or '）') depth++;
            else if (trimmed[index] is '(' or '（' && --depth == 0)
            {
                var english = trimmed[(index + 1)..end].Trim();
                var chinese = trimmed[..index].Trim();
                if (HasChinese(chinese) && !HasChinese(english)) return (chinese, english, alias);
                break;
            }
        }
        return ("", trimmed, alias);
    }

    private static bool HasChinese(string text) => text.Any(ch => ch is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff');

    private static string StripTags(string html) =>
        Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "")), @"\s+", " ").Trim();

    private static string NormalizeSearchTitle(string text) =>
        new(text.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

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
