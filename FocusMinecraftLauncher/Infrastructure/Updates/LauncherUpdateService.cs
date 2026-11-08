using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FMLauncher.UpdateShared;

namespace FocusMinecraftLauncher.Infrastructure.Updates;

public sealed record UpdateRelease(string Version, string Notes, Uri Page, UpdateManifest? Manifest, byte[]? ManifestBytes, byte[]? Signature, Uri? Package);
public sealed record UpdateTransfer(long Bytes, long Total, double BytesPerSecond);

/// <summary>只从正式发布附件读取更新，签名和受管文件验证与临时更新器共用协议。</summary>
public sealed class LauncherUpdateService : IDisposable
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly string _root;
    private readonly string _key = UpdateProduct.PublicKey(typeof(App).Assembly);
    public string CurrentVersion => UpdateProduct.Version(typeof(App).Assembly);
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_key);
    public static Uri ReleasePage => new("https://github.com/" + UpdateProduct.Repository + "/releases");
    public LauncherUpdateService(string root)
    {
        _root = Path.GetFullPath(root);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(FocusMinecraftLauncher.Infrastructure.Runtime.CommunitySource.UserAgent);
    }
    /// <summary>通过官方最新发布跳转和固定附件名检查更新，不依赖匿名 REST API 配额。</summary>
    public async Task<UpdateRelease?> CheckAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasePage.AbsoluteUri + "/latest");
        request.Headers.Accept.ParseAdd("text/html");
        // 只读取重定向后的响应头，不下载整张发布页面。
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        EnsureUpdateResponse(response);
        var page = GitHubAddress(response.RequestMessage?.RequestUri?.AbsoluteUri ?? "");
        var prefix = "/" + UpdateProduct.Repository + "/releases/tag/";
        if (!page.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新服务没有返回有效的正式发布地址。");
        var tag = Uri.UnescapeDataString(page.AbsolutePath[prefix.Length..]);
        if (!ReleaseVersion.TryParse(tag, out var next) || next.IsPrerelease)
            throw new InvalidDataException("没有可用的正式更新版本。");
        if (!ReleaseVersion.TryParse(CurrentVersion, out var current)) throw new InvalidDataException("当前程序版本号无效。");
        if (next.CompareTo(current) <= 0) return null;
        var version = tag.TrimStart('v');
        Uri Asset(string name) => GitHubAddress(ReleasePage.AbsoluteUri + "/download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name));
        async Task<UpdateRelease> ManualAsync() => new(version, await ReleaseNotesAsync(page, timeout.Token).ConfigureAwait(false), page, null, null, null, null);
        if (!IsConfigured || RuntimeInformation.ProcessArchitecture != Architecture.X64) return await ManualAsync().ConfigureAwait(false);
        byte[] bytes, signature;
        try
        {
            // 固定到本次查到的标签，避免检查途中发布新版本造成清单与签名混用。
            bytes = await BytesAsync(Asset(UpdateProduct.ManifestName), 4 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
            signature = await BytesAsync(Asset(UpdateProduct.SignatureName), 128, timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return await ManualAsync().ConfigureAwait(false);
        }
        var manifest = UpdateManifest.Verify(bytes, signature, _key);
        if (manifest.Version != version) throw new InvalidDataException("发布版本与更新清单不一致。");
        return new(version, await ReleaseNotesAsync(page, timeout.Token).ConfigureAwait(false), page,
            manifest, bytes, signature, Asset(manifest.PackageName));
    }
    /// <summary>发布说明从官方订阅读取；说明不可用不影响已经验证的更新附件。</summary>
    private async Task<string> ReleaseNotesAsync(Uri page, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var data = await BytesAsync(new Uri(ReleasePage.AbsoluteUri + ".atom"), 2 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
            XNamespace atom = "http://www.w3.org/2005/Atom";
            var entry = XDocument.Parse(Encoding.UTF8.GetString(data)).Root?.Elements(atom + "entry")
                .FirstOrDefault(item => item.Elements(atom + "link").Any(link => (string?)link.Attribute("href") == page.AbsoluteUri));
            var notes = entry?.Element(atom + "content")?.Value ?? "";
            notes = Regex.Replace(notes, @"</(?:p|div|h[1-6]|ul|ol)>|<br\s*/?>", "\n\n", RegexOptions.IgnoreCase);
            notes = Regex.Replace(notes, @"<li\b[^>]*>", "\n- ", RegexOptions.IgnoreCase);
            notes = WebUtility.HtmlDecode(Regex.Replace(notes, @"<[^>]+>", "")).Trim();
            return notes.Length > 16000 ? notes[..16000] : notes;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return ""; }
        catch (Exception error) when (error is HttpRequestException or IOException or System.Xml.XmlException) { return ""; }
    }
    private static Uri GitHubAddress(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/" + UpdateProduct.Repository + "/releases/", StringComparison.OrdinalIgnoreCase) || uri.UserInfo.Length > 0)
            throw new InvalidDataException("更新地址不属于启动器发布仓库。");
        return uri;
    }
    /// <summary>保留 HTTP 状态供调用方区分附件缺失、访问拒绝与请求过于频繁。</summary>
    private static void EnsureUpdateResponse(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Forbidden => "更新服务拒绝访问（HTTP 403），请稍后重试或打开发布页面。",
            HttpStatusCode.TooManyRequests => "更新请求过于频繁（HTTP 429），请稍后重试。",
            HttpStatusCode.NotFound => "未找到更新版本或附件（HTTP 404），请打开发布页面确认。",
            _ => $"更新服务返回 HTTP {(int)response.StatusCode}，请稍后重试或打开发布页面。"
        };
        throw new HttpRequestException(message, null, response.StatusCode);
    }
    private async Task<byte[]> BytesAsync(Uri uri, int limit, CancellationToken token)
    {
        using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        EnsureUpdateResponse(response);
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("更新服务返回内容过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[16 * 1024]; int length;
        while ((length = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        { if (output.Length + length > limit) throw new InvalidDataException("更新服务返回内容过大。"); await output.WriteAsync(buffer.AsMemory(0, length), token).ConfigureAwait(false); }
        return output.ToArray();
    }
    /// <summary>下载落入独立会话，只有签名、完整包和解压文件全部验证通过才登记为可安装。</summary>
    public async Task<string> DownloadAsync(UpdateRelease release, IEnumerable<string> gameDirectories, IProgress<UpdateTransfer> progress, CancellationToken token)
    {
        var manifest = release.Manifest ?? throw new InvalidOperationException("此版本只能从发布页面手动下载。");
        var id = Guid.NewGuid().ToString("N"); var job = UpdatePaths.Job(_root, id); Directory.CreateDirectory(job);
        UpdatePaths.AtomicBytes(Path.Combine(job, "manifest.json"), release.ManifestBytes!);
        UpdatePaths.AtomicBytes(Path.Combine(job, "manifest.sig"), release.Signature!);
        var journal = new UpdateJournal { Id = id, Root = _root, Version = manifest.Version,
            ProtectedDirectories = gameDirectories.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
        foreach (var file in manifest.Files) UpdatePaths.ProgramFile(_root, file.Path, journal.ProtectedDirectories);
        var partial = Path.Combine(job, "package.part");
        try
        {
            using var response = await _client.GetAsync(release.Package!, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            EnsureUpdateResponse(response);
            if (response.Content.Headers.ContentLength is { } total && total != manifest.PackageSize) throw new InvalidDataException("更新包大小与签名声明不一致。");
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                var buffer = new byte[128 * 1024]; long received = 0; var watch = Stopwatch.StartNew(); long last = -100; int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    received += count; if (received > manifest.PackageSize) throw new InvalidDataException("更新包超出声明大小。");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    if (watch.ElapsedMilliseconds - last >= 100) { progress.Report(new(received, manifest.PackageSize, received / Math.Max(watch.Elapsed.TotalSeconds, .001))); last = watch.ElapsedMilliseconds; }
                }
                progress.Report(new(received, manifest.PackageSize, 0));
            }
            await UpdateManifest.VerifyFileAsync(partial, manifest.PackageSize, manifest.PackageSha256, token).ConfigureAwait(false);
            File.Move(partial, Path.Combine(job, "package.zip"));
            await Task.Run(() => UpdatePackage.PrepareAsync(_root, id, _key, token), token).ConfigureAwait(false);
            UpdatePaths.Save(Path.Combine(job, "job.json"), journal); return id;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
    public async Task<UpdateRelease?> RestorePreparedAsync(string id, CancellationToken token)
    {
        var job = UpdatePaths.Job(_root, id);
        var journal = UpdatePaths.ReadJournal(Path.Combine(job, "job.json"));
        if (journal.Phase != "Prepared") return null;
        var bytes = await File.ReadAllBytesAsync(Path.Combine(job, "manifest.json"), token);
        var signature = await File.ReadAllBytesAsync(Path.Combine(job, "manifest.sig"), token);
        var manifest = UpdateManifest.Verify(bytes, signature, _key);
        if (!ReleaseVersion.TryParse(manifest.Version, out var next) || !ReleaseVersion.TryParse(CurrentVersion, out var current) || next.CompareTo(current) <= 0) return null;
        await UpdateManifest.VerifyFileAsync(Path.Combine(job, "package.zip"), manifest.PackageSize, manifest.PackageSha256, token);
        return new(manifest.Version, "", ReleasePage, manifest, bytes, signature, null);
    }
    public async Task StartUpdaterAsync(string id, CancellationToken token)
    {
        var job = UpdatePaths.Job(_root, id); var path = Path.Combine(job, "job.json"); var journal = UpdatePaths.ReadJournal(path);
        if (journal.Phase != "Prepared") throw new InvalidOperationException("更新包尚未准备完成。");
        await Task.Run(() => UpdatePackage.PrepareAsync(_root, id, _key, token), token);
        using var current = Process.GetCurrentProcess();
        journal.ParentPid = current.Id; journal.ParentStartedUtcTicks = current.StartTime.ToUniversalTime().Ticks;
        journal.ParentExecutable = Environment.ProcessPath ?? throw new IOException("无法确定当前程序位置。");
        UpdatePaths.Save(path, journal);
        var ready = Path.Combine(job, "ready"); if (File.Exists(ready)) File.Delete(ready);
        using var updater = UpdateBootstrap.StartHelper(_root, id, "--apply");
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(ready))
            {
                token.ThrowIfCancellationRequested();
                if (updater.HasExited) throw new IOException("临时更新器未能启动，请检查写入权限或安全软件提示。");
                if (DateTime.UtcNow > deadline) throw new IOException("临时更新器准备超时，请重试。");
                await Task.Delay(100, token);
            }
        }
        catch
        {
            // 主程序仍在运行，更新器尚未进入替换阶段；收回本次启动，避免留下等待退出的进程。
            if (!updater.HasExited) { updater.Kill(true); await updater.WaitForExitAsync(); }
            journal.Phase = "Prepared"; UpdatePaths.Save(path, journal);
            if (File.Exists(ready)) File.Delete(ready);
            throw;
        }
    }
    public void Dispose() => _client.Dispose();
}
