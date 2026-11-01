using System.Windows;
using System.Windows.Controls;
using FocusMinecraftLauncher.Features.Accounts.ViewModels;
namespace FocusMinecraftLauncher.Features.Accounts.Views;
/// <summary>仅处理页面自适应布局；数据和交互命令由 ViewModel 提供。</summary>
public partial class AccountsPage : UserControl
{
    public AccountsPage() => InitializeComponent();
    private void ResizePage(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is AccountsViewModel vm) vm.IsCompact = ActualWidth < 830;
    }
}
