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
    private bool _expanded;
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
    public string SelectionText => _selectedPath.Length == 0 ? "自动选择" : Results.FirstOrDefault(item => item.IsSelected) is { } selected ? "Java " + selected.Major + " / " + selected.Version : "自定义 Java";
    public string SelectedPath => _selectedPath;
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
    public UiCommand SelectAutomatic { get; }
    public UiCommand Install { get; }
    /// <summary>实例页已知所需版本时由外部指定，作为下载对话框的默认选中项。</summary>
    public int? RecommendedMajor { get; set; }
    private bool _installing;
    public bool IsInstalling { get => _installing; private set { if (Set(ref _installing, value)) { Changed(nameof(InstallStatus)); CommandManager.InvalidateRequerySuggested(); } } }
    private double _installProgress;
    public double InstallProgress { get => _installProgress; private set => Set(ref _installProgress, value); }
    private string _installStatus = "";
    public string InstallStatus { get => _installStatus; private set => Set(ref _installStatus, value); }
    private readonly Action<string> _selectPath;
    private readonly Func<bool> _mirror;

    public JavaDetectionViewModel(Action<string> selectPath, Func<bool>? mirror = null)
    {
        _selectPath = selectPath;
        _mirror = mirror ?? (() => false);
        Detect = new(async _ => { IsExpanded = true; await RunAsync(); }, _ => !IsBusy);
        Select = new(value => { if (value is JavaRuntimeOption result) { selectPath(result.Path); IsExpanded = false; } },
            value => HasResults && value is JavaRuntimeOption result && !string.IsNullOrWhiteSpace(result.Path));
        SelectAutomatic = new(_ => { selectPath(""); IsExpanded = false; });
        Install = new(async _ =>
        {
            if (Presentation.Dialogs.JavaDownloadDialog.Ask(RecommendedMajor) is { } major) await InstallAsync(major);
        }, _ => !IsBusy && !IsInstalling);
        Results.CollectionChanged += ResultsChanged;
    }
    /// <summary>下载并安装官方运行时；成功后直接选中它，省去用户再去列表里找。</summary>
    private async Task InstallAsync(int major)
    {
        if (!Infrastructure.Runtime.JavaBootstrap.CanInstall(major))
        {
            InstallStatus = $"启动器不能自动安装 Java {major}，请手动安装后在列表中选择。";
            return;
        }
        IsInstalling = true; InstallProgress = 0; InstallStatus = $"正在下载 Java {major}…";
        try
        {
            var java = await Infrastructure.Runtime.JavaBootstrap.InstallAsync(major, _mirror(), progress => OnUi(() =>
            {
                if (!string.IsNullOrWhiteSpace(progress.Message)) InstallStatus = progress.Message;
                if (progress.Download is { TotalBytes: > 0 } download)
                    InstallProgress = Math.Clamp(download.ProcessedBytes * 100d / download.TotalBytes, 0, 100);
            }), CancellationToken.None);
            OnUi(() => InstallStatus = $"Java {java.Version} 已安装完成。");
            _selectPath(java.Executable);
            await RunAsync();
        }
        catch (Exception error)
        {
            OnUi(() => InstallStatus = "下载失败：" + Infrastructure.Runtime.RuntimeSupport.Error(error));
        }
        finally { OnUi(() => IsInstalling = false); }
    }
    /// <summary>下载进度回调来自后台线程，落到界面状态前统一切回 UI 线程。</summary>
    private static void OnUi(Action action)
    {
        if (System.Windows.Application.Current is { } app && !app.Dispatcher.CheckAccess()) app.Dispatcher.Invoke(action);
        else action();
    }
    /// <summary>启动器打开时预热一次检测，结果保留在选择列表中；已有检测进行时不重复执行。</summary>
    public Task DetectOnceAsync() => IsBusy ? Task.CompletedTask : RunAsync();
    private async Task RunAsync()
    {
        State = JavaDetectionState.Detecting; Error = "";
        try
        {
            var javas = await Infrastructure.Runtime.JavaScanner.GetJavasAsync();
            Results.Clear(); foreach (var java in javas) Results.Add(new(java.MajorVersion, java.Version, java.Executable, java.Architecture ?? "架构未提供"));
            State = Results.Count > 0 ? JavaDetectionState.Succeeded : JavaDetectionState.Empty;
        }
        catch (Exception error) { Error = Infrastructure.Runtime.RuntimeSupport.Error(error); State = JavaDetectionState.Failed; }
    }

    /// <summary>路径变化时同步列表的当前选择标记，结果列表本身不决定保存时机。</summary>
    public void UpdateSelection(string path)
    {
        _selectedPath = path.Trim();
        foreach (var item in Results) item.IsSelected = string.Equals(item.Path, _selectedPath, StringComparison.OrdinalIgnoreCase);
        Changed(nameof(SelectionText)); Changed(nameof(SelectedPath));
    }
    private void ResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    { UpdateSelection(_selectedPath); RefreshState(); CommandManager.InvalidateRequerySuggested(); }
    private void RefreshState() { Changed(nameof(IsBusy)); Changed(nameof(HasResults)); Changed(nameof(HasFailed)); Changed(nameof(StatusText)); }
}

/// <summary>由实际运行 Java 后返回的版本、路径和架构；显示统一为“Java 大版本 / 具体版本 / 路径”。</summary>
public sealed class JavaRuntimeOption(int major, string version, string path, string architecture) : ObservableObject
{
    private bool _selected;
    public int Major { get; } = major;
    public string Version { get; } = version;
    public string Path { get; } = path;
    public string Architecture { get; } = architecture;
    public string Headline => $"Java {Major} / {Version} / {Path}";
    public bool IsSelected { get => _selected; internal set => Set(ref _selected, value); }
}
