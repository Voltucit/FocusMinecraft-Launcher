using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace FocusMinecraftLauncher.Presentation.Controls;

/// <summary>原生 Expander 的局部展开过渡；关闭动效时立即呈现最终状态。</summary>
public sealed class AnimatedExpander : Expander
{
    private RevealDecorator? _body;
    private ToggleButton? _header;
    private RotateTransform? _arrow;
    public AnimatedExpander()
    {
        SetResourceReference(StyleProperty, typeof(AnimatedExpander));
        Unloaded += (_, _) => Snap();
    }
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _body = GetTemplateChild("PART_Reveal") as RevealDecorator;
        _header = GetTemplateChild("PART_Header") as ToggleButton;
        if (GetTemplateChild("PART_Arrow") is Path path)
        { _arrow = new RotateTransform(); path.RenderTransform = _arrow; }
        Snap();
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsExpandedProperty) Transition();
        if ((e.Property == ThemeVisuals.MotionAllowedProperty && e.NewValue is false) || e.Property == ThemeVisuals.IsFlatProperty) Snap();
    }
    private void Snap()
    {
        if (_body == null) return;
        _body.BeginAnimation(RevealDecorator.ProgressProperty, null);
        _body.Progress = IsExpanded ? 1 : 0;
        _body.Visibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        SetInputState();
        if (_arrow != null) { _arrow.BeginAnimation(RotateTransform.AngleProperty, null); _arrow.Angle = IsExpanded ? 180 : 0; }
    }
    private void SetInputState()
    {
        if (_body == null) return;
        if (!IsExpanded && _body.IsKeyboardFocusWithin) _header?.Focus();
        _body.IsHitTestVisible = IsExpanded;
        KeyboardNavigation.SetTabNavigation(_body, IsExpanded ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
    }
    private void Transition()
    {
        if (_body == null) return;
        if (!IsLoaded || ThemeVisuals.GetIsFlat(this) || !ThemeVisuals.GetMotionAllowed(this) || !SystemParameters.ClientAreaAnimation) { Snap(); return; }
        var from = _body.Progress;
        var target = IsExpanded ? 1d : 0d;
        _body.BeginAnimation(RevealDecorator.ProgressProperty, null);
        _body.Visibility = Visibility.Visible;
        _body.Progress = target;
        SetInputState();
        var animation = Ease(from, target);
        animation.Completed += (_, _) =>
        { if (_body != null && !IsExpanded && _body.Progress < 0.001) _body.Visibility = Visibility.Collapsed; };
        _body.BeginAnimation(RevealDecorator.ProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
        if (_arrow != null)
        {
            var angle = _arrow.Angle;
            _arrow.BeginAnimation(RotateTransform.AngleProperty, null); _arrow.Angle = target * 180;
            _arrow.BeginAnimation(RotateTransform.AngleProperty, Ease(angle, target * 180), HandoffBehavior.SnapshotAndReplace);
        }
    }
    private DoubleAnimation Ease(double from, double to) => new(from, to, TimeSpan.FromMilliseconds(IsExpanded ? 250 : 200))
    { EasingFunction = new CubicEase { EasingMode = IsExpanded ? EasingMode.EaseOut : EasingMode.EaseIn }, FillBehavior = FillBehavior.Stop };
}

/// <summary>保持内容自身的排版尺寸，通过可见比例收纳空间，避免缩放文字与控件。</summary>
public sealed class RevealDecorator : Decorator
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(RevealDecorator),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure, (target, e) => ((RevealDecorator)target).Opacity = (double)e.NewValue));
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public RevealDecorator() { ClipToBounds = true; Opacity = 0; }
    protected override Size MeasureOverride(Size constraint)
    {
        if (Child == null) return new Size();
        Child.Measure(new Size(constraint.Width, double.PositiveInfinity));
        return new Size(Child.DesiredSize.Width, Child.DesiredSize.Height * Math.Clamp(Progress, 0, 1));
    }
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child != null) Child.Arrange(new Rect(0, 0, arrangeSize.Width, Child.DesiredSize.Height));
        return arrangeSize;
    }
}
