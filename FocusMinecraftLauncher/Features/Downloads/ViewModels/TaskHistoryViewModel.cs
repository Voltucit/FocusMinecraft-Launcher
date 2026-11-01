using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Preview;
namespace FocusMinecraftLauncher.Features.Downloads.ViewModels;
/// <summary>所有页面共享同一份真实任务集合；任务记录只展示下载与整合包相关的任务。</summary>
public sealed class TaskHistoryViewModel : ObservableObject, IDisposable
{
    public PreviewSession Session { get; }
    public ICollectionView Items { get; }
    public bool HasTasks => !Items.IsEmpty;
    public UiCommand Back { get; }
    public TaskHistoryViewModel(PreviewSession session, Action back, Action<string, bool> notify)
    {
        Session = session;
        // 启动类任务不属于下载记录，独占的视图避免过滤影响其他页面对 Session.Tasks 的绑定。
        Items = new ListCollectionView(session.Tasks)
        {
            Filter = item => item is PreviewTask task && task.Kind != PreviewTaskKind.Launch
        };
        Back = new(_ => back());
        session.Tasks.CollectionChanged += TasksChanged;
    }
    public void Enqueue(VanillaInstallViewModel config) => Session.InstallGame(config.Name, config.Game.Number, config.PrimaryName,
        config.Components.FirstOrDefault(c => c.Name != "OptiFine" && c.Selected != null)?.Selected?.Version ?? "",
        config.Components.First(c => c.Name == "OptiFine").Selected?.Version ?? "");
    private void TasksChanged(object? sender, NotifyCollectionChangedEventArgs e) => Changed(nameof(HasTasks));
    public void Dispose() => Session.Tasks.CollectionChanged -= TasksChanged;
}
