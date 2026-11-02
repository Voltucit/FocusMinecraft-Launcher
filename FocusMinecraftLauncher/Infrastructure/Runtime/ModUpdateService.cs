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
        foreach (var file in Directory.EnumerateFiles(directory, "*.jar"))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(file);
                hashes[Convert.ToHexString(await SHA1.HashDataAsync(stream, token)).ToLowerInvariant()] = file;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        if (hashes.Count == 0) return [];

        var installed = await MatchInstalledAsync(hashes, progress, token);
        if (installed.Count == 0) return [];

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
                    {
                        results.Add(new(item.FileName, group.Key, item.Version, found.Version,
                            found.Url, found.FileName, found.Sha1, found.VersionId, item.VersionId));
                    }
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

        progress?.Report($"检查完成：{results.Count} 个 Mod 有 Modrinth 记录。");
        return results.OrderBy(update => update.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
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
