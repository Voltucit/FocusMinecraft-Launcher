using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FocusMinecraftLauncher.Features.Downloads.ViewModels;
using FocusMinecraftLauncher.Presentation.Controls;
namespace FocusMinecraftLauncher.Features.Downloads.Views;
/// <summary>仅处理页面自适应布局；数据和交互命令由 ViewModel 提供。</summary>
public partial class DownloadsPage : UserControl
{
    private IInputElement? _returnFocus;
    public DownloadsPage() => InitializeComponent();
    private void ResizePage(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is DownloadsViewModel vm) vm.IsCompact = ActualWidth < 850;
    }
    private void OverlayVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            _returnFocus = Keyboard.FocusedElement;
            Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(() =>
            { if(DataContext is DownloadsViewModel vm && vm.IsOverlayOpen) { if(vm.IsImageOpen) CloseImageButton.Focus(); else ClosePreviewButton.Focus(); } }));
        }
        else if(DataContext is DownloadsViewModel { IsOverlayOpen:false } && _returnFocus is UIElement element)
            Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(() =>
            { if(DataContext is DownloadsViewModel { IsOverlayOpen:false } && element.IsVisible && element.IsEnabled) element.Focus(); }));
    }
    private void TargetPickerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            _returnFocus = Keyboard.FocusedElement;
            if (sender is Border border && TargetPickerScale is { } scale)
            {
                if (ThemeVisuals.GetMotionAllowed(border) && SystemParameters.ClientAreaAnimation)
                {
                    border.Opacity = 1; scale.ScaleX = scale.ScaleY = 1;
                    var duration = TimeSpan.FromMilliseconds(250);
                    border.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                    var spring = new BackEase { Amplitude = 0.12, EasingMode = EasingMode.EaseOut };
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = spring });
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = spring });
                }
                else
                {
                    border.BeginAnimation(OpacityProperty, null);
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    border.Opacity = 1; scale.ScaleX = scale.ScaleY = 1;
                }
            }
            Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(() =>
            {
                if(DataContext is DownloadsViewModel vm && vm.IsTargetPickerOpen)
                {
                    if(ConfirmTargetButton.IsEnabled) ConfirmTargetButton.Focus(); else TargetPickerList.Focus();
                }
            }));
        }
        else if(DataContext is DownloadsViewModel { IsOverlayOpen:false } && _returnFocus is UIElement element)
            Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(() =>
            { if(DataContext is DownloadsViewModel { IsOverlayOpen:false } && element.IsVisible && element.IsEnabled) element.Focus(); }));
    }
}
