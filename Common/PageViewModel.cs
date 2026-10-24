using FMLauncher.Infrastructure.Logging;
namespace FMLauncher.Common;

public abstract class PageViewModel : ObservableObject, IDisposable
{
    private string _status = "";
    private readonly List<AsyncCommand> _commands = [];
    public string Status { get => _status; set => Set(ref _status, value); }
    protected AsyncCommand Command(Func<CancellationToken, Task> action)
    {
        var command = new AsyncCommand(async ct => { try { await action(ct); } catch (OperationCanceledException) { Status = "操作已取消。"; } }, ex => { DiagnosticLog.Record(GetType().Name, ex); Status = FailureText.For(ex); });
        _commands.Add(command); return command;
    }
    public virtual void Dispose() { foreach (var command in _commands) command.Dispose(); }
    public Task WhenIdleAsync() => Task.WhenAll(_commands.Select(command => command.Completion));
}

