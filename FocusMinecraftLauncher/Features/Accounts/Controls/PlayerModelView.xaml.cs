using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FocusMinecraftLauncher.Presentation.Controls;
using FocusMinecraftLauncher.Preview;

namespace FocusMinecraftLauncher.Features.Accounts.Controls;

/// <summary>账号外观的原生 3D 展示；不承担认证、纹理下载或游戏文件写入。</summary>
public partial class PlayerModelView : UserControl
{
    public static readonly DependencyProperty AccountProperty = DependencyProperty.Register(
        nameof(Account), typeof(PreviewAccount), typeof(PlayerModelView), new PropertyMetadata(null, AccountChanged));
    public PreviewAccount? Account { get => (PreviewAccount?)GetValue(AccountProperty); set => SetValue(AccountProperty, value); }

    private MinecraftPlayerModel? _model;
    private byte[]? _skin, _cape;
    private bool _slim, _subscribed, _dragging;
    private Point _previous;

    static PlayerModelView()
    {
        ThemeVisuals.MotionAllowedProperty.OverrideMetadata(typeof(PlayerModelView),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits, MotionPreferenceChanged));
        ThemeVisuals.WindowActiveProperty.OverrideMetadata(typeof(PlayerModelView),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits, MotionPreferenceChanged));
    }

    public PlayerModelView() => InitializeComponent();

    private static void AccountChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (PlayerModelView)sender;
        if (view._subscribed)
        {
            if (e.OldValue is PreviewAccount previous) previous.PropertyChanged -= view.AppearanceChanged;
            if (e.NewValue is PreviewAccount current) current.PropertyChanged += view.AppearanceChanged;
        }
        view.StopDragging();
        view._model?.SetWalking(false);
        view._model = null;
        view.RefreshModel();
    }

    private void ViewLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            _subscribed = true;
            if (Account != null) Account.PropertyChanged += AppearanceChanged;
            SystemParameters.StaticPropertyChanged += SystemPreferenceChanged;
        }
        RefreshModel();
        UpdateWalking();
    }

    private void ViewUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            if (Account != null) Account.PropertyChanged -= AppearanceChanged;
            SystemParameters.StaticPropertyChanged -= SystemPreferenceChanged;
            _subscribed = false;
        }
        StopDragging();
        _model?.SetWalking(false);
    }

    private void AppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(PreviewAccount.SkinPng) or nameof(PreviewAccount.CapePng) or nameof(PreviewAccount.IsSlimSkin))
            RefreshModel();
    }

    private void RefreshModel()
    {
        if (PlayerVisual == null || !IsLoaded) return;
        if (Account == null) { PlayerVisual.Content = null; return; }
        if (_model != null && ReferenceEquals(_skin, Account.SkinPng) && ReferenceEquals(_cape, Account.CapePng) && _slim == Account.IsSlimSkin) return;
        var yaw = _model?.Yaw.Angle ?? -25;
        var pitch = _model?.Pitch.Angle ?? 10;
        _model?.SetWalking(false);
        _skin = Account.SkinPng; _cape = Account.CapePng; _slim = Account.IsSlimSkin;
        try { _model = new MinecraftPlayerModel(_skin, _slim, _cape); }
        catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or InvalidOperationException or FileFormatException)
        {
            Trace.TraceWarning("玩家模型纹理无法读取：{0}", error.GetType().Name);
            _model = new MinecraftPlayerModel(null, false, null);
        }
        _model.Yaw.Angle = yaw; _model.Pitch.Angle = pitch;
        PlayerVisual.Content = _model.Root;
        UpdateWalking();
    }

    private static void MotionPreferenceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((PlayerModelView)sender).UpdateWalking();
    private void SystemPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Dispatcher.InvokeAsync(UpdateWalking);
    }
    private void ViewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) StopDragging();
        UpdateWalking();
    }
    private void WalkChanged(object sender, RoutedEventArgs e) => UpdateWalking();
    private void UpdateWalking()
    {
        if (WalkToggle == null) return;
        var allowed = ThemeVisuals.GetMotionAllowed(this) && SystemParameters.ClientAreaAnimation;
        WalkToggle.IsEnabled = allowed;
        _model?.SetWalking(IsLoaded && IsVisible && IsEnabled && allowed
            && ThemeVisuals.GetWindowActive(this) && WalkToggle.IsChecked == true);
    }

    private void ResizeViewport(object sender, SizeChangedEventArgs e)
    {
        if (PlayerViewport.ActualHeight > 0) PlayerCamera.Width = Math.Max(26, 39 * PlayerViewport.ActualWidth / PlayerViewport.ActualHeight);
    }
    private void StartRotation(object sender, MouseButtonEventArgs e)
    {
        if (_model == null) return;
        RotationSurface.Focus();
        _previous = e.GetPosition(RotationSurface);
        _dragging = RotationSurface.CaptureMouse();
        e.Handled = true;
    }
    private void RotatePlayer(object sender, MouseEventArgs e)
    {
        if (!_dragging || _model == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { StopDragging(); return; }
        var point = e.GetPosition(RotationSurface);
        _model.Yaw.Angle = (_model.Yaw.Angle + (point.X - _previous.X) * .6) % 360;
        _model.Pitch.Angle = Math.Clamp(_model.Pitch.Angle - (point.Y - _previous.Y) * .4, -45, 45);
        _previous = point; e.Handled = true;
    }
    private void EndRotation(object sender, MouseButtonEventArgs e) { StopDragging(); e.Handled = true; }
    private void RotationCaptureLost(object sender, MouseEventArgs e) { _dragging = false; }
    private void StopDragging()
    {
        _dragging = false;
        if (RotationSurface?.IsMouseCaptured == true) RotationSurface.ReleaseMouseCapture();
    }
    private void ResetView(object sender, RoutedEventArgs e) => ResetAngles();
    private void ResetAngles()
    {
        if (_model == null) return;
        _model.Yaw.Angle = -25; _model.Pitch.Angle = 10;
    }
    private void RotationKeyDown(object sender, KeyEventArgs e)
    {
        if (_model == null) return;
        switch (e.Key)
        {
            case Key.Left: _model.Yaw.Angle = (_model.Yaw.Angle - 15) % 360; break;
            case Key.Right: _model.Yaw.Angle = (_model.Yaw.Angle + 15) % 360; break;
            case Key.Up: _model.Pitch.Angle = Math.Clamp(_model.Pitch.Angle + 5, -45, 45); break;
            case Key.Down: _model.Pitch.Angle = Math.Clamp(_model.Pitch.Angle - 5, -45, 45); break;
            case Key.Home: ResetAngles(); break;
            default: return;
        }
        e.Handled = true;
    }
}
