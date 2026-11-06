using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Features.Downloads.Models;
using FocusMinecraftLauncher.Infrastructure.Runtime;
using FocusMinecraftLauncher.Preview;
namespace FocusMinecraftLauncher.Features.Home.ViewModels.Instances;

/// <summary>实例内的 Mod、资源包与光影包共用的文件列表；只从归档元数据展示版本，不根据文件名声明兼容性。</summary>
public sealed class InstanceContentViewModel : ObservableObject, IDisposable
{
    private readonly PreviewSession _session;
    private readonly InstanceContentKind _kind;
    private string _query="",_error="",_notice="",_updateSummary="";
    private bool _confirm,_busy,_editing,_reloadRequested,_disposed;
    private CancellationTokenSource? _detailsLookup;
    private CancellationTokenSource? _iconLookup;
    private bool _openingDetails,_checking,_autoChecked;
    private bool CanEdit=>!_busy&&!_editing&&!_session.IsApplyingIsolation&&!_session.Tasks.Any(t=>t.IsActive&&t.Root==Instance.Root&&(!Instance.UsesGameIsolation||t.InstanceId==Instance.Id));
    public PreviewInstance Instance { get; }
    /// <summary>当前页面管理的内容类型；决定目录、扩展名与可用的操作。</summary>
    public InstanceContentKind Kind=>_kind;
    public string Label=>_kind switch{InstanceContentKind.ResourcePack=>"资源包",InstanceContentKind.Shader=>"光影包",_=>"Mod"};
    public ObservableCollection<PreviewModRow> Items { get; }=[];
    public ICollectionView FilteredItems { get; }
    public bool SupportsMods=>Instance.SupportsMods;
    /// <summary>资源包与光影包不依赖加载器，任何游戏都能管理。</summary>
    public bool SupportsKind=>_kind!=InstanceContentKind.Mod||Instance.SupportsMods;
    /// <summary>只有 Mod 能通过改扩展名停用，Minecraft 的资源包与光影包没有这个约定。</summary>
    public bool SupportsToggle=>_kind==InstanceContentKind.Mod;
    public bool CanDownload=>SupportsToggle;
    private string FolderName=>_kind switch{InstanceContentKind.ResourcePack=>"resourcepacks",InstanceContentKind.Shader=>"shaderpacks",_=>"mods"};
    private string FileExtension=>SupportsToggle?".jar":".zip";
    public bool IsLoading=>_busy;
    public bool ShowList=>!IsLoading&&!FilteredItems.IsEmpty;
    public bool HasSelection=>SelectedCount>0;
    public int SelectedCount=>Items.Count(m=>m.IsSelected);
    public string SelectionText=>$"已选 {SelectedCount} 项";
    public string Notice {get=>_notice;private set=>Set(ref _notice,value); }
    /// <summary>提示拖放安装入口；管理员身份运行时 Windows 会拦截来自资源管理器的拖放。</summary>
    public string DropNotice=>!SupportsKind?"":RuntimeSupport.IsElevated?"管理员身份运行时系统会拦截拖放，请改用「从文件安装」。":"也可以把 "+FileExtension+" 文件直接拖到本页安装。";
    public string StateText=>!SupportsKind?"当前游戏未配置 Mod 加载器。":_busy?"正在读取 "+Label:_error.Length>0?_error:FilteredItems.IsEmpty?Items.Count==0?"尚未安装 "+Label:"没有匹配的 "+Label:"";
    public string Query {get=>_query;set {if(Set(ref _query,value)){FilteredItems.Refresh();Refresh();}} }
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
    public UiCommand OpenModDetails {get;}
    public bool IsOpeningDetails
    {get=>_openingDetails;private set{if(Set(ref _openingDetails,value))CommandManager.InvalidateRequerySuggested();}}
    public UiCommand Reload {get;}
    public UiCommand CheckUpdates {get;}
    public UiCommand UpdateSelected {get;}
    public bool IsCheckingUpdates {get=>_checking;private set{if(!Set(ref _checking,value))return;Changed(nameof(IsIdle));CommandManager.InvalidateRequerySuggested();}}
    public bool IsIdle=>!_busy&&!_checking;
    /// <summary>上次检查更新的结论，空串表示还没检查过。</summary>
    public string UpdateSummary {get=>_updateSummary;private set=>Set(ref _updateSummary,value);}
    public InstanceContentViewModel(PreviewSession session,PreviewInstance instance,InstanceContentKind kind)
    {
        _session=session;Instance=instance;_kind=kind;FilteredItems=new ListCollectionView(Items);
        FilteredItems.Filter=o=>o is PreviewModRow m&&new[]{m.Name,m.Description,m.FileName,m.Tags}.Any(s=>s.Contains(Query,StringComparison.OrdinalIgnoreCase));
        EnableSelected=new(async _=>await ChangeAsync(true),_=>CanEdit&&HasSelection);
        DisableSelected=new(async _=>await ChangeAsync(false),_=>CanEdit&&HasSelection);
        ClearSelection=new(_=>{foreach(var item in Items)item.IsSelected=false;});
        RequestRemove=new(_=>ConfirmRemove=true,_=>CanEdit&&HasSelection);CancelRemove=new(_=>ConfirmRemove=false);
        RemoveSelected=new(async _=>await RemoveAsync(),_=>CanEdit&&HasSelection&&ConfirmRemove);
        OpenFolder=new(_=>{try {var directory=Path.Combine(instance.GameDirectory,FolderName);Directory.CreateDirectory(directory);RuntimeSupport.OpenFolder(directory);}catch(Exception error){Notice=RuntimeSupport.Error(error);}},_=>SupportsKind);
        InstallFiles=new(async _=>await InstallAsync(),_=>CanEdit&&SupportsKind);
        OpenModDetails=new(async value=>{if(value is PreviewModRow row)await OpenDetailsAsync(row);},
            value=>!_disposed&&!_busy&&!_editing&&!IsOpeningDetails&&value is PreviewModRow row&&Items.Contains(row));

        // 带上本实例的游戏版本与加载器，下载页据此自动筛选：直接把“找 Mod”落在兼容范围内。
        DownloadMods=new(_=>{
            session.PendingResourceFilter=new(instance.Version,LoaderKey(instance.Loader));
            session.DownloadTab=2;session.Navigate(Navigation.MainPage.Downloads);
        },_=>CanDownload&&SupportsMods);
        Reload=new(async _=>await LoadAsync(),_=>!_busy);
        CheckUpdates=new(async _=>await CheckUpdatesAsync(),_=>SupportsKind&&IsIdle&&Items.Any(item=>item.CanCheckUpdate));
        UpdateSelected=new(async _=>await UpdateAsync(),_=>SupportsKind&&IsIdle&&Items.Any(item=>item.IsSelected&&item.Update?.HasUpdate==true));
        session.InstanceContentChanged += ContentChanged;
        _ = LoadAsync();
    }
    /// <summary>按已记录来源或文件内容查找项目，在现有下载页打开详情并携带当前游戏版本与加载器。</summary>
    private async Task OpenDetailsAsync(PreviewModRow row)
    {
        using var lookup=new CancellationTokenSource();_detailsLookup=lookup;IsOpeningDetails=true;
        Notice="正在查找 "+row.Name+" 的资源详情…";
        try
        {
            var projectId=row.ProjectId.Length>0?row.ProjectId:row.Update?.ProjectId;
            if(string.IsNullOrWhiteSpace(projectId))projectId=await ModUpdateService.FindProjectAsync(row.FullPath,lookup.Token);
            lookup.Token.ThrowIfCancellationRequested();
            if(_disposed||!Items.Contains(row))return;
            if(string.IsNullOrWhiteSpace(projectId)){Notice="未找到这个 "+Label+" 的在线资源记录，无法打开详情。";return;}
            row.ProjectId=projectId;Notice="";
            _session.PendingResourceDetail=new(projectId,new(Instance.Version,LoaderKey(Instance.Loader)),row.Name);
            _session.Navigate(Navigation.MainPage.Downloads);
        }
        catch(OperationCanceledException)when(lookup.IsCancellationRequested){}
        catch(Exception error){if(!_disposed){Notice="无法打开资源详情："+ResourceError(error);_session.Log("查找资源项目失败 ["+CommunitySource.Current.Name+"; UA="+CommunitySource.UserAgent+"]："+RuntimeSupport.SafeText(error.ToString()));}}
        finally{if(ReferenceEquals(_detailsLookup,lookup))_detailsLookup=null;IsOpeningDetails=false;}
    }
    /// <summary>离开本页时取消来源查询，防止请求完成后抢回导航。</summary>
    public void CancelOpenDetails(){_detailsLookup?.Cancel();if(IsOpeningDetails)Notice="";}
    public async Task LoadAsync()
    {
        if(!SupportsKind||_disposed)return;
        if(_busy){_reloadRequested=true;return;}
        CancelOpenDetails();
        _iconLookup?.Cancel();
        _busy=true;_error="";Refresh();
        var selected = Items.Where(item => item.IsSelected).Select(item => item.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            var options=InstanceStore.Read(Instance);
            var displayStyle=Infrastructure.Configuration.LauncherConfiguration.Text(((App)System.Windows.Application.Current).Configuration.ReadLauncher()["downloads"]?["modManagementStyle"]);
            var origins=options.ResourcesFor(Instance);
            var gameDirectory=Instance.GameDirectory;
            var kind=_kind;
            var folder=FolderName;
            var extension=FileExtension;
            var rows=await Task.Run(()=>
            {
                var directory=Path.Combine(gameDirectory,folder);if(!Directory.Exists(directory))return Array.Empty<PreviewModRow>();
                var matches=kind==InstanceContentKind.Mod
                    ?new Func<string,bool>(f=>f.EndsWith(extension,StringComparison.OrdinalIgnoreCase)||f.EndsWith(extension+".disabled",StringComparison.OrdinalIgnoreCase))
                    :f=>f.EndsWith(extension,StringComparison.OrdinalIgnoreCase);
                return Directory.EnumerateFiles(directory).Where(f=>!File.GetAttributes(f).HasFlag(FileAttributes.ReparsePoint)&&matches(f)).Select(f=>ReadRow(f,options,displayStyle,origins,kind)).ToArray();
            });
            foreach(var item in Items)item.PropertyChanged-=RowChanged;Items.Clear();
            foreach(var row in rows){row.IsSelected=selected.Contains(row.FileName);Items.Add(row);row.PropertyChanged+=RowChanged;}
            Instance.Options=options;Instance.RefreshOptiFineState();_session.Signal();
            FilteredItems.Refresh();
            _ = LoadIconsAsync();
        }
        catch(Exception error){_error=RuntimeSupport.Error(error);}
        finally
        {
            _busy=false;Refresh();CommandManager.InvalidateRequerySuggested();
            if(_reloadRequested&&!_disposed){_reloadRequested=false;_ = LoadAsync();}
            // 进入页面时自动查一次更新，省掉用户点按钮。
            else if(SupportsKind&&!_autoChecked&&!_disposed&&Items.Any(item=>item.CanCheckUpdate)){_autoChecked=true;_ = CheckUpdatesAsync();}
        }
    }
    private static PreviewModRow ReadRow(string path,InstanceOptions options,string displayStyle,IReadOnlyDictionary<string,ResourceOrigin> origins,InstanceContentKind kind)
    {
        var file=Path.GetFileName(path);var enabled=!file.EndsWith(".disabled",StringComparison.OrdinalIgnoreCase);
        if(kind!=InstanceContentKind.Mod)
        {
            // 资源包与光影包没有统一元数据，展示压缩包名和包内的 pack.png。
            return new(Path.GetFileNameWithoutExtension(file),file,"","","")
            {FullPath=path,Enabled=enabled,Version="",Loader="",CanOpenDetails=false,Icon=ReadPackIcon(path)};
        }
        var metadata=LocalModMetadata.Read(path);
        var name=metadata.Name;var version=metadata.Version;var loader=metadata.Loader;
        if(options.OptiFineMod is {} opti && (file==opti.FileName||file==opti.FileName+".disabled"))
        {name="OptiFine";version=opti.Edition;loader="Forge";}
        var resource=origins.GetValueOrDefault("mods/"+file)??(!enabled?origins.GetValueOrDefault("mods/"+file[..^9]):null);
        var origin=resource?.Name;
        var display=displayStyle=="仅显示文件名"?file:displayStyle=="标题显示原名，详情显示文件名"?name:origin??name;
        return new(display,file,metadata.Description,"",resource==null?"来源未记录":resource.ProjectId.StartsWith("cf-",StringComparison.Ordinal)?"CurseForge":"Modrinth")
            {FullPath=path,Enabled=enabled,Version=version,Loader=loader,Icon=metadata.Icon,ReadError=metadata.Error,ProjectId=resource?.ProjectId??""};
    }
    /// <summary>读取资源包或光影包压缩包内的 pack.png；没有图标时返回空，界面显示占位图形。</summary>
    private static ImageSource? ReadPackIcon(string path)
    {
        try
        {
            using var archive=ZipFile.OpenRead(path);
            var entry=archive.Entries.FirstOrDefault(entry=>entry.FullName.Equals("pack.png",StringComparison.OrdinalIgnoreCase));
            if(entry is null||entry.Length==0)return null;
            using var stream=entry.Open();
            var image=new BitmapImage();
            image.BeginInit();
            image.CacheOption=BitmapCacheOption.OnLoad;
            image.DecodePixelWidth=64;
            image.StreamSource=stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch(Exception error) when(error is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException){return null;}
    }
    private async Task ChangeAsync(bool enabled)
    {
        if(!SupportsToggle){Notice="只有 Mod 支持启用/禁用。";return;}
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
        if(files.Length==0){Notice="只支持拖入 "+extension+" 格式的"+Label+"文件。";return;}
        await InstallAsync(files);
    }
    private async Task InstallAsync(string[]? dropped=null)
    {
        var files=dropped;
        if(files==null)
        {
            var filter=SupportsToggle?"Java Mod|*.jar":"压缩包|*.zip";
            var dialog=new OpenFileDialog{Title="选择"+Label+"文件",Filter=filter,Multiselect=true};if(dialog.ShowDialog()!=true)return;files=dialog.FileNames;
        }
        _editing=true;Instance.IsEditingContent=true;CommandManager.InvalidateRequerySuggested();
        try
        {
            ModpackArchive.EnsureGameDirectoryWritePath(Instance);
            var directory=ModpackArchive.SafePath(Instance.GameDirectory,FolderName);Directory.CreateDirectory(directory);
            foreach(var file in files)
            {using(var archive=ZipFile.OpenRead(file)){if(archive.Entries.Count==0)throw new InvalidDataException(Label+" 文件为空。");}File.Copy(file,ModpackArchive.SafePath(directory,Path.GetFileName(file)),false);}
            await LoadAsync();
            Notice=dropped==null?"文件已复制，请根据元数据确认游戏和加载器兼容性。":$"已安装 {files.Length} 个{Label}文件，请确认与当前游戏版本兼容。";
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
            {Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(row.FullPath,Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);if(SupportsToggle){options.ResourcesFor(Instance).Remove("mods/"+row.FileName);if(options.OptiFineMod?.FileName==row.FileName||options.OptiFineMod?.FileName+".disabled"==row.FileName)options.OptiFineMod=null;}}
            await InstanceStore.SaveAsync(Instance,options);ConfirmRemove=false;await LoadAsync();Notice="所选文件已移入回收站。";
        }
        catch(Exception error){Notice=RuntimeSupport.Error(error);await LoadAsync();}
        finally{_editing=false;Instance.IsEditingContent=false;CommandManager.InvalidateRequerySuggested();}
    }
    /// <summary>压缩包内没有预览图时（光影包基本都没有），按文件内容去 Modrinth 找项目图标；结果按文件版本缓存。</summary>
    private async Task LoadIconsAsync()
    {
        var pending = Items.Where(item => item.Icon is null && item.FullPath.Length > 0).ToArray();
        if (pending.Length == 0) return;
        var lookup = new CancellationTokenSource();
        _iconLookup = lookup;
        try
        {
            var missing = new List<string>();
            foreach (var item in pending)
            {
                if (PackIconCache.Get(item.FullPath) is { } cached) item.Icon = cached;
                else missing.Add(item.FullPath);
            }
            if (missing.Count == 0) return;

            var addresses = await ModUpdateService.FindIconsAsync(missing, lookup.Token);
            lookup.Token.ThrowIfCancellationRequested();
            foreach (var item in pending)
            {
                if (_disposed || !Items.Contains(item) || item.Icon is not null) continue;
                if (!addresses.TryGetValue(item.FileName, out var address)) continue;
                var image = await RemoteArtwork.LoadAsync(address, 64, lookup.Token);
                if (image is null) continue;
                PackIconCache.Set(item.FullPath, image);
                item.Icon = image;
            }
        }
        catch (OperationCanceledException) when (lookup.IsCancellationRequested) { }
        catch (Exception error) { _session.Log("读取资源封面失败：" + RuntimeSupport.Error(error)); }
        finally
        {
            if (ReferenceEquals(_iconLookup, lookup)) _iconLookup = null;
            lookup.Dispose();
        }
    }
    private void RowChanged(object? sender,PropertyChangedEventArgs e){if(e.PropertyName==nameof(PreviewModRow.IsSelected))ConfirmRemove=false;Refresh();CommandManager.InvalidateRequerySuggested();}
    /// <summary>把实例的加载器名映射成下载页筛选用的键；原版等未知加载器返回空串（不筛加载器）。</summary>
    private static string LoaderKey(string loader)=>loader switch{"Fabric"=>"fabric","Forge"=>"forge","NeoForge"=>"neoforge","Quilt"=>"quilt",_=>""};
    private void ContentChanged(string root,string id)
    {
        if(string.Equals(InstanceStore.Key(root,id),InstanceStore.Key(Instance.Root,Instance.Id),StringComparison.OrdinalIgnoreCase))_ = LoadAsync();
    }
    /// <summary>把本地文件映射到 Modrinth 项目，再查同一项目在当前游戏版本下有没有更新。</summary>
    private async Task CheckUpdatesAsync()
    {
        if(_checking||!SupportsKind)return;
        IsCheckingUpdates=true;Notice="";UpdateSummary="";
        foreach(var row in Items){row.Update=null;row.UpdateText="";}
        try
        {
            var progress=new Progress<string>(text=>UpdateSummary=text);
            var updates=await ModUpdateService.CheckAsync(Instance,FolderName,FileExtension,Label,progress,CancellationToken.None);
            var map=updates.GroupBy(update=>update.FileName,StringComparer.OrdinalIgnoreCase).ToDictionary(group=>group.Key,group=>group.First(),StringComparer.OrdinalIgnoreCase);
            var available=0;
            foreach(var row in Items)
            {
                if(!map.TryGetValue(row.FileName,out var update)){row.Update=null;row.UpdateText="";continue;}
                row.Update=update;
                if(!update.HasUpdate)
                {row.UpdateText=update.HasPublicationDates||update.LatestVersionId==update.InstalledVersionId?"未发现较新兼容版本":"缺少发布时间，暂时无法确认更新。";continue;}
                row.UpdateText=$"可更新 {row.Version} → {update.LatestVersion}";available++;
            }
            UpdateSummary=updates.Count==0
                ?"未匹配到可查询的在线资源记录。"
                :available==0
                    ?"未发现已确认的兼容更新。"
                    :$"{available} 个{Label}有更新：勾选后点「更新所选」直接替换。";
        }
        catch(OperationCanceledException){UpdateSummary="已取消检查更新。";}
        catch(Exception error){UpdateSummary="检查更新未完成："+ResourceError(error);_session.Log("检查资源更新失败 ["+CommunitySource.Current.Name+"; UA="+CommunitySource.UserAgent+"]："+RuntimeSupport.SafeText(error.ToString()));}
        finally{IsCheckingUpdates=false;CommandManager.InvalidateRequerySuggested();}
    }
    private static string ResourceError(Exception error)=>error.GetBaseException() is System.Net.Http.HttpRequestException{StatusCode:System.Net.HttpStatusCode.Forbidden}
        ?"资源服务拒绝了请求，请稍后重试。":RuntimeSupport.Error(error);
    /// <summary>下载并替换所选文件的旧版本，完成后重新读取列表。</summary>
    private async Task UpdateAsync()
    {
        var updates=Items.Where(item=>item.IsSelected&&item.Update?.HasUpdate==true).Select(item=>item.Update!).ToArray();
        if(updates.Length==0)return;
        _editing=true;Instance.IsEditingContent=true;CommandManager.InvalidateRequerySuggested();
        try
        {
            ModpackArchive.EnsureGameDirectoryWritePath(Instance);
            var progress=new Progress<string>(text=>UpdateSummary=text);
            var applied=await ModUpdateService.ApplyAsync(Instance,FolderName,updates,progress,CancellationToken.None);
            await LoadAsync();
            UpdateSummary=$"已更新 {applied} 个{Label}。";
        }
        catch(Exception error){UpdateSummary=RuntimeSupport.Error(error);await LoadAsync();}
        finally{_editing=false;Instance.IsEditingContent=false;CommandManager.InvalidateRequerySuggested();}
    }
    public void Dispose(){_disposed=true;CancelOpenDetails();_iconLookup?.Cancel();_session.InstanceContentChanged-=ContentChanged;foreach(var item in Items)item.PropertyChanged-=RowChanged;}
}
/// <summary>实例内可管理的内容类型；目录、扩展名与操作范围都由它决定。</summary>
public enum InstanceContentKind{Mod,ResourcePack,Shader}

/// <summary>按文件路径缓存资源包与光影包的封面，文件改动后自动失效。</summary>
internal static class PackIconCache
{
    private static readonly Dictionary<string,(long Ticks,long Length,ImageSource Image)> Cached=new(StringComparer.OrdinalIgnoreCase);
    public static ImageSource? Get(string path)
    {
        try
        {
            var info=new FileInfo(path);
            if(!Cached.TryGetValue(path,out var entry))return null;
            if(entry.Ticks!=info.LastWriteTimeUtc.Ticks||entry.Length!=info.Length)return null;
            return entry.Image;
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException){return null;}
    }
    public static void Set(string path,ImageSource image)
    {
        try
        {
            var info=new FileInfo(path);
            if(Cached.Count>=200)Cached.Clear();
            Cached[path]=(info.LastWriteTimeUtc.Ticks,info.Length,image);
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException){}
    }
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
    public bool CanOpenDetails {get;init;}=true;
    /// <summary>是否参与在线来源匹配；资源包与光影包也能查更新，只是不提供详情入口。</summary>
    public bool CanCheckUpdate {get;init;}=true;
    public string ProjectId {get;set;}="";
    public string Version {get;init;}="版本未提供";
    public string Loader {get;init;}="元数据未提供";
    private System.Windows.Media.ImageSource? _icon;
    /// <summary>界面图标；压缩包里没有预览图时，会退回到它在 Modrinth 上的项目图标。</summary>
    public System.Windows.Media.ImageSource? Icon {get=>_icon;set=>Set(ref _icon,value);}
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
