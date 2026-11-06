using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FMCore.Downloads;
using FMCore.Modsloader;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>把 OptiFine 作为 Forge Mod 安装；不改写 Forge 的版本声明、主类或启动参数。</summary>
public static class OptiFineForgeIntegration
{
    private static readonly HttpClient Metadata = new() { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    private static readonly HttpClient Packages = new(new HttpClientHandler { MaxAutomaticRedirections = 5 }) { Timeout = TimeSpan.FromMinutes(5) };
    static OptiFineForgeIntegration()
    {
        Metadata.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", CommunitySource.UserAgent);
        Packages.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", CommunitySource.UserAgent);
    }
    private static readonly SemaphoreSlim CatalogLock = new(1, 1);
    private static IReadOnlyDictionary<string, OptiFineForgeSupport>? _catalog;
    private static DateTimeOffset _catalogUntil;

    /// <summary>按官网每个文件的 Forge 标注建立映射；N/A 与未取得标注分别保存，不用版本号大小推断兼容性。</summary>
    public static async Task<IReadOnlyDictionary<string, OptiFineForgeSupport>> GetSupportAsync(CancellationToken token = default)
    {
        await CatalogLock.WaitAsync(token);
        try
        {
            if (_catalog != null && DateTimeOffset.UtcNow < _catalogUntil) return _catalog;
            var html = await Metadata.GetStringAsync("https://optifine.net/downloads", token);
            var options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
            var result = new Dictionary<string, OptiFineForgeSupport>(StringComparer.OrdinalIgnoreCase);
            foreach (Match row in Regex.Matches(html, @"<tr\b[^>]*class\s*=\s*['""][^'""]*\bdownloadLine\b[^'""]*['""][^>]*>(.*?)</tr>", options))
            {
                var file = Regex.Match(row.Value, @"(?:adloadx|changelog)\?f=([^'""&<>\s]+)", options);
                var forge = Regex.Match(row.Value, @"<td\b[^>]*class\s*=\s*['""]colForge['""][^>]*>(.*?)</td>", options);
                if (!file.Success || !forge.Success) continue;
                var fileName = Uri.UnescapeDataString(WebUtility.HtmlDecode(file.Groups[1].Value));
                if (Path.GetFileName(fileName) != fileName || !fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
                var label = WebUtility.HtmlDecode(Regex.Replace(forge.Groups[1].Value, "<[^>]+>", "")).Trim();
                var build = Regex.Match(label, @"\AForge\s+(N/A|#[0-9]+|[0-9]+(?:\.[0-9]+)+)\z", RegexOptions.IgnoreCase);
                if (!build.Success) continue;
                var dateCell = Regex.Match(row.Value, @"<td\b[^>]*class\s*=\s*['""]colDate['""][^>]*>(.*?)</td>", options);
                var dateText = WebUtility.HtmlDecode(Regex.Replace(dateCell.Groups[1].Value, "<[^>]+>", "")).Trim();
                var date = DateOnly.TryParseExact(dateText, "dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var published)
                    ? published.ToString("yyyy-MM-dd") : "";
                result[fileName] = new(build.Groups[1].Value, date);
            }
            if (result.Count == 0) throw new InvalidDataException("未能读取官网的 OptiFine 与 Forge 兼容标注。");
            _catalog = new ReadOnlyDictionary<string, OptiFineForgeSupport>(result);
            _catalogUntil = DateTimeOffset.UtcNow.AddMinutes(10);
            return _catalog;
        }
        finally { CatalogLock.Release(); }
    }

    public static bool IsKnownCombination(string game, string forge, string edition, string? officialBuild)
    {
        forge = NormalizeForge(game, forge);
        // 此具体组合见 FMCore 的 launcher-workflow 示例；不扩大为其他构建的兼容范围。
        if (game == "1.16.5" && forge == "36.2.34" && edition == "HD_U_G8") return true;
        if (string.IsNullOrWhiteSpace(officialBuild) || officialBuild.Equals("N/A", StringComparison.OrdinalIgnoreCase)) return false;
        return officialBuild.StartsWith('#') ? forge.Split('.').LastOrDefault() == officialBuild[1..] : forge == officialBuild;
    }
    private static string NormalizeForge(string game, string forge) => forge.StartsWith(game + "-", StringComparison.Ordinal) ? forge[(game.Length + 1)..] : forge;

    public static async Task CheckCombinationAsync(OptiFineInstallVersion version,string forge,CancellationToken token)
    {
        if(IsKnownCombination(version.GameVersion,forge,version.Edition,null))return;
        var support=await GetSupportAsync(token).ConfigureAwait(false);
        var official=support.GetValueOrDefault(version.FileName)?.ForgeBuild;
        if(!IsKnownCombination(version.GameVersion,forge,version.Edition,official))
            throw new InvalidOperationException(official=="N/A"?"此 OptiFine 版本未提供 Forge 支持。":"所选 Forge 与 OptiFine 的兼容性尚未确认，请选择官网标注的组合。");
    }
    /// <summary>后台执行下载和包结构检查；镜像未提供官方 SHA 时不伪造远端校验值，本地 SHA-512 仅用于保留本次文件信息。</summary>
    public static Task<OptiFineModComponent> InstallAsync(OptiFineInstallVersion version, string forge, string instanceFolder, Action<DownloadProgress> progress, CancellationToken token)
        => Task.Run(async () =>
        {
            await CheckCombinationAsync(version,forge,token).ConfigureAwait(false);
            var path = ModpackArchive.SafePath(instanceFolder, "mods/" + version.FileName);
            var directory = Path.GetDirectoryName(path)!;
            if (File.Exists(path))
            {
                await ValidateJarAsync(path, version, token).ConfigureAwait(false);
                return await DescribeAsync(path, version, forge, token).ConfigureAwait(false);
            }
            if (File.Exists(path + ".disabled")) throw new IOException("此 OptiFine 文件已被禁用，未自动重新启用。");
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, ".fmlauncher-optifine-" + Guid.NewGuid().ToString("N") + ".part");
            const long maximumSize = 64 * 1024 * 1024;
            try
            {
                if (!Uri.TryCreate(version.DownloadUrl, UriKind.Absolute, out var url) || url.Scheme != "https" || url.Host != "bmclapi2.bangbang93.com")
                    throw new InvalidDataException("OptiFine 下载地址不是当前核心提供的 BMCLAPI 地址。");
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.AcceptEncoding.ParseAdd("identity");
                using var response = await Packages.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentRange != null || total is <= 0 or > maximumSize
                    || response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("OptiFine 安装包响应格式或大小无效。");
                var clock = Stopwatch.StartNew(); long received = 0, measuredBytes = 0; double measuredAt = 0, speed = 0;
                await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var buffer = new byte[65536];
                    progress(new(path, 0, total ?? 0));
                    int count;
                    while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        if (received + count > maximumSize || (total.HasValue && received + count > total.Value)) throw new InvalidDataException("OptiFine 安装包超过声明大小或 64 MiB。");
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); received += count;
                        var elapsed = clock.Elapsed.TotalSeconds;
                        if (elapsed - measuredAt >= .2) { speed = (received - measuredBytes) / (elapsed - measuredAt); measuredBytes = received; measuredAt = elapsed; }
                        progress(new(path, received, total ?? 0) { BytesPerSecond = speed, TransferredBytes = received });
                    }
                    await output.FlushAsync(token).ConfigureAwait(false);
                }
                if (total.HasValue && received != total.Value) throw new InvalidDataException("OptiFine 安装包下载不完整。");
                await ValidateJarAsync(temporary, version, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested(); File.Move(temporary, path, false);
                return await DescribeAsync(path, version, forge, token).ConfigureAwait(false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, token);

    private static async Task ValidateJarAsync(string path, OptiFineInstallVersion version, CancellationToken token)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("OptiFine 安装包过大。");
        using var archive = ZipFile.OpenRead(path);
        if (archive.GetEntry("optifine/OptiFineTransformationService.class") == null && archive.GetEntry("optifine/OptiFineForgeTweaker.class") == null)
            throw new InvalidDataException("此 OptiFine JAR 没有可识别的 Forge 接入文件。");
        var expected = @"OptiFine_" + Regex.Escape(version.GameVersion + "_" + version.Edition) + @"(?![A-Za-z0-9_])";
        foreach (var name in new[] { "net/optifine/Config.class", "Config.class", "optifine/Config.class" })
        {
            var entry = archive.GetEntry(name); if (entry == null) continue;
            if (entry.Length > 8 * 1024 * 1024) throw new InvalidDataException("OptiFine 版本信息文件过大。");
            await using var stream = entry.Open(); using var memory = new MemoryStream(); await stream.CopyToAsync(memory, token).ConfigureAwait(false);
            if (Regex.IsMatch(Encoding.Latin1.GetString(memory.ToArray()), expected)) return;
        }
        throw new InvalidDataException("OptiFine 安装包内的版本与所选版本不一致。");
    }
    private static async Task<OptiFineModComponent> DescribeAsync(string path, OptiFineInstallVersion version, string forge, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        var sha512 = Convert.ToHexString(await SHA512.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
        return new(version.GameVersion, version.Edition, version.FileName, NormalizeForge(version.GameVersion, forge), sha512, new FileInfo(path).Length);
    }
}

public sealed record OptiFineForgeSupport(string ForgeBuild, string Date);
public sealed record OptiFineModComponent(string GameVersion, string Edition, string FileName, string ForgeVersion, string LocalSha512, long Size);
