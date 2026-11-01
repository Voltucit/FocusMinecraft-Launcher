using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using FocusMinecraftLauncher.Common;

namespace FocusMinecraftLauncher.Features.Settings.ViewModels;

public enum JavaDetectionState { NotStarted, Detecting, Succeeded, Empty, Failed }

/// <summary>用户主动检测时调用核心 JavaReader，保存前只维护当前路径草稿。</summary>
public sealed class JavaDetectionViewModel : ObservableObject
{
    private JavaDetectionState _state;
    private string _error = "", _selectedPath = "";
    public ObservableCollection<JavaRuntimeOption> Results { get; } = [];
    public JavaDetectionState State
    {
        get => _state;
        set { if (Set(ref _state, value)) { RefreshState(); CommandManager.InvalidateRequerySuggested(); } }
    }
    public string Error { get => _error; set { if (Set(ref _error, value)) Changed(nameof(StatusText)); } }
    public bool IsBusy => State == JavaDetectionState.Detecting;
    public bool HasResults => State == JavaDetectionState.Succeeded && Results.Count > 0;
    public bool HasFailed => State == JavaDetectionState.Failed;
    public string StatusText => State switch
    {
        JavaDetectionState.Detecting => "正在检测本机 Java…",
        JavaDetectionState.Succeeded when Results.Count > 0 => $"检测结果：{Results.Count} 项",
        JavaDetectionState.Succeeded or JavaDetectionState.Empty => "未找到 Java，可浏览选择可执行文件。",
        JavaDetectionState.Failed => string.IsNullOrWhiteSpace(Error) ? "检测失败，请重试或浏览选择 Java。" : "检测失败：" + Error,
        _ => "尚未检测"
    };
    public UiCommand Detect { get; }
    public UiCommand Select { get; }

    public JavaDetectionViewModel(Action<string> selectPath)
    {
        Detect = new(async _ =>
        {
            State=JavaDetectionState.Detecting;Error="";
            try
            {
                var javas=await FMCore.Java.JavaReader.GetJavasAsync([System.IO.Path.Combine(AppContext.BaseDirectory,"FMLauncher","Java")]);
                Results.Clear();foreach(var java in javas)Results.Add(new(java.Version,"Java",java.Executable,java.Architecture??"架构未提供"));
                State=Results.Count>0?JavaDetectionState.Succeeded:JavaDetectionState.Empty;
            }
            catch(Exception error){Error=Infrastructure.Runtime.RuntimeSupport.Error(error);State=JavaDetectionState.Failed;}
        }, _ => !IsBusy);
        Select = new(value => { if (value is JavaRuntimeOption result) selectPath(result.Path); },
            value => HasResults && value is JavaRuntimeOption result && !string.IsNullOrWhiteSpace(result.Path));
        Results.CollectionChanged += ResultsChanged;
    }

    /// <summary>路径变化时同步列表的当前选择标记，结果列表本身不决定保存时机。</summary>
    public void UpdateSelection(string path)
    {
        _selectedPath = path.Trim();
        foreach (var item in Results) item.IsSelected = string.Equals(item.Path, _selectedPath, StringComparison.OrdinalIgnoreCase);
    }
    private void ResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    { UpdateSelection(_selectedPath); RefreshState(); CommandManager.InvalidateRequerySuggested(); }
    private void RefreshState() { Changed(nameof(IsBusy)); Changed(nameof(HasResults)); Changed(nameof(HasFailed)); Changed(nameof(StatusText)); }
}

/// <summary>由核心实际运行 Java 后返回的版本、路径和架构。</summary>
public sealed class JavaRuntimeOption(string version, string distribution, string path, string architecture) : ObservableObject
{
    private bool _selected;
    public string Version { get; } = version;
    public string Distribution { get; } = distribution;
    public string Path { get; } = path;
    public string Architecture { get; } = architecture;
    public bool IsSelected { get => _selected; internal set => Set(ref _selected, value); }
}
