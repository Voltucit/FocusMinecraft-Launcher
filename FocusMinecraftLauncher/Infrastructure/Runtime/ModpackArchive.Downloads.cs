using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using FMCore.Downloads;
using FocusMinecraftLauncher.Preview;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

public static partial class ModpackArchive
{
    /// <summary>空直链不代表文件不存在；按文件编号生成 CDN 地址，镜像作为最后的候选来源。</summary>
    private static string[] CurseForgeDownloadUrls(CurseForgeFile file)
    {
        var urls = new List<string>();
        void Add(string? address)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                urls.Add(uri.AbsoluteUri);
        }

        var direct = file.DownloadUrl?.Replace("-service.overwolf.wtf", ".forgecdn.net", StringComparison.OrdinalIgnoreCase);
        Add(direct);
        var path = "/files/" + (file.Id / 1000).ToString(CultureInfo.InvariantCulture) + "/"
            + (file.Id % 1000).ToString(CultureInfo.InvariantCulture) + "/" + Uri.EscapeDataString(file.FileName);
        Add("https://mediafilez.forgecdn.net" + path);
        var edge = "https://edge.forgecdn.net" + path;
        Add(edge);
        Add("https://media.forgecdn.net" + path);
        if (!string.IsNullOrWhiteSpace(direct)) Add(CurseForgeClient.Rewrite(direct));
        Add(CurseForgeClient.Rewrite(edge));
        return urls.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>仅编排资源来源；传输、重试、SHA-1、大小校验和原子写入仍由核心负责。</summary>
    private static async Task DownloadPackFilesAsync(PackFile[] files, PreviewInstance instance, int concurrency,
        Action<DownloadBatchProgress> progress, CancellationToken token)
    {
        if (files.Length == 0) return;
        var received = new long[files.Length];
        var transferred = new long[files.Length];
        var failures = new Exception?[files.Length];
        var gate = new object();
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var totalBytes = files.Sum(file => file.Size);
        var completed = 0;
        var active = 0;
        var peak = 0;

        // 调用方持有 gate；限频，避免分块回调堆积到 UI 线程。
        void Report(bool force = false)
        {
            if (!force && clock.Elapsed - lastReport < TimeSpan.FromMilliseconds(100)) return;
            lastReport = clock.Elapsed;
            var processed = received.Sum();
            var moved = transferred.Sum();
            var speed = clock.Elapsed.TotalSeconds > 0 ? moved / clock.Elapsed.TotalSeconds : 0;
            progress(new DownloadBatchProgress(files.Length, completed, active, peak, processed, totalBytes)
            {
                TransferredBytes = moved, BytesPerSecond = speed,
                RemainingTime = speed > 0 ? TimeSpan.FromSeconds((totalBytes - processed) / speed) : null
            });
        }

        lock (gate) Report(true);
        await Parallel.ForEachAsync(Enumerable.Range(0, files.Length),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = token }, async (index, downloadToken) =>
        {
            var file = files[index];
            var destination = SafePath(instance.GameDirectory, file.Path);
            // 本地文件冲突单独报告，不能误当成 CDN 失败并要求用户重新下载。
            if (File.Exists(destination) && !await MatchesFileAsync(file, destination, downloadToken))
                throw new IOException("已有文件与指定版本不同，未覆盖：" + file.FileName);
            lock (gate) { active++; peak = Math.Max(peak, active); Report(); }
            try
            {
                var errors = new List<Exception>();
                long previousTransferred = 0;
                foreach (var url in file.Urls.Distinct(StringComparer.Ordinal))
                {
                    downloadToken.ThrowIfCancellationRequested();
                    long sourceTransferred = 0;
                    try
                    {
                        await FileDownloader.DownloadAsync(new DownloadRequest
                        {
                            Url = url, FilePath = destination, Size = file.Size, Sha1 = file.Sha1
                        }, value =>
                        {
                            lock (gate)
                            {
                                received[index] = value.DownloadedBytes;
                                sourceTransferred = value.TransferredBytes;
                                transferred[index] = previousTransferred + sourceTransferred;
                                Report();
                            }
                        }, downloadToken);
                        lock (gate) { received[index] = file.Size; completed++; Report(); }
                        return;
                    }
                    catch (Exception error) when (!downloadToken.IsCancellationRequested
                        && error is HttpRequestException or HttpIOException or TimeoutException or InvalidDataException)
                    {
                        errors.Add(new IOException($"{file.FileName}（{new Uri(url).Host}）：{error.Message}", error));
                        previousTransferred += sourceTransferred;
                        lock (gate) { received[index] = 0; Report(); }
                    }
                }
                var failure = new AggregateException("所有候选来源均下载失败：" + file.FileName, errors);
                if (file.CurseForgeFileId == 0) throw failure;
                failures[index] = failure;
            }
            finally { lock (gate) { active--; Report(true); } }
        });
        token.ThrowIfCancellationRequested();
        var missing = files.Where((_, index) => failures[index] != null).ToArray();
        if (missing.Length > 0)
            throw new PackDownloadUnavailableException(missing, failures.OfType<Exception>().ToArray());
    }
}

/// <summary>自动来源已尝试完毕；调用方释放目录写锁后，再复用手动补齐流程。</summary>
public sealed class PackDownloadUnavailableException(IReadOnlyList<PackFile> files, IReadOnlyList<Exception> errors)
    : IOException($"{files.Count} 项整合包资源的自动下载来源均不可用。", new AggregateException(errors))
{
    public IReadOnlyList<PackFile> Files { get; } = files;
}
