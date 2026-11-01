using System.Collections.Specialized;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Preview;
namespace FocusMinecraftLauncher.Features.Downloads.ViewModels;
/// <summary>所有页面共享同一份真实任务集合。</summary>
public sealed class TaskHistoryViewModel : ObservableObject, IDisposable
{
    public PreviewSession Session { get; }
    public object Items => Session.Tasks;
    public bool HasTasks => Session.HasTask;
    public UiCommand Back { get; }
    public TaskHistoryViewModel(PreviewSession session, Action back, Action<string, bool> notify)
    { Session = session; Back = new(_ => back()); session.Tasks.CollectionChanged += TasksChanged; }
    public void Enqueue(VanillaInstallViewModel config) => Session.InstallGame(config.Name, config.Game.Number, config.PrimaryName,
        config.Components.FirstOrDefault(c => c.Name != "OptiFine" && c.Selected != null)?.Selected?.Version ?? "",
        config.Components.First(c => c.Name == "OptiFine").Selected?.Version ?? "");
    private void TasksChanged(object? sender, NotifyCollectionChangedEventArgs e) => Changed(nameof(HasTasks));
    public void Dispose() => Session.Tasks.CollectionChanged -= TasksChanged;
}
