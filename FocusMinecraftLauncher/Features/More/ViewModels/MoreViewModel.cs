using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Features.More.Models;
using FocusMinecraftLauncher.Preview;
namespace FocusMinecraftLauncher.Features.More.ViewModels;
public sealed class MoreViewModel : ObservableObject
{
    public PreviewSession Session {get;}
    private string _notice="";
    public string Notice {get=>_notice;private set=>Set(ref _notice,value);}
    public LauncherUpdateViewModel Updates { get; }
    public string Version=>Updates.Version;
    public UiCommand ExportLog {get;}
    /// <summary>“特别鸣谢”列表，按“名字、头像、介绍、个人主页”展示；为空时整块不显示。</summary>
    public ObservableCollection<Acknowledgment> Acknowledgments { get; } = [];
    public bool HasAcknowledgments => Acknowledgments.Count > 0;
    public UiCommand OpenAcknowledgmentPage { get; }
    public MoreViewModel(PreviewSession session)
    {
        Session=session; Updates=((App)System.Windows.Application.Current).Updates;
        OpenAcknowledgmentPage=new(page=>{
            if(page is not string address||string.IsNullOrWhiteSpace(address))return;
            try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(address){UseShellExecute=true});}
            catch(Exception error){Notice=Infrastructure.Runtime.RuntimeSupport.Error(error);}
        });
        Acknowledgments.CollectionChanged+=(_,_)=>Changed(nameof(HasAcknowledgments));

        // 示例：复制下面一行并取消注释即可新增条目（头像可填 https 链接或本地绝对路径；主页留空则不显示按钮）。
        // Acknowledgments.Add(new("名字", "https://example.com/avatar.png", "一句话介绍", "https://example.com"));
        
        //特别鸣谢区域
        Acknowledgments.Add(new("Voltucit", "https://i0.hdslb.com/bfs/face/876238092f96097678b9a16af77440846aeaa837.webp@150w_150h.jpg", "FML作者，天依中毒者。", "https://space.bilibili.com/38782485"));
        Acknowledgments.Add(new("bangbang93", "https://i0.hdslb.com/bfs/face/1d297f1fc4725391d1910c10988cdb7945747e69.jpg", "BMCLAPI 下载源提供者", "https://space.bilibili.com/9322"));
        Acknowledgments.Add(new("MCIMirror", "https://www.mcimirror.top/brand/logo-mark-192.png", "Modrinth与CurseForge镜像源提供", "https://www.mcimirror.top/"));
        
        
        
        
        
        
        ExportLog=new(async _=>
        {
            var dialog=new SaveFileDialog{Title="导出启动器日志",Filter="文本日志|*.log",DefaultExt=".log",AddExtension=true,FileName="FMLauncher-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".log"};
            if(dialog.ShowDialog()!=true)return;
            try {await File.WriteAllLinesAsync(dialog.FileName,session.Logs.Reverse().Select(Infrastructure.Runtime.RuntimeSupport.SafeText));Notice="日志已保存。";}
            catch(Exception error){Notice=Infrastructure.Runtime.RuntimeSupport.Error(error);}
        });
    }
}
