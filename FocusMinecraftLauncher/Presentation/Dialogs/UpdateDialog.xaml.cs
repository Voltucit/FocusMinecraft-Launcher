using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using FocusMinecraftLauncher.Features.More.ViewModels;
using FocusMinecraftLauncher.Infrastructure.Updates;
using FocusMinecraftLauncher.Presentation.Controls;

namespace FocusMinecraftLauncher.Presentation.Dialogs;

/// <summary>自动检查发现新版本时的更新卡片：复用主题画刷，下载与安装沿用“更多”页里的同一套流程。</summary>
public partial class UpdateDialog : Window
{
    private readonly LauncherUpdateViewModel _updates;

    private UpdateDialog(LauncherUpdateViewModel updates, UpdateRelease release)
    {
        InitializeComponent();
        _updates = updates;
        DataContext = updates;
        Title = "发现新版本";
        Headline.Text = "发现新版本 " + release.Version;
        VersionLine.Text = "当前版本 " + updates.Version + "，可按需下载后重启安装。";
        NotesBlock.Text = string.IsNullOrWhiteSpace(release.Notes)
            ? (release.Manifest == null ? "此版本只能从发布页面手动下载。" : "此版本没有附带更新说明。")
            : release.Notes;
        updates.PropertyChanged += UpdatesChanged;
        Loaded += OnLoaded;
        Closed += (_, _) => updates.PropertyChanged -= UpdatesChanged;
    }

    /// <summary>在主窗口之上弹出更新提示；没有待提示的版本或主窗口尚未就绪时静默跳过。</summary>
    public static void Prompt(LauncherUpdateViewModel updates)
    {
        if (updates.PendingPrompt is not { } release) return;
        var application = Application.Current;
        if (application?.MainWindow is not { IsLoaded: true } owner) return;
        var dialog = new UpdateDialog(updates, release) { Owner = owner };
        ThemeVisuals.SetMotionAllowed(dialog, ThemeVisuals.GetMotionAllowed(owner));
        dialog.ShowDialog();
    }

    private void UpdatesChanged(object? sender, PropertyChangedEventArgs e) => SyncActions();

    /// <summary>下载完成后主按钮变成“重启并安装”，更新流程与“更多”页共用，不另起一套状态。</summary>
    private void SyncActions()
    {
        if (_updates.HasPreparedUpdate)
        {
            ActionButton.Content = "重启并安装";
            ActionButton.IsEnabled = _updates.CanInstall;
        }
        else
        {
            ActionButton.Content = "立即下载";
            ActionButton.IsEnabled = _updates.CanDownload;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ThemeVisuals.GetMotionAllowed(this) && SystemParameters.ClientAreaAnimation)
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        SyncActions();
        ActionButton.Focus();
    }

    private void Act(object sender, RoutedEventArgs e)
    {
        if (_updates.HasPreparedUpdate) _updates.Install.Execute(null);
        else _updates.Download.Execute(null);
        SyncActions();
    }

    private void Later(object sender, RoutedEventArgs e)
    {
        _updates.DismissPrompt();
        Close();
    }

    private void OpenPage(object sender, RoutedEventArgs e) => _updates.OpenRelease.Execute(null);

    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }
}
