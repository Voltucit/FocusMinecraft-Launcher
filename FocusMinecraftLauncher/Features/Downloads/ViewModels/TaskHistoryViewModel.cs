using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Preview;
namespace FocusMinecraftLauncher.Features.Downloads.ViewModels;
public sealed record LaunchTimingRow(string StartedAt, string Elapsed, string Breakdown);

/// <summary>下载任务与当前实例的启动耗时均来自共享会话。</summary>
public sealed class TaskHistoryViewModel : ObservableObject, IDisposable
{
    public PreviewSession Session { get; }
    public ICollectionView Items { get; }
    public bool HasTasks => !Items.IsEmpty;
    public IReadOnlyList<LaunchTimingRow> LaunchTimings { get; private set; } = [];
    public string LaunchTimingNotice => Session.CurrentInstance == null ? "选择游戏后可查看启动耗时。"
        : LaunchTimings.Count == 0 ? "这个游戏还没有启动耗时记录。" : "";
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
        session.PropertyChanged += SessionChanged;
        session.LaunchTimingsChanged += RefreshLaunchTimings;
        RefreshLaunchTimings();
    }
    public void Enqueue(VanillaInstallViewModel config) => Session.InstallGame(config.Name, config.Game.Number, config.PrimaryName,
        config.Components.FirstOrDefault(c => c.Name != "OptiFine" && c.Selected != null)?.Selected?.Version ?? "",
        config.Components.First(c => c.Name == "OptiFine").Selected?.Version ?? "");
    private void TasksChanged(object? sender, NotifyCollectionChangedEventArgs e) => Changed(nameof(HasTasks));
    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PreviewSession.CurrentInstance)) RefreshLaunchTimings();
    }

    private void RefreshLaunchTimings()
    {
        LaunchTimings = Session.CurrentInstance is { } instance
            ? Session.GetLaunchTimings(instance).Select(sample => new LaunchTimingRow(
                sample.StartedAt.ToLocalTime().ToString("MM-dd HH:mm:ss"), FormatDuration(sample.Elapsed),
                string.Join("　", sample.Phases.Select(phase => phase.Name + " " + FormatDuration(phase.Elapsed))))).ToArray()
            : [];
        Changed(nameof(LaunchTimings));
        Changed(nameof(LaunchTimingNotice));
    }

    private static string FormatDuration(TimeSpan elapsed) => elapsed.TotalMinutes >= 1
        ? $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒"
        : elapsed.TotalMilliseconds < 100 ? $"{elapsed.TotalMilliseconds:0} 毫秒" : $"{elapsed.TotalSeconds:0.0} 秒";

    public void Dispose()
    {
        Session.Tasks.CollectionChanged -= TasksChanged;
        Session.PropertyChanged -= SessionChanged;
        Session.LaunchTimingsChanged -= RefreshLaunchTimings;
    }
}
