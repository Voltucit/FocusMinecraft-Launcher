using System.Windows;
using System.Windows.Controls;
using FMLauncher.CoreIntegration.Models;
using FMLauncher.Features.Accounts.ViewModels;
using FMLauncher.Navigation;
namespace FMLauncher.Features.Accounts.Views;
/// <summary>窗口内账号面板，仅协调行点击和关闭；账号变更仍由 ViewModel 与 SDK 处理。</summary>
public partial class AccountsPage : UserControl
{
    public AccountsPage(AccountsViewModel model) { InitializeComponent(); DataContext = model; }
    private async void ChooseAccount(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AccountsViewModel model || sender is not FrameworkElement { DataContext: AccountItem account } || !model.Switch.CanExecute(null)) return;
        model.Selected = account; model.Switch.Execute(null); await model.Switch.Completion;
        if (model.Current?.Id == account.Id) NavigationActions.CloseAccounts.Execute(null, this);
        e.Handled = true;
    }
}
