using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using FocusMinecraftLauncher.Infrastructure.Runtime;
using FocusMinecraftLauncher.Presentation.Controls;

namespace FocusMinecraftLauncher.Presentation.Dialogs;

public partial class ManualPackFilesDialog : Window
{
    private readonly ManualPackFilesViewModel _model;

    private ManualPackFilesDialog(IReadOnlyList<PackFile> files, CancellationToken token)
    {
        InitializeComponent();
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 40);
        DataContext = _model = new ManualPackFilesViewModel(this, files, token);
        Loaded += (_, _) =>
        {
            if (ThemeVisuals.GetMotionAllowed(this) && SystemParameters.ClientAreaAnimation)
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
                    { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        };
        Closed += (_, _) => _model.Dispose();
    }

    public static IReadOnlyDictionary<string, string>? Ask(IReadOnlyList<PackFile> files, CancellationToken token)
    {
        var application = Application.Current;
        if (token.IsCancellationRequested || application == null || application.Dispatcher.HasShutdownStarted) return null;
        if (!application.Dispatcher.CheckAccess()) return application.Dispatcher.Invoke(() => Ask(files, token));
        if (application.MainWindow is not { IsLoaded: true } owner) return null;
        // 需要用户补齐文件时恢复原窗口，避免拥有者最小化导致补齐窗口不可见。
        if (!owner.IsVisible) owner.Show();
        if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
        var dialog = new ManualPackFilesDialog(files, token) { Owner = owner };
        ThemeVisuals.SetMotionAllowed(dialog, ThemeVisuals.GetMotionAllowed(owner));
        using var cancellation = token.Register(() => dialog.Dispatcher.BeginInvoke(new Action(() => { if (dialog.IsVisible) dialog.Close(); })));
        return dialog.ShowDialog() == true ? dialog._model.Sources : null;
    }

    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }
    private void WindowKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }
    private void Cancel(object sender, RoutedEventArgs e) => Close();
    private void Continue(object sender, RoutedEventArgs e) { if (_model.CanContinue) DialogResult = true; }
}
