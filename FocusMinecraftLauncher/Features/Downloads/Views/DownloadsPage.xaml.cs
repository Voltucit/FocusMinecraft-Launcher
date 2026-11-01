using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FocusMinecraftLauncher.Features.Downloads.ViewModels;
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
}
