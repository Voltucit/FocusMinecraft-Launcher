using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using FMLauncher.Features.Launch.Models;
namespace FMLauncher.Features.Launch.Services;

/// <summary>界面与后台宿主的当前用户命名管道；关闭界面不会通过 Dispose 误杀游戏。</summary>
public sealed class GameSessionService
{
    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private Task? _listen;
    private TaskCompletionSource? _start;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    public bool Running { get; private set; }
    public bool Preparing { get; private set; }
    public event Action<SessionMessage>? Received;

    /// <summary>创建后台宿主并等待真实进程创建结果；取消不被解释为启动成功。</summary>
    public async Task LaunchAsync(LaunchRequest request, CancellationToken ct)
    {
        if (Running || Preparing) throw new InvalidOperationException("已有游戏正在运行或准备。");
        Preparing = true; _start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var name = "FMLauncher-" + Guid.NewGuid().ToString("N");
        _pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(App).Assembly.Location);
            start.ArgumentList.Add("--game-host"); start.ArgumentList.Add(name);
            using var host = Process.Start(start) ?? throw new InvalidOperationException("无法创建游戏宿主。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var connection = _pipe.WaitForConnectionAsync(timeout.Token);
            var exited = host.WaitForExitAsync(timeout.Token);
            if (await Task.WhenAny(connection, exited) == exited) throw new InvalidOperationException("游戏宿主在建立连接前退出，请检查发布文件是否完整。");
            await connection;
            _writer = new(_pipe) { AutoFlush = true };
            await _writer.WriteLineAsync(JsonSerializer.Serialize(request));
            _listen = ListenAsync(_pipe);
            using var registration = ct.Register(() => _ = StopAsync());
            await _start.Task.WaitAsync(ct);
        }
        catch { _pipe?.Dispose(); _pipe = null; Preparing = false; throw; }
    }
    private async Task ListenAsync(NamedPipeServerStream connection)
    {
        try
        {
            using var reader = new StreamReader(connection);
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length > 128 * 1024) throw new InvalidDataException("宿主消息超过限制。");
                var message = JsonSerializer.Deserialize<SessionMessage>(line);
                if (message == null) continue;
                if (message.Kind == "running") { Running = true; Preparing = false; _start?.TrySetResult(); }
                if (message.Kind == "failed") { Preparing = false; _start?.TrySetException(new InvalidOperationException(message.Text)); }
                if (message.Kind == "exit") { Running = false; Preparing = false; }
                Received?.Invoke(message);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException) { }
        finally
        {
            Running = Preparing = false;
            _start?.TrySetException(new InvalidOperationException("游戏宿主连接已结束。"));
            Received?.Invoke(new("disconnected"));
        }
    }
    public async Task StopAsync()
    {
        await SendAsync("stop");
    }
    private async Task SendAsync(string command)
    {
        await _sendGate.WaitAsync();
        try { if (_writer != null) await _writer.WriteLineAsync(command); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally { _sendGate.Release(); }
    }
    /// <summary>保留游戏时通知宿主脱离界面；停止模式等候核心释放后才关闭管道。</summary>
    public async Task CloseAsync(bool keepGame)
    {
        try
        {
            if (Running && keepGame && _writer != null) await SendAsync("detach");
            else
            {
                await StopAsync();
                if (_listen != null) await _listen.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
        finally { _pipe?.Dispose(); _pipe = null; _writer = null; }
    }
}

