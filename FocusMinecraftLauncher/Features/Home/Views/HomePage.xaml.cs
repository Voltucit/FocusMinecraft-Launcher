using System;
using System.Windows;
using System.Windows.Controls;
using FocusMinecraftLauncher.Features.Home.ViewModels;
namespace FocusMinecraftLauncher.Features.Home.Views;

/// <summary>只负责自适应尺寸与经典布局动作行的开合；动作与共享状态由 ViewModel 管理。</summary>
public partial class HomePage : UserControl
{
    public HomePage() => InitializeComponent();

    private void ResizePage(object sender, SizeChangedEventArgs e)
    { if (DataContext is HomeViewModel vm) { vm.IsCompactHeight = ActualHeight < 680; vm.IsNarrowHome = ActualWidth < 700; vm.LaunchPanelWidth = Math.Min(360, Math.Max(200, ActualWidth - 64 - vm.SimpleAvatarSize - 24)); vm.PanelWidth = ActualWidth < 900 ? 296 : 328; vm.IsInfoNarrow = ActualWidth < 740; } }
    private void ClassicVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    { if (e.NewValue is false) ClassicActions.IsOpen = false; }
    /// <summary>经典布局点头像：动作行在头像下展开，整列随之重新居中，头像被顶上去。</summary>
    private void OpenClassicAccountActions(object sender, RoutedEventArgs e) => ClassicActions.Toggle();
    private void PageUnloaded(object sender, RoutedEventArgs e) => CloseAccountActions();
    /// <summary>切走页面或换布局时收起动作行，回来时是收缩状态。</summary>
    public void CloseAccountActions() => ClassicActions.IsOpen = false;
}
