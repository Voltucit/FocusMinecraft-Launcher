using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Preview;
namespace FocusMinecraftLauncher.Features.Home.ViewModels.Instances;

/// <summary>管理当前实例的显示信息、目录和受管文件；删除只在用户明确确认后执行。</summary>
public sealed class InstanceOverviewViewModel : ObservableObject
{
    private readonly PreviewSession _session;
    private string _name, _description, _category, _nameError="", _notice="";
    private ImageSource? _icon;
    private string _iconPath="";
    private bool _favorite, _confirm;
    public PreviewInstance Instance { get; }
    public string Name { get=>_name;set { Set(ref _name,value);NameError="";Notice=""; } }
    public string Description { get=>_description;set { Set(ref _description,value);Notice=""; } }
    public string Category { get=>_category;set { Set(ref _category,value);Notice=""; } }
    public bool Favorite { get=>_favorite;set { Set(ref _favorite,value);Notice=""; } }
    public ImageSource? Icon { get=>_icon;set { if(Set(ref _icon,value)) Changed(nameof(DisplayIcon)); } }
    public ImageSource DisplayIcon => Icon ?? Instance.FallbackIconImage ?? Instance.DefaultIcon;
    public string NameError { get=>_nameError;private set=>Set(ref _nameError,value); }
    public string Notice { get=>_notice;private set=>Set(ref _notice,value); }
    public bool ConfirmRemove { get=>_confirm;set=>Set(ref _confirm,value); }
    public bool HasLoaderVersion => Instance.Loader!="原版";
    public UiCommand OpenFolder { get; }
    public UiCommand Repair { get; }
    public UiCommand ExportScript { get; }
    public UiCommand ChooseIcon { get; }
    public UiCommand ClearIcon { get; }
    public UiCommand Save { get; }
    public UiCommand RequestRemove { get; }
    public UiCommand CancelRemove { get; }
    public UiCommand RemovePreview { get; }
    public InstanceOverviewViewModel(PreviewSession session,PreviewInstance instance,Action back)
    {
        _session=session;Instance=instance;_name=instance.Name;_description=instance.Description;_category=instance.Category;_favorite=instance.IsFavorite;_icon=instance.IconImage;
        ChooseIcon=new(_=>PickIcon());ClearIcon=new(_=>{Icon=null;_iconPath="";});
        Save=new(async _=>await SaveDraftAsync(),_=>session.Instances.Contains(instance));
        RequestRemove=new(_=>ConfirmRemove=true,_=>!session.IsBusy && session.Instances.Contains(instance));
        CancelRemove=new(_=>ConfirmRemove=false);
        OpenFolder=new(p=>
        {
            try {Infrastructure.Runtime.RuntimeSupport.OpenFolder(p is string folder&&folder.Length>0?Path.Combine(instance.Folder,folder):instance.Folder);}
            catch(Exception error){Notice=Infrastructure.Runtime.RuntimeSupport.Error(error);}
        });
        Repair=new(_=>session.Enqueue(PreviewTaskKind.Repair,instance.Name+"文件检查",instance.Version,instance.Root,instance.Id,(task,token)=>session.EnsureFilesAsync(instance,task,token)),_=>!session.IsBusy);
        ExportScript=new(async _=>
        {
            var dialog=new SaveFileDialog{Title="导出启动脚本",Filter="Windows 启动脚本|*.cmd",DefaultExt=".cmd",AddExtension=true,FileName="启动游戏.cmd"};
            if(dialog.ShowDialog()!=true)return;
            try
            {
                string Escape(string value)=>value.Replace("%","%%");
                var executable=Environment.ProcessPath??Path.Combine(AppContext.BaseDirectory,"FocusMinecraftLauncher.exe");
                await File.WriteAllTextAsync(dialog.FileName,"@echo off\r\nchcp 65001 >nul\r\nsetlocal DisableDelayedExpansion\r\nstart \"\" \""+Escape(executable)+"\" --launch-root \""+Escape(instance.Root)+"\" --launch-instance \""+Escape(instance.Id)+"\"\r\n",System.Text.Encoding.Default);
                Notice="启动脚本已保存，将通过 FMLauncher 检查文件并启动游戏。";
            }
            catch(Exception error){Notice=Infrastructure.Runtime.RuntimeSupport.Error(error);}
        });
        RemovePreview=new(async _=>
        {
            try
            {
                var folder=Path.GetFullPath(instance.Folder);var versions=Path.GetFullPath(Path.Combine(instance.Root,"versions"));
                if(!string.Equals(Path.GetDirectoryName(folder),versions,StringComparison.OrdinalIgnoreCase)||File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint))throw new IOException("实例目录不符合安全删除范围。");
                var reader=new FMCore.Versions.MinecraftVersionReader();var all=await reader.GetVersionsAsync(instance.Root);
                if(all.Any(v=>v.Id!=instance.Id&&reader.GetInheritanceChain(v,all).Any(parent=>parent.Id==instance.Id)))throw new InvalidOperationException("其他实例依赖此版本，请先处理这些实例后再删除。");
                await Task.Run(()=>Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(folder,Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin));
                await session.RefreshInstancesAsync();ConfirmRemove=false;back();
            }
            catch(Exception error){Notice=Infrastructure.Runtime.RuntimeSupport.Error(error);}
        },_=>ConfirmRemove&&!session.IsBusy&&session.Instances.Contains(instance));
    }
    private void PickIcon()
    {
        var dialog=new OpenFileDialog { Filter="图像文件|*.png;*.jpg;*.jpeg;*.ico" };
        if(dialog.ShowDialog()!=true)return;
        try
        {
            var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;
            image.DecodePixelWidth=128;image.UriSource=new Uri(dialog.FileName);image.EndInit();image.Freeze();Icon=image;_iconPath=dialog.FileName;Notice="";
        }
        catch(Exception e) when(e is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException)
        { Notice="无法读取这个图像，请选择其他文件。"; }
    }
    /// <summary>保存显示名称和来源旁的个性化字段，不改名已有实例目录。</summary>
    private async Task SaveDraftAsync()
    {
        if(string.IsNullOrWhiteSpace(Name)){NameError="请输入实例名称。";return;}
        try
        {
            Infrastructure.Runtime.ModpackArchive.EnsureInstanceWritePath(Instance);
            string? savedIcon=null;
            if(_iconPath.Length>0)
            {
                var target=Path.Combine(Instance.Folder,"fmlauncher.icon"+Path.GetExtension(_iconPath));
                if(!string.Equals(target,_iconPath,StringComparison.OrdinalIgnoreCase))File.Copy(_iconPath,target,true);savedIcon=target;
            }
            var options=await Infrastructure.Runtime.InstanceStore.UpdateAsync(Instance,options=>
            {
                options.Name=Name.Trim();options.Description=Description.Trim();options.Category=Category.Trim();options.Favorite=Favorite;
                if(savedIcon!=null)options.IconPath=savedIcon;else if(Icon==null)options.IconPath="";
            });
            Instance.Name=options.Name;Instance.Description=options.Description;Instance.Category=options.Category;Instance.IsFavorite=options.Favorite;
            Infrastructure.Runtime.InstanceStore.RefreshIcons(Instance,options);Icon=Instance.IconImage;
            Instance.Options=options;_session.Signal();Notice="已保存个性化设置。";
        }
        catch(Exception error){Notice=Infrastructure.Runtime.RuntimeSupport.Error(error);}
    }
}
