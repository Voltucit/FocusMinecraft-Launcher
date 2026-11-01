using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Features.Home.Models;
using FocusMinecraftLauncher.Infrastructure.Runtime;
using FocusMinecraftLauncher.Preview;
namespace FocusMinecraftLauncher.Features.Home.ViewModels.Instances;

/// <summary>实际 mrpack 导出与三态文件选择；目标位置在开始导出后由系统对话框确定。</summary>
public sealed class InstanceExportViewModel : ObservableObject, IDisposable
{
    private readonly PreviewSession _session;
    private PreviewTask? _exportTask;
    private string _name,_version="1.0.0",_author="",_description="",_destinationPath="",_includes="",_excludes="",_notice="",_nameError="",_versionError="",_includesError="",_excludesError="",_state="";
    private bool _onlyModrinth,_busy,_advanced,_bundle,_completed,_loading=true;
    private string[] _allFiles=[];
    public PreviewInstance Instance { get; }
    public ObservableCollection<ExportSelectionNode> Content { get; }=[];
    public ObservableCollection<string> SourceConflicts { get; }=[];
    public string[] Formats { get; }=["Modrinth (.mrpack)"];
    public string Name { get=>_name;set {Set(ref _name,value);NameError="";} }
    public string Version { get=>_version;set {Set(ref _version,value);VersionError="";} }
    public string Author { get=>_author;set=>Set(ref _author,value); }
    public string Description { get=>_description;set=>Set(ref _description,value); }
    public string DestinationPath { get=>_destinationPath;private set=>Set(ref _destinationPath,value); }
    public string Includes { get=>_includes;set {Set(ref _includes,value);IncludesError="";RulesChanged();} }
    public string Excludes { get=>_excludes;set {Set(ref _excludes,value);ExcludesError="";RulesChanged();} }
    public bool OnlyModrinth { get=>_onlyModrinth;set {Set(ref _onlyModrinth,value);UpdateConflicts();} }
    public bool BundleResources { get=>_bundle;set=>Set(ref _bundle,value); }
    public bool AdvancedExpanded { get=>_advanced;set=>Set(ref _advanced,value); }
    public bool IsBusy { get=>_busy;private set=>Set(ref _busy,value); }
    public bool HasConflicts=>SourceConflicts.Count>0;
    public bool HasResult=>_completed;
    public double ProgressPercent=>_exportTask?.Percent??0;
    public bool IsProgressIndeterminate=>_exportTask?.IsIndeterminate??true;
    public string ProgressText=>_exportTask?.DownloadLabel??"";
    public string Notice { get=>_notice;private set=>Set(ref _notice,value); }
    public string Stage { get=>_state;private set=>Set(ref _state,value); }
    public string NameError { get=>_nameError;private set=>Set(ref _nameError,value); }
    public string VersionError { get=>_versionError;private set=>Set(ref _versionError,value); }
    public string IncludesError { get=>_includesError;private set=>Set(ref _includesError,value); }
    public string ExcludesError { get=>_excludesError;private set=>Set(ref _excludesError,value); }
    public string RulesError=>IncludesError+ExcludesError;
    public string SelectionText=>$"已选择 {ResolveSelection().Length} 个文件";
    public string PrivacyWarning=>ResolveSelection().Any(p=>p.StartsWith("saves/",StringComparison.OrdinalIgnoreCase)||p.Equals("servers.dat",StringComparison.OrdinalIgnoreCase))?"包含存档或服务器记录，请确认分享范围。":"";
    public UiCommand SelectAll { get; }
    public UiCommand SelectNone { get; }
    public UiCommand StartPreview { get; }
    public UiCommand Cancel { get; }
    public UiCommand OpenResult { get; }
    public UiCommand LoadConfiguration { get; }
    public UiCommand SaveConfiguration { get; }
    public UiCommand Guide { get; }
    public InstanceExportViewModel(PreviewSession session,PreviewInstance instance,InstanceModsViewModel mods)
    {
        _session=session;Instance=instance;_name=instance.Name;
        SelectAll=new(_=>{foreach(var node in Content)node.IsChecked=true;},_=>!IsBusy&&!_loading);
        SelectNone=new(_=>{foreach(var node in Content)node.IsChecked=false;},_=>!IsBusy&&!_loading);
        StartPreview=new(async _=>await ExportAsync(),_=>!IsBusy&&!_loading&&!session.IsBusy);
        Cancel=new(_=> { if(_exportTask!=null)_session.CancelTask.Execute(_exportTask); },_=>IsBusy);
        OpenResult=new(_=>RuntimeSupport.OpenFolder(Path.GetDirectoryName(DestinationPath)!),_=>HasResult);
        LoadConfiguration=new(async _=>await ConfigurationAsync(false),_=>!IsBusy&&!_loading);
        SaveConfiguration=new(async _=>await ConfigurationAsync(true),_=>!IsBusy&&!_loading);
        Guide=new(_=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack"){UseShellExecute=true}));
        session.PropertyChanged+=TaskStateChanged;
        _ = ReadFilesAsync();
    }
    public Task RefreshFilesAsync() => IsBusy || _loading ? Task.CompletedTask : ReadFilesAsync();
    private async Task ReadFilesAsync()
    {
        var previous = Content.SelectMany(node=>node.Walk()).Where(node=>node.Children.Count==0&&node.CanToggle)
            .ToDictionary(node=>node.Key,node=>node.IsChecked,StringComparer.OrdinalIgnoreCase);
        var expanded = Content.SelectMany(node=>node.Walk()).ToDictionary(node=>node.Key,node=>node.IsExpanded,StringComparer.OrdinalIgnoreCase);
        _loading=true;CommandManager.InvalidateRequerySuggested();
        try
        {
            Stage="扫描实例文件";
            Instance.Options=InstanceStore.Read(Instance);
            _allFiles=await Task.Run(()=>ModpackArchive.Files(Instance.Folder).Select(f=>Path.GetRelativePath(Instance.Folder,f).Replace('\\','/')).Where(f=>!ModpackArchive.Private(f)&&f!=InstanceStore.FileName&&f!=Instance.Id+".json"&&f!=Instance.Id+".jar"&&!f.StartsWith("natives/")&&!f.StartsWith(".fmcore")).Order(StringComparer.OrdinalIgnoreCase).ToArray());
            foreach(var old in Content.SelectMany(node=>node.Walk()))old.PropertyChanged-=NodeChanged;
            Content.Clear();
            Content.Add(new("manifest","游戏版本与加载器信息",true,"仅声明版本，不复制游戏本体。",false));
            bool ShaderConfig(string path)=>path is "optionsshaders.txt" or "optionsof.txt" or "config/iris.properties" or "config/oculus.properties"
                ||path.StartsWith("config/iris/",StringComparison.OrdinalIgnoreCase)||path.StartsWith("config/oculus/",StringComparison.OrdinalIgnoreCase);
            var assigned=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ExportSelectionNode Group(string key,string title)=>new(key,title){IsExpanded=expanded.GetValueOrDefault(key,false)};
            void AddFiles(ExportSelectionNode parent,IEnumerable<string> paths,string prefix,bool selected)
            {
                foreach(var path in paths)
                {
                    assigned.Add(path);var local=path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)?path[prefix.Length..]:path;
                    var parts=local.Split('/');var folder=parent;var relative=prefix.TrimEnd('/');
                    for(var index=0;index<parts.Length-1;index++)
                    {
                        relative+=(relative.Length>0?"/":"")+parts[index];var key="folder:"+relative;
                        var child=folder.Children.FirstOrDefault(node=>node.Key==key);
                        if(child==null){child=Group(key,parts[index]);folder.Add(child);}folder=child;
                    }
                    folder.Add(new(path,parts[^1],previous.TryGetValue(path,out var wasSelected)?wasSelected==true:selected,path,
                        sensitive:path.StartsWith("saves/",StringComparison.OrdinalIgnoreCase)||path.Equals("servers.dat",StringComparison.OrdinalIgnoreCase)));
                }
            }
            var game=Group("section:game","游戏设置");AddFiles(game,_allFiles.Where(path=>path.Equals("options.txt",StringComparison.OrdinalIgnoreCase)),"",true);if(game.Children.Count>0)Content.Add(game);
            var mods=Group("section:mods","Mod");AddFiles(mods,_allFiles.Where(path=>path.StartsWith("mods/",StringComparison.OrdinalIgnoreCase)),"mods/",true);
            var modConfig=Group("section:mod-config","Mod 配置");AddFiles(modConfig,_allFiles.Where(path=>path.StartsWith("config/",StringComparison.OrdinalIgnoreCase)&&!ShaderConfig(path)),"config/",true);
            if(modConfig.Children.Count>0)mods.Add(modConfig);if(mods.Children.Count>0)Content.Add(mods);
            var resources=Group("section:resourcepacks","资源包");AddFiles(resources,_allFiles.Where(path=>path.StartsWith("resourcepacks/",StringComparison.OrdinalIgnoreCase)),"resourcepacks/",true);if(resources.Children.Count>0)Content.Add(resources);
            var shaders=Group("section:shaders","光影包");AddFiles(shaders,_allFiles.Where(path=>path.StartsWith("shaderpacks/",StringComparison.OrdinalIgnoreCase)),"shaderpacks/",false);
            var shaderConfig=Group("section:shader-config","光影配置");AddFiles(shaderConfig,_allFiles.Where(ShaderConfig),"",false);
            if(shaderConfig.Children.Count>0)shaders.Add(shaderConfig);if(shaders.Children.Count>0)Content.Add(shaders);
            var saves=Group("section:saves","单人存档");AddFiles(saves,_allFiles.Where(path=>path.StartsWith("saves/",StringComparison.OrdinalIgnoreCase)),"saves/",false);if(saves.Children.Count>0)Content.Add(saves);
            var other=Group("section:other","其他实例文件");AddFiles(other,_allFiles.Where(path=>!assigned.Contains(path)),"",false);if(other.Children.Count>0)Content.Add(other);
            foreach(var node in Content.SelectMany(n=>n.Walk()))node.PropertyChanged+=NodeChanged;
            Stage="";UpdateConflicts();Refresh();
        }
        catch(Exception error){Stage=RuntimeSupport.Error(error);}
        finally{_loading=false;CommandManager.InvalidateRequerySuggested();}
    }
    private IEnumerable<string> SelectedFiles()=>Content.SelectMany(n=>n.Walk()).Where(n=>n.Children.Count==0&&n.CanToggle&&n.IsChecked==true).Select(n=>n.Key);
    private static bool InvalidRule(string text)=>text.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries).Any(line=>Path.IsPathRooted(line.Trim())||line.Replace('\\','/').Split('/').Contains("..")||line.Contains(':'));
    private static bool Match(string path,string pattern)
    {
        pattern=pattern.Trim().Replace('\\','/');
        if(path==pattern||path.StartsWith(pattern.TrimEnd('/')+"/",StringComparison.OrdinalIgnoreCase))return true;
        return Regex.IsMatch(path,"^"+Regex.Escape(pattern).Replace("\\*\\*",".*").Replace("\\*","[^/]*").Replace("\\?","[^/]")+"$",RegexOptions.IgnoreCase);
    }
    private string[] ResolveSelection()
    {
        var includes=Includes.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries);var excludes=Excludes.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries);
        return SelectedFiles().Concat(_allFiles.Where(p=>includes.Any(rule=>Match(p,rule)))).Distinct(StringComparer.OrdinalIgnoreCase).Where(p=>!excludes.Any(rule=>Match(p,rule))).ToArray();
    }
    private void UpdateConflicts()
    {
        SourceConflicts.Clear();
        if(OnlyModrinth)
            foreach(var path in ResolveSelection().Where(p=>p.Split('/')[0] is "mods" or "resourcepacks" or "shaderpacks"))
                if(!Instance.Options.Resources.TryGetValue(path,out var origin)||!Uri.TryCreate(origin.Url,UriKind.Absolute,out var uri)||uri.Host!="cdn.modrinth.com")SourceConflicts.Add(path);
        Changed(nameof(HasConflicts));
    }
    private void NodeChanged(object? sender,PropertyChangedEventArgs e){if(e.PropertyName==nameof(ExportSelectionNode.IsChecked)){Changed(nameof(SelectionText));Changed(nameof(PrivacyWarning));UpdateConflicts();}}
    private void RulesChanged(){Changed(nameof(SelectionText));Changed(nameof(PrivacyWarning));UpdateConflicts();}
    private async Task ExportAsync()
    {
        NameError=string.IsNullOrWhiteSpace(Name)?"请输入整合包名称。":"";VersionError=string.IsNullOrWhiteSpace(Version)?"请输入整合包版本。":"";
        IncludesError=InvalidRule(Includes)?"包含规则必须为实例内相对路径。":"";ExcludesError=InvalidRule(Excludes)?"排除规则必须为实例内相对路径。":"";
        UpdateConflicts();if(NameError.Length+VersionError.Length+RulesError.Length>0||HasConflicts){AdvancedExpanded=HasConflicts||RulesError.Length>0;return;}
        var files=ResolveSelection();
        if(PrivacyWarning.Length>0&&MessageBox.Show(PrivacyWarning,"导出整合包",MessageBoxButton.OKCancel,MessageBoxImage.Warning)!=MessageBoxResult.OK)return;
        var dialog=new SaveFileDialog{Title="导出整合包",Filter="Modrinth 整合包|*.mrpack",DefaultExt=".mrpack",AddExtension=true,OverwritePrompt=true,FileName=string.Join("_",(Name+"-"+Version).Split(Path.GetInvalidFileNameChars()))};
        if(dialog.ShowDialog(Application.Current.MainWindow)!=true)return;
        DestinationPath=dialog.FileName;_exportTask=null;_completed=false;IsBusy=true;Notice="";Changed(nameof(HasResult));CommandManager.InvalidateRequerySuggested();
        try
        {
            var name=Name;var version=Version;var destination=DestinationPath;var bundle=BundleResources;var onlyModrinth=OnlyModrinth;
            var summary=Description+(Author.Length>0?"\n作者："+Author:"");
            _exportTask=_session.Enqueue(PreviewTaskKind.Export,name,version,Instance.Root,Instance.Id,async (task,token)=>
            {
                void ApplyProgress(PackExportProgress value)
                {
                    if(!task.IsActive)return;
                    Stage=value.Stage;task.Stage=value.Stage;task.Bytes=value.ProcessedBytes;task.Total=value.TotalBytes;
                    task.CompletedFiles=value.CompletedFiles;task.TotalFiles=value.TotalFiles;task.Refresh();_session.Signal();
                }
                var progress=new Progress<PackExportProgress>(ApplyProgress);PackExportProgress? last=null;
                await Task.Run(()=>ModpackArchive.ExportAsync(Instance,destination,name,version,summary,files,bundle,onlyModrinth,value=>{last=value;((IProgress<PackExportProgress>)progress).Report(value);},token),token);
                if(last!=null)ApplyProgress(last);
            });
            await _session.WaitForTaskAsync(_exportTask);
            _completed=_exportTask.State=="已完成";
            Stage=_completed?"导出完成":_exportTask.Error.Length>0?_exportTask.Error:_exportTask.State;
            if(_completed)Notice=Path.GetFileName(DestinationPath);
        }
        catch(Exception error){Stage=RuntimeSupport.Error(error);_session.Log("导出整合包："+Stage);}
        finally{IsBusy=false;Changed(nameof(HasResult));CommandManager.InvalidateRequerySuggested();}
    }
    private sealed record ExportConfiguration(string Name,string Version,string Author,string Description,string Includes,string Excludes,bool OnlyModrinth,bool BundleResources,string[] Selected);
    private async Task ConfigurationAsync(bool save)
    {
        try
        {
            if(save)
            {
                var dialog=new SaveFileDialog{Title="保存导出配置",Filter="JSON 配置|*.json",DefaultExt=".json",AddExtension=true};if(dialog.ShowDialog()!=true)return;
                await File.WriteAllTextAsync(dialog.FileName,JsonSerializer.Serialize(new ExportConfiguration(Name,Version,Author,Description,Includes,Excludes,OnlyModrinth,BundleResources,SelectedFiles().ToArray()),new JsonSerializerOptions{WriteIndented=true}));Notice="导出配置已保存。";
            }
            else
            {
                var dialog=new OpenFileDialog{Title="读取导出配置",Filter="JSON 配置|*.json"};if(dialog.ShowDialog()!=true)return;
                var config=JsonSerializer.Deserialize<ExportConfiguration>(await File.ReadAllTextAsync(dialog.FileName))??throw new InvalidDataException("导出配置为空。");
                NameError=string.IsNullOrWhiteSpace(config.Name)||config.Name.Length>128?"配置中的名称须为 1–128 个字符。":"";
                VersionError=string.IsNullOrWhiteSpace(config.Version)||config.Version.Length>64?"配置中的版本须为 1–64 个字符。":"";
                IncludesError=InvalidRule(config.Includes??"")?"配置中的包含规则不是有效的相对路径。":"";
                ExcludesError=InvalidRule(config.Excludes??"")?"配置中的排除规则不是有效的相对路径。":"";
                if(NameError.Length+VersionError.Length+RulesError.Length>0){AdvancedExpanded=RulesError.Length>0;Notice="导出配置有误，未应用。";return;}
                Name=config.Name;Version=config.Version;Author=config.Author??"";Description=config.Description??"";Includes=config.Includes??"";Excludes=config.Excludes??"";OnlyModrinth=config.OnlyModrinth;BundleResources=config.BundleResources;
                var selected=(config.Selected??[]).ToHashSet(StringComparer.OrdinalIgnoreCase);foreach(var node in Content.SelectMany(n=>n.Walk()).Where(n=>n.Children.Count==0&&n.CanToggle))node.IsChecked=selected.Contains(node.Key);
                Notice="导出配置已读取。";UpdateConflicts();
            }
        }
        catch(Exception error){Notice=RuntimeSupport.Error(error);}
    }
    private void TaskStateChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(_exportTask==null)return;
        IsBusy=_exportTask.IsActive;
        Changed(nameof(ProgressPercent));Changed(nameof(IsProgressIndeterminate));Changed(nameof(ProgressText));
        if(!IsBusy)
        {
            _completed=_exportTask.State=="已完成";
            Stage=_completed?"导出完成":_exportTask.Error.Length>0?_exportTask.Error:_exportTask.State;
            if(_completed)Notice=Path.GetFileName(DestinationPath);
        }
        Changed(nameof(HasResult));
    }
    public void Dispose(){_session.PropertyChanged-=TaskStateChanged;if(_exportTask?.IsActive==true)_session.CancelTask.Execute(_exportTask);foreach(var node in Content.SelectMany(n=>n.Walk()))node.PropertyChanged-=NodeChanged;}
}
