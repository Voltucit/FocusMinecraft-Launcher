using System.IO;
using System.Windows;
using FMLauncher.Services;

namespace FMLauncher;

public partial class App : Application
{
    public static bool IsPreview => Environment.GetCommandLineArgs().Contains("--ui-preview");
    protected override void OnStartup(StartupEventArgs e)
    {
        if (Diagnostics.AccountCacheProbe.TryRun(e.Args)) { Shutdown(); return; }
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        ThemeService.Apply(SettingsService.Load().Appearance);
        base.OnStartup(e);
    }
}
