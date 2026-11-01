using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FocusMinecraftLauncher.Presentation.Controls;

/// <summary>沿用原生折叠框语义，使用项目统一的标题与展开动画。</summary>
public sealed class AnimatedExpander : Expander
{
    public AnimatedExpander() => SetResourceReference(StyleProperty, typeof(AnimatedExpander));
}

/// <summary>共享的折叠箭头；旋转可中断，并遵循应用及 Windows 动画偏好。</summary>
public sealed class DisclosureArrow : System.Windows.Shapes.Shape
{
    private readonly RotateTransform _rotation = new();
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(DisclosureArrow), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    protected override Geometry DefiningGeometry => Data ?? Geometry.Empty;
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(DisclosureArrow), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty ExpandedAngleProperty = DependencyProperty.Register(nameof(ExpandedAngle), typeof(double), typeof(DisclosureArrow), new PropertyMetadata(180d, Changed));
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public double ExpandedAngle { get => (double)GetValue(ExpandedAngleProperty); set => SetValue(ExpandedAngleProperty, value); }
    public DisclosureArrow()
    {
        RenderTransform = _rotation; RenderTransformOrigin = new Point(.5, .5); IsHitTestVisible = false;
        Loaded += (_, _) => Update(false); Unloaded += (_, _) => Update(false);
    }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((DisclosureArrow)sender).Update(true);
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ThemeVisuals.MotionAllowedProperty && e.NewValue is false) Update(false);
    }
    private void Update(bool animate)
    {
        var from = _rotation.Angle; var target = IsExpanded ? ExpandedAngle : 0;
        _rotation.BeginAnimation(RotateTransform.AngleProperty, null); _rotation.Angle = target;
        if (animate && IsLoaded && ThemeVisuals.GetMotionAllowed(this) && SystemParameters.ClientAreaAnimation)
            _rotation.BeginAnimation(RotateTransform.AngleProperty, RevealDecorator.Animation(from, target, IsExpanded), HandoffBehavior.SnapshotAndReplace);
    }
}

/// <summary>以可见高度和透明度收纳内容，不缩放文字；快速反向操作从当前进度继续。</summary>
public sealed class RevealDecorator : Decorator
{
    public static readonly DependencyProperty IsRevealedProperty = DependencyProperty.Register(nameof(IsRevealed), typeof(bool), typeof(RevealDecorator), new PropertyMetadata(false, (sender, _) => ((RevealDecorator)sender).Transition()));
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(RevealDecorator),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure, (sender, e) => ((RevealDecorator)sender).Opacity = Math.Clamp((double)e.NewValue, 0, 1)));
    public bool IsRevealed { get => (bool)GetValue(IsRevealedProperty); set => SetValue(IsRevealedProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public RevealDecorator()
    {
        ClipToBounds = true; Opacity = 0; Visibility = Visibility.Collapsed;
        Loaded += (_, _) => Snap(); Unloaded += (_, _) => Snap();
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ThemeVisuals.MotionAllowedProperty && e.NewValue is false) Snap();
    }
    private void InputState()
    {
        if (!IsRevealed && IsKeyboardFocusWithin)
            for (var parent = VisualTreeHelper.GetParent(this); parent != null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is Expander expander) { (expander.Template?.FindName("PART_Header", expander) as Control)?.Focus(); break; }
                if (parent is TreeViewItem item) { item.Focus(); break; }
            }
        IsHitTestVisible = IsRevealed;
        KeyboardNavigation.SetTabNavigation(this, IsRevealed ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
    }
    private void Snap()
    {
        BeginAnimation(ProgressProperty, null); Progress = IsRevealed ? 1 : 0;
        Visibility = IsRevealed ? Visibility.Visible : Visibility.Collapsed; InputState();
    }
    private void Transition()
    {
        if (!IsLoaded || !ThemeVisuals.GetMotionAllowed(this) || !SystemParameters.ClientAreaAnimation) { Snap(); return; }
        var from = Progress; var target = IsRevealed ? 1d : 0d;
        BeginAnimation(ProgressProperty, null); Progress = target; Visibility = Visibility.Visible; InputState();
        var animation = Animation(from, target, IsRevealed);
        animation.Completed += (_, _) => { if (!IsRevealed && Progress < .001) Visibility = Visibility.Collapsed; };
        BeginAnimation(ProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
    internal static DoubleAnimation Animation(double from, double to, bool opening) => new(from, to, TimeSpan.FromMilliseconds(opening ? 220 : 180))
    { EasingFunction = new CubicEase { EasingMode = opening ? EasingMode.EaseOut : EasingMode.EaseIn }, FillBehavior = FillBehavior.Stop };
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
