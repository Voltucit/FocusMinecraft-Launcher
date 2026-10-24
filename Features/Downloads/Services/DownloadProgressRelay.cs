using System.Diagnostics;
using System.Windows.Threading;
using FMLauncher.CoreIntegration.Models;
namespace FMLauncher.Features.Downloads.Services;

/// <summary>后台进度按 200 ms 合并到 UI。字节按文件及重试次数计差额，避免并发文件进度相互覆盖。</summary>
internal sealed class DownloadProgressRelay : IProgress<WorkProgress>, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _files = new();
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Action<WorkProgress, string> _show;
    private WorkProgress _latest = new("准备");
    private long _bytes;
    private bool _disposed;
    public DownloadProgressRelay(Action<WorkProgress, string> show) { _show = show; _timer.Tick += Tick; _timer.Start(); }
    public void Report(WorkProgress value)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _latest = value;
            if (!string.IsNullOrEmpty(value.File))
            {
                var key = value.File + ":" + value.Attempt;
                _files.TryGetValue(key, out var last);
                if (value.Bytes > last) { _bytes += value.Bytes - last; _files[key] = value.Bytes; }
                // 统计只用于展示；不允许大规模资源任务无限增加 UI 内存。
                if (_files.Count > 16384) _files.Clear();
            }
        }
    }
    private void Tick(object? sender, EventArgs e)
    {
        WorkProgress value; long bytes;
        lock (_gate) { value = _latest; bytes = _bytes; }
        var speed = bytes / Math.Max(0.2, _elapsed.Elapsed.TotalSeconds);
        var remaining = value.Total > 0 ? $" · 当前阶段剩余 {Math.Max(0, value.Total - value.Completed)} 文件" : "";
        _show(value, $"{bytes / 1048576d:F1} MiB · 均速 {speed / 1048576d:F2} MiB/s" + remaining);
    }
    public void Dispose() { lock (_gate) _disposed = true; _timer.Stop(); _timer.Tick -= Tick; }
}

