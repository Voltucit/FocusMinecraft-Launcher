using System.Windows.Controls;
using System.Windows.Input;
using FocusMinecraftLauncher.Features.Home.ViewModels.Instances;
using FocusMinecraftLauncher.Infrastructure.Runtime;
namespace FocusMinecraftLauncher.Features.Home.Views.Instances;

/// <summary>存档管理只做查看与删除，双击一行直接在资源管理器里打开那个世界。</summary>
public partial class InstanceSavesPage : UserControl
{
    public InstanceSavesPage()=>InitializeComponent();
    private void ListDoubleClick(object sender,MouseButtonEventArgs e)
    {
        if((e.OriginalSource as System.Windows.FrameworkElement)?.DataContext is not PreviewSaveRow row)return;
        try{RuntimeSupport.OpenFolder(row.Path);}catch(System.Exception){}
    }
}
