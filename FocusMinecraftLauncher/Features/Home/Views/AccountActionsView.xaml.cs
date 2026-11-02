using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FocusMinecraftLauncher.Features.Home.ViewModels;

namespace FocusMinecraftLauncher.Features.Home.Views;

/// <summary>
/// 头像旁展开的三个账号动作。经典/居中布局用纵向展开（占用真实高度，把头像顶上去），
/// 留白布局用横向展开（从头像右侧滑出）。两种模式都有完整的展开与收起动画。
/// </summary>
public partial class AccountActionsView : UserControl
{
    /// <summary>展开方向。</summary>
    public enum RevealMode { Vertical, Horizontal }

    private const double FallbackHeight = 76;
    private const double StaggerDelay = 45;
    private bool _open;

    public AccountActionsView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (!IsVisible && _open) Snap(false); };
    }

    private HomeViewModel? Home => DataContext as HomeViewModel;
    private bool Motion => Home?.Session.MotionAllowed ?? true;

    /// <summary>纵向：在头像下方撑开高度；横向：在头像右侧滑出。XAML 里设置，默认为纵向。</summary>
    public RevealMode Mode { get; set; } = RevealMode.Vertical;

    /// <summary>当前是否展开；父视图切换布局或点头像时直接翻转这个值即可。</summary>
    public bool IsOpen
    {
        get => _open;
        set
        {
            if (_open == value) return;
            _open = value;
            if (Motion) Animate(value);
            else Snap(value);
        }
    }

    /// <summary>点头像调用：展开时再点一次就收起。</summary>
    public void Toggle() => IsOpen = !IsOpen;

    private void Animate(bool open)
    {
        if (Mode == RevealMode.Horizontal) AnimateHorizontal(open);
        else AnimateVertical(open);
    }

    /// <summary>纵向展开：从 0 撑到自然高度，收起时量着当前实际高度缩回去。</summary>
    private void AnimateVertical(bool open)
    {
        Host.Visibility = Visibility.Visible;
        var target = RevealTarget();
        if (open)
        {
            Host.Height = 0;
            Host.Opacity = 0;
            Shift.Y = -8;
        }
        Run(Host, HeightProperty, open ? target : 0, open ? 240 : 180, Ease(open), () =>
        {
            Host.Height = open ? target : 0;
            if (!open) { Host.Visibility = Visibility.Collapsed; Host.Opacity = 0; }
        });
        Run(Host, OpacityProperty, open ? 1 : 0, open ? 180 : 140, Ease(open), () => Host.Opacity = open ? 1 : 0);
        Run(Shift, TranslateTransform.YProperty, open ? 0 : -6, open ? 240 : 160, Ease(open));
        Stagger(open);
    }

    /// <summary>横向展开：宽度不变，靠位移与透明度从头像右侧滑出并向左收回。</summary>
    private void AnimateHorizontal(bool open)
    {
        Host.Visibility = Visibility.Visible;
        Host.Height = double.NaN;
        if (open)
        {
            Host.Opacity = 0;
            Shift.X = -16;
        }
        Run(Host, OpacityProperty, open ? 1 : 0, open ? 190 : 140, Ease(open), () =>
        {
            Host.Opacity = open ? 1 : 0;
            if (!open) Host.Visibility = Visibility.Collapsed;
        });
        Run(Shift, TranslateTransform.XProperty, open ? 0 : -12, open ? 240 : 150, Ease(open));
        Stagger(open);
    }

    private void Stagger(bool open)
    {
        var index = 0;
        foreach (var button in Row.Children.OfType<FrameworkElement>())
        {
            var shift = button.RenderTransform as TranslateTransform ?? new TranslateTransform();
            button.RenderTransform = shift;
            var begin = TimeSpan.FromMilliseconds(open ? index * StaggerDelay : 0);
            if (open) button.Opacity = 0;
            Run(button, OpacityProperty, open ? 1 : 0, open ? 170 : 100, Ease(open), () => button.Opacity = open ? 1 : 0, begin);
            Run(shift, TranslateTransform.YProperty, open ? 0 : 4, open ? 200 : 100, Ease(open), null, begin);
            index++;
        }
    }

    private void Snap(bool open)
    {
        StopAnimations();
        Host.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        Host.Height = Mode == RevealMode.Vertical ? (open ? RevealTarget() : 0) : double.NaN;
        Host.Opacity = open ? 1 : 0;
        Shift.X = Shift.Y = 0;
        foreach (var button in Row.Children.OfType<FrameworkElement>())
        {
            button.Opacity = 1;
            if (button.RenderTransform is TranslateTransform shift) shift.X = shift.Y = 0;
        }
    }

    private static IEasingFunction Ease(bool open) => new QuinticEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn };

    /// <summary>从当前值（含动画中途的值）过渡到目标值，结束后写回基值，确保下一次仍能从正确位置起步。</summary>
    private static void Run(DependencyObject host, DependencyProperty property, double to, double milliseconds, IEasingFunction easing, Action? done = null, TimeSpan? delay = null)
    {
        var from = host.GetValue(property) is double value ? value : 0;
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = easing,
            BeginTime = delay ?? TimeSpan.Zero,
            FillBehavior = FillBehavior.Stop,
        };
        if (done != null) animation.Completed += (_, _) => done();
        if (host is IAnimatable animatable) animatable.BeginAnimation(property, animation);
    }

    private void StopAnimations()
    {
        ((IAnimatable)Host).BeginAnimation(HeightProperty, null);
        ((IAnimatable)Host).BeginAnimation(OpacityProperty, null);
        Shift.BeginAnimation(TranslateTransform.XProperty, null);
        Shift.BeginAnimation(TranslateTransform.YProperty, null);
        foreach (var button in Row.Children.OfType<FrameworkElement>())
        {
            ((IAnimatable)button).BeginAnimation(OpacityProperty, null);
            if (button.RenderTransform is TranslateTransform shift)
            {
                shift.BeginAnimation(TranslateTransform.XProperty, null);
                shift.BeginAnimation(TranslateTransform.YProperty, null);
            }
        }
    }

    /// <summary>量一次自然高度，动画从 0 展开到它，避免写死数值在系统缩放变化时错位。</summary>
    private double RevealTarget()
    {
        var width = double.IsNaN(Width) ? ActualWidth : Width;
        if (width <= 0) width = 296;
        var height = Host.Height;
        Host.Height = double.NaN;
        Host.Measure(new Size(width, double.PositiveInfinity));
        var measured = Host.DesiredSize.Height;
        Host.Height = height;
        return measured > 0 ? measured : FallbackHeight;
    }

    private void OpenMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } element) return;
        menu.PlacementTarget = element;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 4;
        menu.IsOpen = true;
    }

    /// <summary>每次打开菜单都按当前账号重建披风行：旧行先删，避免重复或残留上一账号的披风。</summary>
    private void CapeMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || Home is not { } home) return;
        home.EnsureCapesLoaded();
        foreach (var stale in menu.Items.OfType<MenuItem>().Where(item => Equals(item.Tag, "cape")).ToArray())
            menu.Items.Remove(stale);
        if (menu.Items.OfType<MenuItem>().LastOrDefault(item => item.Name == nameof(RefreshCapesItem)) is not { } refresh) return;
        var index = menu.Items.IndexOf(refresh);
        var style = (Style)FindResource("CapeMenuItem");
        foreach (var choice in home.CapeOptions)
            menu.Items.Insert(index++, new MenuItem { DataContext = choice, Style = style, Tag = "cape" });
    }
}
