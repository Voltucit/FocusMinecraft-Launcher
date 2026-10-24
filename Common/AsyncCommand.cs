using System.Windows.Input;
namespace FMLauncher.Common;

/// <summary>统一处理异步命令的重入、取消及异常；调用方负责界面错误反馈。</summary>
public sealed class AsyncCommand(Func<CancellationToken, Task> execute, Action<Exception> failed) : ObservableObject, ICommand, IDisposable
{
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    private TaskCompletionSource? _completion;
    public Task Completion => _completion?.Task ?? Task.CompletedTask;
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); CanExecuteChanged?.Invoke(this, EventArgs.Empty); } }
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !IsBusy;
    public async void Execute(object? parameter)
    {
        if (IsBusy) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = completion;
        IsBusy = true;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try { await execute(cancellation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { failed(ex); }
        finally { _cancellation = null; completion.TrySetResult(); IsBusy = false; }
    }
    public void Cancel() => _cancellation?.Cancel();
    public void Dispose() => Cancel();
}

