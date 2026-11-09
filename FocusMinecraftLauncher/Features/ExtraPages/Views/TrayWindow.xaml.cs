using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FocusMinecraftLauncher.Features.ExtraPages.ViewModels;
using FocusMinecraftLauncher.Presentation.Controls;
using FocusMinecraftLauncher.Presentation.Tray;

namespace FocusMinecraftLauncher.Features.ExtraPages.Views;

/// <summary>无业务初始化的精简窗口；使用有保护的失焦检查，菜单、拖动和托盘手势不触发误隐藏。</summary>
public partial class TrayWindow : Window
{
    private readonly Func<bool> _interactionPending;
    private readonly DispatcherTimer _autoHide;
    private bool _allowClose, _placing;
    private long _transition;
    private TrayAnchor _anchor;

    internal TrayWindow(TrayViewModel viewModel, Func<bool> interactionPending)
    {
        _interactionPending = interactionPending;
        _autoHide = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _autoHide.Tick += CheckOutside;
        InitializeComponent();
        DataContext = viewModel;
    }

    internal void ShowAt(TrayAnchor anchor)
    {
        _anchor = anchor;
        _transition++;
        _autoHide.Stop();
        BeginAnimation(OpacityProperty, null); RevealOffset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        Opacity = 1; RevealOffset.Y = 0; IsHitTestVisible = true;
        _placing = true;
        try
        {
            TrayPositioner.Place(this, anchor);
            if (!IsVisible) Show();
            UpdateLayout();
            TrayPositioner.Place(this, anchor, true);
        }
        finally { _placing = false; }
        if (!Activate()) TrayPositioner.ActivateOrFlash(this);
        if (ThemeVisuals.GetMotionAllowed(this) && SystemParameters.ClientAreaAnimation)
        {
            var duration = TimeSpan.FromMilliseconds(140);
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { FillBehavior = FillBehavior.Stop });
            RevealOffset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(6, 0, duration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }
    }

    internal void HidePanel(bool animate = false)
    {
        _autoHide.Stop();
        var transition = ++_transition;
        if (!IsVisible) return;
        if (!animate || !ThemeVisuals.GetMotionAllowed(this) || !SystemParameters.ClientAreaAnimation)
        { FinishHide(); return; }
        IsHitTestVisible = false;
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(100));
        fade.Completed += (_, _) => { if (transition == _transition) FinishHide(); };
        BeginAnimation(OpacityProperty, fade);
    }

    private void FinishHide()
    {
        BeginAnimation(OpacityProperty, null);
        RevealOffset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        if (IsVisible) Hide();
        Opacity = 1; RevealOffset.Y = 0; IsHitTestVisible = true;
    }

    private void PanelActivated(object? sender, EventArgs e) => _autoHide.Stop();
    private void PanelDeactivated(object? sender, EventArgs e) { if (!_allowClose) _autoHide.Start(); }
    private void CheckOutside(object? sender, EventArgs e)
    {
        if (!IsVisible || IsActive) { _autoHide.Stop(); return; }
        if (IsMouseCaptureWithin || _interactionPending() || TrayPositioner.ForegroundIsLauncherPopup()) return;
        HidePanel(true);
    }

    private void PanelKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { HidePanel(); e.Handled = true; } }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (!IsVisible || _placing) return;
        Dispatcher.BeginInvoke(new Action(() => { if (IsVisible && !_allowClose) TrayPositioner.Place(this, _anchor, true); }));
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_allowClose) return;
        e.Cancel = true;
        Dispatcher.BeginInvoke(new Action(() => HidePanel()));
    }
    internal void CloseForExit()
    {
        _allowClose = true; _transition++;
        _autoHide.Stop(); _autoHide.Tick -= CheckOutside;
        BeginAnimation(OpacityProperty, null);
        RevealOffset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        Close();
    }
}
