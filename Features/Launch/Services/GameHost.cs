using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using FMCore.Authentication;
using FMCore.Launch;
using FMLauncher.CoreIntegration.Adapters;
using FMLauncher.Features.Launch.Models;
using FMLauncher.Infrastructure.Configuration;
using FMLauncher.Infrastructure.Logging;
namespace FMLauncher.Features.Launch.Services;

/// <summary>无窗口后台宿主持有 SDK 生命周期。界面退出后可继续等待游戏退出，最后正常释放核心资源。</summary>
internal static class GameHost
{
    /// <summary>在当前用户管道内等待启动请求，持有核心直到游戏退出并完成安全日志导出。</summary>
    public static async Task RunAsync(string pipeName)
    {
        using var connection = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await connection.ConnectAsync(connectTimeout.Token);
        using var reader = new StreamReader(connection);
        using var writer = new StreamWriter(connection) { AutoFlush = true };
        var line = await reader.ReadLineAsync(connectTimeout.Token) ?? throw new InvalidDataException("缺少启动请求。");
        if (line.Length > 128 * 1024) throw new InvalidDataException("启动请求超过限制。");
        var request = JsonSerializer.Deserialize<LaunchRequest>(line) ?? throw new InvalidDataException("启动请求无效。");
        var paths = new AppPaths();
        using var accounts = new AccountManager(paths.AccountsDirectory);
        using var prepare = new CancellationTokenSource();
        // 输出队列有界；满载时跳过新的高频日志，结果消息等待空位，不阻塞 SDK 日志线程。
        var messages = Channel.CreateBounded<SessionMessage>(new BoundedChannelOptions(2048) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
        var connected = true; var running = false; var detached = false;
        var completing = false;
        using var commandLifetime = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            await foreach (var message in messages.Reader.ReadAllAsync())
            {
                if (!connected) continue;
                try { await writer.WriteLineAsync(JsonSerializer.Serialize(message)); }
                catch (IOException) { connected = false; }
                catch (ObjectDisposedException) { connected = false; }
            }
        });
        MinecraftLauncher? launcher = null;
        var commands = Task.Run(async () =>
        {
            try
            {
                while (await reader.ReadLineAsync(commandLifetime.Token) is { } command)
                {
                    if (command == "stop")
                    {
                        prepare.Cancel();
                        if (launcher != null) await launcher.StopAsync();
                    }
                    if (command == "detach") { detached = true; break; }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) when (commandLifetime.IsCancellationRequested) { }
            finally
            {
                if (!completing) connected = false;
                if (!completing && (!detached || !running || !request.Settings.KeepGamesOnExit))
                {
                    prepare.Cancel();
                    if (launcher != null) try { await launcher.StopAsync(); } catch (Exception) { }
                }
            }
        });
        try
        {
            LegacySessionGuard.Check(request.Settings.GameRoot, request.Version);
            launcher = await LaunchAdapter.CreateAsync(request, accounts, prepare.Token);
            var core = launcher;
            core.OutputReceived += (_, e) => messages.Writer.TryWrite(new("log", e.Text));
            core.ErrorReceived += (_, e) => messages.Writer.TryWrite(new("log", e.Text));
            core.StateChanged += (_, e) => messages.Writer.TryWrite(new("state", PlayerText.State(e.State)));
            var result = await core.LaunchAsync(new InlineProgress<LaunchProgress>(p => messages.Writer.TryWrite(new("progress", PlayerText.Stage(p.Stage.ToString())))), prepare.Token);
            if (!result.Success)
            {
                DiagnosticLog.Record("LaunchResult", $"{result.Error?.Code}/{result.Error?.DetailCode}");
                await messages.Writer.WriteAsync(new("failed", PlayerText.LaunchFailure(result.Error)));
                return;
            }
            running = true;
            await messages.Writer.WriteAsync(new("running", "游戏进程已创建", result.ProcessId));
            var exit = await core.WaitForExitAsync();
            await messages.Writer.WriteAsync(new("exit", core.LastDiagnostic?.Summary ?? (exit.StoppedByCaller ? "游戏已由启动器停止" : "游戏已退出"), ExitCode: exit.ExitCode));

        }
        catch (OperationCanceledException) { await messages.Writer.WriteAsync(new("failed", "启动已取消。")); }
        catch (Exception ex) { DiagnosticLog.Record("LaunchHost", ex); await messages.Writer.WriteAsync(new("failed", FailureText.For(ex))); }
        finally
        {
            // 先结束命令读取和 StopAsync，再释放核心，避免控制线程与 DisposeAsync 并发。
            completing = true; commandLifetime.Cancel();
            try { await commands; } catch (Exception ex) { messages.Writer.TryWrite(new("failed", FailureText.For(ex))); }
            // 核心在准备失败时也保留安全日志；日志导出失败不改变真实启动结果。
            if (launcher != null)
            {
                try
                {
                    Directory.CreateDirectory(paths.LogsDirectory);
                    await launcher.Logger.SaveAsync(Path.Combine(paths.LogsDirectory, "game-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + ".jsonl"));
                }
                catch (Exception ex) { DiagnosticLog.Record("ExportCoreLog", ex); }
            }
            try { if (launcher != null) await launcher.DisposeAsync(); }
            finally
            {
                messages.Writer.TryComplete();
                try { await pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (TimeoutException) { }
                connection.Dispose();
            }
        }
    }
}

