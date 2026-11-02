using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
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
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("FMLauncher/" + CurrentVersion);
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }
    public async Task<UpdateRelease?> CheckAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var data = await BytesAsync(new Uri("https://api.github.com/repos/" + UpdateProduct.Repository + "/releases/latest"), 2 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
        var release = JsonNode.Parse(data) as JsonObject ?? throw new InvalidDataException("更新服务返回了无效内容。");
        var tag = release["tag_name"]?.GetValue<string>() ?? "";
        if (release["draft"]?.GetValue<bool>() == true || release["prerelease"]?.GetValue<bool>() == true
            || !ReleaseVersion.TryParse(tag, out var next) || next.IsPrerelease) throw new InvalidDataException("没有可用的正式更新版本。");
        if (!ReleaseVersion.TryParse(CurrentVersion, out var current)) throw new InvalidDataException("当前程序版本号无效。");
        if (next.CompareTo(current) <= 0) return null;
        var page = GitHubAddress(release["html_url"]?.GetValue<string>() ?? "");
        var notes = (release["body"]?.GetValue<string>() ?? "").Trim(); if (notes.Length > 16000) notes = notes[..16000];
        var version = tag.TrimStart('v');
        UpdateRelease Manual() => new(version, notes, page, null, null, null, null);
        if (!IsConfigured || RuntimeInformation.ProcessArchitecture != Architecture.X64) return Manual();
        var assets = release["assets"] as JsonArray ?? [];
        Uri? Asset(string name)
        {
            var asset = assets.FirstOrDefault(a => a?["name"]?.GetValue<string>() == name);
            return asset == null ? null : GitHubAddress(asset["browser_download_url"]?.GetValue<string>() ?? "");
        }
        var manifestUrl = Asset(UpdateProduct.ManifestName); var signatureUrl = Asset(UpdateProduct.SignatureName);
        if (manifestUrl == null || signatureUrl == null) return Manual();
        var bytes = await BytesAsync(manifestUrl, 4 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
        var signature = await BytesAsync(signatureUrl, 128, timeout.Token).ConfigureAwait(false);
        var manifest = UpdateManifest.Verify(bytes, signature, _key);
        if (manifest.Version != version) throw new InvalidDataException("发布版本与更新清单不一致。");
        var package = Asset(manifest.PackageName) ?? throw new InvalidDataException("此版本的更新包尚未发布完整，请稍后重试。");
        return new(version, notes, page, manifest, bytes, signature, package);
    }
    private static Uri GitHubAddress(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/" + UpdateProduct.Repository + "/releases", StringComparison.OrdinalIgnoreCase) || uri.UserInfo.Length > 0)
            throw new InvalidDataException("更新地址不属于启动器发布仓库。");
        return uri;
    }
    private async Task<byte[]> BytesAsync(Uri uri, int limit, CancellationToken token)
    {
        using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new HttpRequestException("更新服务请求受限，请稍后重试。");
        response.EnsureSuccessStatusCode();
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
            response.EnsureSuccessStatusCode();
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
