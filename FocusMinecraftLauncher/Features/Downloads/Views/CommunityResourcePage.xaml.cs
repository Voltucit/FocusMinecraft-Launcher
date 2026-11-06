using System.Windows;
using System.Windows.Controls;
using FocusMinecraftLauncher.Features.Downloads.ViewModels;
namespace FocusMinecraftLauncher.Features.Downloads.Views;
/// <summary>名称占主要宽度；其余字段由 WrapPanel 自然换行，不改变筛选和分页状态。</summary>
public partial class CommunityResourcePage : UserControl
{
    public CommunityResourcePage() => InitializeComponent();
    private void ResizeFilters(object sender, SizeChangedEventArgs e) => UpdateWidth();
    private void UpdateFilterWidth(object sender, RoutedEventArgs e) => UpdateWidth();
    private void UpdateWidth()
    {
        if (DataContext is CommunityCatalogViewModel vm) vm.NameFieldWidth = Math.Max(180, ActualWidth - 300);
    }
}
