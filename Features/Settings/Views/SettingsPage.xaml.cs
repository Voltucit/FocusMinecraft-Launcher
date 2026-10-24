using System.Windows.Controls;
namespace FMLauncher.Features.Settings.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage(FMLauncher.Features.Settings.ViewModels.SettingsViewModel model) { InitializeComponent(); DataContext = model; }
}

