using System.Windows.Controls;
namespace FMLauncher.Features.Settings.Views;

public partial class GameSettingsPage : UserControl
{
    public GameSettingsPage(FMLauncher.Features.Settings.ViewModels.SettingsViewModel model) { InitializeComponent(); DataContext = model; }
}

