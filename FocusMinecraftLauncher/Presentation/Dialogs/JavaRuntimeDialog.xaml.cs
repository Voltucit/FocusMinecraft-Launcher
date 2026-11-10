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
    UseHigher,
    Manual
}

public partial class JavaRuntimeDialog : Window
{
    private JavaRuntimeDialog(int? required, int? higher, bool canDownload, bool installing, bool allowManual)
    {
        InitializeComponent();
        var action = installing ? "安装" : "启动";
        Title = required.HasValue ? "需要 Java " + required : "需要 Java";
        System.Windows.Automation.AutomationProperties.SetName(this, Title);
        Headline.Text = (installing ? "安装需要 Java" : "这个游戏需要 Java") + (required.HasValue ? " " + required : "");

        var body = required.HasValue ? "启动器在本机没有找到 Java " + required + "。" : "启动器在本机没有找到可用的 Java。";
        if (!required.HasValue)
            body += "\n\n可以选择要下载的官方 Java 版本，安装完成后会继续" + action + "。";
        else if (canDownload && higher is { } higherVersion)
            body += "\n\n可以让启动器下载官方 Java " + required + "，也可以先用已安装的 Java " + higherVersion + " 尝试" + action + "。";
        else if (canDownload)
            body += "\n\n可以让启动器下载官方 Java " + required + "，安装完成后会自动继续" + action + "。";
        else if (higher is { } only)
            body += "\n\n启动器不能自动安装这个版本，可以先用已安装的 Java " + only + " 尝试" + action + "，或取消后在设置中手动选择。";
        else
            body += "\n\n启动器不能自动安装这个版本，请手动安装 Java " + required + " 或更高版本，然后在设置中选择。";
        if (allowManual) body += "\n\n也可以手动选择本机已有的 java.exe。";
        Body.Text = body;

        if (!canDownload) DownloadButton.Visibility = Visibility.Collapsed;
        else
        {
            DownloadButton.Content = required.HasValue ? $"下载 Java {required}" : "下载 Java";
            System.Windows.Automation.AutomationProperties.SetName(DownloadButton, required.HasValue ? "下载 Java " + required : "选择 Java 下载版本");
        }
        ManualButton.Visibility = allowManual ? Visibility.Visible : Visibility.Collapsed;

        if (higher is { } installed)
        {
            HigherButton.Content = "用 Java " + installed + " " + action;
            System.Windows.Automation.AutomationProperties.SetName(HigherButton, "用 Java " + installed + " " + action);
        }
        else HigherButton.Visibility = Visibility.Collapsed;

        Loaded += OnLoaded;
    }

    /// <summary>收集用户选择；无法弹出窗口时按取消处理。</summary>
    public static JavaRuntimeChoice Ask(int? required, int? higher, bool canDownload, bool installing = false, bool allowManual = false)
    {
        var application = Application.Current;
        if (application == null) return JavaRuntimeChoice.Cancel;
        if (!application.Dispatcher.CheckAccess())
            return application.Dispatcher.Invoke(() => Ask(required, higher, canDownload, installing, allowManual));
        if (application.MainWindow is not { IsLoaded: true } owner) return JavaRuntimeChoice.Cancel;
        var dialog = new JavaRuntimeDialog(required, higher, canDownload, installing, allowManual) { Owner = owner };
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
        (DownloadButton.Visibility == Visibility.Visible ? DownloadButton
            : ManualButton.Visibility == Visibility.Visible ? ManualButton
            : HigherButton).Focus();
    }

    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void Download(object sender, RoutedEventArgs e) => Close(JavaRuntimeChoice.Download);
    private void UseHigher(object sender, RoutedEventArgs e) => Close(JavaRuntimeChoice.UseHigher);
    private void PickManual(object sender, RoutedEventArgs e) => Close(JavaRuntimeChoice.Manual);
    private void Decline(object sender, RoutedEventArgs e) => Close(JavaRuntimeChoice.Cancel);

    private void Close(JavaRuntimeChoice choice)
    {
        Choice = choice;
        DialogResult = choice != JavaRuntimeChoice.Cancel;
    }
}
