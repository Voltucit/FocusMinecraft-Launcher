using System.Windows.Controls;
namespace FMLauncher.Appearance.Views;

public partial class AppearancePage : UserControl
{
    public AppearancePage(FMLauncher.Appearance.ViewModels.AppearanceViewModel model) { InitializeComponent(); DataContext = model; SecretBox.Password = model.ApiKey; }
    private void KeyChanged(object sender, System.Windows.RoutedEventArgs e) { if (DataContext is FMLauncher.Appearance.ViewModels.AppearanceViewModel model) model.ApiKey = SecretBox.Password; }
}

