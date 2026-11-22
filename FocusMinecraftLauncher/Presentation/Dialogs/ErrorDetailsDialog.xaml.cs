using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Infrastructure.Runtime;
using FocusMinecraftLauncher.Presentation.Controls;
using Microsoft.Win32;
using Screen = System.Windows.Forms.Screen;

namespace FocusMinecraftLauncher.Presentation.Dialogs;

internal sealed record ErrorRecoveryAction(string Label, Action Execute, Func<bool>? CanExecute = null);

public partial class ErrorDetailsDialog : Window
{
    private readonly LauncherError _report;
    private readonly CancellationTokenSource _bundleLifetime = new();
    private IReadOnlyList<DiagnosticFile>? _bundle;
    private bool _preparingBundle;
    private ErrorDetailsDialog(LauncherError report, IReadOnlyList<ErrorRecoveryAction> recovery)
    {
        InitializeComponent();
        _report = report;
        DataContext = report;
        DetailsText.Text = report.BuildText();
        OpenReportButton.IsEnabled = report.ReportPath.Length > 0;
        foreach (var action in recovery)
        {
            var button = new Wpf.Ui.Controls.Button { Content = action.Label, Margin = new Thickness(0, 0, 8, 8) };
            button.Command = new UiCommand(_ =>
            {
                Close();
                Dispatcher.BeginInvoke(() =>
                {
                    if (action.CanExecute?.Invoke() != false) action.Execute();
                });
            }, _ => action.CanExecute?.Invoke() != false);
            RecoveryButtons.Children.Add(button);
        }
        if (RecoveryButtons.Children.Count > 0) RecoveryButtons.Visibility = Visibility.Visible;
    }

    public static void Show(LauncherError report) => Show(report, []);

    internal static void Show(LauncherError report, IReadOnlyList<ErrorRecoveryAction> recovery)
    {
        var app = Application.Current;
        if (app == null || app.Dispatcher.HasShutdownStarted) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.Invoke(() => Show(report, recovery)); return; }
        var owner = app.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
            ?? (app.MainWindow?.IsVisible == true ? app.MainWindow : null);
        var dialog = new ErrorDetailsDialog(report, recovery);
        if (owner != null)
        {
            dialog.Owner = owner;
            ThemeVisuals.SetMotionAllowed(dialog, ThemeVisuals.GetMotionAllowed(owner));
        }
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.FitWorkArea(owner);
        dialog.ShowDialog();
    }

    private void FitWorkArea(Window? owner)
    {
        var workArea = SystemParameters.WorkArea;
        var handle = owner != null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero;
        if (owner != null && handle != IntPtr.Zero)
        {
            var ownerWorkArea = Screen.FromHandle(handle).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(owner);
            workArea = new Rect(0, 0, ownerWorkArea.Width / dpi.DpiScaleX, ownerWorkArea.Height / dpi.DpiScaleY);
        }
        Width = Math.Min(Width, Math.Max(1, workArea.Width - 24));
        Height = Math.Min(Height, Math.Max(1, workArea.Height - 24));
    }

    private void CopyReport(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_report.BuildText()); Feedback.Text = "错误信息已复制。"; }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException)
        { Feedback.Text = "剪贴板正被占用，请稍后再试。"; }
    }
    private void OpenReport(object sender, RoutedEventArgs e)
    {
        try { RuntimeSupport.OpenFolder(Path.GetDirectoryName(_report.ReportPath)!); }
        catch (Exception error) { Feedback.Text = RuntimeSupport.Error(error); }
    }

    private async void PreviewBundle(object sender, RoutedEventArgs e)
    {
        SetPreviewVisible(true);
        if (_bundle != null || _preparingBundle) return;
        _preparingBundle = true;
        var token = _bundleLifetime.Token;
        BundleNotice.Text = "正在准备诊断包…";
        try
        {
            var files = await Task.Run(() => DiagnosticBundle.Prepare(_report, token), token);
            if (token.IsCancellationRequested) return;
            _bundle = files;
            BundleFiles.ItemsSource = files;
            BundleFiles.SelectedIndex = 0;
            BundleFiles.Visibility = BundleText.Visibility = Visibility.Visible;
            SaveBundleButton.IsEnabled = true;
            BundleNotice.Text = $"共 {files.Count} 个文本文件";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { BundleNotice.Text = "无法生成诊断包预览。 " + RuntimeSupport.Error(error); }
        finally { _preparingBundle = false; }
    }

    private void BackToReport(object sender, RoutedEventArgs e) => SetPreviewVisible(false);

    private void SetPreviewVisible(bool show)
    {
        DialogHeading.Text = Title = show ? "诊断包预览" : "错误详情";
        ReportScroll.Visibility = ReportActions.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        BundlePreview.Visibility = BundleActions.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) BackToReportButton.Focus();
        else PreviewBundleButton.Focus();
    }

    private void BundleTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox preview) return;
        preview.CaretIndex = 0;
        preview.ScrollToHome();
    }

    private async void SaveBundle(object sender, RoutedEventArgs e)
    {
        if (_bundle is not { } files || !SaveBundleButton.IsEnabled) return;
        var dialog = new SaveFileDialog
        {
            Title = "保存诊断包",
            FileName = "FMLauncher-诊断-" + _report.Id + ".zip",
            DefaultExt = ".zip",
            Filter = "诊断压缩包 (*.zip)|*.zip",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var token = _bundleLifetime.Token;
        SaveBundleButton.IsEnabled = false;
        BundleNotice.Text = "正在保存诊断包…";
        try
        {
            await Task.Run(() => DiagnosticBundle.SaveAsync(dialog.FileName, files, token), token);
            if (!token.IsCancellationRequested) BundleNotice.Text = "诊断包已保存。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { BundleNotice.Text = "无法保存诊断包。 " + RuntimeSupport.Error(error); }
        finally { if (!token.IsCancellationRequested) SaveBundleButton.IsEnabled = true; }
    }

    protected override void OnClosed(EventArgs e)
    {
        _bundleLifetime.Cancel();
        _bundleLifetime.Dispose();
        base.OnClosed(e);
    }

    private void CloseDialog(object sender, RoutedEventArgs e) => Close();
    private void DragWindow(object sender, MouseButtonEventArgs e)
    { if (e.ChangedButton == MouseButton.Left) { try { DragMove(); } catch (InvalidOperationException) { } } }
}
