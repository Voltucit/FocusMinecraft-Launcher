using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FocusMinecraftLauncher.Presentation.Controls;

/// <summary>Soft UI 专用的有限次悬浮、按压和焦点反馈；不注册渲染循环或计时器。</summary>
public static class SoftMotion
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SoftMotion), new PropertyMetadata(false, EnabledChanged));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State), typeof(SoftMotion));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    internal static void RefreshPreferences(DependencyObject target) => (target.GetValue(StateProperty) as State)?.Update(false);

    private static void EnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not Control control) return;
        (target.GetValue(StateProperty) as State)?.Dispose();
        target.ClearValue(StateProperty);
        if (e.NewValue is true) target.SetValue(StateProperty, new State(control));
    }

    private sealed class State : IDisposable
    {
        private readonly Control _control;
        private readonly TranslateTransform _lift = new();
        private readonly ScaleTransform _scale = new(1, 1);
        private FrameworkElement? _root, _shadow, _halo;
        private Transform? _originalTransform;
        private Point _originalOrigin;
        private bool _pressed, _keyboardPressed, _disposed;

        public State(Control control)
        {
            _control = control;
            control.Loaded += Loaded; control.Unloaded += Unloaded;
            control.MouseEnter += Hover; control.MouseLeave += Hover;
            control.PreviewMouseLeftButtonDown += Down; control.PreviewMouseLeftButtonUp += Up;
            control.LostMouseCapture += CaptureLost;
            control.PreviewKeyDown += KeyDown; control.PreviewKeyUp += KeyUp;
            control.GotKeyboardFocus += Focus; control.LostKeyboardFocus += Focus;
            control.IsEnabledChanged += EnabledChanged;
            // 主题在已显示页面中切换时，等新模板落定后获取部件；回调不启动持续任务。
            if (control.IsLoaded) control.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FindParts));
        }

        private void Loaded(object sender, RoutedEventArgs e) => FindParts();
        private void FindParts()
        {
            if (_disposed || !_control.IsLoaded) return;
            _control.ApplyTemplate();
            var root = _control.Template?.FindName("PART_SoftRoot", _control) as FrameworkElement;
            if (!ReferenceEquals(root, _root))
            {
                ResetParts();
                _root = root;
                if (_root != null)
                {
                    _originalTransform = _root.RenderTransform; _originalOrigin = _root.RenderTransformOrigin;
                    var group = new TransformGroup(); group.Children.Add(_originalTransform); group.Children.Add(_scale); group.Children.Add(_lift);
                    // 每个模板实例拥有自己的可变变换，不修改共享或已冻结的 Freezable。
                    _root.RenderTransform = group; _root.RenderTransformOrigin = new Point(.5, .5);
                }
            }
            _shadow = _control.Template?.FindName("PART_SoftShadow", _control) as FrameworkElement;
            _halo = _control.Template?.FindName("PART_SoftHalo", _control) as FrameworkElement;
            Update(false);
        }

        private void Unloaded(object sender, RoutedEventArgs e) { _pressed = _keyboardPressed = false; Update(false); }
        private void Hover(object sender, MouseEventArgs e) => Update(true);
        private void Focus(object sender, KeyboardFocusChangedEventArgs e)
        { if (!_control.IsKeyboardFocusWithin) _pressed = _keyboardPressed = false; Update(true); }
        private void EnabledChanged(object sender, DependencyPropertyChangedEventArgs e) { if (e.NewValue is false) _pressed = _keyboardPressed = false; Update(true); }
        private void Down(object sender, MouseButtonEventArgs e) { if (_control is ButtonBase && _control.IsEnabled) { _keyboardPressed = false; _pressed = true; Update(true); } }
        private void Up(object sender, MouseButtonEventArgs e) { _pressed = _keyboardPressed = false; Update(true); }
        private void CaptureLost(object sender, MouseEventArgs e) { _pressed = _keyboardPressed = false; Update(true); }
        private void KeyDown(object sender, KeyEventArgs e)
        { if (_control is ButtonBase && !e.IsRepeat && e.Key is Key.Space or Key.Enter) { _pressed = _keyboardPressed = true; Update(true); } }
        private void KeyUp(object sender, KeyEventArgs e)
        { if (e.Key is Key.Space or Key.Enter) { _pressed = _keyboardPressed = false; Update(true); } }

        public void Update(bool animate)
        {
            var motion = !_disposed && _control.IsLoaded && _control.IsVisible && _control.IsEnabled
                && ThemeVisuals.GetIsSoft(_control) && ThemeVisuals.GetMotionAllowed(_control)
                && ThemeVisuals.GetWindowActive(_control) && SystemParameters.ClientAreaAnimation;
            var down = motion && _pressed && (_keyboardPressed || _control.IsMouseOver);
            var floating = _control is ButtonBase or ListBoxItem;
            var hover = motion && floating && _control.IsMouseOver;
            animate &= motion;
            var duration = down ? 150 : 250;
            Move(_lift, TranslateTransform.YProperty, hover && !down ? -2 : 0, animate, duration);
            Move(_scale, ScaleTransform.ScaleXProperty, down ? .96 : 1, animate, duration);
            Move(_scale, ScaleTransform.ScaleYProperty, down ? .96 : 1, animate, duration);
            if (_shadow != null) Move(_shadow, UIElement.OpacityProperty, !_control.IsEnabled ? 0 : down ? .3 : hover ? 1 : .65, animate, 250);
            if (_halo != null) Move(_halo, UIElement.OpacityProperty, _control.IsKeyboardFocusWithin && _control.IsEnabled ? 1 : 0, animate, 250);
        }

        private static void Move(Animatable target, DependencyProperty property, double value, bool animate, int duration)
        {
            var from = (double)target.GetValue(property); target.BeginAnimation(property, null); target.SetValue(property, value);
            if (animate) target.BeginAnimation(property, new DoubleAnimation(from, value, TimeSpan.FromMilliseconds(duration))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }
        private static void Move(FrameworkElement target, DependencyProperty property, double value, bool animate, int duration)
        {
            var from = (double)target.GetValue(property); target.BeginAnimation(property, null); target.SetValue(property, value);
            if (animate) target.BeginAnimation(property, new DoubleAnimation(from, value, TimeSpan.FromMilliseconds(duration))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }

        private void ResetParts()
        {
            Move(_lift, TranslateTransform.YProperty, 0, false, 0);
            Move(_scale, ScaleTransform.ScaleXProperty, 1, false, 0); Move(_scale, ScaleTransform.ScaleYProperty, 1, false, 0);
            if (_shadow != null) Move(_shadow, UIElement.OpacityProperty, .65, false, 0);
            if (_halo != null) Move(_halo, UIElement.OpacityProperty, 0, false, 0);
            if (_root != null) { _root.RenderTransform = _originalTransform ?? Transform.Identity; _root.RenderTransformOrigin = _originalOrigin; }
            _root = null; _shadow = null; _halo = null;
        }

        public void Dispose()
        {
            _disposed = true; ResetParts();
            _control.Loaded -= Loaded; _control.Unloaded -= Unloaded;
            _control.MouseEnter -= Hover; _control.MouseLeave -= Hover;
            _control.PreviewMouseLeftButtonDown -= Down; _control.PreviewMouseLeftButtonUp -= Up;
            _control.LostMouseCapture -= CaptureLost; _control.PreviewKeyDown -= KeyDown; _control.PreviewKeyUp -= KeyUp;
            _control.GotKeyboardFocus -= Focus; _control.LostKeyboardFocus -= Focus; _control.IsEnabledChanged -= EnabledChanged;
        }
    }
}
