using System.IO;
using System.Text;
using System.Text.Json;

namespace FocusMinecraftLauncher.Features.Multiplayer.Services;

/// <summary>本机管道及房间控制连接共用有长度上限的消息帧；不输出消息正文或网络密钥。</summary>
internal sealed class NetworkWire(Stream stream) : IDisposable
{
    // 一帧可包含房间内最多 128 名成员及其 256 字节头像，仍保持明确的消息上限。
    private const int MaximumFrameLength = 256 * 1024;
    private readonly StreamReader _reader = new(stream, new UTF8Encoding(false), false, 4096, true);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposed;

    public async Task<T?> ReadAsync<T>(CancellationToken token) where T : class
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        // ReadLineAsync 先分配整行，不能用它接收不可信网络输入。
        var text = new StringBuilder(); var buffer = new char[1];
        while (await _reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) != 0)
        {
            if (buffer[0] == '\n') return JsonSerializer.Deserialize<T>(text.ToString()) ?? throw new InvalidDataException("联机消息为空。");
            if (text.Length >= MaximumFrameLength) throw new InvalidDataException("联机消息过长。");
            text.Append(buffer[0]);
        }
        if (text.Length != 0) throw new EndOfStreamException("联机消息未接收完整。");
        return null;
    }

    public async Task WriteAsync<T>(T value, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var json = JsonSerializer.Serialize(value);
        if (json.Length > MaximumFrameLength) throw new InvalidDataException("联机消息过长。");
        var frame = Encoding.UTF8.GetBytes(json + Environment.NewLine);
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            // 完整消息直接写入管道或套接字；释放时无需再次 Flush，传输失败仍由本次写入报告。
            await stream.WriteAsync(frame, token).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>只释放消息读取包装，底层连接由调用方关闭；重复释放不产生写入或覆盖原始异常。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _reader.Dispose();
    }
}
