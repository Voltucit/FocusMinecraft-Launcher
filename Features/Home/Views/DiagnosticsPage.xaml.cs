using System.Windows.Controls;
using FMLauncher.Features.Home.ViewModels;
namespace FMLauncher.Features.Home.Views;
/// <summary>复用主页的现有维护命令和会话日志，不创建独立实例缓存。</summary>
public partial class DiagnosticsPage : UserControl
{
    public DiagnosticsPage(HomeViewModel model) { InitializeComponent(); DataContext = model; }
}
