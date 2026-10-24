using System.Windows.Controls;
namespace FMLauncher.Features.Downloads.Views;

public partial class DownloadsPage : UserControl
{
    public DownloadsPage(FMLauncher.Features.Downloads.ViewModels.DownloadsViewModel model) { InitializeComponent(); DataContext = model; }
}

