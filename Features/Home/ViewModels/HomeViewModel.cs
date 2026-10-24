using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;
using FMLauncher.Common;
using System.ComponentModel;
using FMLauncher.Features.Instances.Services;
using FMLauncher.CoreIntegration.Adapters;
using FMLauncher.CoreIntegration.Models;
using FMLauncher.Features.Accounts.ViewModels;
using FMLauncher.Features.Downloads.Services;
using FMLauncher.Features.Launch.Models;
using FMLauncher.Features.Launch.Services;
using FMLauncher.Infrastructure.Configuration;
namespace FMLauncher.Features.Home.ViewModels;

/// <summary>主页编排版本启动和维护；所有游戏工作目录固定为 versions/版本。</summary>
public sealed class HomeViewModel : PageViewModel
{
    private readonly InstanceCatalog _catalog;
    private readonly SettingsStore _settings;
    private readonly GameSessionService _sessions;
    private readonly AccountsViewModel _accounts;
    private readonly ConcurrentQueue<SessionMessage> _pending = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    public ReadOnlyObservableCollection<VersionItem> Versions => _catalog.Versions;
    private bool _gameActive;
    public bool IsGameActive { get => _gameActive; private set => Set(ref _gameActive, value); }
    public bool HasVersion => Version != null;
    public ObservableCollection<string> Logs { get; } = [];
    public AccountsViewModel Accounts => _accounts;
    public VersionItem? Version { get => _catalog.Selected; set { if (value != null) _catalog.Select(value); } }
    public AsyncCommand Reload { get; }
    public AsyncCommand Launch { get; }
    public AsyncCommand Stop { get; }
    public AsyncCommand Verify { get; }
    public AsyncCommand Repair { get; }
    public AsyncCommand ExportLogs { get; }
    public AsyncCommand OpenFolder { get; }
    public HomeViewModel(InstanceCatalog catalog, SettingsStore settings, GameSessionService sessions, AccountsViewModel accounts, InstallationAdapter installation, OperationService operations)
    {
        _catalog = catalog; catalog.PropertyChanged += CatalogPropertyChanged; _settings = settings; _sessions = sessions; _accounts = accounts;
        Reload = Command(async ct => { await ReloadAsync(ct); Status = "游戏列表已更新。"; });
        Launch = Command(async ct =>
        {
            if (operations.Busy) throw new InvalidOperationException("请先完成或取消当前文件任务，再启动游戏。");
            var account = accounts.Current ?? throw new InvalidOperationException("请先点击头像登录或添加离线账号。");
            var version = Version?.Id ?? throw new InvalidOperationException("请选择本地版本。");
            settings.Current.VersionId = version; settings.Current.AccountId = account.Id; settings.Save();
            Status = "正在准备游戏…";
            await sessions.LaunchAsync(new(settings.Snapshot(), version, account.Id), ct);
            Status = "游戏正在启动，请稍候。";
        });
        Stop = Command(async ct => { Launch.Cancel(); await sessions.StopAsync(); Status = "已请求停止游戏。"; });
        Verify = Command(ct => CheckAsync(false, ct));
        Repair = Command(ct => CheckAsync(true, ct));
        ExportLogs = Command(ct =>
        {
            var dialog = new SaveFileDialog { Filter = "文本日志|*.txt", FileName = "FMLauncher-session.txt" };
            if (dialog.ShowDialog() == true) File.WriteAllLines(dialog.FileName, Logs);
            return Task.CompletedTask;
        });
        OpenFolder = Command(ct =>
        {
            var id = Version?.Id ?? throw new InvalidOperationException("请选择版本。");
            var path = Path.Combine(settings.Current.GameRoot, "versions", id);
            if (!Directory.Exists(path)) throw new InvalidOperationException("目录尚不存在。");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return Task.CompletedTask;
        });
        sessions.Received += Receive; _timer.Tick += Drain; _timer.Start();
        async Task CheckAsync(bool repair, CancellationToken ct)
        {
            if (sessions.Running || sessions.Preparing) throw new InvalidOperationException("请先停止游戏再校验或修复。");
            var version = Version?.Id ?? throw new InvalidOperationException("请先选择版本。");
            var snapshot = settings.Snapshot();
            Status = await operations.RunAsync(repair ? "修复游戏" : "校验游戏", (p, token) => installation.VerifyAsync(snapshot, version, repair, p, token), ct);
        }
    }
    private void Receive(SessionMessage value) { _pending.Enqueue(value); while (_pending.Count > 2000) _pending.TryDequeue(out _); }
    private void Drain(object? sender, EventArgs e)
    {
        for (var i = 0; i < 100 && _pending.TryDequeue(out var message); i++)
        {
            if (message.Kind == "log") { Logs.Add(message.Text); while (Logs.Count > 400) Logs.RemoveAt(0); }
            else if (message.Kind == "exit") { Status = message.Text; Logs.Add($"游戏退出代码：{message.ExitCode}"); }
            else if (message.Kind != "disconnected") Status = message.Text;
        }
        IsGameActive = _sessions.Running || _sessions.Preparing || Launch.IsBusy;
    }
    public async Task ReloadAsync(CancellationToken ct)
    {
        await _catalog.RefreshAsync(ct);
        await _accounts.ReloadAsync(ct);
    }
    private void CatalogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(InstanceCatalog.Selected)) { Changed(nameof(Version)); Changed(nameof(HasVersion)); } }
    public override void Dispose() { _catalog.PropertyChanged -= CatalogPropertyChanged; base.Dispose(); _timer.Stop(); _timer.Tick -= Drain; _sessions.Received -= Receive; }
}
