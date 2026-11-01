using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FocusMinecraftLauncher.Features.Downloads.Models;
using FocusMinecraftLauncher.Presentation.Controls;
using Wpf.Ui.Controls;

namespace FocusMinecraftLauncher.Features.Downloads.Views;

/// <summary>复用 WPF UI InfoBar，从右侧滑入；重复提示复用同一控件，卸载时停止计时与动画。</summary>
public partial class DownloadNotificationView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    public static readonly DependencyProperty NotificationProperty = DependencyProperty.Register(nameof(Notification),
        typeof(DownloadNotification), typeof(DownloadNotificationView), new PropertyMetadata(null,
            (target, e) => { if (e.NewValue != null) ((DownloadNotificationView)target).ShowNotification(); }));
    public DownloadNotification? Notification { get => (DownloadNotification?)GetValue(NotificationProperty); set => SetValue(NotificationProperty, value); }
    private bool MotionAllowed => ThemeVisuals.GetMotionAllowed(this) && SystemParameters.ClientAreaAnimation;

    public DownloadNotificationView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => HideNotification();
    }

    private void ShowNotification()
    {
        if (!IsLoaded || Notification == null) return;
        _timer.Stop();
        BeginAnimation(OpacityProperty, null); Slide.BeginAnimation(TranslateTransform.XProperty, null);
        NoticeBar.Severity = Notification.IsError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        NoticeBar.IsOpen = true; Visibility = Visibility.Visible;
        Opacity = 1; Slide.X = 0;
        if (MotionAllowed)
        {
            Slide.BeginAnimation(TranslateTransform.XProperty, Animate(512, 0, true));
            BeginAnimation(OpacityProperty, Animate(0, 1, true));
        }
        _timer.Start();
    }

    private void HideNotification()
    {
        _timer.Stop();
        if (!MotionAllowed) { Stop(); return; }
        var notification = Notification;
        var exit = Animate(Slide.X, 512, false);
        exit.Completed += (_, _) => { if (ReferenceEquals(notification, Notification)) Stop(); };
        Slide.BeginAnimation(TranslateTransform.XProperty, exit);
        BeginAnimation(OpacityProperty, Animate(Opacity, 0, false));
    }

    private static DoubleAnimation Animate(double from, double to, bool entering) =>
        new(from, to, TimeSpan.FromMilliseconds(entering ? 250 : 200))
        { EasingFunction = new CubicEase { EasingMode = entering ? EasingMode.EaseOut : EasingMode.EaseIn }, FillBehavior = FillBehavior.Stop };
    private void NoticeVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    { if (e.NewValue is false) Stop(); }
    private void StopNotification(object sender, RoutedEventArgs e) => Stop();
    private void Stop()
    {
        _timer.Stop(); Visibility = Visibility.Collapsed;
        BeginAnimation(OpacityProperty, null); Slide.BeginAnimation(TranslateTransform.XProperty, null);
    }
}
