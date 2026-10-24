using System.Windows.Controls;
namespace FMLauncher.Features.Home.Views;

public partial class HomePage : UserControl
{
    public HomePage(FMLauncher.Features.Home.ViewModels.HomeViewModel model) { InitializeComponent(); DataContext = model; }
}

