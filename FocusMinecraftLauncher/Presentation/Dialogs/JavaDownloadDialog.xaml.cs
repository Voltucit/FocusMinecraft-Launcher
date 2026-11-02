using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using FocusMinecraftLauncher.Presentation.Controls;

namespace FocusMinecraftLauncher.Presentation.Dialogs;

/// <summary>选择要下载的官方 Java 大版本；沿用三套主题卡片样式。</summary>
public partial class JavaDownloadDialog : Window
{
    private JavaDownloadDialog(int? recommended)
    {
        InitializeComponent();
        Title = "下载 Java";
        System.Windows.Automation.AutomationProperties.SetName(this, "下载 Java");
        var preferred = recommended is 8 or 16 or 17 or 21 or 25 ? recommended.Value : 21;
        RadioButton? selected = preferred switch
        {
            8 => Option8,
            16 => Option16,
            17 => Option17,
            21 => Option21,
            _ => Option25
        };
        selected.IsChecked = true;
        Loaded += OnLoaded;
    }

    /// <summary>让用户选择版本并确认；取消或缺少主窗口时返回 null。可在任意线程调用。</summary>
    public static int? Ask(int? recommended)
    {
        var application = Application.Current;
        if (application == null) return null;
        if (!application.Dispatcher.CheckAccess()) return application.Dispatcher.Invoke(() => Ask(recommended));
        if (application.MainWindow is not { IsLoaded: true } owner) return null;
        var dialog = new JavaDownloadDialog(recommended) { Owner = owner };
        ThemeVisuals.SetMotionAllowed(dialog, ThemeVisuals.GetMotionAllowed(owner));
        return dialog.ShowDialog() == true ? dialog.Selected : null;
    }

    private int? Selected => IsChecked(Option8) ? 8 : IsChecked(Option16) ? 16 : IsChecked(Option17) ? 17 : IsChecked(Option21) ? 21 : IsChecked(Option25) ? 25 : null;
    private static bool IsChecked(RadioButton button) => button.IsChecked == true;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ThemeVisuals.GetMotionAllowed(this) && SystemParameters.ClientAreaAnimation)
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void Accept(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Decline(object sender, RoutedEventArgs e) => DialogResult = false;
}
