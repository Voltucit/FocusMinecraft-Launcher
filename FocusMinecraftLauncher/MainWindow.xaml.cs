using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using FocusMinecraftLauncher.Navigation;
using FocusMinecraftLauncher.Presentation;
using FocusMinecraftLauncher.Presentation.Controls;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace FocusMinecraftLauncher;

/// <summary>主窗口协调已有导航、共享应用状态及侧栏动效，不执行业务服务。</summary>
public partial class MainWindow : FluentWindow
{
    private readonly NavigationViewModel _navigation = new();
    private readonly Dictionary<MainPage, UserControl> _pages = [];
    private bool _restoringAccountsStarted;
    private bool _isExiting,_stoppingBackend,_backendStopped;
    private bool _exitAnimationFinished;
    private CaptionButtonFeedback? _captionFeedback;
    private bool _compactNavigation;
    private bool _expandedBeforeCompact;

    public MainWindow()
    {
        InitializeComponent();
        AppTitleBar.WndProcInvoked += FixedWindowHitTest;
        UiTheme.Apply(_navigation.Session.Theme, _navigation.Session.GlassTransparency, _navigation.Session.HasCustomBackgroundImage);
        _navigation.PropertyChanged += NavigationChanged;
        _navigation.Session.NavigateRequested += NavigateTo;
        _navigation.Session.PropertyChanged += SessionChanged;
        SystemParameters.StaticPropertyChanged += SystemPreferenceChanged;
        DataContext = _navigation;
        PresentPage();
    }
    /// <summary>关闭控件库自定义边框的缩放命中区域，保留标题栏拖动和最小化。</summary>
    protected override void SetWindowChrome()
    {
        base.SetWindowChrome();
        if (WindowChrome.GetWindowChrome(this) is not { } chrome) return;
        var fixedChrome = (WindowChrome)chrome.CloneCurrentValue();
        fixedChrome.ResizeBorderThickness = new Thickness(0);
        WindowChrome.SetWindowChrome(this, fixedChrome);
    }
    /// <summary>覆盖 TitleBar 保留的最小缩放边缘，按钮和图标的原生处理继续由控件库负责。</summary>
    private void FixedWindowHitTest(object? sender, Wpf.Ui.Controls.HwndProcEventArgs e)
    {
        const int wmNcHitTest = 0x0084, htClient = 1, htCaption = 2;
        if (e.Message != wmNcHitTest) return;

        // lParam 为带符号的屏幕像素坐标，PointFromScreen 同时处理负坐标和当前 DPI。
        var coordinates = e.LParam.ToInt64();
        var screenPoint = new Point((short)(coordinates & 0xffff), (short)((coordinates >> 16) & 0xffff));
        if (AppTitleBar.CloseWindowByDoubleClickOnIcon
            && AppTitleBar.Template.FindName("PART_Icon", AppTitleBar) is UIElement icon
            && new Rect(icon.RenderSize).Contains(icon.PointFromScreen(screenPoint))) return;

        // 标题栏按钮在此事件前已由 WPF UI 命中；这里只保留空白标题栏拖动，其余区域交给 WPF。
        var titlePoint = AppTitleBar.PointFromScreen(screenPoint);
        var overTitleBar = new Rect(AppTitleBar.RenderSize).Contains(titlePoint);
        e.ReturnValue = (nint)(overTitleBar && !e.IsMouseOverDetectedHeaderContent ? htCaption : htClient);
        e.Handled = true;
    }
    private void ToggleSidebar(object sender, RoutedEventArgs e) => _navigation.IsExpanded = !_navigation.IsExpanded;

    /// <summary>在控件库完成原生标题栏初始化后，补充按下反馈，不替代其窗口消息处理。</summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        UiTheme.ApplyWindowMaterial(this, _navigation.Session.Theme, _navigation.Session.HasCustomBackgroundImage);
        if (!_restoringAccountsStarted)
        {
            _restoringAccountsStarted = true;
            _ = InitializeBackendAsync();
        }
        if (_captionFeedback == null && PresentationSource.FromVisual(this) is HwndSource source)
            _captionFeedback = new CaptionButtonFeedback(source, AppTitleBar);
    }

    private async Task InitializeBackendAsync()
    {
        try
        {
            await _navigation.Session.InitializeRuntimeAsync();
            await _navigation.Session.RestoreMicrosoftAccountsAsync();
            if(((App)Application.Current).RequestedLaunchId is {Length:>0} id)
            {
                var root=((App)Application.Current).RequestedLaunchRoot;
                if(root.Length>0)await _navigation.Session.SelectRootAsync(root);
                _navigation.Session.CurrentInstance=_navigation.Session.Instances.FirstOrDefault(i=>i.Id==id);
                if(_navigation.Session.CanLaunch)_navigation.Session.Launch.Execute(null);
                else _navigation.Session.NotifyTask("无法启动指定版本，请检查账号与实例。",true);
            }
        }
        catch(Exception error){_navigation.Session.NotifyTask(Infrastructure.Runtime.RuntimeSupport.Error(error),true);}
    }
    private static bool IsPack(string path)=>System.IO.File.Exists(path)&&(path.EndsWith(".mrpack",StringComparison.OrdinalIgnoreCase)||path.EndsWith(".zip",StringComparison.OrdinalIgnoreCase));
    private void WindowDragOver(object sender,DragEventArgs e)
    {
        if(!e.Data.GetDataPresent(DataFormats.FileDrop))return;
        var files=e.Data.GetData(DataFormats.FileDrop) as string[];
        e.Effects=files is {Length:1}&&IsPack(files[0])&&!_navigation.Session.IsBusy?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;
    }
    private async void WindowDrop(object sender,DragEventArgs e)
    {
        if(!e.Data.GetDataPresent(DataFormats.FileDrop))return;
        e.Handled=true;
        if(e.Data.GetData(DataFormats.FileDrop) is string[] {Length:1} files&&IsPack(files[0])&&!_navigation.Session.IsBusy)await _navigation.Session.ImportModpackAsync(files[0]);
        else _navigation.Session.NotifyTask("请一次拖入一个整合包，并等待当前任务结束。",true);
    }
    /// <summary>贴靠形成窄窗口时收起导航，恢复宽度后还原此前选择，为内容保留空间。</summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!IsInitialized || Sidebar == null) return;
        var compact = sizeInfo.NewSize.Width < 1000;
        if (compact == _compactNavigation) return;
        _compactNavigation = compact;
        if (compact)
        {
            _expandedBeforeCompact = _navigation.IsExpanded;
            _navigation.IsExpanded = false;
        }
        else _navigation.IsExpanded = _expandedBeforeCompact;
        MenuToggle.IsEnabled = !compact;
    }
    private void NavigateTo(MainPage page) => _navigation.SelectedItem = _navigation.Items.First(i => i.Page == page);
    private void OpenAccountAdder(bool microsoft)
    {
        NavigateTo(MainPage.Accounts);
        if (_pages[MainPage.Accounts].DataContext is Features.Accounts.ViewModels.AccountsViewModel accounts)
            accounts.BeginAdd(microsoft);
    }
    private void OpenOfflineAccountRename()
    {
        NavigateTo(MainPage.Accounts);
        if(_pages[MainPage.Accounts].DataContext is Features.Accounts.ViewModels.AccountsViewModel accounts)
            accounts.BeginRename(_navigation.Session.CurrentAccount);
    }
    private void NavigationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NavigationViewModel.IsExpanded)) AnimateSidebar();
        else if (e.PropertyName == nameof(NavigationViewModel.SelectedItem)) PresentPage();
    }
    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Preview.PreviewSession.AnimationsEnabled) && !_navigation.Session.AnimationsEnabled)
        {
            Sidebar.BeginAnimation(WidthProperty, null); Sidebar.Width = _navigation.IsExpanded ? 236 : 72;
            if (_isExiting) FinishAnimatedClose();
        }
    }
    private void SystemPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation))) return;
        Dispatcher.InvokeAsync(() =>
        {
            UiTheme.Apply(_navigation.Session.Theme, _navigation.Session.GlassTransparency, _navigation.Session.HasCustomBackgroundImage); _navigation.Session.Refresh();
            if (!SystemParameters.ClientAreaAnimation)
            {
                Sidebar.BeginAnimation(WidthProperty, null); Sidebar.Width = _navigation.IsExpanded ? 236 : 72;
                if (_isExiting) FinishAnimatedClose();
            }
        });
    }
    /// <summary>从当前宽度衔接下一次侧栏操作，关闭动画后立即呈现完整布局。</summary>
    private void AnimateSidebar()
    {
        var from = Sidebar.ActualWidth;
        var target = _navigation.IsExpanded ? 236d : 72d;
        Sidebar.BeginAnimation(WidthProperty, null); Sidebar.Width = target;
        if (!SystemParameters.ClientAreaAnimation || !_navigation.Session.AnimationsEnabled || _navigation.Session.IsFlatTheme) return;
        Sidebar.BeginAnimation(WidthProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(_navigation.IsExpanded ? 250 : 200))
        {
            EasingFunction = new CubicEase { EasingMode = _navigation.IsExpanded ? EasingMode.EaseOut : EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop
        });
    }
    /// <summary>缓存页面及会话；宿主直接绑定主页的表面状态，导航离开后恢复其他页面的面板。</summary>
    private void PresentPage()
    {
        if (_navigation.SelectedItem is not { } selected) return;
        if (!_pages.TryGetValue(selected.Page, out var page))
        {
            var session = _navigation.Session;
            page = selected.Page switch
            {
                MainPage.Home => new Features.Home.Views.HomePage { DataContext = new Features.Home.ViewModels.HomeViewModel(session, OpenAccountAdder, OpenOfflineAccountRename) },
                MainPage.Accounts => new Features.Accounts.Views.AccountsPage { DataContext = new Features.Accounts.ViewModels.AccountsViewModel(session) },
                MainPage.Downloads => new Features.Downloads.Views.DownloadsPage { DataContext = new Features.Downloads.ViewModels.DownloadsViewModel(session) },
                MainPage.Settings => new Features.Settings.Views.SettingsPage { DataContext = new Features.Settings.ViewModels.SettingsViewModel(session) },
                MainPage.More => new Features.More.Views.MorePage { DataContext = new Features.More.ViewModels.MoreViewModel(session) },
                _ => throw new ArgumentOutOfRangeException(nameof(selected.Page))
            };
            _pages.Add(selected.Page, page);
        }
        PageContent.Content = page;
        // 直接使用当前页面的 ViewModel 作为来源，不从内容模板内部回查附加属性。
        // 重新绑定会释放上一个来源，页面切换不增加事件订阅。
        if (page.DataContext is Features.Home.ViewModels.HomeViewModel home)
            InformationPanel.SetBinding(ThemeVisuals.PageSurfaceVisibleProperty, new Binding(nameof(home.ShowPageSurface))
            { Source = home, Mode = BindingMode.OneWay });
        else
            ThemeVisuals.SetPageSurfaceVisible(InformationPanel, true);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!e.Handled && e.Key == Key.Escape && _navigation.IsExpanded)
        {
            _navigation.IsExpanded = false;
            if (Sidebar.IsKeyboardFocusWithin) MenuToggle.Focus();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
    /// <summary>关闭按钮、Alt+F4 及 Close 共用退出过渡；关闭动画时直接退出。</summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
        {
            if (_isExiting) RestoreCancelledExit();
            return;
        }
        if(!_backendStopped && _navigation.Session.HasPendingWrites)
        {
            e.Cancel=true;if(_stoppingBackend)return;_stoppingBackend=true;
            try {await _navigation.Session.StopPendingWritesAsync();}
            finally {_backendStopped=true;_stoppingBackend=false;Close();}
            return;
        }
        if (_exitAnimationFinished || !IsVisible || WindowState == WindowState.Minimized || !_navigation.Session.MotionAllowed)
            return;

        e.Cancel = true;
        if (_isExiting) return;
        _isExiting = true;
        WindowSurface.IsHitTestVisible = false;

        var duration = TimeSpan.FromMilliseconds(200);
        var scale = new DoubleAnimation(1, 0.94, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        ExitScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        ExitScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale.Clone());

        var fade = new DoubleAnimation(Opacity, 0, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) => FinishAnimatedClose();
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>过渡完成后只执行一次真正关闭，由原有 OnClosed 释放会话和订阅。</summary>
    private void FinishAnimatedClose()
    {
        if (!_isExiting || _exitAnimationFinished) return;
        _exitAnimationFinished = true;
        Close();
    }

    /// <summary>其他关闭处理取消退出时，恢复界面，避免窗口保持透明或不可操作。</summary>
    private void RestoreCancelledExit()
    {
        ClearExitAnimations();
        WindowSurface.IsHitTestVisible = true;
        _isExiting = _exitAnimationFinished = false;
    }

    private void ClearExitAnimations()
    {
        BeginAnimation(OpacityProperty, null);
        ExitScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ExitScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    /// <summary>最小化时通过继承属性暂停主题交互动效。</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        ThemeVisuals.SetWindowActive(this, WindowState != WindowState.Minimized);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_isExiting) e.Handled = true;
        base.OnPreviewKeyDown(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        AppTitleBar.WndProcInvoked -= FixedWindowHitTest;
        _captionFeedback?.Dispose();
        SystemParameters.StaticPropertyChanged -= SystemPreferenceChanged;
        _navigation.PropertyChanged -= NavigationChanged;
        _navigation.Session.NavigateRequested -= NavigateTo;
        _navigation.Session.PropertyChanged -= SessionChanged;
        foreach(var disposable in _pages.Values.Select(page=>page.DataContext).OfType<IDisposable>())disposable.Dispose();
        _navigation.Session.Dispose();
        ClearExitAnimations();
        Sidebar.BeginAnimation(WidthProperty, null);
        base.OnClosed(e);
    }
}
