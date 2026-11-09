using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Infrastructure.Configuration;
using FocusMinecraftLauncher.Infrastructure.Updates;
using FocusMinecraftLauncher.Preview;

namespace FocusMinecraftLauncher.Features.More.ViewModels;

/// <summary>所有更新入口共用一个状态；下载百分比不表示可以安装或更新成功。</summary>
public sealed class LauncherUpdateViewModel : ObservableObject, IDisposable
{
    private readonly LauncherConfiguration _configuration;
    private readonly LauncherUpdateService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private PreviewSession? _session;
    private UpdateRelease? _release;
    private UpdateRelease? _prompt;
    private string _job = "", _status = "尚未检查更新", _error = "", _transfer = "";
    private bool _working, _downloading, _applying, _ready;
    private double _percent;
    public string Version => _service.CurrentVersion;
    public string Status => _status;
    public string Error => _error;
    public string Transfer => _transfer;
    public string Notes => _release?.Notes ?? "";
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
    public bool IsWorking => _working;
    public bool IsDownloading => _downloading;
    public bool IsIndeterminate => _working && (!_downloading || _transfer.Length == 0);
    public double Percent => _percent;
    public bool CanCheck => !_working;
    public bool HasDownload => !_ready && _release?.Manifest != null;
    public bool HasPreparedUpdate => _ready;
    public bool CanDownload => !_working && !_ready && _release?.Manifest != null;
    public bool CanInstall => !_working && _ready && _session is { IsBusy: false, IsLoading: false, IsAuthenticating: false };
    public string InstallHint => CanInstall ? "" : _ready ? "请等待游戏和正在执行的任务结束" : "";
    public UiCommand Check { get; }
    public UiCommand Download { get; }
    public UiCommand Cancel { get; }
    public UiCommand Install { get; }
    public UiCommand OpenRelease { get; }
    /// <summary>启动器启动后自动发现新版本时触发，由主窗口弹出更新提示卡片。</summary>
    public event EventHandler? PromptAvailable;
    public UpdateRelease? PendingPrompt => _prompt;
    /// <summary>记下用户已选择“稍后”的版本，同一版本不再打扰，换版本后重新提醒。</summary>
    private string DismissedVersion => LauncherConfiguration.Text(_configuration.ReadLauncher()["updates"]?["dismissedVersion"]);
    public void DismissPrompt()
    {
        _prompt = null;
        if (_release == null) return;
        var version = _release.Version;
        _configuration.ChangeLauncher(root => LauncherConfiguration.Object(root, "updates")["dismissedVersion"] = version);
    }
    public bool AutoCheck
    {
        get => LauncherConfiguration.Boolean(_configuration.ReadLauncher()["updates"]?["autoCheck"], true);
        set { _configuration.ChangeLauncher(root => LauncherConfiguration.Object(root, "updates")["autoCheck"] = value); Changed(); }
    }
    public bool AutoDownload
    {
        get => LauncherConfiguration.Boolean(_configuration.ReadLauncher()["updates"]?["autoDownload"], false);
        set { _configuration.ChangeLauncher(root => LauncherConfiguration.Object(root, "updates")["autoDownload"] = value); Changed(); }
    }
    public LauncherUpdateViewModel(LauncherConfiguration configuration)
    {
        _configuration = configuration; _service = new(configuration.BaseDirectory);
        Check = new(async _ => await CheckAsync(false), _ => CanCheck);
        Download = new(async _ => await DownloadAsync(), _ => CanDownload);
        Cancel = new(_ => _operation?.Cancel(), _ => _downloading && !_applying);
        Install = new(async _ => await InstallAsync(), _ => CanInstall);
        OpenRelease = new(_ =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo((_release?.Page ?? LauncherUpdateService.ReleasePage).AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception error) { _error = error.Message; Publish(); }
        });
    }
    public void Attach(PreviewSession session)
    {
        if (ReferenceEquals(_session, session)) return;
        if (_session != null) _session.PropertyChanged -= SessionChanged;
        _session = session; session.PropertyChanged += SessionChanged;
        if (UpdateBootstrap.StartupJob.Length > 0) { session.TryBeginLauncherUpdate(); _working = true; _status = "正在完成启动器更新"; }
    }
    private void SessionChanged(object? sender, PropertyChangedEventArgs e) { Changed(nameof(CanInstall)); Changed(nameof(InstallHint)); CommandManager.InvalidateRequerySuggested(); }
    private void Publish() { Refresh(); CommandManager.InvalidateRequerySuggested(); }
    private void StartOperation()
    { _operation?.Dispose(); _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _working = true; _error = ""; _transfer = ""; _percent = 0; Publish(); }
    private void FinishOperation() { _working = _downloading = false; Publish(); }
    private void SavePrepared(string id) => _configuration.ChangeLauncher(root => LauncherConfiguration.Object(root, "updates")["preparedJob"] = id);
    /// <summary>主窗口绘制后才进行恢复与自动检查，不阻塞启动动画或配置加载。</summary>
    public async Task OnMainWindowReadyAsync()
    {
        try
        {
            await UpdateBootstrap.CompleteStartupAsync(_lifetime.Token);
            if (UpdateBootstrap.StartupJob.Length > 0) { _session?.EndLauncherUpdate(); _working = false; Publish(); }
            if (UpdateBootstrap.StartupNotice.Length > 0) _session?.NotifyTask(UpdateBootstrap.StartupNotice, UpdateBootstrap.IsRollbackNotice);
            var id = LauncherConfiguration.Text(_configuration.ReadLauncher()["updates"]?["preparedJob"]);
            if (id.Length > 0)
            {
                _working = true; _status = "正在读取更新状态"; Publish();
                try
                {
                    _release = await Task.Run(() => _service.RestorePreparedAsync(id, _lifetime.Token), _lifetime.Token);
                    if (_release != null) { _job = id; _ready = true; _status = "更新已下载，重启后安装 " + _release.Version; return; }
                    SavePrepared("");
                }
                catch (Exception error) when (error is not OperationCanceledException) { SavePrepared(""); _session?.Log("恢复更新缓存：" + error.Message); }
                finally { _working = false; Publish(); }
            }
            var last = LauncherConfiguration.Text(_configuration.ReadLauncher()["updates"]?["lastCheckUtc"]);
            if (AutoCheck && (!DateTimeOffset.TryParse(last, out var checkedAt) || DateTimeOffset.UtcNow - checkedAt > TimeSpan.FromHours(7))) await CheckAsync(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { _error = error.Message; _status = "更新状态读取失败"; Publish(); }
    }
    private async Task CheckAsync(bool automatic)
    {
        if (_working) return; StartOperation(); _status = "正在检查更新"; Publish();
        try
        {
            var release = await _service.CheckAsync(_operation!.Token);
            if (release == null)
            { _release = null; _prompt = null; _ready = false; _job = ""; SavePrepared(""); _status = "当前已是最新正式版"; }
            else
            {
                if (_release?.Manifest?.PackageSha256 != release.Manifest?.PackageSha256 || _release?.Version != release.Version)
                { _ready = false; _job = ""; SavePrepared(""); }
                _release = release;
                _status = _ready ? "更新已下载，重启后安装 " + release.Version : release.Manifest == null
                    ? "发现新版本 " + release.Version + "，可从发布页面下载" : "发现新版本 " + release.Version;
            }
            _configuration.ChangeLauncher(root => LauncherConfiguration.Object(root, "updates")["lastCheckUtc"] = DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (OperationCanceledException) { _status = "检查更新已取消或超时"; }
        catch (Exception error) { _status = "检查更新失败"; _error = error.Message; _session?.Log("检查更新：" + error.Message); }
        finally { FinishOperation(); }
        if (automatic && AutoDownload && CanDownload) await DownloadAsync();
        if (automatic) PromptIfNeeded();
    }
    /// <summary>自动检查发现新版本后弹出提示；同一版本被“稍后”过一次就不再打扰，换新版本会重新提醒。</summary>
    private void PromptIfNeeded()
    {
        if (_ready || _session == null || _release == null) return;
        if (DismissedVersion.Equals(_release.Version, StringComparison.OrdinalIgnoreCase)) return;
        _prompt = _release;
        PromptAvailable?.Invoke(this, EventArgs.Empty);
    }
    private async Task DownloadAsync()
    {
        if (!CanDownload || _session == null) return;
        StartOperation(); _downloading = true; _status = "正在下载更新"; Publish();
        try
        {
            var roots = _session.GameFolders.Append(_session.SelectedRoot).Append(_session.SavedSettings.GameDirectory);
            _job = await _service.DownloadAsync(_release!, roots, new Progress<UpdateTransfer>(p =>
            {
                if (!_working || !_downloading) return;
                _percent = p.Total > 0 ? Math.Clamp(100d * p.Bytes / p.Total, 0, 100) : 0;
                _transfer = $"{p.Bytes / 1048576d:0.0} / {p.Total / 1048576d:0.0} MiB" + (p.BytesPerSecond > 0 ? $"，{p.BytesPerSecond / 1048576d:0.0} MiB/s" : "");
                if (p.Bytes == p.Total) _status = "正在校验更新文件"; Publish();
            }), _operation!.Token);
            _ready = true; SavePrepared(_job);
            if (!await _configuration.FlushAsync()) throw new IOException("更新已下载，但无法保存更新状态，请检查配置目录权限。");
            _status = "更新已下载，重启后安装 " + _release!.Version;
            _session.NotifyTask("启动器更新已下载，可在“更多”中重启安装。", false);
        }
        catch (OperationCanceledException) { _status = "更新下载已取消或超时"; }
        catch (Exception error) { _status = _ready ? "更新已下载，暂时无法保存更新状态" : "更新下载失败"; _error = error.Message; }
        finally { FinishOperation(); }
    }
    private async Task InstallAsync()
    {
        if (!CanInstall || _session == null || !_session.TryBeginLauncherUpdate()) return;
        bool handedOff = false;
        StartOperation(); _applying = true; _status = "正在准备重启更新"; Publish();
        try
        {
            if (!await _configuration.FlushAsync()) throw new IOException("配置尚未保存，已停止重启更新。");
            await _service.StartUpdaterAsync(_job, _operation!.Token);
            handedOff = true;
            if (Application.Current.MainWindow is MainWindow mainWindow) mainWindow.CloseLauncher();
            else Application.Current.MainWindow.Close();
        }
        catch (Exception error) { _status = "无法开始更新"; _error = error.Message; }
        finally { if (!handedOff) { _session.EndLauncherUpdate(); _applying = false; FinishOperation(); } }
    }
    public void Dispose()
    { _lifetime.Cancel(); _operation?.Cancel(); _operation?.Dispose(); _lifetime.Dispose(); _service.Dispose(); if (_session != null) _session.PropertyChanged -= SessionChanged; }
}
