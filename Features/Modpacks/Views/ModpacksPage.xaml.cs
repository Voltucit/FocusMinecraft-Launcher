using System.Windows.Controls;
namespace FMLauncher.Features.Modpacks.Views;

public partial class ModpacksPage : UserControl
{
    public ModpacksPage(FMLauncher.Features.Modpacks.ViewModels.ModpacksViewModel model) { InitializeComponent(); DataContext = model; }
}

