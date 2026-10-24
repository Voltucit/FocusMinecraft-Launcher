using System.Windows.Controls;
namespace FMLauncher.Features.Mods.Views;

public partial class ModsPage : UserControl
{
    public ModsPage(FMLauncher.Features.Mods.ViewModels.ModsViewModel model) { InitializeComponent(); DataContext = model; }
}

