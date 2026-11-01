using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using FocusMinecraftLauncher.Infrastructure.Runtime;
using FocusMinecraftLauncher.Presentation.Controls;

namespace FocusMinecraftLauncher.Presentation.Dialogs;

/// <summary>整合包导入前的确认卡片；沿用三套主题的画刷、圆角与按钮样式，替代系统消息框。</summary>
public partial class PackImportDialog : Window
{
    public PackImportDialog(PackManifest manifest, string instanceName, int optionalFiles)
    {
        InitializeComponent();
        PackName.Text = Trim(manifest.Name);
        MinecraftValue.Text = string.IsNullOrWhiteSpace(manifest.Minecraft) ? "未知" : manifest.Minecraft;
        LoaderValue.Text = DescribeLoader(manifest.Loader, manifest.LoaderVersion);
        InstanceValue.Text = instanceName;
        FilesValue.Text = DescribeFiles(manifest.Files);
        OptionalNote.Text = $"可选文件 {optionalFiles} 项将跳过";
        OptionalNote.Visibility = optionalFiles > 0 ? Visibility.Visible : Visibility.Collapsed;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ThemeVisuals.GetMotionAllowed(this))
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        ConfirmButton.Focus();
    }

    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void Accept(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Decline(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string Trim(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "未命名整合包";
        name = name.Trim();
        return name.Length <= 96 ? name : name[..96].TrimEnd() + "…";
    }

    private static string DescribeLoader(string? loader, string? version)
    {
        var name = loader?.Trim().ToLowerInvariant() switch
        {
            null or "" or "vanilla" => "原版",
            "fabric" => "Fabric",
            "quilt" => "Quilt",
            "forge" => "Forge",
            "neoforge" => "NeoForge",
            _ => loader!.Trim()
        };
        return string.IsNullOrWhiteSpace(version) ? name : name + " " + version.Trim();
    }

    private static string DescribeFiles(IReadOnlyList<PackFile> files)
    {
        var count = $"{files.Count} 个文件";
        var size = files.Sum(file => file.Size);
        return size > 0 ? count + "，共 " + FormatSize(size) : count;
    }

    private static string FormatSize(long bytes)
    {
        const double kilo = 1024, mega = kilo * 1024, giga = mega * 1024;
        return bytes switch
        {
            >= (long)giga => (bytes / giga).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
            >= (long)mega => (bytes / mega).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
            >= (long)kilo => (bytes / kilo).ToString("0", CultureInfo.InvariantCulture) + " KB",
            _ => bytes.ToString(CultureInfo.InvariantCulture) + " B"
        };
    }
}
