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
    private string _query="",_error="",_notice="",_kind="Mod",_updateSummary="";
    private bool _confirm,_busy,_editing,_reloadRequested,_disposed;
    private bool CanEdit=>!_busy&&!_editing&&!_session.IsApplyingIsolation&&!_session.Tasks.Any(t=>t.IsActive&&t.Root==Instance.Root&&(!Instance.UsesGameIsolation||t.InstanceId==Instance.Id));
    public PreviewInstance Instance { get; }
    public IReadOnlyList<string> Kinds { get; }=["Mod","资源包","光影包"];
    /// <summary>当前查看的内容类型；资源包与光影包复用同一套列表与文件操作。</summary>
    public string Kind
    {
        get=>_kind;
        set { if(!Set(ref _kind,value))return; Changed(nameof(IsModKind)); ResetForKind(); }
    }
    public bool IsModKind=>_kind=="Mod";
    public ObservableCollection<PreviewModRow> Items { get; }=[];
    public ICollectionView FilteredItems { get; }
    public bool SupportsMods=>Instance.SupportsMods;
    /// <summary>资源包与光影包不依赖加载器，任何游戏都能管理。</summary>
    public bool SupportsKind=>IsModKind?Instance.SupportsMods:true;
    private string FolderName=>_kind switch{"资源包"=>"resourcepacks","光影包"=>"shaderpacks",_=>"mods"};
    private string FileExtension=>IsModKind?".jar":".zip";
    public bool IsLoading=>_busy;
    public bool ShowList=>!IsLoading&&!FilteredItems.IsEmpty;
    public bool HasSelection=>SelectedCount>0;
    public int SelectedCount=>Items.Count(m=>m.IsSelected);
    public string SelectionText=>$"已选 {SelectedCount} 项";
    public string Notice { get=>_notice;private set=>Set(ref _notice,value); }
    /// <summary>提示拖放安装入口；管理员身份运行时 Windows 会拦截来自资源管理器的拖放。</summary>
    public string DropNotice=>!SupportsKind?"":RuntimeSupport.IsElevated?"管理员身份运行时系统会拦截拖放，请改用「从文件安装」。":"也可以把 "+FileExtension+" 文件直接拖到本页安装。";
    public string StateText=>!SupportsKind?"当前游戏未配置 Mod 加载器。":_busy?"正在读取 "+_kind:_error.Length>0?_error:FilteredItems.IsEmpty?Items.Count==0?"尚未安装 "+_kind:"没有匹配的 "+_kind:"";
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
    public UiCommand CheckUpdates {get;}
    public UiCommand UpdateSelected {get;}
    private bool _checking,_autoChecked;
    public bool IsCheckingUpdates {get=>_checking;private set{if(!Set(ref _checking,value))return;Changed(nameof(IsIdle));CommandManager.InvalidateRequerySuggested();}}
    public bool IsIdle=>!_busy&&!_checking;
    /// <summary>上次检查更新的结论，空串表示还没检查过。</summary>
    public string UpdateSummary {get=>_updateSummary;private set=>Set(ref _updateSummary,value);}
    public InstanceModsViewModel(PreviewSession session,PreviewInstance instance)
    {
        _session=session;Instance=instance;FilteredItems=new ListCollectionView(Items);
        FilteredItems.Filter=o=>o is PreviewModRow m&&new[]{m.Name,m.Description,m.FileName,m.Tags}.Any(s=>s.Contains(Query,StringComparison.OrdinalIgnoreCase));
        EnableSelected=new(async _=>await ChangeAsync(true),_=>CanEdit&&HasSelection);
        DisableSelected=new(async _=>await ChangeAsync(false),_=>CanEdit&&HasSelection);
        ClearSelection=new(_=>{foreach(var item in Items)item.IsSelected=false;});
        RequestRemove=new(_=>ConfirmRemove=true,_=>CanEdit&&HasSelection);CancelRemove=new(_=>ConfirmRemove=false);
        RemoveSelected=new(async _=>await RemoveAsync(),_=>CanEdit&&HasSelection&&ConfirmRemove);
        OpenFolder=new(_=>{try {var directory=Path.Combine(instance.GameDirectory,FolderName);Directory.CreateDirectory(directory);RuntimeSupport.OpenFolder(directory);}catch(Exception error){Notice=RuntimeSupport.Error(error);}},_=>SupportsKind);
        InstallFiles=new(async _=>await InstallAsync(),_=>CanEdit&&SupportsKind);
        
        // 带上本实例的游戏版本与加载器，下载页据此自动筛选：直接把“找 Mod”落在兼容范围内。
        DownloadMods=new(_=>{
            session.PendingResourceFilter=new(instance.Version,LoaderKey(instance.Loader));
            session.DownloadTab=2;session.Navigate(Navigation.MainPage.Downloads);
        },_=>IsModKind&&SupportsMods);
        Reload=new(async _=>await LoadAsync(),_=>!_busy);
        CheckUpdates=new(async _=>await CheckUpdatesAsync(),_=>IsModKind&&SupportsMods&&IsIdle);
        UpdateSelected=new(async _=>await UpdateAsync(),_=>IsModKind&&SupportsMods&&IsIdle&&Items.Any(item=>item.IsSelected&&item.Update?.HasUpdate==true));
        session.InstanceContentChanged += ContentChanged;
        _ = LoadAsync();
    }
    public async Task LoadAsync()
    {
        if(!SupportsKind||_disposed)return;
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
                var directory=Path.Combine(gameDirectory,FolderName);if(!Directory.Exists(directory))return Array.Empty<PreviewModRow>();
                var extension=FileExtension;
                return Directory.EnumerateFiles(directory).Where(f=>!File.GetAttributes(f).HasFlag(FileAttributes.ReparsePoint)&&(f.EndsWith(extension,StringComparison.OrdinalIgnoreCase)||f.EndsWith(extension+".disabled",StringComparison.OrdinalIgnoreCase))).Select(f=>ReadMod(f,options,displayStyle,origins,_kind)).ToArray();
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
            // 进入 Mod 管理页时自动查一次更新，省掉用户点按钮。
            else if(IsModKind&&SupportsMods&&!_autoChecked&&!_disposed&&Items.Count>0){_autoChecked=true;_ = CheckUpdatesAsync();}
        }
    }
    private static PreviewModRow ReadMod(string path,InstanceOptions options,string displayStyle,IReadOnlyDictionary<string,ResourceOrigin> origins,string kind)
    {
        var file=Path.GetFileName(path);var enabled=!file.EndsWith(".disabled",StringComparison.OrdinalIgnoreCase);
        if(kind!="Mod")
        {
            // 资源包与光影包没有统一元数据，展示压缩包名即可。
            return new(Path.GetFileNameWithoutExtension(file),file,"","",""){FullPath=path,Enabled=enabled,Version="",Loader=""};
        }
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
        if(!IsModKind){Notice="只有 Mod 支持启用/禁用。";return;}
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
    /// <summary>拖拽进来的文件：按当前内容类型校验扩展名，其余忽略。</summary>
    public async Task InstallDroppedAsync(IEnumerable<string> paths)
    {
        if(!SupportsKind){Notice="当前游戏未配置 Mod 加载器。";return;}
        if(!CanEdit){Notice="当前有正在进行的任务，稍后再安装。";return;}
        var extension=FileExtension;
        var files=paths.Where(path=>path.EndsWith(extension,StringComparison.OrdinalIgnoreCase)&&File.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(files.Length==0){Notice="只支持拖入 "+extension+" 格式的"+_kind+"文件。";return;}
        await InstallAsync(files);
    }
    private async Task InstallAsync(string[]? dropped=null)
    {
        var files=dropped;
        if(files==null)
        {
            var filter=IsModKind?"Java Mod|*.jar":"压缩包|*.zip";
            var dialog=new OpenFileDialog{Title="选择"+_kind+"文件",Filter=filter,Multiselect=true};if(dialog.ShowDialog()!=true)return;files=dialog.FileNames;
        }
        _editing=true;Instance.IsEditingContent=true;CommandManager.InvalidateRequerySuggested();
        try
        {
            ModpackArchive.EnsureGameDirectoryWritePath(Instance);
            var directory=ModpackArchive.SafePath(Instance.GameDirectory,FolderName);Directory.CreateDirectory(directory);
            foreach(var file in files)
            {using(var archive=ZipFile.OpenRead(file)){if(archive.Entries.Count==0)throw new InvalidDataException(_kind+" 文件为空。");}File.Copy(file,ModpackArchive.SafePath(directory,Path.GetFileName(file)),false);}
            await LoadAsync();
            Notice=dropped==null?"文件已复制，请根据元数据确认游戏和加载器兼容性。":$"已安装 {files.Length} 个{_kind}文件，请确认与当前游戏版本兼容。";
        }
        catch(Exception error){Notice=RuntimeSupport.Error(error);await LoadAsync();}
        finally{_editing=false;Instance.IsEditingContent=false;CommandManager.InvalidateRequerySuggested();}
    }
    private async Task RemoveAsync()
    {
        _editing=true;Instance.IsEditingContent=true;CommandManager.InvalidateRequerySuggested();
        try
        {
            ModpackArchive.EnsureGameDirectoryWritePath(Instance); _ = ModpackArchive.SafePath(Instance.GameDirectory,FolderName);
            var options=InstanceStore.Read(Instance);
            foreach(var row in Items.Where(i=>i.IsSelected).ToArray())
            {Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(row.FullPath,Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);if(IsModKind){options.ResourcesFor(Instance).Remove("mods/"+row.FileName);if(options.OptiFineMod?.FileName==row.FileName||options.OptiFineMod?.FileName+".disabled"==row.FileName)options.OptiFineMod=null;}}
            await InstanceStore.SaveAsync(Instance,options);ConfirmRemove=false;await LoadAsync();Notice="所选文件已移入回收站。";
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
    /// <summary>把本地 jar 映射到 Modrinth 版本，再查同一项目在当前游戏版本与加载器下有没有更新。</summary>
    private async Task CheckUpdatesAsync()
    {
        if(_checking||!IsModKind)return;
        IsCheckingUpdates=true;Notice="";UpdateSummary="";
        try
        {
            var progress=new Progress<string>(text=>UpdateSummary=text);
            var updates=await ModUpdateService.CheckAsync(Instance,progress,CancellationToken.None);
            var map=updates.GroupBy(update=>update.FileName,StringComparer.OrdinalIgnoreCase).ToDictionary(group=>group.Key,group=>group.First(),StringComparer.OrdinalIgnoreCase);
            var available=0;
            foreach(var row in Items)
            {
                if(!map.TryGetValue(row.FileName,out var update)){row.Update=null;row.UpdateText="";continue;}
                row.Update=update;
                if(!update.HasUpdate){row.UpdateText="已是最新 "+update.LatestVersion;continue;}
                row.UpdateText=$"可更新 {row.Version} → {update.LatestVersion}";available++;
            }
            UpdateSummary=updates.Count==0
                ?"这些文件在 Modrinth 上没有记录（可能来自 CurseForge 或本地安装）。"
                :available==0
                    ?"全部已是最新。"
                    :$"{available} 个 Mod 有更新：勾选后点「更新所选」直接替换。";
        }
        catch(OperationCanceledException){UpdateSummary="已取消检查更新。";}
        catch(Exception error){UpdateSummary=RuntimeSupport.Error(error);}
        finally{IsCheckingUpdates=false;CommandManager.InvalidateRequerySuggested();}
    }
    /// <summary>下载并替换所选 Mod 的旧文件，完成后重新读取列表。</summary>
    private async Task UpdateAsync()
    {
        var updates=Items.Where(item=>item.IsSelected&&item.Update?.HasUpdate==true).Select(item=>item.Update!).ToArray();
        if(updates.Length==0)return;
        _editing=true;Instance.IsEditingContent=true;CommandManager.InvalidateRequerySuggested();
        try
        {
            ModpackArchive.EnsureGameDirectoryWritePath(Instance);
            var progress=new Progress<string>(text=>UpdateSummary=text);
            var applied=await ModUpdateService.ApplyAsync(Instance,updates,progress,CancellationToken.None);
            await LoadAsync();
            UpdateSummary=$"已更新 {applied} 个 Mod。";
        }
        catch(Exception error){UpdateSummary=RuntimeSupport.Error(error);await LoadAsync();}
        finally{_editing=false;Instance.IsEditingContent=false;CommandManager.InvalidateRequerySuggested();}
    }
    private void ResetForKind()
    {
        ConfirmRemove=false;Notice="";_error="";_autoChecked=false;
        Changed(nameof(DropNotice));
        foreach(var item in Items)item.PropertyChanged-=RowChanged;
        Items.Clear();FilteredItems.Refresh();Refresh();
        _ = LoadAsync();
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
    private string _updateText="";
    private ModUpdateService.ModUpdate? _update;
    /// <summary>检查更新后的一句话结论，空串表示还没检查或未匹配到。</summary>
    public string UpdateText {get=>_updateText;set=>Set(ref _updateText,value);}
    /// <summary>检查到的更新信息，勾选本行后可直接下载替换。</summary>
    public ModUpdateService.ModUpdate? Update {get=>_update;set=>Set(ref _update,value);}
}
