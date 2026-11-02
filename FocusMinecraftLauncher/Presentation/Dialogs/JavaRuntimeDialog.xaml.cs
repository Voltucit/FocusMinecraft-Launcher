using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using FocusMinecraftLauncher.Presentation.Controls;

namespace FocusMinecraftLauncher.Presentation.Dialogs;

/// <summary>缺少匹配 Java 时的三选一：下载所需版本、尝试更高版本、取消。</summary>
public enum JavaRuntimeChoice
{
    Cancel,
    Download,
    UseHigher
}

public partial class JavaRuntimeDialog : Window
{
    private JavaRuntimeDialog(int required, int? higher, bool canDownload)
    {
        InitializeComponent();
        Title = "需要 Java " + required;
        System.Windows.Automation.AutomationProperties.SetName(this, Title);
        Headline.Text = "这个实例需要 Java " + required;

        var body = "启动器在本机没有找到 Java " + required + "。";
        if (canDownload && higher is { } higherVersion)
            body += "\n\n可以让启动器下载官方 Java " + required + "，也可以先用已安装的 Java " + higherVersion + " 尝试启动。";
        else if (canDownload)
            body += "\n\n可以让启动器下载官方 Java " + required + "，安装完成后会自动继续启动。";
        else if (higher is { } only)
            body += "\n\n启动器不能自动安装这个版本，可以先用已安装的 Java " + only + " 尝试启动，或取消后在设置中手动选择。";
        else
            body += "\n\n启动器不能自动安装这个版本，请手动安装 Java " + required + " 或更高版本，然后在设置中选择。";
        Body.Text = body;

        if (!canDownload) DownloadButton.Visibility = Visibility.Collapsed;
        else System.Windows.Automation.AutomationProperties.SetName(DownloadButton, "下载 Java " + required);

        if (higher is { } installed)
        {
            HigherButton.Content = "用 Java " + installed + " 启动";
            System.Windows.Automation.AutomationProperties.SetName(HigherButton, "用 Java " + installed + " 启动");
        }
        else HigherButton.Visibility = Visibility.Collapsed;

        Loaded += OnLoaded;
    }

    /// <summary>收集用户选择；无法弹出窗口时按取消处理。</summary>
    public static JavaRuntimeChoice Ask(int required, int? higher, bool canDownload)
    {
        var application = Application.Current;
        if (application == null) return JavaRuntimeChoice.Cancel;
        if (!application.Dispatcher.CheckAccess())
            return application.Dispatcher.Invoke(() => Ask(required, higher, canDownload));
        if (application.MainWindow is not { IsLoaded: true } owner) return JavaRuntimeChoice.Cancel;
        var dialog = new JavaRuntimeDialog(required, higher, canDownload) { Owner = owner };
        ThemeVisuals.SetMotionAllowed(dialog, ThemeVisuals.GetMotionAllowed(owner));
        dialog.ShowDialog();
        return dialog.Choice;
    }

    private JavaRuntimeChoice Choice { get; set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ThemeVisuals.GetMotionAllowed(this) && SystemParameters.ClientAreaAnimation)
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        (DownloadButton.Visibility == Visibility.Visible ? DownloadButton : HigherButton).Focus();
    }

    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void Download(object sender, RoutedEventArgs e) => Close(JavaRuntimeChoice.Download);
    private void UseHigher(object sender, RoutedEventArgs e) => Close(JavaRuntimeChoice.UseHigher);
    private void Decline(object sender, RoutedEventArgs e) => Close(JavaRuntimeChoice.Cancel);

    private void Close(JavaRuntimeChoice choice)
    {
        Choice = choice;
        DialogResult = choice != JavaRuntimeChoice.Cancel;
    }
}
