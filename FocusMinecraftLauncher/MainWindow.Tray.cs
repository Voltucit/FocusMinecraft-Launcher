using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using FocusMinecraftLauncher.Infrastructure.Configuration;
using FocusMinecraftLauncher.Presentation.Controls;
using FocusMinecraftLauncher.Presentation.Dialogs;
using FocusMinecraftLauncher.Presentation.Tray;

namespace FocusMinecraftLauncher;

public partial class MainWindow
{
    private TrayService? _trayService;
    private bool _exitRequested, _networkExitConfirmed, _showingCloseChoice, _minimizingToTray, _sessionEnding;

    private void InitializeTray()
    {
        if (_trayService != null) return;
        try
        {
            _trayService = new TrayService(this, Session, CloseLauncher,
                () => !_exitRequested && !_isExiting && !_stoppingNetwork && !_stoppingBackend && !_sessionEnding && !_showingCloseChoice);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or System.IO.IOException or ExternalException)
        {
            ((App)Application.Current).Configuration.ReportNotice("系统托盘暂不可用，最小化将保留任务栏入口。" + Infrastructure.Runtime.RuntimeSupport.Error(error));
        }
    }

    private string NetworkExitMessage()
    {
        var network = ((App)Application.Current).Multiplayer;
        return network.IsInstallingComponents ? "关闭启动器会取消联机组件的安装。"
            : !network.HasSession ? "关闭启动器会取消正在进行的联机准备。"
            : network.IsHosting ? "关闭启动器会解散当前房间并结束联机。" : "关闭启动器会断开当前联机连接。";
    }

    private bool ChooseCloseAction(CancelEventArgs e)
    {
        if (_exitRequested) return false;
        e.Cancel = true;
        if (_showingCloseChoice) return true;
        var behavior = ((App)Application.Current).Configuration.CloseBehavior;
        if (behavior == LauncherCloseBehavior.Minimize)
        {
            Dispatcher.BeginInvoke(new Action(MinimizeToBackground));
            return true;
        }
        if (behavior == LauncherCloseBehavior.Close)
        {
            _exitRequested = true;
            _networkExitConfirmed = false;
            e.Cancel = false;
            return false;
        }
        _showingCloseChoice = true;
        try
        {
            var networkActive = ((App)Application.Current).Multiplayer.HasActivity;
            var message = "最小化到系统托盘后，启动器会继续在后台运行，可从托盘图标恢复窗口。";
            if (networkActive) message += "\n\n" + NetworkExitMessage();
            var choice = MessageDialog.ChooseWindowClose(message);
            if (choice == MessageDialog.CloseChoice.Minimize)
                Dispatcher.BeginInvoke(new Action(MinimizeToBackground));
            else if (choice == MessageDialog.CloseChoice.Close)
            {
                _exitRequested = true;
                _networkExitConfirmed = networkActive;
                QueueClose();
            }
        }
        finally { _showingCloseChoice = false; }
        return true;
    }

    /// <summary>托盘的明确退出及更新重启绕过关闭选择，仍执行原有联机与写入收尾。</summary>
    public void CloseLauncher()
    {
        if (_showingCloseChoice || _stoppingNetwork || _stoppingBackend || _isExiting || _sessionEnding) return;
        RestoreFromTray();
        _exitRequested = true;
        _networkExitConfirmed = false;
        QueueClose();
    }

    private void QueueClose() => Dispatcher.BeginInvoke(new Action(Close));

    private void MinimizeToBackground()
    {
        if (_minimizingToTray || _exitRequested || _isExiting || _sessionEnding) return;
        _minimizingToTray = true;
        try
        {
            InitializeTray();
            if (_trayService == null) { WindowState = WindowState.Minimized; return; }
            _trayService.MinimizeMainWindow();
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or System.IO.IOException or ExternalException)
        {
            RestoreFromTray();
            MessageDialog.Show("无法最小化到托盘", Infrastructure.Runtime.RuntimeSupport.Error(error));
        }
        finally { _minimizingToTray = false; }
    }

    private void RestoreFromTray()
    {
        if (_stoppingNetwork || _stoppingBackend || _isExiting || _sessionEnding) return;
        if (_trayService != null) { _trayService.RestoreMainWindow(); return; }
        if (!IsVisible) Show();
        WindowState = WindowState.Normal;
        Activate();
        ThemeVisuals.SetWindowActive(this, true);
    }

    private void SystemSessionEnding(object sender, SessionEndingCancelEventArgs e) => _sessionEnding = true;
}
