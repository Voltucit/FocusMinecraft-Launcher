using System.Net;
using System.Net.Http;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>镜像请求的排队与重试：预热、翻译、检查更新会同时发起请求，统一在这里限流，避免把镜像当成压测目标。</summary>
public static class MirrorGate
{
    /// <summary>同时在途的镜像请求上限。镜像本身会缓存上游结果，请求再快也只是排队。</summary>
    public const int MaxConcurrent = 8;
    private const int MaxAttempts = 3;
    private static readonly SemaphoreSlim Slots = new(MaxConcurrent, MaxConcurrent);
    private static readonly SemaphoreSlim BackgroundSlots = new(1, 1);

    /// <summary>后台预热最多占用一个名额，为用户正在浏览的查询保留并发空间。</summary>
    private static async Task EnterAsync(CancellationToken token, bool background)
    {
        if (background) await BackgroundSlots.WaitAsync(token);
        try { await Slots.WaitAsync(token); }
        catch { if (background) BackgroundSlots.Release(); throw; }
    }
    private static void Release(bool background)
    {
        Slots.Release();
        if (background) BackgroundSlots.Release();
    }

    /// <summary>排队执行一次完整调用（查询、翻译、检查更新），等待空位的时间不计入调用本身的超时。</summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken token = default, bool background = false)
    {
        await EnterAsync(token, background);
        try { return await work(); }
        finally { Release(background); }
    }

    public static async Task RunAsync(Func<Task> work, CancellationToken token = default, bool background = false)
    {
        await EnterAsync(token, background);
        try { await work(); }
        finally { Release(background); }
    }

    /// <summary>发一次请求，429、5xx 与临时断线都退避重试；返回的响应由调用方释放。</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, Func<HttpRequestMessage> build, CancellationToken token = default, bool background = false)
    {
        await EnterAsync(token, background);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                using var request = build();
                HttpResponseMessage response;
                try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); }
                catch (Exception error) when (error is HttpRequestException && attempt < MaxAttempts && !token.IsCancellationRequested)
                {
                    // 连接被重置或握手失败时镜像本身没收到请求，等一个退避窗口再试，不把一次网络抖动直接抛给界面。
                    await Task.Delay(Backoff(attempt), token);
                    continue;
                }
                if (attempt >= MaxAttempts || !Retryable(response.StatusCode)) return response;
                var wait = RetryAfter(response) ?? Backoff(attempt);
                response.Dispose();
                await Task.Delay(wait, token);
            }
        }
        finally { Release(background); }
    }

    /// <summary>镜像按小时缓存结果，重试一味加码反而更糟：只重试两次，退避从 400ms 起翻倍。</summary>
    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(400 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 150));

    /// <summary>服务端给出 Retry-After 时照办，但最多等 5 秒，免得界面长时间停在“正在加载”。</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        var wait = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (wait is not { } value) return null;
        return value <= TimeSpan.Zero ? TimeSpan.Zero : value > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : value;
    }

    private static bool Retryable(HttpStatusCode status) => status is HttpStatusCode.TooManyRequests
        or HttpStatusCode.RequestTimeout
        or HttpStatusCode.InternalServerError
        or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;
}
