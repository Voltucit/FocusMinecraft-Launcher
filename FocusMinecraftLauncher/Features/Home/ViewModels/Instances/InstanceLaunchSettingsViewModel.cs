using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
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
    private string _java="",_width="1280",_height="720",_jvm="",_gameArgs="",_notice="",_javaError="",_windowError="",_placeholderOverrides="",_placeholderError="";
    private string? _recommendedJava;
    private SettingsDraft _saved=new();
    private bool _saving;
    private readonly HashSet<string> _pendingFields = [];
    private bool _initializing = true;
    public PreviewInstance Instance { get; }
    public bool UseGlobalJava
    {
        get => _globalJava;
        set { if (Set(ref _globalJava, value)) { JavaError = ""; RefreshJavaDisplay(); QueueSave(); } }
    }
    public bool IsUsingGlobalJavaSettings { get => UseGlobalJava; set => UseGlobalJava = value; }
    public bool UseGlobalMemory { get=>_globalMemory;set {if(Set(ref _globalMemory,value))QueueSave();} }
    public bool UseGlobalWindow { get=>_globalWindow;set {if(Set(ref _globalWindow,value)){if(value)WindowError="";QueueSave();}} }
    public bool UseGlobalArguments { get=>_globalArgs;set {if(Set(ref _globalArgs,value))QueueSave();} }
    public string JavaPath
    {
        get => _java;
        set
        {
            var path = string.IsNullOrWhiteSpace(value) || value.Trim() == "自动选择" ? "" : value;
            if (Set(ref _java, path)) { JavaError = ""; JavaDetection.UpdateSelection(path); RefreshJavaDisplay(); QueueSave(); }
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
    public string Width { get=>_width;set {if(Set(ref _width,value)){WindowError="";QueueSave();}} }
    public string Height { get=>_height;set {if(Set(ref _height,value)){WindowError="";QueueSave();}} }
    public bool Fullscreen { get=>_fullscreen;set {if(Set(ref _fullscreen,value))QueueSave();} }
    public string JvmArguments { get=>_jvm;set {if(Set(ref _jvm,value))QueueSave();} }
    public string GameArguments { get=>_gameArgs;set {if(Set(ref _gameArgs,value))QueueSave();} }
    /// <summary>每行“名称=值”，只用于版本 JSON 中非标准占位符。</summary>
    public string PlaceholderOverridesText { get=>_placeholderOverrides;set {if(Set(ref _placeholderOverrides,value)){PlaceholderError="";QueueSave();}} }
    public string PlaceholderError {get=>_placeholderError;private set=>Set(ref _placeholderError,value);}
    public string Notice { get=>_notice;private set=>Set(ref _notice,value); }
    public string JavaError { get=>_javaError;private set=>Set(ref _javaError,value); }
    public string WindowError { get=>_windowError;private set=>Set(ref _windowError,value); }
    public string GlobalMemory => $"{_session.SavedSettings.MemoryMb / 1024d:0.0} GB";
    public string Directory => Instance.GameDirectory;
    public string IsolationText => Instance.IsolationText;
    public UiCommand BrowseJava { get; }
    public UiCommand ClearJava { get; }
    public InstanceLaunchSettingsViewModel(PreviewSession session,PreviewInstance instance)
    {
        _session=session;Instance=instance;
        JavaDetection = new(path => JavaPath = path, () => session.FileSource == FMCore.Downloads.DownloadSource.BmclApi, javas => _ = session.SaveJavaScanCacheAsync(javas));
        JavaDetection.RecommendedMajor = instance.RequiredJava;
        BrowseJava=new(_=>{var d=new OpenFileDialog{Filter="Java|java.exe;javaw.exe"};if(d.ShowDialog()==true)JavaPath=d.FileName;}, _=>!UseGlobalJava);
        ClearJava=new(_=>JavaPath="", _=>!UseGlobalJava && !string.IsNullOrWhiteSpace(JavaPath));
        MemoryAllocation.PropertyChanged += MemoryChanged;
        session.PropertyChanged+=GlobalChanged;
        RecommendedJavaVersion = instance.RequiredJava?.ToString();
        MemoryAllocation.ModCount = Infrastructure.Runtime.RuntimeSupport.ModCount(instance);
        LoadStored(); RestoreSaved(); JavaDetection.UpdateSelection(JavaPath); Notice = "";
        _initializing = false;
    }

    /// <summary>每项只保存最后一次修改；写入期间的新修改排队，不覆盖其他入口维护的配置。</summary>
    private void QueueSave([CallerMemberName] string property = "")
    {
        if (_initializing) return;
        _pendingFields.Add(property);
        if (!_saving) _ = SavePendingAsync();
    }
    private async Task SavePendingAsync()
    {
        _saving = true;
        try
        {
            while (_pendingFields.Count > 0)
            {
                var property = _pendingFields.First();
                _pendingFields.Remove(property);
                await SaveDraftAsync(property);
            }
        }
        finally { _saving = false; }
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
        if (e.PropertyName is nameof(MemoryAllocationViewModel.AllocatedMemoryGb) or nameof(MemoryAllocationViewModel.IsAutomatic) or nameof(MemoryAllocationViewModel.OptimizeBeforeLaunch)) QueueSave(nameof(MemoryAllocation));
    }
    private async Task SaveDraftAsync(string property)
    {
        Notice="";
        if (property is nameof(JavaPath) or nameof(UseGlobalJava))
        {
            JavaError = !UseGlobalJava && !string.IsNullOrWhiteSpace(JavaPath) && !Path.IsPathFullyQualified(JavaPath.Trim()) ? "请输入完整路径，或留空自动选择。" : "";
            if (JavaError.Length > 0) return;
        }
        if (property is nameof(Width) or nameof(Height) or nameof(UseGlobalWindow))
        {
            WindowError = (property != nameof(UseGlobalWindow) || !UseGlobalWindow) && (!int.TryParse(Width,out var w)||w<=0||!int.TryParse(Height,out var h)||h<=0) ? "宽度和高度必须为正整数。" : "";
            if (WindowError.Length > 0) return;
        }
        Dictionary<string,string>? placeholderOverrides = null;
        if (property == nameof(PlaceholderOverridesText))
        {
            placeholderOverrides = ParsePlaceholderOverrides(PlaceholderOverridesText, out var error);
            PlaceholderError = error;
            if (error.Length > 0) return;
        }
        var draft = new SettingsDraft(UseGlobalJava,UseGlobalMemory,UseGlobalWindow,UseGlobalArguments,JavaPath.Trim(),MemoryAllocation.MemoryMb,Width,Height,Fullscreen,JvmArguments,GameArguments,MemoryAllocation.IsAutomatic,MemoryAllocation.OptimizeBeforeLaunch,PlaceholderOverridesText);
        try
        {
            var options = await Infrastructure.Runtime.InstanceStore.UpdateAsync(Instance,options=>
            {
                switch (property)
                {
                    case nameof(UseGlobalJava): options.GlobalJava=draft.GlobalJava;if(!draft.GlobalJava)options.JavaPath=draft.Java; break;
                    case nameof(UseGlobalMemory): options.GlobalMemory=draft.GlobalMemory; break;
                    case nameof(UseGlobalWindow):
                        options.GlobalWindow=draft.GlobalWindow;
                        if(!draft.GlobalWindow){options.Width=int.Parse(draft.Width);options.Height=int.Parse(draft.Height);}
                        break;
                    case nameof(UseGlobalArguments): options.GlobalArguments=draft.GlobalArguments; break;
                    case nameof(JavaPath): options.JavaPath=draft.Java;options.GlobalJava=draft.GlobalJava; break;
                    case nameof(MemoryAllocation): options.MemoryMb=draft.MemoryMb;options.AutomaticMemory=draft.AutomaticMemory;options.OptimizeMemory=draft.OptimizeMemory; break;
                    case nameof(Width):
                    case nameof(Height):
                        options.Width=int.Parse(draft.Width);options.Height=int.Parse(draft.Height);options.GlobalWindow=draft.GlobalWindow;
                        break;
                    case nameof(Fullscreen): options.Fullscreen=draft.Fullscreen; break;
                    case nameof(JvmArguments): options.JvmArguments=draft.Jvm; break;
                    case nameof(GameArguments): options.GameArguments=draft.Game; break;
                    case nameof(PlaceholderOverridesText): options.PlaceholderOverrides=placeholderOverrides!; break;
                }
            });
            Instance.Options=options;
            Notice="已自动保存。";
        }
        catch(Exception error){Notice=Infrastructure.Runtime.RuntimeSupport.Error(error);}
    }
    private void LoadStored()
    {
        var o=Infrastructure.Runtime.InstanceStore.Read(Instance);
        _saved=new(o.GlobalJava,o.GlobalMemory,o.GlobalWindow,o.GlobalArguments,o.JavaPath,o.MemoryMb,o.Width.ToString(),o.Height.ToString(),o.Fullscreen,o.JvmArguments,o.GameArguments,o.AutomaticMemory,o.OptimizeMemory,FormatPlaceholderOverrides(o.PlaceholderOverrides));
    }
    private void RestoreSaved()
    {
        UseGlobalJava=_saved.GlobalJava;UseGlobalMemory=_saved.GlobalMemory;UseGlobalWindow=_saved.GlobalWindow;UseGlobalArguments=_saved.GlobalArguments;
        JavaPath=_saved.Java;MemoryAllocation.LoadMemoryMb(_saved.MemoryMb);
        MemoryAllocation.IsAutomatic=_saved.AutomaticMemory;MemoryAllocation.OptimizeBeforeLaunch=_saved.OptimizeMemory;
        Width=_saved.Width;Height=_saved.Height;Fullscreen=_saved.Fullscreen;JvmArguments=_saved.Jvm;GameArguments=_saved.Game;
        PlaceholderOverridesText=_saved.Placeholder;PlaceholderError="";JavaError=WindowError="";Notice="已恢复保存的游戏设置。";
    }
    private static string FormatPlaceholderOverrides(IReadOnlyDictionary<string,string> values)
        => string.Join(Environment.NewLine, values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));

    private static Dictionary<string,string> ParsePlaceholderOverrides(string text, out string error)
    {
        Dictionary<string,string> values = new(StringComparer.Ordinal);
        error = "";
        int lineNumber = 0;
        foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            lineNumber++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                error = $"第 {lineNumber} 行格式应为 名称=值。";
                return values;
            }
            string name = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (name.Length == 0 || name.Contains("${", StringComparison.Ordinal) || name.Contains('}')
                || name.Any(char.IsWhiteSpace) || name.Any(char.IsControl))
            {
                error = $"第 {lineNumber} 行的占位符名称无效。";
                return values;
            }
            if (value.Any(char.IsControl))
            {
                error = $"第 {lineNumber} 行的值包含控制字符。";
                return values;
            }
            if (!values.TryAdd(name, value))
            {
                error = $"占位符“{name}”重复。";
                return values;
            }
        }
        return values;
    }
    public void Dispose() { _session.PropertyChanged-=GlobalChanged;MemoryAllocation.PropertyChanged-=MemoryChanged; }
    private sealed record SettingsDraft(bool GlobalJava=true,bool GlobalMemory=true,bool GlobalWindow=true,bool GlobalArguments=true,string Java="",int MemoryMb=4096,string Width="1280",string Height="720",bool Fullscreen=false,string Jvm="",string Game="",bool AutomaticMemory=true,bool OptimizeMemory=false,string Placeholder="");
}
