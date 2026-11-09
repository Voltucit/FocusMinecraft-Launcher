using System.Windows;
using FocusMinecraftLauncher.Features.ExtraPages.ViewModels;
using FocusMinecraftLauncher.Features.ExtraPages.Views;
using FocusMinecraftLauncher.Presentation.Controls;
using FocusMinecraftLauncher.Preview;

namespace FocusMinecraftLauncher.Presentation.Tray;

/// <summary>单一托盘入口及两个现有窗口之间的协调；会话仍由 MainWindow 持有。</summary>
internal sealed class TrayService : IDisposable
{
    private readonly MainWindow _mainWindow;
    private readonly PreviewSession _session;
    private readonly Action _exit;
    private readonly Func<bool> _canInteract;
    private readonly LauncherTrayIcon _icon;
    private TrayViewModel? _viewModel;
    private TrayWindow? _panel;
    private bool _disposed;
    private bool IsInTray => !_mainWindow.IsVisible && _mainWindow.WindowState == WindowState.Minimized;

    public TrayService(MainWindow mainWindow, PreviewSession session, Action exit, Func<bool> canInteract)
    {
        _mainWindow = mainWindow; _session = session; _exit = exit; _canInteract = canInteract;
        _icon = new LauncherTrayIcon(mainWindow, TogglePanel, RestoreMainWindow, ShowPanel, ExitLauncher);
        _mainWindow.IsVisibleChanged += MainWindowVisibilityChanged;
        _mainWindow.StateChanged += MainWindowStateChanged;
    }

    private void TogglePanel(TrayAnchor anchor)
    {
        if (_disposed || !IsInTray || !_canInteract()) return;
        if (_panel?.IsVisible == true) HidePanel();
        else ShowPanel(anchor);
    }

    private void ShowPanel(TrayAnchor anchor)
    {
        if (_disposed || !IsInTray || !_canInteract() || !_mainWindow.IsEnabled) return;
        try
        {
            _viewModel ??= new TrayViewModel(_session, RestoreMainWindow, ExitLauncher);
            _panel ??= new TrayWindow(_viewModel, () => _icon.HasPendingInteraction);
            _viewModel.RefreshState();
            _panel.ShowAt(anchor);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            RestoreMainWindow();
            _session.NotifyTask("精简面板无法打开：" + Infrastructure.Runtime.RuntimeSupport.Error(error), true);
        }
    }

    public void HidePanel() => _panel?.HidePanel();

    private void MainWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateTrayVisibility();
    private void MainWindowStateChanged(object? sender, EventArgs e) => UpdateTrayVisibility();
    private void UpdateTrayVisibility()
    {
        if (_disposed || IsInTray) return;
        HidePanel();
        _icon.Hide();
    }

    public void MinimizeMainWindow()
    {
        if (_disposed || !_canInteract()) return;
        HidePanel();
        _mainWindow.WindowState = WindowState.Minimized;
        _icon.Show();
        _mainWindow.Hide();
        ThemeVisuals.SetWindowActive(_mainWindow, false);
    }

    public void RestoreMainWindow()
    {
        if (_disposed || !_canInteract()) return;
        HidePanel();
        _icon.Hide();
        if (!_mainWindow.IsVisible) _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        ThemeVisuals.SetWindowActive(_mainWindow, true);
        if (!_mainWindow.Activate()) TrayPositioner.ActivateOrFlash(_mainWindow);
    }

    private void ExitLauncher()
    {
        if (_disposed) return;
        HidePanel();
        _exit();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mainWindow.IsVisibleChanged -= MainWindowVisibilityChanged;
        _mainWindow.StateChanged -= MainWindowStateChanged;
        _icon.Dispose();
        _panel?.CloseForExit();
        _viewModel?.Dispose();
    }
}
