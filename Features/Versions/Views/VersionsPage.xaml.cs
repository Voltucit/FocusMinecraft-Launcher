using System.Windows.Controls;
namespace FMLauncher.Features.Versions.Views;

public partial class VersionsPage : UserControl
{
    public VersionsPage(FMLauncher.Features.Versions.ViewModels.VersionsViewModel model) { InitializeComponent(); DataContext = model; }
}
