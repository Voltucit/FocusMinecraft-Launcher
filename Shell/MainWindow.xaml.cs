using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FMLauncher.Appearance.Animations;
using FMLauncher.Infrastructure;
using FMLauncher.Infrastructure.Logging;
using FMLauncher.Navigation;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
namespace FMLauncher.Shell;
/// <summary>仅负责窗口布局、缓存页面、转场与关闭协调，业务对象继续由原有组合根管理。</summary>
public partial class MainWindow : FluentWindow
{
    private readonly LauncherServices _services;
    private readonly NavigationService _navigation;
    private readonly Dictionary<string, UserControl> _pages = new();
    private CancellationTokenSource? _transition;
    private IInputElement? _accountReturnFocus;
    private bool _ready, _closed, _closing, _preferencesOpened;
    public MainWindow(LauncherServices services)
    {
        _services = services; _navigation = new(services);
        InitializeComponent(); DataContext = _navigation;
        _navigation.Navigated += PresentPage; services.Themes.Applied += ApplyLayout;
        _ready = true; ApplyLayout(); FitWorkArea(); _navigation.Open("Home", false);
    }
    private void SectionChanged(object sender, RoutedEventArgs e)
    { if (_ready && sender is RadioButton { Tag: string section }) _navigation.SwitchSection(section); }
    private void OpenPage(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is string key)
        {
            if (key == "Accounts")
            {
                _accountReturnFocus = Keyboard.FocusedElement;
                AccountContent.Content ??= new Features.Accounts.Views.AccountsPage(_services.Accounts);
                AccountOverlay.Visibility = Visibility.Visible;
                PageAnimation.Show(AccountPanel, _services.Themes.AnimationsEnabled, false);
                AccountPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
            else { HideAccounts(); _navigation.Open(key); }
        }
        CommandManager.InvalidateRequerySuggested(); e.Handled = true;
    }
    private void HideAccounts()
    {
        if (AccountOverlay.Visibility != Visibility.Visible) return;
        AccountOverlay.Visibility = Visibility.Collapsed;
        if (_accountReturnFocus != null) Keyboard.Focus(_accountReturnFocus);
        _accountReturnFocus = null; CommandManager.InvalidateRequerySuggested();
    }
    private void CloseAccounts(object sender, ExecutedRoutedEventArgs e) { HideAccounts(); e.Handled = true; }
    private void DismissAccounts(object sender, MouseButtonEventArgs e) { HideAccounts(); e.Handled = true; }
    private void OpenNews(object sender, ExecutedRoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://www.minecraft.net/zh-hans/articles") { UseShellExecute = true }); }
        catch (Exception ex) { DiagnosticLog.Record("OpenOfficialNews", ex); System.Windows.MessageBox.Show(FailureText.For(ex), "无法打开浏览器"); }
        e.Handled = true;
    }
    private void Back(object sender, ExecutedRoutedEventArgs e) { if (AccountOverlay.Visibility == Visibility.Visible) HideAccounts(); else _navigation.Back(); e.Handled = true; }
    private void CanBack(object sender, CanExecuteRoutedEventArgs e) { e.CanExecute = AccountOverlay?.Visibility == Visibility.Visible || _navigation?.CanGoBack == true; e.Handled = true; }
    private void ReloadSettings(object sender, ExecutedRoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show("放弃当前未保存的设置草稿，并重新读取已保存配置？", "重新读取设置", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _services.Preferences.RefreshDraft();
        e.Handled = true;
    }
    private async void PresentPage(NavigationEntry entry, bool back)
    {
        _transition?.Cancel();
        var transition = new CancellationTokenSource(); _transition = transition;
        try
        {
            if (PageContent.Content != null) await PageAnimation.HideAsync(PageContent, _services.Themes.AnimationsEnabled, transition.Token);
            transition.Token.ThrowIfCancellationRequested();
            if (!_pages.TryGetValue(entry.Key, out var page)) { page = CreatePage(entry.Key); _pages.Add(entry.Key, page); }
            PageContent.Content = page;
            PageAnimation.Show(PageContent, _services.Themes.AnimationsEnabled, back);
            CommandManager.InvalidateRequerySuggested();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Windows.MessageBox.Show(FailureText.For(ex), "页面无法打开"); }
        finally { if (ReferenceEquals(_transition, transition)) _transition = null; transition.Dispose(); }
    }
    private UserControl CreatePage(string page)
    {
        if ((page is "Settings" or "GameSettings") && !_preferencesOpened)
        { _services.Preferences.RefreshDraft(); _preferencesOpened = true; }
        return page switch
        {
            "Home" => new Features.Home.Views.HomePage(_services.Home),
            "Versions" => new Features.Versions.Views.VersionsPage(_services.Versions),
            "Downloads" => new Features.Downloads.Views.DownloadsPage(_services.Downloads),
            "Tasks" => new Features.Downloads.Views.TasksPage(_services.Downloads),
            "Diagnostics" => new Features.Home.Views.DiagnosticsPage(_services.Home),
            "GameSettings" => new Features.Settings.Views.GameSettingsPage(_services.Preferences),
            "Java" => new Features.Java.Views.JavaPage(_services.Java),
            "Mods" => new Features.Mods.Views.ModsPage(_services.Mods),
            "Modpacks" => new Features.Modpacks.Views.ModpacksPage(_services.Modpacks),
            "Appearance" => new Appearance.Views.AppearancePage(_services.Appearance),
            "Settings" => new Features.Settings.Views.SettingsPage(_services.Preferences),
            _ => new Features.About.Views.AboutPage()
        };
    }
    private void ApplyLayout() => SidebarColumn.Width = new(_services.Themes.Layout == "Compact" ? 168 : 184);
    /// <summary>用 WPF 逻辑单位适配工作区，较小屏幕通过页面内部滚动保留操作。</summary>
    private void FitWorkArea()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1280, Math.Max(1, area.Width - 16));
        Height = Math.Min(768, Math.Max(1, area.Height - 16));
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        if (_closing) { e.Cancel = true; return; }
        _closing = true; _transition?.Cancel(); e.Cancel = true; IsEnabled = false;
        try { await _services.ShutdownAsync(); }
        catch (Exception ex) { System.Windows.MessageBox.Show(FailureText.For(ex), "关闭时的操作状态"); }
        finally
        {
            _closed = true; _navigation.Navigated -= PresentPage; _services.Themes.Applied -= ApplyLayout;
            Close(); Application.Current.Shutdown();
        }
    }
}
