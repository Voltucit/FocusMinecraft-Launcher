using System.ComponentModel;
using System.Runtime.InteropServices;
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
    // 设置页的视图模型提前创建，启动时就能预热一次 Java 检测并让结果直接显示在设置页。
    private readonly Features.Settings.ViewModels.SettingsViewModel _settings;
    private bool _restoringAccountsStarted;
    private bool _isExiting,_stoppingBackend,_backendStopped;
    private bool _exitAnimationFinished;
    private CaptionButtonFeedback? _captionFeedback;
    private bool _compactNavigation;
    private bool _expandedBeforeCompact;

    public Preview.PreviewSession Session => _navigation.Session;

    public MainWindow()
    {
        InitializeComponent();        InitializeComponent();
        ((App)Application.Current).Updates.Attach(_navigation.Session);
        _settings = new(_navigation.Session);
        AppTitleBar.WndProcInvoked += FixedWindowHitTest;
        UiTheme.Apply(_navigation.Session.Theme, _navigation.Session.GlassTransparency, _navigation.Session.HasCustomBackgroundImage || _navigation.Session.HasWallpaper);
        _navigation.PropertyChanged += NavigationChanged;
        _navigation.Session.NavigateRequested += NavigateTo;
        _navigation.Session.PropertyChanged += SessionChanged;
        SystemParameters.StaticPropertyChanged += SystemPreferenceChanged;
        DataContext = _navigation;
        PresentPage();
    }

    private void Wallpaper_OnRenderStateChanged(object? sender, WallpaperRenderEventArgs e)
        => _navigation.Session.ReportWallpaperRenderState(e.Version, e.Error);

    /// <summary>首帧之前按窗口所在显示器的工作区定尺寸，避免 1440×900 顶出小屏幕或超宽屏留白过多。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        FitWindowToScreen(false);
    }

    /// <summary>换到不同缩放的显示器后，只在窗口超出新工作区时再调整，保留用户自己拖出的大小。</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        FitWindowToScreen(true);
    }

    /// <summary>上限 1440×900、下限取窗口最小尺寸，四周为任务栏与边框留 24 px。</summary>
    private void FitWindowToScreen(bool onlyWhenOverflowing)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;
        var information = new MonitorInformation { Size = Marshal.SizeOf<MonitorInformation>() };
        if (!GetMonitorInfo(monitor, ref information)) return;

        // 工作区是物理像素，除以当前 DPI 才是 WPF 的 DIP。
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (information.Work.Right - information.Work.Left) / dpi.DpiScaleX - 48;
        var height = (information.Work.Bottom - information.Work.Top) / dpi.DpiScaleY - 48;
        if (onlyWhenOverflowing && Width <= width && Height <= height) return;

        Width = Math.Clamp(width, MinWidth, 1440);
        Height = Math.Clamp(height, MinHeight, 900);
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInformation
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInformation information);
    /// <summary>保留 6 px 的原生缩放入口，标题栏拖动仍由下面的命中处理负责。</summary>
    protected override void SetWindowChrome()
    {
        base.SetWindowChrome();
        if (WindowChrome.GetWindowChrome(this) is not { } chrome) return;
        var fixedChrome = (WindowChrome)chrome.CloneCurrentValue();
        fixedChrome.ResizeBorderThickness = new Thickness(6);
        WindowChrome.SetWindowChrome(this, fixedChrome);
    }
    /// <summary>标题栏区域按拖动处理，其余位置交回 WindowChrome，让窗口边缘可以拖动缩放。</summary>
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

        // 标题栏按钮在此事件前已由 WPF UI 命中；这里只处理标题栏，窗口边缘留给 WindowChrome 缩放。
        var titlePoint = AppTitleBar.PointFromScreen(screenPoint);
        if (!new Rect(AppTitleBar.RenderSize).Contains(titlePoint)) return;

        e.ReturnValue = (nint)(e.IsMouseOverDetectedHeaderContent ? htClient : htCaption);
        e.Handled = true;
    }
    private void ToggleSidebar(object sender, RoutedEventArgs e) => _navigation.IsExpanded = !_navigation.IsExpanded;

    /// <summary>在控件库完成原生标题栏初始化后，补充按下反馈，不替代其窗口消息处理。</summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        UiTheme.ApplyWindowMaterial(this, _navigation.Session.Theme, _navigation.Session.HasCustomBackgroundImage || _navigation.Session.HasWallpaper);
        if (!_restoringAccountsStarted)
        {
            _restoringAccountsStarted = true;
            // 资源预热独立于账号会话恢复，避免登录服务较慢时拖延社区列表。
            _ = Features.Downloads.ViewModels.CommunityCatalogViewModel.PrefetchAsync();
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
            _ = _settings.JavaDetection.DetectOnceAsync();
            await _navigation.Session.RestoreMicrosoftAccountsAsync();
            await _navigation.Session.RestoreYggdrasilAccountsAsync();
            if(((App)Application.Current).RequestedLaunchId is {Length:>0} id)
            {
                var root=((App)Application.Current).RequestedLaunchRoot;
                if(root.Length>0)await _navigation.Session.SelectRootAsync(root);
                _navigation.Session.CurrentInstance=_navigation.Session.Instances.FirstOrDefault(i=>i.Id==id);
                if(_navigation.Session.CanLaunch)_navigation.Session.Launch.Execute(null);
                else _navigation.Session.NotifyTask("无法启动指定版本，请检查账号与游戏。",true);
            }
        }
        catch(Exception error){_navigation.Session.NotifyTask(Infrastructure.Runtime.RuntimeSupport.Error(error),true);}
    }
    private static bool IsPack(string path)=>System.IO.File.Exists(path)&&(path.EndsWith(".mrpack",StringComparison.OrdinalIgnoreCase)||path.EndsWith(".zip",StringComparison.OrdinalIgnoreCase));
    /// <summary>拖拽目标是内容管理页时让事件继续冒泡，由该页按当前类型把 jar 或 zip 装进实例。</summary>
    private static bool IsContentDrop(object? source,DragEventArgs e)=>
        e.Data.GetDataPresent(DataFormats.FileDrop)
        && e.Data.GetData(DataFormats.FileDrop) is string[] files
        && files.Length>0
        && files.All(path=>path.EndsWith(".jar",StringComparison.OrdinalIgnoreCase)||path.EndsWith(".zip",StringComparison.OrdinalIgnoreCase))
        && FindContentPage(source)!=null;
    private static Features.Home.Views.Instances.InstanceContentPage? FindContentPage(object? source)
    {
        for(var node=source as DependencyObject;node!=null;)
        {
            if(node is Features.Home.Views.Instances.InstanceContentPage page)return page;
            node=node is Visual or System.Windows.Media.Media3D.Visual3D?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
    private void WindowDragOver(object sender,DragEventArgs e)
    {
        if(!e.Data.GetDataPresent(DataFormats.FileDrop))return;
        if(IsContentDrop(e.OriginalSource,e))return;
        var files=e.Data.GetData(DataFormats.FileDrop) as string[];
        e.Effects=files is {Length:1}&&IsPack(files[0])&&!_navigation.Session.IsBusy?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;
    }
    private async void WindowDrop(object sender,DragEventArgs e)
    {
        if(!e.Data.GetDataPresent(DataFormats.FileDrop))return;
        if(IsContentDrop(e.OriginalSource,e))return;
        e.Handled=true;
        if(e.Data.GetData(DataFormats.FileDrop) is string[] {Length:1} files&&IsPack(files[0])&&!_navigation.Session.IsBusy)await _navigation.Session.ImportModpackAsync(files[0]);
        else _navigation.Session.NotifyTask("请一次拖入一个整合包，或把 Mod、资源包、光影包拖到对应的管理页，并等待当前任务结束。",true);
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
            UiTheme.Apply(_navigation.Session.Theme, _navigation.Session.GlassTransparency, _navigation.Session.HasCustomBackgroundImage || _navigation.Session.HasWallpaper); _navigation.Session.Refresh();
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
                MainPage.Multiplayer => new Features.Multiplayer.Views.MultiplayerPage { DataContext = new Features.Multiplayer.ViewModels.MultiplayerViewModel(session) },
                MainPage.Settings => new Features.Settings.Views.SettingsPage { DataContext = _settings },
                MainPage.More => new Features.More.Views.MorePage { DataContext = new Features.More.ViewModels.MoreViewModel(session) },
                _ => throw new ArgumentOutOfRangeException(nameof(selected.Page))
            };
            _pages.Add(selected.Page, page);
        }
        var previous = PageContent.Content;
        PageContent.Content = page;
        AnimatePageChange(page, previous != null && !ReferenceEquals(previous, page));
        // 直接使用当前页面的 ViewModel 作为来源，不从内容模板内部回查附加属性。
        // 重新绑定会释放上一个来源，页面切换不增加事件订阅。
        if (page.DataContext is Features.Home.ViewModels.HomeViewModel home)
            InformationPanel.SetBinding(ThemeVisuals.PageSurfaceVisibleProperty, new Binding(nameof(home.ShowPageSurface))
            { Source = home, Mode = BindingMode.OneWay });
        else
            ThemeVisuals.SetPageSurfaceVisible(InformationPanel, true);
    }
    /// <summary>换页时让新页面淡入并轻微上移收位；首次呈现和关闭动画时直接切换。</summary>
    private void AnimatePageChange(UserControl page, bool animate)
    {
        if (!animate || !_navigation.Session.MotionAllowed) return;
        if (page.RenderTransform is not TranslateTransform shift)
        {
            if (page.RenderTransform is not null && page.RenderTransform != Transform.Identity) return;
            shift = new TranslateTransform();
            page.RenderTransform = shift;
        }
        var duration = TimeSpan.FromMilliseconds(190);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        page.Opacity = 1; shift.Y = 0;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration)
        { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, duration)
        { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
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
    private bool _stoppingNetwork;
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
        {
            if (_isExiting) RestoreCancelledExit();
            return;
        }
        var network = ((App)Application.Current).Multiplayer;
        if (_stoppingNetwork) { e.Cancel = true; return; }
        if (network.HasActivity)
        {
            e.Cancel = true;
            var exitMessage = network.IsInstallingComponents ? "退出会取消联机组件的安装。"
                : !network.HasSession ? "退出会取消正在进行的联机准备。"
                : network.IsHosting ? "退出会解散当前房间并结束联机。" : "退出会断开当前联机连接。";
            if (!Presentation.Dialogs.MessageDialog.Confirm("退出启动器？", exitMessage, "退出并关闭", "取消")) return;
            _stoppingNetwork = true; network.PreventStart(true);
            try
            {
                await network.StopAsync();
                _stoppingNetwork = false;
                Close();
            }
            catch (Exception error)
            {
                _stoppingNetwork = false; network.PreventStart(false);
                Presentation.Dialogs.MessageDialog.Show("尚未退出联机", Features.Multiplayer.Services.EasyTierWorker.Describe(error));
            }
            return;
        }
        network.PreventStart(true);
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
        ((App)Application.Current).Multiplayer.PreventStart(false);
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
