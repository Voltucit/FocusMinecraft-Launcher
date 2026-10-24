using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using FMLauncher.Common;
using FMLauncher.CoreIntegration.Models;
using FMLauncher.Infrastructure.Logging;
namespace FMLauncher.Features.Downloads.Services;

/// <summary>所有大型文件变更共用串行入口。只有 await 完成才报告成功，不依赖最后一次进度回调。</summary>
public sealed class OperationService : ObservableObject, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _active;
    private string _status = "没有正在执行的下载";
    private bool _busy;
    private double _percent;
    private bool _indeterminate = true;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }
    public double Percent { get => _percent; private set => Set(ref _percent, value); }
    public bool Indeterminate { get => _indeterminate; private set => Set(ref _indeterminate, value); }
    public ObservableCollection<string> History { get; } = [];
    /// <summary>串行执行文件任务，取消排队或当前工作；回调仅更新展示，最终结果以 await 为准。</summary>
    public async Task<T> RunAsync<T>(string title, Func<IProgress<WorkProgress>, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _active = cancellation; Busy = true; Status = title; Percent = 0; Indeterminate = true;
        using var progress = new DownloadProgressRelay((value, telemetry) =>
        {
            // Progress 回调可能比任务完成更晚到达，且并发下载频率很高。
            if (!ReferenceEquals(_active, cancellation)) return;
            Status = title + " · " + value.Label + " · " + telemetry;
            Indeterminate = value.Total <= 0;
            Percent = value.Total > 0 ? Math.Clamp(100d * value.Completed / value.Total, 0, 100) : 0;
        });
        try
        {
            var result = await work(progress, cancellation.Token);
            Record(title + " · 已完成"); return result;
        }
        catch (OperationCanceledException) { Record(title + " · 已取消"); throw; }
        catch (Exception ex) { Record(title + " · " + FailureText.For(ex)); throw; }
        finally { _active = null; Busy = false; _gate.Release(); }
    }
    private void Record(string text)
    {
        Status = text; History.Insert(0, DateTime.Now.ToString("HH:mm") + "  " + text);
        while (History.Count > 30) History.RemoveAt(History.Count - 1);
    }
    public void Cancel() => _active?.Cancel();
    public void Dispose() => Cancel();
}

