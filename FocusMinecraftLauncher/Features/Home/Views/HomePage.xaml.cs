using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FocusMinecraftLauncher.Features.Home.ViewModels;
namespace FocusMinecraftLauncher.Features.Home.Views;

/// <summary>只负责自适应尺寸和原生弹出菜单；动作与共享状态由 ViewModel 管理。</summary>
public partial class HomePage : UserControl
{
    private const string CapeItemTag = "cape-choice";
    public HomePage() => InitializeComponent();
    private void ResizePage(object sender, SizeChangedEventArgs e)
    { if (DataContext is HomeViewModel vm) { vm.IsCompactHeight = ActualHeight < 680; vm.IsNarrowHome = ActualWidth < 700; vm.LaunchPanelWidth = Math.Min(360, Math.Max(200, ActualWidth - 64 - vm.SimpleAvatarSize - 24)); vm.PanelWidth = ActualWidth < 900 ? 296 : 328; vm.IsInfoNarrow = ActualWidth < 740; } }
    private void ClassicVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    { if (e.NewValue is false) CloseMenus(this, new RoutedEventArgs()); }
    private void OpenMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        foreach (var other in new[] { AppearanceButton, ProfileButton, SwitchButton })
            if (other != button && other.ContextMenu != null) other.ContextMenu.IsOpen = false;
        menu.PlacementTarget = button; menu.Placement = PlacementMode.Bottom;
        if (button == AppearanceButton && DataContext is HomeViewModel home)
        {
            home.EnsureCapesLoaded();
            BuildCapeItems(menu, home);
        }
        menu.IsOpen = true;
    }
    private void CloseMenus(object sender, RoutedEventArgs e)
    {
        foreach (var button in new[] { AppearanceButton, ProfileButton, SwitchButton })
            if (button?.ContextMenu != null) button.ContextMenu.IsOpen = false;
    }
    /// <summary>披风行直接放进菜单：菜单项模板没有子菜单弹出层，嵌套 ItemsSource 展开后不显示任何内容。</summary>
    private void BuildCapeItems(ContextMenu menu, HomeViewModel home)
    {
        foreach (var stale in menu.Items.OfType<MenuItem>().Where(item => Equals(item.Tag, CapeItemTag)).ToArray()) menu.Items.Remove(stale);
        var anchor = menu.Items.IndexOf(RefreshCapesItem);
        if (anchor < 0) return;
        foreach (var choice in home.CapeOptions)
        {
            menu.Items.Insert(anchor++, new MenuItem
            {
                DataContext = choice,
                Style = (Style)FindResource("CapeMenuItem"),
                Tag = CapeItemTag,
            });
        }
    }
}
