using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using FocusMinecraftLauncher.Features.Home.ViewModels.Instances;
namespace FocusMinecraftLauncher.Features.Home.Views.Instances;
/// <summary>实例管理的原生视图，交互和状态由对应 ViewModel 提供；Mod、资源包与光影包共用这一页。</summary>
public partial class InstanceContentPage : UserControl
{
    public InstanceContentPage()
    {
        InitializeComponent();
        Unloaded+=(_,_)=>{if(DataContext is InstanceContentViewModel model)model.CancelOpenDetails();};
    }
    private string Extensions=>DataContext is InstanceContentViewModel model?model.Kind==InstanceContentKind.Mod?".jar":".zip":".jar";
    private void PageDragOver(object sender,DragEventArgs e)
    {
        var files=DroppedFiles(e);
        if(files.Length==0||DataContext is not InstanceContentViewModel model||!model.SupportsKind){e.Effects=DragDropEffects.None;DropHint.Visibility=Visibility.Collapsed;e.Handled=true;return;}
        e.Effects=DragDropEffects.Copy;DropHintText.Text=$"松开以安装 {files.Length} 个{model.Label}文件";DropHint.Visibility=Visibility.Visible;e.Handled=true;
    }
    // 拖拽事件在页内移动时会反复触发离开/进入，DragOver 会立刻把提示重新显示出来。
    private void PageDragLeave(object sender,DragEventArgs e)=>DropHint.Visibility=Visibility.Collapsed;
    private async void PageDrop(object sender,DragEventArgs e)
    {
        DropHint.Visibility=Visibility.Collapsed;e.Handled=true;
        var files=DroppedFiles(e);if(files.Length==0||DataContext is not InstanceContentViewModel model)return;
        await model.InstallDroppedAsync(files);
    }
    private string[] DroppedFiles(DragEventArgs e)=>
        e.Data.GetDataPresent(DataFormats.FileDrop)&&e.Data.GetData(DataFormats.FileDrop) is string[] paths
            ?paths.Where(path=>path.EndsWith(Extensions,StringComparison.OrdinalIgnoreCase)).ToArray():[];
}
