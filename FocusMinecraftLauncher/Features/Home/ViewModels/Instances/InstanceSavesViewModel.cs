using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Infrastructure.Runtime;
using FocusMinecraftLauncher.Preview;
namespace FocusMinecraftLauncher.Features.Home.ViewModels.Instances;

/// <summary>读取实例 saves 目录下的存档；图标取自每个存档自己的 icon.png，与实际存档画面一致。</summary>
public sealed class InstanceSavesViewModel : ObservableObject, IDisposable
{
    private readonly PreviewSession _session;
    private string _notice="",_query="";
    private bool _busy,_confirm,_disposed;
    public PreviewInstance Instance { get; }
    public string SavesDirectory=>Path.Combine(Instance.GameDirectory,"saves");
    public ObservableCollection<PreviewSaveRow> Items { get; }=[];
    public ICollectionView FilteredItems { get; }
    public bool IsLoading=>_busy;
    public bool ShowList=>!IsLoading&&!FilteredItems.IsEmpty;
    public bool HasSelection=>SelectedCount>0;
    public int SelectedCount=>Items.Count(item=>item.IsSelected);
    public string SelectionText=>$"已选 {SelectedCount} 个存档";
    public string Notice {get=>_notice;private set=>Set(ref _notice,value);}
    public bool ConfirmRemove {get=>_confirm;set=>Set(ref _confirm,value);}
    public string StateText=>_busy?"正在读取存档":FilteredItems.IsEmpty?Items.Count==0?"还没有存档":"没有匹配的存档":"";
    public string Query {get=>_query;set {if(Set(ref _query,value)){FilteredItems.Refresh();Refresh();}} }
    public bool AllVisibleSelected {get=>!FilteredItems.IsEmpty&&FilteredItems.Cast<PreviewSaveRow>().All(item=>item.IsSelected);set {foreach(var item in FilteredItems.Cast<PreviewSaveRow>().ToArray())item.IsSelected=value;Refresh();}}
    public UiCommand Reload {get;}
    public UiCommand OpenFolder {get;}
    public UiCommand RequestRemove {get;}
    public UiCommand CancelRemove {get;}
    public UiCommand RemoveSelected {get;}
    public UiCommand ClearSelection {get;}
    private bool CanEdit=>!_busy&&!_session.Tasks.Any(task=>task.IsActive&&task.Root==Instance.Root&&(!Instance.UsesGameIsolation||task.InstanceId==Instance.Id));
    public InstanceSavesViewModel(PreviewSession session,PreviewInstance instance)
    {
        _session=session;Instance=instance;FilteredItems=new ListCollectionView(Items);
        FilteredItems.Filter=o=>o is PreviewSaveRow row&&row.Name.Contains(Query,StringComparison.OrdinalIgnoreCase);
        Reload=new(async _=>await LoadAsync(),_=>!_busy);
        OpenFolder=new(_=>{try {Directory.CreateDirectory(SavesDirectory);RuntimeSupport.OpenFolder(SavesDirectory);}catch(Exception error){Notice=RuntimeSupport.Error(error);}});
        RequestRemove=new(_=>ConfirmRemove=true,_=>CanEdit&&HasSelection);
        CancelRemove=new(_=>ConfirmRemove=false);
        RemoveSelected=new(async _=>await RemoveAsync(),_=>CanEdit&&HasSelection&&ConfirmRemove);
        ClearSelection=new(_=>{foreach(var item in Items)item.IsSelected=false;});
        _ = LoadAsync();
    }
    public async Task LoadAsync()
    {
        if(_disposed)return;
        if(_busy)return;
        _busy=true;Notice="";Refresh();
        var selected=Items.Where(item=>item.IsSelected).Select(item=>item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            var directory=SavesDirectory;
            var rows=await Task.Run(()=>Read(directory));
            foreach(var item in Items)item.PropertyChanged-=RowChanged;Items.Clear();
            foreach(var row in rows){row.IsSelected=selected.Contains(row.Name);Items.Add(row);row.PropertyChanged+=RowChanged;}
            FilteredItems.Refresh();
        }
        catch(Exception error){Notice=RuntimeSupport.Error(error);}
        finally{_busy=false;Refresh();CommandManager.InvalidateRequerySuggested();}
    }
    private static PreviewSaveRow[] Read(string directory)
    {
        if(!Directory.Exists(directory))return [];
        var rows=new List<PreviewSaveRow>();
        foreach(var path in Directory.EnumerateDirectories(directory))
        {
            var name=Path.GetFileName(path);
            if(name.StartsWith('.'))continue;
            try
            {
                var size=0L;var last=Directory.GetLastWriteTime(path);
                foreach(var file in Directory.EnumerateFiles(path,"*",SearchOption.AllDirectories))
                {
                    var info=new FileInfo(file);size+=info.Length;
                    if(info.LastWriteTime>last)last=info.LastWriteTime;
                }
                rows.Add(new PreviewSaveRow(name,path,RuntimeSupport.FormatSize(size),last,ReadIcon(Path.Combine(path,"icon.png"))));
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException){}
        }
        return rows.OrderByDescending(row=>row.LastPlayed).ToArray();
    }
    /// <summary>读取存档目录里的 icon.png；没有截图时返回空，界面显示占位图形。</summary>
    private static ImageSource? ReadIcon(string path)
    {
        if(!File.Exists(path))return null;
        try
        {
            var image=new BitmapImage();
            image.BeginInit();
            image.CacheOption=BitmapCacheOption.OnLoad;
            image.DecodePixelWidth=128;
            image.UriSource=new Uri(path,UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch(Exception error) when(error is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException){return null;}
    }
    private async Task RemoveAsync()
    {
        var targets=Items.Where(item=>item.IsSelected).ToArray();
        if(targets.Length==0)return;
        _busy=true;Refresh();CommandManager.InvalidateRequerySuggested();
        try
        {
            var removed=0;
            await Task.Run(()=>{
                foreach(var row in targets)
                {
                    foreach(var file in Directory.EnumerateFiles(row.Path,"*",SearchOption.AllDirectories))File.SetAttributes(file,FileAttributes.Normal);
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(row.Path,Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    removed++;
                }
            });
            ConfirmRemove=false;await LoadAsync();Notice=$"已把 {removed} 个存档移入回收站。";
        }
        catch(Exception error){Notice=RuntimeSupport.Error(error);await LoadAsync();}
        finally{_busy=false;Refresh();CommandManager.InvalidateRequerySuggested();}
    }
    private void RowChanged(object? sender,PropertyChangedEventArgs e){if(e.PropertyName==nameof(PreviewSaveRow.IsSelected))ConfirmRemove=false;Refresh();}
    public void Dispose()=>_disposed=true;
}
/// <summary>一个存档目录的展示信息；图片来自存档自己的 icon.png。</summary>
public sealed class PreviewSaveRow(string name,string path,string sizeText,DateTime lastPlayed,ImageSource? icon):ObservableObject
{
    private bool _selected;
    public string Name {get;}=name;
    public string Path {get;}=path;
    public string SizeText {get;}=sizeText;
    public DateTime LastPlayed {get;}=lastPlayed;
    public string LastPlayedText=>"上次游玩 "+LastPlayed.ToString("yyyy-MM-dd HH:mm");
    public string Detail=>SizeText+"   "+LastPlayedText;
    public ImageSource? Icon {get;}=icon;
    public bool ShowPlaceholder=>Icon is null;
    public bool IsSelected {get=>_selected;set=>Set(ref _selected,value);}
}
