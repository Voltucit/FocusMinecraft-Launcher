using System.Windows.Controls;
namespace FMLauncher.Features.Downloads.Views;
/// <summary>展示原有全局任务服务，不创建新的队列或模拟进度。</summary>
public partial class TasksPage : UserControl
{
    public TasksPage(ViewModels.DownloadsViewModel model) { InitializeComponent(); DataContext = model; }
}
