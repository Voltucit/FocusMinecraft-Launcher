using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace FocusMinecraftLauncher.Presentation.Controls;

/// <summary>主题下拉框：保留原生选择和关闭行为，补充裁剪展开、回收与箭头过渡。</summary>
public sealed class AnimatedComboBox : ComboBox
{
    private Popup? _popup;
    private PopupRevealDecorator? _reveal;
    private RotateTransform? _arrow;
    // 与 WPF 原生 Popup 淡出关闭的 150ms 生命周期保持一致。
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(150);
    private bool MotionAllowed => ThemeVisuals.GetMotionAllowed(this) && SystemParameters.ClientAreaAnimation;
    public AnimatedComboBox()
    {
        SetResourceReference(StyleProperty, typeof(AnimatedComboBox));
        Unloaded += (_, _) => Snap();
    }

    public override void OnApplyTemplate()
    {
        if (_popup != null) { _popup.Opened -= PopupOpened; _popup.Closed -= PopupClosed; }
        Snap();
        base.OnApplyTemplate();
        _popup = GetTemplateChild("PART_Popup") as Popup;
        _reveal = GetTemplateChild("PART_Reveal") as PopupRevealDecorator;
        if (GetTemplateChild("PART_Arrow") is Path arrow)
        { _arrow = new RotateTransform(); arrow.RenderTransform = _arrow; }
        if (_popup != null) { _popup.Opened += PopupOpened; _popup.Closed += PopupClosed; }
        UpdateAnimation(); Snap();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        // 在 IsOpen 的模板绑定关闭 Popup 之前开始回收，使用原生淡出的存续时间完成动画。
        if (e.Property == IsDropDownOpenProperty && e.NewValue is false) Animate(false);
        base.OnPropertyChanged(e);
        if (e.Property == ThemeVisuals.MotionAllowedProperty || e.Property == ThemeVisuals.IsFlatProperty)
        { UpdateAnimation(); if (!MotionAllowed) Snap(); }
    }

    private void PopupOpened(object? sender, EventArgs e) { if (IsDropDownOpen) Animate(true); }
    private void PopupClosed(object? sender, EventArgs e) { if (!IsDropDownOpen) Snap(); }
    private void UpdateAnimation() => _popup?.SetCurrentValue(Popup.PopupAnimationProperty,
        MotionAllowed ? PopupAnimation.Fade : PopupAnimation.None);

    private void Snap()
    {
        if (_reveal != null)
        { _reveal.BeginAnimation(PopupRevealDecorator.ProgressProperty, null); _reveal.Progress = IsDropDownOpen ? 1 : 0; }
        if (_arrow != null)
        { _arrow.BeginAnimation(RotateTransform.AngleProperty, null); _arrow.Angle = IsDropDownOpen ? 180 : 0; }
    }

    private void Animate(bool opening)
    {
        if (_reveal == null) return;
        if (!MotionAllowed || !IsLoaded) { Snap(); return; }
        var target = opening ? 1d : 0d;
        var progress = _reveal.Progress;
        _reveal.BeginAnimation(PopupRevealDecorator.ProgressProperty, null);
        _reveal.Progress = target;
        _reveal.BeginAnimation(PopupRevealDecorator.ProgressProperty, Ease(progress, target, opening), HandoffBehavior.SnapshotAndReplace);
        if (_arrow != null)
        {
            var angle = _arrow.Angle;
            _arrow.BeginAnimation(RotateTransform.AngleProperty, null); _arrow.Angle = target * 180;
            _arrow.BeginAnimation(RotateTransform.AngleProperty, Ease(angle, target * 180, opening), HandoffBehavior.SnapshotAndReplace);
        }
    }
    private static DoubleAnimation Ease(double from, double to, bool opening) => new(from, to, Duration)
    { EasingFunction = new CubicEase { EasingMode = opening ? EasingMode.EaseOut : EasingMode.EaseIn }, FillBehavior = FillBehavior.Stop };
}

/// <summary>只改变可见裁剪区域，不缩放菜单文字，也不反复改变原生弹出窗口尺寸。</summary>
public sealed class PopupRevealDecorator : Decorator
{
    private readonly RectangleGeometry _clip = new();
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(PopupRevealDecorator),
        new FrameworkPropertyMetadata(0d, (target, _) => ((PopupRevealDecorator)target).UpdateClip()));
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public PopupRevealDecorator() => Clip = _clip;
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo) { base.OnRenderSizeChanged(sizeInfo); UpdateClip(); }
    private void UpdateClip() => _clip.Rect = new Rect(0, 0, ActualWidth, ActualHeight * Math.Clamp(Progress, 0, 1));
}
