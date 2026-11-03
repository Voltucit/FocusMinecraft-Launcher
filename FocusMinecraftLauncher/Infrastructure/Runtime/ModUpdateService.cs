using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FocusMinecraftLauncher.Preview;
namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>
/// 把本地 jar 的 SHA-1 映射到 Modrinth 上的版本，再按“当前游戏版本 + 加载器”查同一项目有没有更新的版本。
/// 只做 Modrinth：其它来源的 Mod 会查不到记录，不会误报更新。
/// </summary>
public static class ModUpdateService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
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
        /// <summary>最新版本的 id 与本地文件所属版本不同，才算真的有更新。</summary>
        public bool HasUpdate => LatestVersionId.Length > 0
            && !string.Equals(LatestVersionId, InstalledVersionId, StringComparison.Ordinal);
    }

    private const int BatchSize = 100;
    private const int ProjectParallelism = 4;

    public static async Task<IReadOnlyList<ModUpdate>> CheckAsync(PreviewInstance instance, IProgress<string>? progress, CancellationToken token)
    {
        var directory = Path.Combine(instance.GameDirectory, "mods");
        if (!Directory.Exists(directory)) return [];

        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fingerprints = new Dictionary<uint, string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.jar"))
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
        if (installed.Count > 0) results.AddRange(await CheckModrinthAsync(instance, installed, progress, token));

        var matched = installed.Select(item => item.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = fingerprints.Where(pair => !matched.Contains(Path.GetFileName(pair.Value))).ToDictionary(pair => pair.Key, pair => pair.Value);
        if (remaining.Count > 0) results.AddRange(await CheckCurseForgeAsync(instance, remaining, progress, token));

        progress?.Report($"检查完成：{results.Count} 个 Mod 有可用更新。");
        return results.OrderBy(update => update.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<IReadOnlyList<ModUpdate>> CheckModrinthAsync(PreviewInstance instance, List<(string FileName, string ProjectId, string VersionId, string Version)> installed, IProgress<string>? progress, CancellationToken token)
    {
        var results = new List<ModUpdate>();
        var gate = new SemaphoreSlim(ProjectParallelism);
        var projects = installed.GroupBy(item => item.ProjectId, StringComparer.OrdinalIgnoreCase).ToArray();
        var finished = 0;
        var tasks = projects.Select(async group =>
        {
            await gate.WaitAsync(token);
            try
            {
                var latest = await FindLatestAsync(group.Key, instance, token);
                if (latest is not { } found) return;
                lock (results)
                {
                    foreach (var item in group)
                        results.Add(new(item.FileName, group.Key, item.Version, found.Version, found.Url, found.FileName, found.Sha1, found.VersionId, item.VersionId));
                }
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException) { }
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
    private static async Task<IReadOnlyList<ModUpdate>> CheckCurseForgeAsync(PreviewInstance instance, Dictionary<uint, string> remaining, IProgress<string>? progress, CancellationToken token)
    {
        var results = new List<ModUpdate>();
        var prints = remaining.Keys.ToArray();
        for (var offset = 0; offset < prints.Length; offset += BatchSize)
        {
            token.ThrowIfCancellationRequested();
            var batch = prints.Skip(offset).Take(BatchSize).ToArray();
            progress?.Report($"正在核对 CurseForge 文件 {offset + 1}-{offset + batch.Length}/{prints.Length}");
            var body = JsonSerializer.Serialize(new { fingerprints = batch });
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://mod.mcimirror.top/curseforge/v1/fingerprints")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            using var response = await Client.SendAsync(request, token);
            if (!response.IsSuccessStatusCode) continue;
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            if (!document.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("exactMatches", out var matches)) continue;
            // 接口按请求顺序返回，没有匹配的位置是 null，据此把结果对回本地文件。
            var position = 0;
            foreach (var match in matches.EnumerateArray())
            {
                var local = position < batch.Length ? remaining[batch[position]] : "";
                position++;
                if (match.ValueKind != JsonValueKind.Object || local.Length == 0) continue;
                if (!match.TryGetProperty("file", out var file)) continue;
                var modId = file.TryGetProperty("modId", out var id) ? id.GetInt64() : 0;
                var fileId = file.TryGetProperty("id", out var fid) ? fid.GetInt64() : 0;
                if (modId == 0 || fileId == 0) continue;
                try
                {
                    var candidates = await CurseForge.GetFilesAsync(modId, instance.Version, instance.Loader, token);
                    // 作者关闭第三方分发时下载地址为空，这种文件不能当作可更新的目标。
                    var latest = candidates.Where(item => !string.IsNullOrWhiteSpace(item.DownloadUrl)).OrderByDescending(item => item.Date).FirstOrDefault();
                    if (latest == null || latest.Id == fileId) continue;
                    results.Add(new(Path.GetFileName(local), "cf-" + modId, Text(file, "displayName") ?? "", latest.DisplayName,
                        CurseForgeClient.Rewrite(latest.DownloadUrl), latest.FileName, latest.Sha1 ?? "", latest.Id.ToString(), fileId.ToString()));
                }
                catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException) { }
            }
        }
        return results;
    }

    /// <summary>CurseForge 的指纹算法：MurmurHash2（seed 1），与官方客户端一致。</summary>
    private static uint Murmur2(byte[] data)
    {
        const uint m = 0x5bd1e995;
        const int r = 24;
        var length = data.Length;
        var hash = 1u ^ (uint)length;
        var index = 0;
        while (length - index >= 4)
        {
            var chunk = (uint)(data[index] | data[index + 1] << 8 | data[index + 2] << 16 | data[index + 3] << 24);
            chunk *= m; chunk ^= chunk >> r; chunk *= m;
            hash *= m; hash ^= chunk;
            index += 4;
        }
        switch (length - index)
        {
            case 3: hash ^= (uint)data[index + 2] << 16; goto case 2;
            case 2: hash ^= (uint)data[index + 1] << 8; goto case 1;
            case 1: hash ^= data[index]; hash *= m; break;
        }
        hash ^= hash >> 13; hash *= m; hash ^= hash >> 15;
        return hash;
    }

    /// <summary>批量把 SHA-1 换成本地文件对应的版本信息（项目 id、版本 id、版本号）。</summary>
    private static async Task<List<(string FileName, string ProjectId, string VersionId, string Version)>> MatchInstalledAsync(
        Dictionary<string, string> hashes, IProgress<string>? progress, CancellationToken token)
    {
        var found = new List<(string, string, string, string)>();
        var keys = hashes.Keys.ToArray();
        for (var offset = 0; offset < keys.Length; offset += BatchSize)
        {
            var batch = keys.Skip(offset).Take(BatchSize).ToArray();
            progress?.Report($"正在核对本地文件 {offset + 1}-{offset + batch.Length}/{keys.Length}");
            var body = JsonSerializer.Serialize(new { hashes = batch, algorithm = "sha1" });
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.modrinth.com/v2/version_files")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            AddHeaders(request);
            using var response = await Client.SendAsync(request, token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Modrinth 返回 " + (int)response.StatusCode + "，稍后再试。");
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!hashes.TryGetValue(property.Name, out var file)) continue;
                var version = property.Value;
                var projectId = Text(version, "project_id");
                var versionId = Text(version, "id");
                if (projectId is null || versionId is null) continue;
                found.Add((Path.GetFileName(file), projectId, versionId, Text(version, "version_number") ?? versionId));
            }
        }
        return found;
    }

    /// <summary>查项目在当前游戏版本与加载器下的最新版本；没有可用版本时返回 null。</summary>
    private static async Task<(string VersionId, string Version, string Url, string FileName, string Sha1)?> FindLatestAsync(
        string projectId, PreviewInstance instance, CancellationToken token)
    {
        var loaders = LoaderKey(instance.Loader);
        var query = new StringBuilder("https://api.modrinth.com/v2/project/")
            .Append(Uri.EscapeDataString(projectId)).Append("/version?");
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(instance.Version))
            filters.Add("game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { instance.Version })));
        if (loaders.Length > 0)
            filters.Add("loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loaders })));
        query.Append(string.Join('&', filters));

        using var request = new HttpRequestMessage(HttpMethod.Get, query.ToString());
        AddHeaders(request);
        using var response = await Client.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0) return null;

        var version = document.RootElement[0];
        if (!version.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0) return null;
        var primary = files[0];
        var url = Text(primary, "url");
        var fileName = Text(primary, "filename");
        if (url is null || fileName is null) return null;
        var sha1 = primary.TryGetProperty("hashes", out var hashes) ? Text(hashes, "sha1") ?? "" : "";
        return (Text(version, "id") ?? "", Text(version, "version_number") ?? "", url, fileName, sha1);
    }

    /// <summary>下载并替换所选 Mod；返回成功替换的数量。旧文件按删除按钮同样的方式移入回收站。</summary>
    public static async Task<int> ApplyAsync(PreviewInstance instance, IReadOnlyList<ModUpdate> updates, IProgress<string>? progress, CancellationToken token)
    {
        var directory = Path.Combine(instance.GameDirectory, "mods");
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
                using (var response = await Client.GetAsync(update.DownloadUrl, token))
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
        request.Headers.TryAddWithoutValidation("User-Agent", "FMLauncher/2.0.3 (mod update)");

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
