using System.Windows;
using System.Windows.Media.Imaging;
using FocusMinecraftLauncher.Presentation;
using FocusMinecraftLauncher.Preview;
using FocusMinecraftLauncher.Infrastructure.Configuration;
using System.Text.Json.Nodes;
using FocusMinecraftLauncher.Features.ExtraPages.Views;
using FocusMinecraftLauncher.Features.More.ViewModels;
using FocusMinecraftLauncher.Infrastructure.Updates;

namespace FocusMinecraftLauncher;

/// <summary>按系统动画偏好显示启动画面，完成后交接到主窗口。</summary>
public partial class App : Application
{
    public LauncherConfiguration Configuration { get; private set; } = null!;
    public BackgroundImageStore BackgroundImages { get; private set; } = null!;
    public LauncherUpdateViewModel Updates { get; private set; } = null!;
    private BitmapSource? _initialBackground;
    public string RequestedLaunchId { get; private set; } = "";
    public string RequestedLaunchRoot { get; private set; } = "";
    public string InitialBackgroundError { get; private set; } = "";
    /// <summary>把启动前加载的冻结图片交给共享会话，避免保留额外的图片引用。</summary>
    public BitmapSource? TakeInitialBackground()
    { var image = _initialBackground; _initialBackground = null; return image; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string updateError = "";
        try { if (UpdateBootstrap.RecoverBeforeStartup(e.Args)) { Shutdown(); return; } }
        catch (Exception error) { updateError = "上次更新需要处理，请查看 FMLauncher/Updates 中的记录。"; System.Diagnostics.Trace.TraceError("更新恢复：{0}", error); }
        for(var index=0;index<e.Args.Length-1;index++)
        {if(e.Args[index]=="--launch-instance")RequestedLaunchId=e.Args[++index];else if(e.Args[index]=="--launch-root")RequestedLaunchRoot=e.Args[++index];}

        Configuration = await LauncherConfiguration.OpenAsync();
        if (updateError.Length > 0) Configuration.ReportNotice(updateError);
        Updates = new(Configuration);
        var appearance = Configuration.ReadLauncher()["appearance"];
        BackgroundImages = new BackgroundImageStore(Configuration.ConfigDirectory);
        var storedTheme = LauncherConfiguration.Text(appearance?["theme"], "Glass");
        var backgroundKey = storedTheme is "MinimalistFlat" or "SoftUI" ? storedTheme : "Glass";
        var backgroundPath = LauncherConfiguration.Text(appearance?["themes"]?[backgroundKey]?["backgroundImage"]);
        if (backgroundPath.Length > 0)
        {
            try { _initialBackground = await BackgroundImages.LoadAsync(backgroundPath); }
            catch (Exception error) when (PreviewSession.IsBackgroundError(error))
            { InitialBackgroundError = "背景图片无法读取，已使用默认背景，请重新选择图片。"; }
        }
        if (!SystemParameters.ClientAreaAnimation || !LauncherConfiguration.Boolean(appearance?["animationsEnabled"], true))
        {
            ShowMainWindow();
            return;
        }

        var startup = new StartupWindow { Opacity = 0 };
        startup.Completed += StartupCompleted;
        MainWindow = startup;
        startup.Show();
    }

    /// <summary>补齐最后一次尚在合并等待中的写入，失败时保留明确反馈。</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        Updates?.Dispose();
        if (Configuration != null && !Configuration.FlushAsync().GetAwaiter().GetResult())
            MessageBox.Show("配置保存失败。请检查程序目录的写入权限，本次更改可能未保存。", "FMLauncher", MessageBoxButton.OK, MessageBoxImage.Warning);
        base.OnExit(e);
    }

    private void StartupCompleted(object? sender, EventArgs e)
    {
        if (sender is StartupWindow startup)
            startup.Completed -= StartupCompleted;

        ShowMainWindow();
    }

    /// <summary>在关闭启动画面前更换应用主窗口，保持关闭行为一致。</summary>
    private void ShowMainWindow()
    {
        MainWindow = new MainWindow();
        MainWindow.ContentRendered += MainWindowReady;
        MainWindow.Show();
    }
    private async void MainWindowReady(object? sender, EventArgs e)
    {
        if (sender is Window window) window.ContentRendered -= MainWindowReady;
        await Updates.OnMainWindowReadyAsync();
    }
}
