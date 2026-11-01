using System.Windows;
using System.Windows.Controls;
using FocusMinecraftLauncher.Features.More.ViewModels;
namespace FocusMinecraftLauncher.Features.More.Views;
/// <summary>仅处理页面自适应布局；数据和交互命令由 ViewModel 提供。</summary>
public partial class MorePage : UserControl
{
    public MorePage() => InitializeComponent();
    private void ResizePage(object sender, SizeChangedEventArgs e)
    {
        // 内容通过滚动容器及共享设置行适配可用空间。
    }
}
