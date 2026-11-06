using System.Net.Http;
using System.Text.Json;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>MCIM 的简介翻译：把资源简介换成中文，取不到译文时界面继续显示原文。</summary>
public static class TranslationService
{
    private const string MirrorRoot = "https://mod.mcimirror.top/translate";
    private const string CurseForgePrefix = "cf-";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>没有译文的项目也记一笔，避免每次翻页都去问一遍。</summary>
    private static readonly Dictionary<string, (DateTime At, string? Text)> Cache = [];
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    static TranslationService()
    {
        Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", CommunitySource.UserAgent);
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>批量取简介译文，返回「项目标识 → 译文」；没有译文或请求失败的项目不会出现在结果里。</summary>
    public static async Task<Dictionary<string, string>> TranslateAsync(IEnumerable<string> projectIds)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<string>();
        foreach (var id in projectIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Cache.TryGetValue(id, out var cached) && DateTime.UtcNow - cached.At < Lifetime)
            {
                if (cached.Text is { Length: > 0 } text) result[id] = text;
                continue;
            }
            // 内存里没有时先看磁盘：重启启动器后常见项目的译文不必再问一遍。
            var stored = CommunityCache.ReadText("translate:" + id, Lifetime);
            if (stored is not null)
            {
                Cache[id] = (DateTime.UtcNow, stored.Length == 0 ? null : stored);
                if (stored.Length > 0) result[id] = stored;
                continue;
            }
            pending.Add(id);
        }
        if (pending.Count == 0) return result;
        await TranslateModrinthAsync(pending, result);
        await TranslateCurseForgeAsync(pending, result);
        foreach (var id in pending)
            if (!result.ContainsKey(id)) Cache[id] = (DateTime.UtcNow, null);
        foreach (var id in pending)
            CommunityCache.WriteText("translate:" + id, result.TryGetValue(id, out var text) ? text : "");
        return result;
    }

    private static async Task TranslateModrinthAsync(IReadOnlyList<string> projectIds, Dictionary<string, string> result)
    {
        var ids = projectIds.Where(id => !id.StartsWith(CurseForgePrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (ids.Length == 0) return;
        try
        {
            using var response = await MirrorGate.SendAsync(Client, () => new HttpRequestMessage(HttpMethod.Post, MirrorRoot + "/modrinth") { Content = Body(new { project_ids = ids }) });
            if (!response.IsSuccessStatusCode) { Trace(response); return; }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (document.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var id = Text(item, "project_id");
                var translated = Text(item, "translated");
                if (id.Length > 0 && translated.Length > 0) Store(id, translated, result);
            }
        }
        catch (Exception error) { TraceFailure("modrinth", error); }
    }

    private static async Task TranslateCurseForgeAsync(IReadOnlyList<string> projectIds, Dictionary<string, string> result)
    {
        var ids = projectIds
            .Where(id => id.StartsWith(CurseForgePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(id => id[CurseForgePrefix.Length..])
            .Where(id => int.TryParse(id, out _))
            .Select(int.Parse)
            .ToArray();
        if (ids.Length == 0) return;
        try
        {
            using var response = await MirrorGate.SendAsync(Client, () => new HttpRequestMessage(HttpMethod.Post, MirrorRoot + "/curseforge") { Content = Body(new { modids = ids }) });
            if (!response.IsSuccessStatusCode) { Trace(response); return; }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (document.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("modid", out var modId) || !modId.TryGetInt32(out var number)) continue;
                var translated = Text(item, "translated");
                if (translated.Length > 0) Store(CurseForgePrefix + number, translated, result);
            }
        }
        catch (Exception error) { TraceFailure("curseforge", error); }
    }

    private static void Store(string projectId, string translated, Dictionary<string, string> result)
    {
        result[projectId] = translated;
        Cache[projectId] = (DateTime.UtcNow, translated);
    }

    private static StringContent Body(object value) => new(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");

    private static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? ""
        : "";

    /// <summary>翻译只是锦上添花，任何失败都只留一行调试记录，界面继续用原文。</summary>
    private static void TraceFailure(string section, Exception error) => System.Diagnostics.Trace.TraceWarning("翻译简介失败（{0}）：{1}", section, error.GetType().Name);

    private static void Trace(HttpResponseMessage response) => System.Diagnostics.Trace.TraceWarning("翻译简介失败（HTTP {0}）", (int)response.StatusCode);
}
