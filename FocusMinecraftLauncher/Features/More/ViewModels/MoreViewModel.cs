using System.IO;
using Microsoft.Win32;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Preview;
namespace FocusMinecraftLauncher.Features.More.ViewModels;
public sealed class MoreViewModel : ObservableObject
{
    public PreviewSession Session {get;}
    private string _notice="";
    public string Notice {get=>_notice;private set=>Set(ref _notice,value);}
    public string Version=>typeof(App).Assembly.GetName().Version?.ToString(3)??"";
    public UiCommand ExportLog {get;}
    public MoreViewModel(PreviewSession session)
    {
        Session=session;
        ExportLog=new(async _=>
        {
            var dialog=new SaveFileDialog{Title="导出启动器日志",Filter="文本日志|*.log",DefaultExt=".log",AddExtension=true,FileName="FMLauncher-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".log"};
            if(dialog.ShowDialog()!=true)return;
            try {await File.WriteAllLinesAsync(dialog.FileName,session.Logs.Reverse().Select(Infrastructure.Runtime.RuntimeSupport.SafeText));Notice="日志已保存。";}
            catch(Exception error){Notice=Infrastructure.Runtime.RuntimeSupport.Error(error);}
        });
    }
}
