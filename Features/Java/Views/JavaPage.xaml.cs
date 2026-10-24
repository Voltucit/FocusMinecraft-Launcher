using System.Windows.Controls;
namespace FMLauncher.Features.Java.Views;

public partial class JavaPage : UserControl
{
    public JavaPage(FMLauncher.Features.Java.ViewModels.JavaViewModel model) { InitializeComponent(); DataContext = model; }
}

