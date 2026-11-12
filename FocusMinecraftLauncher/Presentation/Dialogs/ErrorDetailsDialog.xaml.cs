using System.IO;
using System.Windows;
using System.Windows.Input;
using FocusMinecraftLauncher.Infrastructure.Runtime;
using FocusMinecraftLauncher.Presentation.Controls;

namespace FocusMinecraftLauncher.Presentation.Dialogs;

public partial class ErrorDetailsDialog : Window
{
    private readonly LauncherError _report;
    private ErrorDetailsDialog(LauncherError report)
    {
        InitializeComponent();
        _report = report;
        DataContext = report;
        DetailsText.Text = report.BuildText();
        OpenReportButton.IsEnabled = report.ReportPath.Length > 0;
        Width = Math.Min(680, SystemParameters.WorkArea.Width - 24);
        MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 24);
        ReportScroll.MaxHeight = Math.Max(100, MaxHeight - 190);
    }

    public static void Show(LauncherError report)
    {
        var app = Application.Current;
        if (app == null || app.Dispatcher.HasShutdownStarted) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.Invoke(() => Show(report)); return; }
        var owner = app.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
            ?? (app.MainWindow?.IsVisible == true ? app.MainWindow : null);
        var dialog = new ErrorDetailsDialog(report);
        if (owner != null)
        {
            dialog.Owner = owner;
            ThemeVisuals.SetMotionAllowed(dialog, ThemeVisuals.GetMotionAllowed(owner));
        }
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
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
    private void CloseDialog(object sender, RoutedEventArgs e) => Close();
    private void DragWindow(object sender, MouseButtonEventArgs e)
    { if (e.ChangedButton == MouseButton.Left) { try { DragMove(); } catch (InvalidOperationException) { } } }
}
