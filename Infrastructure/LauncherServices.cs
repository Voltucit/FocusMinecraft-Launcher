using System.IO;
using System.Windows;
using System.Windows.Threading;
using FMLauncher.Appearance.Themes;
using FMLauncher.Appearance.Wallpapers;
using FMLauncher.Appearance.ViewModels;
using FMLauncher.CoreIntegration.Adapters;
using FMLauncher.Features.Accounts.ViewModels;
using FMLauncher.Features.Downloads.Services;
using FMLauncher.Features.Downloads.ViewModels;
using FMLauncher.Features.Home.ViewModels;
using FMLauncher.Features.Versions.ViewModels;
using FMLauncher.Features.Java.ViewModels;
using FMLauncher.Features.Launch.Services;
using FMLauncher.Features.Modpacks.ViewModels;
using FMLauncher.Features.Mods.ViewModels;
using FMLauncher.Features.Settings.ViewModels;
using FMLauncher.Infrastructure.Configuration;
using FMLauncher.Infrastructure.Persistence;
using FMLauncher.Features.Instances.Services;
namespace FMLauncher.Infrastructure;

/// <summary>显式组合根，统一应用生命周期；构造时不扫描 Java、不认证、不访问游戏服务。</summary>
public sealed class LauncherServices : IDisposable
{
    public AppPaths Paths { get; } = new();
    public SettingsStore Settings { get; }
    public SecretStore Secrets { get; }
    public DeveloperServices Developer { get; } = DeveloperServices.Load();
    public ThemeService Themes { get; } = new();
    public WallpaperManager Wallpapers { get; }
    public GameSessionService Sessions { get; } = new();
    public OperationService Operations { get; } = new();
    public AccountAdapter AccountAdapter { get; }
    public AccountsViewModel Accounts { get; }
    public InstanceCatalog Instances { get; }
    public VersionsViewModel Versions { get; }
    public HomeViewModel Home { get; }
    public DownloadsViewModel Downloads { get; }
    public JavaViewModel Java { get; }
    public ModsViewModel Mods { get; }
    public ModpacksViewModel Modpacks { get; }
    public SettingsViewModel Preferences { get; }
    public AppearanceViewModel Appearance { get; }
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _wallpaperTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private Task? _wallpaperTask;
    public Task? Initialization { get; private set; }
    public bool IsClosing => _lifetime.IsCancellationRequested;
    public Task StartAsync() => Initialization = InitializeAsync();
    public LauncherServices()
    {
        Settings = new(Paths); Settings.Load();
        Secrets = new(Path.Combine(Paths.UserDirectory, "secrets"));
        Wallpapers = new(Paths, Secrets);
        AccountAdapter = new(Paths);
        var versions = new VersionAdapter(); var installer = new InstallationAdapter();
        Accounts = new(AccountAdapter, Settings, Developer);
        Instances = new(versions, Settings, Application.Current.Dispatcher);
        Versions = new(Instances, Settings);
        Home = new(Instances, Settings, Sessions, Accounts, installer, Operations);
        Downloads = new(versions, installer, Settings, Operations, Instances);
        Java = new(new(), Settings, Operations);
        Mods = new(new ResourceAdapter(), Versions);
        Modpacks = new(new ModpackAdapter());
        Preferences = new(Settings, () => !Operations.Busy && !Sessions.Running && !Sessions.Preparing, async ct => { Themes.Apply(Settings.Current.Appearance); await Instances.ChangeRootAsync(Settings.Current.GameRoot, ct); });
        Appearance = new(Themes, Wallpapers, Settings, Secrets, Paths);
        Wallpapers.Changed += WallpaperChanged;
        _wallpaperTimer.Tick += WallpaperTick;
    }
    public async Task InitializeAsync()
    {
        if (!string.IsNullOrWhiteSpace(Settings.Current.LegacyOfflineName))
        {
            try
            {
                await AccountAdapter.AddOfflineAsync(Settings.Current.LegacyOfflineName, _lifetime.Token);
                var account = (await AccountAdapter.ListAsync(_lifetime.Token)).FirstOrDefault(a => a.Name == Settings.Current.LegacyOfflineName && a.Kind == "Offline");
                Settings.Current.AccountId = account?.Id; Settings.Current.LegacyOfflineName = ""; Settings.Save();
            }
            catch (Exception ex) { Home.Status = Logging.FailureText.For(ex); }
        }
        try { await Home.ReloadAsync(_lifetime.Token); }
        catch (Exception ex) { Home.Status = Logging.FailureText.For(ex); }
        if (!string.IsNullOrEmpty(Settings.MigrationNotice)) Home.Status = Settings.MigrationNotice;
        _wallpaperTask = Wallpapers.RefreshAsync(Settings.Current.Wallpaper, Settings.Current.Wallpaper.Frequency == "Startup", _lifetime.Token);
        try { await _wallpaperTask; } catch (OperationCanceledException) { }
        _wallpaperTimer.Start();
    }
    private void WallpaperChanged()
    {
        if (Application.Current.Dispatcher.HasShutdownStarted) return;
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            Application.Current.Resources["WallpaperImage"] = Wallpapers.Current;
            if (Wallpapers.Current != null) Themes.ExtractAccent(Wallpapers.Current);
        }));
    }
    private async void WallpaperTick(object? sender, EventArgs e)
    {
        if (_wallpaperTask is { IsCompleted: false }) return;
        if (Settings.Current.Wallpaper.Frequency is "Manual" or "Startup") return;
        try { _wallpaperTask = Wallpapers.RefreshAsync(Settings.Current.Wallpaper, false, _lifetime.Token); await _wallpaperTask; }
        catch (OperationCanceledException) { }
    }
    public async Task ShutdownAsync()
    {
        _lifetime.Cancel(); _wallpaperTimer.Stop(); Operations.Cancel();
        if (Initialization != null) try { await Initialization; } catch (OperationCanceledException) { }
        var pages = new Common.PageViewModel[] { Home, Accounts, Versions, Downloads, Java, Mods, Modpacks, Preferences, Appearance };
        foreach (var vm in pages) vm.Dispose();
        await Task.WhenAll(pages.Select(page => page.WhenIdleAsync()));
        await Sessions.CloseAsync(Settings.Current.KeepGamesOnExit);
        if (_wallpaperTask != null) try { await _wallpaperTask; } catch (OperationCanceledException) { }
    }
    public void Dispose()
    {
        _wallpaperTimer.Stop(); _wallpaperTimer.Tick -= WallpaperTick;
        Wallpapers.Changed -= WallpaperChanged; Wallpapers.Dispose(); Themes.Dispose();
        Operations.Dispose(); Instances.Dispose(); AccountAdapter.Dispose(); _lifetime.Dispose();
    }
}

