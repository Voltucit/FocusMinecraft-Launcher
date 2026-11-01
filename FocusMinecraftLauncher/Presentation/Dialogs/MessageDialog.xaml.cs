using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using FocusMinecraftLauncher.Presentation.Controls;

namespace FocusMinecraftLauncher.Presentation.Dialogs;

/// <summary>沿用三套主题画刷与按钮样式的提示及确认卡片，用于替代系统消息框。</summary>
public partial class MessageDialog : Window
{
    private MessageDialog(string headline, string body)
    {
        InitializeComponent();
        Title = headline;
        System.Windows.Automation.AutomationProperties.SetName(this, headline);
        Headline.Text = headline;
        Body.Text = body;
        Loaded += OnLoaded;
    }

    /// <summary>在主窗口之上显示一条提示；可在任意线程调用，取消或缺少主窗口时静默略过。</summary>
    public static void Show(string headline, string body)
    {
        var application = Application.Current;
        if (application == null) return;
        if (!application.Dispatcher.CheckAccess())
        {
            application.Dispatcher.Invoke(() => Show(headline, body));
            return;
        }
        if (application.MainWindow is not { IsLoaded: true } owner) return;
        var dialog = new MessageDialog(headline, body) { Owner = owner };
        ThemeVisuals.SetMotionAllowed(dialog, ThemeVisuals.GetMotionAllowed(owner));
        dialog.ShowDialog();
    }

    /// <summary>显示需要明确同意的确认卡片；默认聚焦保留现状的按钮，Esc 或窗口关闭均不执行操作。</summary>
    public static bool Confirm(string headline, string body, string confirmText, string cancelText)
    {
        var application = Application.Current;
        if (application == null) return false;
        if (!application.Dispatcher.CheckAccess())
            return application.Dispatcher.Invoke(() => Confirm(headline, body, confirmText, cancelText));
        if (application.MainWindow is not { IsLoaded: true } owner) return false;
        var dialog = new MessageDialog(headline, body) { Owner = owner };
        ThemeVisuals.SetMotionAllowed(dialog, ThemeVisuals.GetMotionAllowed(owner));
        dialog.ConfirmButton.Content = confirmText;
        dialog.ConfirmButton.IsDefault = false;
        dialog.ConfirmButton.IsCancel = false;
        dialog.ConfirmButton.SetResourceReference(StyleProperty, "DangerButton");
        System.Windows.Automation.AutomationProperties.SetName(dialog.ConfirmButton, confirmText);
        dialog.CancelButton.Content = cancelText;
        dialog.CancelButton.IsDefault = true;
        dialog.CancelButton.IsCancel = true;
        dialog.CancelButton.Visibility = Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetName(dialog.CancelButton, cancelText);
        return dialog.ShowDialog() == true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ThemeVisuals.GetMotionAllowed(this) && SystemParameters.ClientAreaAnimation)
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        if (CancelButton.Visibility == Visibility.Visible) CancelButton.Focus();
        else ConfirmButton.Focus();
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
