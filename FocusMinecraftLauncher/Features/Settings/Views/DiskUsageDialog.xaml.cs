using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using FocusMinecraftLauncher.Infrastructure.Runtime;
using FocusMinecraftLauncher.Presentation.Controls;
using FocusMinecraftLauncher.Preview;

namespace FocusMinecraftLauncher.Features.Settings.Views;

public partial class DiskUsageDialog : Window
{
    private readonly PreviewSession _session;
    private readonly string? _affectedRoot;
    private readonly CancellationTokenSource _lifetime = new();
    private StorageSnapshot? _snapshot;
    private bool _busy;
    private bool _cleaning;

    private DiskUsageDialog(PreviewSession session, string? affectedRoot)
    {
        _session = session;
        _affectedRoot = affectedRoot;
        InitializeComponent();
        Width = Math.Min(680, SystemParameters.WorkArea.Width - 24);
        MaxHeight = SystemParameters.WorkArea.Height - 24;
        UsageScroll.MaxHeight = Math.Clamp(MaxHeight - 150, 90, 490);
        Loaded += async (_, _) => await ScanAsync();
        Closed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    internal static void Show(PreviewSession session, string? affectedRoot = null)
    {
        var app = Application.Current;
        var owner = app.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
            ?? (app.MainWindow?.IsVisible == true ? app.MainWindow : null);
        var dialog = new DiskUsageDialog(session, affectedRoot);
        if (owner != null)
        {
            dialog.Owner = owner;
            ThemeVisuals.SetMotionAllowed(dialog, ThemeVisuals.GetMotionAllowed(owner));
        }
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
    }

    private async Task ScanAsync()
    {
        if (_busy) return;
        SetBusy(true);
        var token = _lifetime.Token;
        try
        {
            _snapshot = null;
            CleanupSummary.Text = "正在统计文件大小…";
            UsageNotice.Text = "";
            var storage = _session.GetStorage(_affectedRoot);
            var snapshot = await Task.Run(() => storage.Scan(token), token);
            if (token.IsCancellationRequested) return;
            _snapshot = snapshot;
            Drives.ItemsSource = snapshot.Drives;
            UsageRows.ItemsSource = snapshot.Rows;
            ColumnHeaders.Visibility = Visibility.Visible;
            CleanupSummary.Text = snapshot.Cleanup.Count > 0
                ? $"预计可清理 {RuntimeSupport.FormatSize(snapshot.Cleanup.Sum(file => file.Length))}，共 {snapshot.Cleanup.Count} 个缓存文件。"
                : "当前没有可清理的缓存。";
            UsageNotice.Text = snapshot.ArchivesInUse
                ? "正在处理任务，暂时保留整合包下载缓存。任务结束后可重新统计。"
                : "清理范围为资源与整合包缓存。待继续的安装所需文件会保留。";
            if (snapshot.IsPartial) UsageNotice.Text += "\n部分路径是链接或无法读取，统计已跳过。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { CleanupSummary.Text = "无法统计磁盘占用。 " + RuntimeSupport.Error(error); }
        finally { if (!token.IsCancellationRequested) SetBusy(false); }
    }

    private async void CleanUsage(object sender, RoutedEventArgs e)
    {
        if (_busy || _snapshot is not { Cleanup.Count: > 0 } preview) return;
        _cleaning = true;
        CloseButton.IsEnabled = false;
        SetBusy(true);
        CleanupSummary.Text = "正在清理缓存…";
        CleanupResult.Text = "";
        try
        {
            var result = await _session.CleanStorageAsync(preview, _affectedRoot);
            var skipped = result.Skipped > 0 ? $"已跳过 {result.Skipped} 项被保留、占用或已变化的文件。" : "";
            CleanupResult.Text = $"本次清理 {result.Count} 个文件，共 {RuntimeSupport.FormatSize(result.Bytes)}。{skipped}";
        }
        catch (Exception error) { CleanupResult.Text = "清理失败。 " + RuntimeSupport.Error(error); }
        finally
        {
            _cleaning = false;
            CloseButton.IsEnabled = true;
            _snapshot = null;
            SetBusy(false);
        }
        await ScanAsync();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        CleanupButton.IsEnabled = !busy && _snapshot is { Cleanup.Count: > 0 };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_cleaning) e.Cancel = true;
    }

    private async void RefreshUsage(object sender, RoutedEventArgs e) => await ScanAsync();
    private void CloseDialog(object sender, RoutedEventArgs e) => Close();
    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
