using System.ComponentModel;
using System.IO;
using Microsoft.Win32;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Preview;
using FocusMinecraftLauncher.Presentation.ViewModels;
namespace FocusMinecraftLauncher.Features.Home.ViewModels.Instances;

/// <summary>实例级启动设置，与全局设置分别持久化。</summary>
public sealed class InstanceLaunchSettingsViewModel : ObservableObject, IDisposable
{
    private readonly PreviewSession _session;
    private bool _globalJava=true,_globalMemory=true,_globalWindow=true,_globalArgs=true,_fullscreen;
    private string _java="",_width="1280",_height="720",_jvm="",_gameArgs="",_notice="",_javaError="",_windowError="";
    private string? _recommendedJava;
    private SettingsDraft _saved=new();
    private bool _saving;
    public PreviewInstance Instance { get; }
    public bool UseGlobalJava
    {
        get => _globalJava;
        set { if (Set(ref _globalJava, value)) { JavaError = ""; Notice = ""; RefreshJavaDisplay(); } }
    }
    public bool IsUsingGlobalJavaSettings { get => UseGlobalJava; set => UseGlobalJava = value; }
    public bool UseGlobalMemory { get=>_globalMemory;set {Set(ref _globalMemory,value);Notice="";} }
    public bool UseGlobalWindow { get=>_globalWindow;set {Set(ref _globalWindow,value);if(value)WindowError="";Notice="";} }
    public bool UseGlobalArguments { get=>_globalArgs;set {Set(ref _globalArgs,value);Notice="";} }
    public string JavaPath
    {
        get => _java;
        set
        {
            var path = string.IsNullOrWhiteSpace(value) || value.Trim() == "自动选择" ? "" : value;
            if (Set(ref _java, path)) { JavaError = ""; Notice = ""; JavaDetection.UpdateSelection(path); RefreshJavaDisplay(); }
        }
    }
    public MemoryAllocationViewModel MemoryAllocation { get; } = new();
    public Features.Settings.ViewModels.JavaDetectionViewModel JavaDetection { get; }
    public string MinecraftVersion => Instance.Version;
    public string SelectedJavaPath => UseGlobalJava
        ? (string.IsNullOrWhiteSpace(_session.SavedSettings.JavaPath) ? "" : _session.SavedSettings.JavaPath) : JavaPath;
    public bool IsJavaAutoSelected => string.IsNullOrWhiteSpace(SelectedJavaPath);
    public string JavaDisplayText => IsJavaAutoSelected ? "自动选择" : UseGlobalJava ? "使用全局 Java" : "自定义 Java";
    /// <summary>仅接收未来服务提供的推荐主版本，空值表示未知；本层不实现兼容性推断。</summary>
    public string? RecommendedJavaVersion
    {
        get => _recommendedJava;
        set { if (Set(ref _recommendedJava, value)) Changed(nameof(JavaRecommendationText)); }
    }
    public string JavaRecommendationText => string.IsNullOrWhiteSpace(RecommendedJavaVersion)
        ? "版本元数据未声明 Java 要求" : "推荐 Java " + RecommendedJavaVersion;
    public string Width { get=>_width;set {Set(ref _width,value);WindowError="";Notice="";} }
    public string Height { get=>_height;set {Set(ref _height,value);WindowError="";Notice="";} }
    public bool Fullscreen { get=>_fullscreen;set {Set(ref _fullscreen,value);Notice="";} }
    public string JvmArguments { get=>_jvm;set {Set(ref _jvm,value);Notice="";} }
    public string GameArguments { get=>_gameArgs;set {Set(ref _gameArgs,value);Notice="";} }
    public string Notice { get=>_notice;private set=>Set(ref _notice,value); }
    public string JavaError { get=>_javaError;private set=>Set(ref _javaError,value); }
    public string WindowError { get=>_windowError;private set=>Set(ref _windowError,value); }
    public string GlobalMemory => $"{_session.SavedSettings.MemoryMb / 1024d:0.0} GB";
    public string Directory => Instance.GameDirectory;
    public string IsolationText => Instance.IsolationText;
    public UiCommand BrowseJava { get; }
    public UiCommand ClearJava { get; }
    public UiCommand Save { get; }
    public UiCommand Restore { get; }
    public InstanceLaunchSettingsViewModel(PreviewSession session,PreviewInstance instance)
    {
        _session=session;Instance=instance;
        JavaDetection = new(path => JavaPath = path);
        BrowseJava=new(_=>{var d=new OpenFileDialog{Filter="Java|java.exe;javaw.exe"};if(d.ShowDialog()==true)JavaPath=d.FileName;}, _=>!UseGlobalJava);
        ClearJava=new(_=>JavaPath="", _=>!UseGlobalJava && !string.IsNullOrWhiteSpace(JavaPath));
        MemoryAllocation.PropertyChanged += MemoryChanged;
        Save=new(async _=>await SaveDraftAsync(),_=>!_saving);Restore=new(_=>{LoadStored();RestoreSaved();},_=>!_saving);session.PropertyChanged+=GlobalChanged;
        RecommendedJavaVersion = instance.RequiredJava?.ToString();
        LoadStored(); RestoreSaved(); JavaDetection.UpdateSelection(JavaPath); Notice = "";
    }
    /// <summary>全局设置变化只更新继承展示，不覆盖实例独立编辑草稿。</summary>
    private void GlobalChanged(object? sender,PropertyChangedEventArgs e)
    { RefreshJavaDisplay();Changed(nameof(GlobalMemory));Changed(nameof(Directory)); Changed(nameof(IsolationText)); }
    private void RefreshJavaDisplay()
    {
        Changed(nameof(IsUsingGlobalJavaSettings)); Changed(nameof(SelectedJavaPath)); Changed(nameof(IsJavaAutoSelected));
        Changed(nameof(JavaDisplayText)); System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
    private void MemoryChanged(object? sender,PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MemoryAllocationViewModel.AllocatedMemoryGb) or nameof(MemoryAllocationViewModel.IsAutomatic) or nameof(MemoryAllocationViewModel.OptimizeBeforeLaunch)) Notice = "";
    }
    private async Task SaveDraftAsync()
    {
        JavaError=!UseGlobalJava && !string.IsNullOrWhiteSpace(JavaPath) && !Path.IsPathFullyQualified(JavaPath.Trim())?"请输入完整路径，或留空自动选择。":"";
        WindowError=!UseGlobalWindow && (!int.TryParse(Width,out var w)||w<=0||!int.TryParse(Height,out var h)||h<=0)?"宽度和高度必须为正整数。":"";
        if(JavaError.Length+WindowError.Length>0){Notice="";return;}
        _saving=true;System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        try
        {
            if(!UseGlobalJava&&!string.IsNullOrWhiteSpace(JavaPath))
            {
                try{await FMCore.Java.JavaReader.GetJavaInfoAsync(JavaPath.Trim());}
                catch(Exception error){JavaError=Infrastructure.Runtime.RuntimeSupport.Error(error);return;}
            }
            var options = await Infrastructure.Runtime.InstanceStore.UpdateAsync(Instance,options=>
            {
            options.GlobalJava=UseGlobalJava;options.GlobalMemory=UseGlobalMemory;options.GlobalWindow=UseGlobalWindow;options.GlobalArguments=UseGlobalArguments;
            options.JavaPath=JavaPath;options.MemoryMb=MemoryAllocation.MemoryMb;options.AutomaticMemory=MemoryAllocation.IsAutomatic;options.OptimizeMemory=MemoryAllocation.OptimizeBeforeLaunch;
            options.Width=int.TryParse(Width,out var width)?width:1280;options.Height=int.TryParse(Height,out var height)?height:720;options.Fullscreen=Fullscreen;options.JvmArguments=JvmArguments;options.GameArguments=GameArguments;
            });
            Instance.Options=options;
            _saved=new(UseGlobalJava,UseGlobalMemory,UseGlobalWindow,UseGlobalArguments,JavaPath,MemoryAllocation.MemoryMb,Width,Height,Fullscreen,JvmArguments,GameArguments,MemoryAllocation.IsAutomatic,MemoryAllocation.OptimizeBeforeLaunch);
            Notice="已保存此实例的启动设置。";
        }
        catch(Exception error){Notice=Infrastructure.Runtime.RuntimeSupport.Error(error);}
        finally{_saving=false;System.Windows.Input.CommandManager.InvalidateRequerySuggested();}
    }
    private void LoadStored()
    {
        var o=Infrastructure.Runtime.InstanceStore.Read(Instance);
        _saved=new(o.GlobalJava,o.GlobalMemory,o.GlobalWindow,o.GlobalArguments,o.JavaPath,o.MemoryMb,o.Width.ToString(),o.Height.ToString(),o.Fullscreen,o.JvmArguments,o.GameArguments,o.AutomaticMemory,o.OptimizeMemory);
    }
    private void RestoreSaved()
    {
        UseGlobalJava=_saved.GlobalJava;UseGlobalMemory=_saved.GlobalMemory;UseGlobalWindow=_saved.GlobalWindow;UseGlobalArguments=_saved.GlobalArguments;
        JavaPath=_saved.Java;MemoryAllocation.LoadMemoryMb(_saved.MemoryMb);
        MemoryAllocation.IsAutomatic=_saved.AutomaticMemory;MemoryAllocation.OptimizeBeforeLaunch=_saved.OptimizeMemory;
        Width=_saved.Width;Height=_saved.Height;Fullscreen=_saved.Fullscreen;JvmArguments=_saved.Jvm;GameArguments=_saved.Game;
        JavaError=WindowError="";Notice="已恢复保存的实例设置。";
    }
    public void Dispose() { _session.PropertyChanged-=GlobalChanged;MemoryAllocation.PropertyChanged-=MemoryChanged; }
    private sealed record SettingsDraft(bool GlobalJava=true,bool GlobalMemory=true,bool GlobalWindow=true,bool GlobalArguments=true,string Java="",int MemoryMb=4096,string Width="1280",string Height="720",bool Fullscreen=false,string Jvm="",string Game="",bool AutomaticMemory=false,bool OptimizeMemory=false);
}
