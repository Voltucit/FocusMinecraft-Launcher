using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace FocusMinecraftLauncher.Features.Downloads.Views;
/// <summary>社区筛选和详情共用的版本系列列表，不使用弹出窗口或手动输入。</summary>
public partial class GameVersionFilterBody : UserControl
{
    public GameVersionFilterBody() => InitializeComponent();
    /// <summary>从固定筛选入口展开时显示列表开头；内部版本系列展开不改变主滚动位置。</summary>
    private void FilterExpanded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource)) return;
        for (var parent = VisualTreeHelper.GetParent(this); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer viewer) { viewer.ScrollToTop(); break; }
    }
}
