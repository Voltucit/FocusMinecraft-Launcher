using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Navigation;
using FocusMinecraftLauncher.Preview;

namespace FocusMinecraftLauncher.Features.ExtraPages.ViewModels;

/// <summary>只投影现有会话；不创建核心、账号服务或新的启动/下载任务。</summary>
public sealed class TrayViewModel : ObservableObject, IDisposable
{
    public PreviewSession Session { get; }
    public PreviewInstance? CurrentGame => Session.CurrentInstance;
    public bool HasCurrentGame => CurrentGame != null;
    public ObservableCollection<PreviewInstance> QuickGames { get; } = [];
    public bool HasQuickGames => QuickGames.Count > 0;
    private bool _hasRecent;
    public string QuickGamesTitle => _hasRecent ? "最近启动" : "快速切换";
    private bool Running => Session.Tasks.Any(task => task.IsRunningGame && task.IsActive);
    public string RuntimeStatus => Running ? "游戏运行中" : Session.IsLaunching ? "正在启动游戏"
        : Session.IsLoading ? "正在读取游戏" : Session.Tasks.Any(task => task.IsActive) ? "正在处理任务" : "";
    public string LaunchText => Running ? "游戏运行中" : Session.IsLaunching ? "正在启动" : "启动游戏";
    public string LaunchHint => Session.IsLoading ? "正在读取游戏"
        : !HasCurrentGame ? "请在完整启动器中选择或安装游戏"
        : Session.CurrentAccount == null ? "请在完整启动器中添加账号"
        : Session.CurrentAccount.IsRestoring ? "正在恢复登录"
        : !Session.CurrentAccount.IsValid ? "请在完整启动器中重新登录"
        : Session.InstanceError.Length > 0 ? Session.InstanceError
        : !Running && !Session.IsLaunching && Session.IsBusy ? "等待当前任务结束后即可启动" : "";
    public PreviewTask? SummaryTask => Session.Tasks.FirstOrDefault(task => task.IsActive && !task.IsRunningGame)
        ?? Session.Tasks.FirstOrDefault(task => task.State == "失败");
    public bool HasSummaryTask => SummaryTask != null;
    public int FailureCount => Session.Tasks.Count(task => task.State == "失败");
    public bool HasFailures => FailureCount > 0;
    public string FailureText => $"查看 {FailureCount} 个失败任务";
    public string TaskDetail => SummaryTask is not { } task ? "" : task.State == "失败" ? task.Error : task.Stage;
    public string TransferText => SummaryTask is not { } task ? "" : string.Join("  ", new[] { task.DownloadLabel, task.Speed }.Where(text => text.Length > 0));
    public UiCommand LaunchGame { get; }
    public UiCommand ChooseGame { get; }
    public UiCommand RestoreMainWindow { get; }
    public UiCommand OpenSettings { get; }
    public UiCommand OpenTasks { get; }
    public UiCommand OpenGameFolder { get; }
    public UiCommand Exit { get; }
    private readonly HashSet<PreviewTask> _observedTasks = [];
    private PreviewInstance? _observedGame;
    private PreviewAccount? _observedAccount;
    private bool _disposed;

    public TrayViewModel(PreviewSession session, Action restore, Action exit)
    {
        Session = session;
        RestoreMainWindow = new(_ => restore());
        OpenSettings = new(_ => { restore(); session.Navigate(MainPage.Settings); });
        OpenTasks = new(_ => { restore(); session.OpenTasks.Execute(null); });
        Exit = new(_ => exit());
        LaunchGame = new(_ => session.Launch.Execute(null), _ => session.Launch.CanExecute(null));
        ChooseGame = new(item => { if (item is PreviewInstance game) session.CurrentInstance = game; },
            item => item is PreviewInstance game && session.Instances.Contains(game)
                && !session.IsEditingBlocked && !session.IsLoading && !session.IsAuthenticating);
        OpenGameFolder = new(_ =>
        {
            if (CurrentGame is not { } game) return;
            try { Process.Start(new ProcessStartInfo(game.GameDirectory) { UseShellExecute = true }); }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or System.IO.IOException)
            { session.NotifyTask("无法打开游戏目录：" + Infrastructure.Runtime.RuntimeSupport.Error(error), true); }
        }, _ => HasCurrentGame);
        session.PropertyChanged += SessionChanged;
        session.Instances.CollectionChanged += InstancesChanged;
        session.Tasks.CollectionChanged += TasksChanged;
        RefreshState();
    }

    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(_observedGame, CurrentGame) || !ReferenceEquals(_observedAccount, Session.CurrentAccount)) RefreshState();
        else Refresh();
    }
    private void InstancesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshState();
    private void TasksChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshState();
    private void ItemChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    public void RefreshState()
    {
        if (_disposed) return;
        foreach (var old in _observedTasks.Where(task => !Session.Tasks.Contains(task)).ToArray())
        { old.PropertyChanged -= ItemChanged; _observedTasks.Remove(old); }
        foreach (var task in Session.Tasks)
            if (_observedTasks.Add(task)) task.PropertyChanged += ItemChanged;
        if (!ReferenceEquals(_observedGame, CurrentGame))
        {
            if (_observedGame != null) _observedGame.PropertyChanged -= ItemChanged;
            _observedGame = CurrentGame;
            if (_observedGame != null) _observedGame.PropertyChanged += ItemChanged;
        }
        if (!ReferenceEquals(_observedAccount, Session.CurrentAccount))
        {
            if (_observedAccount != null) _observedAccount.PropertyChanged -= ItemChanged;
            _observedAccount = Session.CurrentAccount;
            if (_observedAccount != null) _observedAccount.PropertyChanged += ItemChanged;
        }
        var recent = Session.Tasks.Where(task => task.Kind == PreviewTaskKind.Launch)
            .Select(task => Session.Instances.FirstOrDefault(game => game.Id.Equals(task.InstanceId, StringComparison.OrdinalIgnoreCase)
                && game.Root.Equals(task.Root, StringComparison.OrdinalIgnoreCase)))
            .OfType<PreviewInstance>().Where(game => !ReferenceEquals(game, CurrentGame)).Distinct().ToArray();
        _hasRecent = recent.Length > 0;
        var games = (_hasRecent ? recent.AsEnumerable() : Session.Instances.Where(game => !ReferenceEquals(game, CurrentGame)))
            .Distinct().Take(3).ToArray();
        if (!QuickGames.SequenceEqual(games))
        { QuickGames.Clear(); foreach (var game in games) QuickGames.Add(game); }
        Refresh();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Session.PropertyChanged -= SessionChanged;
        Session.Instances.CollectionChanged -= InstancesChanged;
        Session.Tasks.CollectionChanged -= TasksChanged;
        foreach (var task in _observedTasks) task.PropertyChanged -= ItemChanged;
        _observedTasks.Clear();
        if (_observedGame != null) _observedGame.PropertyChanged -= ItemChanged;
        if (_observedAccount != null) _observedAccount.PropertyChanged -= ItemChanged;
    }
}
