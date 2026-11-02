using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Infrastructure.Runtime;
using FocusMinecraftLauncher.Preview;
namespace FocusMinecraftLauncher.Features.Home.ViewModels.Instances;

/// <summary>游戏快照的列表与操作；只读写启动器自己的备份目录，不碰 Minecraft 文件结构。</summary>
public sealed class InstanceBackupsViewModel : ObservableObject, IDisposable
{
    private readonly PreviewSession _session;
    private string _notice="",_stage="";
    private bool _busy,_confirm,_auto=true;
    private int _retention=5;
    private CancellationTokenSource? _cancel;
    public PreviewInstance Instance { get; }
    public ObservableCollection<BackupRow> Items { get; }=[];
    public bool IsBusy=>_busy;
    public bool IsIdle=>!_busy;
    public bool HasItems=>Items.Count>0;
    public static IReadOnlyList<int> RetentionChoices { get; } = [1,2,3,4,5,6,7,8,9,10];
    public bool HasSelection=>Items.Any(item=>item.IsSelected);
    public bool ConfirmRemove {get=>_confirm;set=>Set(ref _confirm,value);}
    public string Notice {get=>_notice;private set=>Set(ref _notice,value);}
    public string Stage {get=>_stage;private set=>Set(ref _stage,value);}
    public string StateText=>_busy?Stage:Items.Count==0?"还没有备份。备份包含 Mod、配置与存档，不含日志和账号。":$"共 {Items.Count} 份备份";
    public bool AutoBackup
    {
        get=>_auto;
        set { if(!Set(ref _auto,value))return; _ = SaveAsync(); }
    }
    public int Retention
    {
        get=>_retention;
        set { var clamped=Math.Clamp(value,1,20); if(!Set(ref _retention,clamped))return; _ = SaveAsync(); }
    }
    public UiCommand Reload {get;}
    public UiCommand Create {get;}
    public UiCommand Restore {get;}
    public UiCommand RequestRemove {get;}
    public UiCommand CancelRemove {get;}
    public UiCommand Remove {get;}
    public UiCommand OpenFolder {get;}
    public UiCommand Cancel {get;}
    public InstanceBackupsViewModel(PreviewSession session,PreviewInstance instance)
    {
        _session=session;Instance=instance;
        var options=InstanceStore.Read(instance);
        _auto=options.AutoBackup;_retention=Math.Clamp(options.BackupRetention,1,20);
        Reload=new(_=>Load(),_=>!_busy);
        Create=new(async _=>await CreateAsync("手动备份"),_=>!_busy);
        Restore=new(async _=>await RestoreAsync(),_=>!_busy&&HasSelection);
        RequestRemove=new(_=>ConfirmRemove=true,_=>!_busy&&HasSelection);
        CancelRemove=new(_=>ConfirmRemove=false);
        Remove=new(_=>RemoveAsync(),_=>!_busy&&HasSelection&&ConfirmRemove);
        OpenFolder=new(_=>{try{var directory=GameBackupService.BackupDirectory(instance);System.IO.Directory.CreateDirectory(directory);RuntimeSupport.OpenFolder(directory);}catch(Exception error){Notice=RuntimeSupport.Error(error);}});
        Cancel=new(_=>_cancel?.Cancel(),_=>_busy);
        Load();
    }
    private void Load()
    {
        Items.Clear();
        foreach(var backup in GameBackupService.List(Instance))
        {
            var row=new BackupRow(backup);
            row.PropertyChanged+=RowChanged;
            Items.Add(row);
        }
        ConfirmRemove=false;RefreshAll();
    }
    private async Task CreateAsync(string label,bool automatic=false)
    {
        if(_busy)return;
        _busy=true;_cancel=new CancellationTokenSource();RefreshAll();
        try
        {
            var progress=new Progress<string>(text=>Stage=text);
            var backup=await Task.Run(()=>GameBackupService.CreateAsync(Instance,label,automatic,progress,_cancel.Token),_cancel.Token);
            GameBackupService.Prune(Instance,Retention);
            Load();Notice="已生成备份："+backup.Name;
        }
        catch(Exception error){Notice=RuntimeSupport.Error(error);}
        finally{_busy=false;_cancel.Dispose();_cancel=null;RefreshAll();}
    }
    private async Task RestoreAsync()
    {
        if(Items.FirstOrDefault(item=>item.IsSelected) is not {} row){Notice="请先选择要还原的备份。";return;}
        _busy=true;ConfirmRemove=false;_cancel=new CancellationTokenSource();RefreshAll();
        try
        {
            var progress=new Progress<string>(text=>Stage=text);
            try { await Task.Run(()=>GameBackupService.CreateAsync(Instance,"还原前自动备份",true,progress,_cancel!.Token),_cancel.Token); }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException){Notice="空间不足或无法写入，未能生成还原前备份，仍继续还原。";}
            await Task.Run(()=>GameBackupService.RestoreAsync(Instance,row.Backup,progress,_cancel!.Token),_cancel.Token);
            GameBackupService.Prune(Instance,Retention);
            Load();Notice="已还原：" + row.Backup.Name;_session.Signal();
        }
        catch(Exception error){Notice=RuntimeSupport.Error(error);}
        finally{_busy=false;_cancel.Dispose();_cancel=null;RefreshAll();}
    }
    private void RemoveAsync()
    {
        try
        {
            foreach(var row in Items.Where(item=>item.IsSelected).ToArray())GameBackupService.Delete(row.Backup);
            Load();Notice="所选备份已移入回收站。";
        }
        catch(Exception error){Notice=RuntimeSupport.Error(error);}
        finally{ConfirmRemove=false;}
    }
    private async Task SaveAsync()
    {
        try
        {
            var options=await InstanceStore.UpdateAsync(Instance,value=>{value.AutoBackup=_auto;value.BackupRetention=Math.Clamp(_retention,1,20);});
            Instance.Options=options;Notice=AutoBackup?"启动前会自动备份，最多保留 "+_retention+" 份。":"已关闭启动前自动备份。";
        }
        catch(Exception error){Notice=RuntimeSupport.Error(error);}
    }
    private void RowChanged(object? sender,PropertyChangedEventArgs e){if(e.PropertyName==nameof(BackupRow.IsSelected))ConfirmRemove=false;RefreshAll();}
    private void RefreshAll(){Changed(nameof(StateText));Changed(nameof(HasItems));Changed(nameof(HasSelection));CommandManager.InvalidateRequerySuggested();}
    public void Dispose(){_cancel?.Cancel();foreach(var item in Items)item.PropertyChanged-=RowChanged;}
}
public sealed class BackupRow(GameBackup backup):ObservableObject
{
    private bool _selected;
    public GameBackup Backup {get;}=backup;
    public string Name=>Backup.Name;
    public string Title=>Backup.Created.ToString("yyyy-MM-dd HH:mm");
    public string Detail=>Backup.Label + " · " + RuntimeSupport.FormatSize(Backup.Size) + (Backup.Automatic?" · 自动":" · 手动");
    public bool IsSelected {get=>_selected;set=>Set(ref _selected,value);}
}
