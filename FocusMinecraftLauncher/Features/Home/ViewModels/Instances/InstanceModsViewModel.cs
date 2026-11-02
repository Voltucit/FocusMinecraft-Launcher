using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Infrastructure.Runtime;
using FocusMinecraftLauncher.Preview;
namespace FocusMinecraftLauncher.Features.Home.ViewModels.Instances;

/// <summary>读取实例内的真实 Mod 文件；只从归档元数据展示版本，不根据文件名声明兼容性。</summary>
public sealed class InstanceModsViewModel : ObservableObject, IDisposable
{
    private readonly PreviewSession _session;
    private string _query="",_error="",_notice="";
    private bool _confirm,_busy,_editing,_reloadRequested,_disposed;
    private bool CanEdit=>!_busy&&!_editing&&!_session.IsApplyingIsolation&&!_session.Tasks.Any(t=>t.IsActive&&t.Root==Instance.Root&&(!Instance.UsesGameIsolation||t.InstanceId==Instance.Id));
    public PreviewInstance Instance { get; }
    public ObservableCollection<PreviewModRow> Items { get; }=[];
    public ICollectionView FilteredItems { get; }
    public bool SupportsMods=>Instance.SupportsMods;
    public bool IsLoading=>_busy;
    public bool ShowList=>!IsLoading&&!FilteredItems.IsEmpty;
    public bool HasSelection=>SelectedCount>0;
    public int SelectedCount=>Items.Count(m=>m.IsSelected);
    public string SelectionText=>$"已选 {SelectedCount} 项";
    public string Notice { get=>_notice;private set=>Set(ref _notice,value); }
    public string StateText=>!SupportsMods?"当前游戏未配置 Mod 加载器。":_busy?"正在读取 Mod":_error.Length>0?_error:FilteredItems.IsEmpty?Items.Count==0?"尚未安装 Mod":"没有匹配的 Mod":"";
    public string Query { get=>_query;set {if(Set(ref _query,value)){FilteredItems.Refresh();Refresh();}} }
    public bool AllVisibleSelected {get=>!FilteredItems.IsEmpty&&FilteredItems.Cast<PreviewModRow>().All(m=>m.IsSelected);set {foreach(var m in FilteredItems.Cast<PreviewModRow>().ToArray())m.IsSelected=value;Refresh();}}
    public bool ConfirmRemove {get=>_confirm;set=>Set(ref _confirm,value);}
    public UiCommand EnableSelected {get;}
    public UiCommand DisableSelected {get;}
    public UiCommand ClearSelection {get;}
    public UiCommand RequestRemove {get;}
    public UiCommand CancelRemove {get;}
    public UiCommand RemoveSelected {get;}
    public UiCommand OpenFolder {get;}
    public UiCommand InstallFiles {get;}
    public UiCommand DownloadMods {get;}
    public UiCommand Reload {get;}
    public InstanceModsViewModel(PreviewSession session,PreviewInstance instance)
    {
        _session=session;Instance=instance;FilteredItems=new ListCollectionView(Items);
        FilteredItems.Filter=o=>o is PreviewModRow m&&new[]{m.Name,m.Description,m.FileName,m.Tags}.Any(s=>s.Contains(Query,StringComparison.OrdinalIgnoreCase));
        EnableSelected=new(async _=>await ChangeAsync(true),_=>CanEdit&&HasSelection);
        DisableSelected=new(async _=>await ChangeAsync(false),_=>CanEdit&&HasSelection);
        ClearSelection=new(_=>{foreach(var item in Items)item.IsSelected=false;});
        RequestRemove=new(_=>ConfirmRemove=true,_=>CanEdit&&HasSelection);CancelRemove=new(_=>ConfirmRemove=false);
        RemoveSelected=new(async _=>await RemoveAsync(),_=>CanEdit&&HasSelection&&ConfirmRemove);
        OpenFolder=new(_=>{try {var directory=Path.Combine(instance.GameDirectory,"mods");Directory.CreateDirectory(directory);RuntimeSupport.OpenFolder(directory);}catch(Exception error){Notice=RuntimeSupport.Error(error);}},_=>SupportsMods);
        InstallFiles=new(async _=>await InstallAsync(),_=>CanEdit&&SupportsMods);
        
        // 带上本实例的游戏版本与加载器，下载页据此自动筛选：直接把“找 Mod”落在兼容范围内。
        DownloadMods=new(_=>{
            session.PendingResourceFilter=new(instance.Version,LoaderKey(instance.Loader));
            session.DownloadTab=2;session.Navigate(Navigation.MainPage.Downloads);
        },_=>SupportsMods);
        Reload=new(async _=>await LoadAsync(),_=>!_busy);
        session.InstanceContentChanged += ContentChanged;
        _ = LoadAsync();
    }
    public async Task LoadAsync()
    {
        if(!SupportsMods||_disposed)return;
        if(_busy){_reloadRequested=true;return;}
        _busy=true;_error="";Refresh();
        var selected = Items.Where(item => item.IsSelected).Select(item => item.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            var options=InstanceStore.Read(Instance);
            var displayStyle=Infrastructure.Configuration.LauncherConfiguration.Text(((App)System.Windows.Application.Current).Configuration.ReadLauncher()["downloads"]?["modManagementStyle"]);
            var origins=options.ResourcesFor(Instance);
            var gameDirectory=Instance.GameDirectory;
            var rows=await Task.Run(()=>
            {
                var directory=Path.Combine(gameDirectory,"mods");if(!Directory.Exists(directory))return Array.Empty<PreviewModRow>();
                return Directory.EnumerateFiles(directory).Where(f=>!File.GetAttributes(f).HasFlag(FileAttributes.ReparsePoint)&&(f.EndsWith(".jar",StringComparison.OrdinalIgnoreCase)||f.EndsWith(".jar.disabled",StringComparison.OrdinalIgnoreCase))).Select(f=>ReadMod(f,options,displayStyle,origins)).ToArray();
            });
            foreach(var item in Items)item.PropertyChanged-=RowChanged;Items.Clear();
            foreach(var row in rows){row.IsSelected=selected.Contains(row.FileName);Items.Add(row);row.PropertyChanged+=RowChanged;}
            Instance.Options=options;Instance.RefreshOptiFineState();_session.Signal();
            FilteredItems.Refresh();
        }
        catch(Exception error){_error=RuntimeSupport.Error(error);}
        finally
        {
            _busy=false;Refresh();CommandManager.InvalidateRequerySuggested();
            if(_reloadRequested&&!_disposed){_reloadRequested=false;_ = LoadAsync();}
        }
    }
    private static PreviewModRow ReadMod(string path,InstanceOptions options,string displayStyle,IReadOnlyDictionary<string,ResourceOrigin> origins)
    {
        var file=Path.GetFileName(path);var enabled=!file.EndsWith(".disabled",StringComparison.OrdinalIgnoreCase);
        var metadata=LocalModMetadata.Read(path);
        var name=metadata.Name;var version=metadata.Version;var loader=metadata.Loader;
        if(options.OptiFineMod is {} opti && (file==opti.FileName||file==opti.FileName+".disabled"))
        {name="OptiFine";version=opti.Edition;loader="Forge";}
        var origin=origins.GetValueOrDefault("mods/"+file)?.Name;
        var display=displayStyle=="仅显示文件名"?file:displayStyle=="标题显示原名，详情显示文件名"?name:origin??name;
        return new(display,file,metadata.Description,"",origin==null?"来源未记录":"Modrinth")
            {FullPath=path,Enabled=enabled,Version=version,Loader=loader,Icon=metadata.Icon,ReadError=metadata.Error};
    }
    private async Task ChangeAsync(bool enabled)
    {
        _editing=true;Instance.IsEditingContent=true;CommandManager.InvalidateRequerySuggested();
        try
        {
            ModpackArchive.EnsureGameDirectoryWritePath(Instance); _ = ModpackArchive.SafePath(Instance.GameDirectory,"mods");
            foreach(var row in Items.Where(i=>i.IsSelected&&i.Enabled!=enabled).ToArray())
            {
                var target=enabled?row.FullPath[..^9]:row.FullPath+".disabled";
                if(File.Exists(target))throw new IOException("目标文件已存在："+Path.GetFileName(target));
                File.Move(row.FullPath,target);
                var options=InstanceStore.Read(Instance);var old="mods/"+row.FileName;
                if(options.ResourcesFor(Instance).Remove(old,out var origin))options.ResourcesFor(Instance)["mods/"+Path.GetFileName(target)]=origin;
                await InstanceStore.SaveAsync(Instance,options);
            }
            await LoadAsync();Notice=enabled?"已启用所选 Mod。":"已禁用所选 Mod。";
        }
        catch(Exception error){Notice=RuntimeSupport.Error(error);await LoadAsync();}
        finally{_editing=false;Instance.IsEditingContent=false;CommandManager.InvalidateRequerySuggested();}
    }
    private async Task InstallAsync()
    {
        var dialog=new OpenFileDialog{Title="选择 Mod 文件",Filter="Java Mod|*.jar",Multiselect=true};if(dialog.ShowDialog()!=true)return;
        _editing=true;Instance.IsEditingContent=true;CommandManager.InvalidateRequerySuggested();
        try
        {
            ModpackArchive.EnsureGameDirectoryWritePath(Instance);
            var directory=ModpackArchive.SafePath(Instance.GameDirectory,"mods");Directory.CreateDirectory(directory);
            foreach(var file in dialog.FileNames)
            {using(var archive=ZipFile.OpenRead(file)){if(archive.Entries.Count==0)throw new InvalidDataException("Mod 文件为空。");}File.Copy(file,ModpackArchive.SafePath(directory,Path.GetFileName(file)),false);}
            await LoadAsync();Notice="文件已复制，请根据 Mod 元数据确认游戏和加载器兼容性。";
        }
        catch(Exception error){Notice=RuntimeSupport.Error(error);await LoadAsync();}
        finally{_editing=false;Instance.IsEditingContent=false;CommandManager.InvalidateRequerySuggested();}
    }
    private async Task RemoveAsync()
    {
        _editing=true;Instance.IsEditingContent=true;CommandManager.InvalidateRequerySuggested();
        try
        {
            ModpackArchive.EnsureGameDirectoryWritePath(Instance); _ = ModpackArchive.SafePath(Instance.GameDirectory,"mods");
            var options=InstanceStore.Read(Instance);
            foreach(var row in Items.Where(i=>i.IsSelected).ToArray())
            {Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(row.FullPath,Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);options.ResourcesFor(Instance).Remove("mods/"+row.FileName);if(options.OptiFineMod?.FileName==row.FileName||options.OptiFineMod?.FileName+".disabled"==row.FileName)options.OptiFineMod=null;}
            await InstanceStore.SaveAsync(Instance,options);ConfirmRemove=false;await LoadAsync();Notice="所选 Mod 已移入回收站。";
        }
        catch(Exception error){Notice=RuntimeSupport.Error(error);await LoadAsync();}
        finally{_editing=false;Instance.IsEditingContent=false;CommandManager.InvalidateRequerySuggested();}
    }
    private void RowChanged(object? sender,PropertyChangedEventArgs e){if(e.PropertyName==nameof(PreviewModRow.IsSelected))ConfirmRemove=false;Refresh();CommandManager.InvalidateRequerySuggested();}
    /// <summary>把实例的加载器名映射成下载页筛选用的键；原版等未知加载器返回空串（不筛加载器）。</summary>
    private static string LoaderKey(string loader)=>loader switch{"Fabric"=>"fabric","Forge"=>"forge","NeoForge"=>"neoforge","Quilt"=>"quilt",_=>""};
    private void ContentChanged(string root,string id)
    {
        if(string.Equals(InstanceStore.Key(root,id),InstanceStore.Key(Instance.Root,Instance.Id),StringComparison.OrdinalIgnoreCase))_ = LoadAsync();
    }
    public void Dispose(){_disposed=true;_session.InstanceContentChanged-=ContentChanged;foreach(var item in Items)item.PropertyChanged-=RowChanged;}
}
public sealed class PreviewModRow(string name,string file,string description,string tags,string origin):ObservableObject
{
    private bool _selected,_enabled=true;
    public string Name {get;}=name;
    public string FileName {get;}=file;
    public bool ShowFileName=>Name!=FileName;
    public string FullPath {get;init;}="";
    public string Description {get;}=description;
    public string Tags {get;}=tags;
    public string Origin {get;}=origin;
    public string Version {get;init;}="版本未提供";
    public string Loader {get;init;}="元数据未提供";
    public System.Windows.Media.ImageSource? Icon {get;init;}
    public string ReadError {get;init;}="";
    public bool IsSelected {get=>_selected;set=>Set(ref _selected,value);}
    public bool Enabled {get=>_enabled;set {if(Set(ref _enabled,value))Changed(nameof(StateText));}}
    public string StateText=>ReadError.Length>0?ReadError:Enabled?"已启用":"已禁用";
}
