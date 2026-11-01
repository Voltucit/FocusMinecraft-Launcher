using System.Windows.Input;
namespace FocusMinecraftLauncher.Common;
/// <summary>MVVM 命令适配；耗时操作由对应异步处理方法执行。</summary>
public sealed class UiCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(parameter); }
    public event EventHandler? CanExecuteChanged
    { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}
