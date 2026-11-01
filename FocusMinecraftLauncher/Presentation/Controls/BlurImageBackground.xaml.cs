using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace FocusMinecraftLauncher.Presentation.Controls;

/// <summary>零模糊直接绘制；动态模式在两份固定模糊缓存之间过渡，不逐帧计算高斯模糊。</summary>
public partial class BlurImageBackground : UserControl
{
    public static readonly DependencyProperty BlurRadiusProperty = DependencyProperty.Register(
        nameof(BlurRadius), typeof(double), typeof(BlurImageBackground), new PropertyMetadata(0d, PreferenceChanged));
    public static readonly DependencyProperty IsDynamicBlurEnabledProperty = DependencyProperty.Register(
        nameof(IsDynamicBlurEnabled), typeof(bool), typeof(BlurImageBackground), new PropertyMetadata(false, PreferenceChanged));
    public static readonly DependencyProperty MotionAllowedProperty = DependencyProperty.Register(
        nameof(MotionAllowed), typeof(bool), typeof(BlurImageBackground), new PropertyMetadata(true, PreferenceChanged));

    public double BlurRadius { get => (double)GetValue(BlurRadiusProperty); set => SetValue(BlurRadiusProperty, value); }
    public bool IsDynamicBlurEnabled { get => (bool)GetValue(IsDynamicBlurEnabledProperty); set => SetValue(IsDynamicBlurEnabledProperty, value); }
    public bool MotionAllowed { get => (bool)GetValue(MotionAllowedProperty); set => SetValue(MotionAllowedProperty, value); }

    private readonly BlurEffect _blur = new() { KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
    private readonly BlurEffect _lowBlur = new() { KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
    private readonly BlurEffect _highBlur = new() { KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
    private readonly BitmapCache _lowCache = new() { EnableClearType = false };
    private readonly BitmapCache _highCache = new() { EnableClearType = false };
    private Window? _owner;

    public BlurImageBackground()
    {
        InitializeComponent();
        Loaded += BackgroundLoaded;
        Unloaded += BackgroundUnloaded;
        IsVisibleChanged += (_, _) => UpdateBlur();
        SizeChanged += (_, _) => UpdateBlur();
    }

    private static void PreferenceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        => ((BlurImageBackground)sender).UpdateBlur();

    private void BackgroundLoaded(object sender, RoutedEventArgs e)
    {
        _owner = Window.GetWindow(this);
        if (_owner != null) _owner.StateChanged += OwnerStateChanged;
        UpdateBlur();
    }

    /// <summary>卸载时移除动画时钟与窗口订阅，避免后台仍更新不可见图片。</summary>
    private void BackgroundUnloaded(object sender, RoutedEventArgs e)
    {
        if (_owner != null) _owner.StateChanged -= OwnerStateChanged;
        _owner = null;
        StopDynamic();
        Picture.Effect = null;
    }

    private void OwnerStateChanged(object? sender, EventArgs e) => UpdateBlur();

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    { base.OnDpiChanged(oldDpi, newDpi); UpdateBlur(); }

    /// <summary>基准为零时关闭全部效果与缓存；只有动态模式启用两份固定模糊缓存。</summary>
    private void UpdateBlur()
    {
        if (Picture == null) return;
        StopDynamic();
        var radius = Math.Clamp(double.IsFinite(BlurRadius) ? BlurRadius : 0, 0, 50);
        _blur.Radius = radius;
        if (!IsLoaded || !IsVisible || radius == 0)
        {
            Picture.Effect = null;
            return;
        }
        Picture.Effect = _blur;
        if (!IsDynamicBlurEnabled || !MotionAllowed || !SystemParameters.ClientAreaAnimation
            || SystemParameters.HighContrast || _owner?.WindowState == WindowState.Minimized) return;

        // 以滑块为基准小幅变化；两端缩小振幅，0 与 100 不越界、不伪装为非零模糊。
        var amplitude = Math.Min(3, Math.Min(radius, 50 - radius));
        if (amplitude <= 0) return;

        // 仅保留两份背景缓存，并限制缓存尺寸；字体、导航与其他控件不进入缓存。
        var dpi = VisualTreeHelper.GetDpi(this);
        var longestEdge = Math.Max((ActualWidth + 120) * dpi.DpiScaleX, (ActualHeight + 120) * dpi.DpiScaleY);
        var cacheScale = Math.Min(1, 1024d / longestEdge);
        _lowCache.RenderAtScale = _highCache.RenderAtScale = cacheScale;
        _lowBlur.Radius = radius - amplitude;
        _highBlur.Radius = radius + amplitude;
        LowPicture.Effect = _lowBlur.Radius > 0 ? _lowBlur : null;
        HighPicture.Effect = _highBlur;
        LowCacheHost.CacheMode = _lowCache;
        HighCacheHost.CacheMode = _highCache;
        Picture.Effect = null;
        Picture.Visibility = Visibility.Collapsed;
        DynamicLayers.Visibility = Visibility.Visible;

        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(12), RepeatBehavior = RepeatBehavior.Forever
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0.5, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(Frame(1, 3));
        animation.KeyFrames.Add(Frame(0, 9));
        animation.KeyFrames.Add(Frame(0.5, 12));
        Timeline.SetDesiredFrameRate(animation, 30);
        HighBlend.BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>最小化、隐藏、切换到静态或卸载时停止循环并释放两个缓存宿主。</summary>
    private void StopDynamic()
    {
        HighBlend.BeginAnimation(OpacityProperty, null);
        HighBlend.Opacity = 0.5;
        DynamicLayers.Visibility = Visibility.Collapsed;
        LowCacheHost.CacheMode = HighCacheHost.CacheMode = null;
        LowPicture.Effect = HighPicture.Effect = null;
        Picture.Visibility = Visibility.Visible;
    }

    private static EasingDoubleKeyFrame Frame(double opacity, int seconds) => new(opacity, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds)))
    { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
}
