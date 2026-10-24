using System.Windows;
using FMLauncher.Features.Launch.Services;
using FMLauncher.Infrastructure;
using FMLauncher.Infrastructure.Logging;
using FMLauncher.Shell;
namespace FMLauncher;

public partial class App : Application
{
    private LauncherServices? _services;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--game-host")
        {
            try { await GameHost.RunAsync(e.Args[1]); }
            catch (Exception) { /* 无界面宿主不把异常或认证状态写入未过滤的控制台。 */ }
            Shutdown(); return;
        }
        try
        {
            _services = new LauncherServices();
            var window = new MainWindow(_services);
            MainWindow = window; window.Show();
            _services.Themes.Apply(_services.Settings.Current.Appearance);
            await _services.StartAsync();
        }
        catch (Exception ex)
        {
            if (_services?.IsClosing == true) return;
            MessageBox.Show(FailureText.For(ex), "FMLauncher 无法初始化", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { _services?.Dispose(); base.OnExit(e); }
}
