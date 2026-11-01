using System.IO;
using Microsoft.Win32;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Preview;
using FocusMinecraftLauncher.Features.Home.Models;
using FocusMinecraftLauncher.Presentation.ViewModels;
namespace FocusMinecraftLauncher.Features.Settings.ViewModels;

/// <summary>管理全局设置的编辑、校验与程序目录配置保存。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private string _java, _directory, _jvm, _javaError = "", _directoryError = "", _notice = "";
    private bool _saving, _importingBackground;
    public string BackgroundActionText => _importingBackground ? "正在读取…" : "选择图片";
    private bool _themeSettingsExpanded;
    public bool IsThemeSettingsExpanded { get => _themeSettingsExpanded; set => Set(ref _themeSettingsExpanded, value); }
    private bool _homeLayoutsExpanded;
    public bool IsHomeLayoutsExpanded { get => _homeLayoutsExpanded; set => Set(ref _homeLayoutsExpanded, value); }
    private int _selectedTab;
    public int SelectedTab { get => _selectedTab; set => Set(ref _selectedTab, value); }
    public PreviewSession Session { get; }
    public OtherSettingsViewModel Other { get; } = new();
    public string[] Themes { get; } = [Presentation.UiTheme.GlassThemeName, Presentation.UiTheme.FlatThemeName, Presentation.UiTheme.SoftThemeName];
    public IReadOnlyList<HomeLayoutOption> HomeLayouts { get; } =
    [
        new(HomeLayout.Classic, "经典布局", "保留完整主页", true),
        new(HomeLayout.Minimal, "留白布局", "头像在左下，启动操作在右下"),
        new(HomeLayout.Centered, "居中启动", "头像与启动操作集中显示")
    ];
    public string JavaPath
    {
        get => _java;
        set
        {
            // “自动选择”仅为显示状态，存储仍沿用已有空路径约定。
            var path = string.IsNullOrWhiteSpace(value) || value.Trim() == "自动选择" ? "" : value;
            if (!Set(ref _java, path)) return;
            JavaError = ""; Notice = ""; Changed(nameof(IsJavaAutoSelected)); Changed(nameof(JavaDisplayText));
            JavaDetection.UpdateSelection(path);
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool IsJavaAutoSelected => string.IsNullOrWhiteSpace(JavaPath);
    public string JavaDisplayText => IsJavaAutoSelected ? "自动选择" : JavaPath;
    public MemoryAllocationViewModel MemoryAllocation { get; } = new();
    public JavaDetectionViewModel JavaDetection { get; }
    public string GameDirectory { get => _directory; set { if (Set(ref _directory, value)) { DirectoryError = ""; Notice = ""; } } }
    public string JvmArguments { get => _jvm; set { if (Set(ref _jvm, value)) Notice = ""; } }
    public string JavaError { get => _javaError; private set => Set(ref _javaError, value); }
    public string DirectoryError { get => _directoryError; private set => Set(ref _directoryError, value); }
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public IReadOnlyList<Presentation.ThemeColorPreset> FlatColorPresets => Presentation.UiTheme.FlatColorPresets;
    public IReadOnlyList<Presentation.ThemeColorPreset> SoftColorPresets => Presentation.UiTheme.SoftColorPresets;
    public UiCommand Save { get; }
    public UiCommand Restore { get; }
    public UiCommand BrowseJava { get; }
    public UiCommand ClearJava { get; }
    public UiCommand BrowseDirectory { get; }
    public UiCommand BrowseBackground { get; }
    public UiCommand ResetBackground { get; }
    public SettingsViewModel(PreviewSession session)
    {
        JavaDetection = new(path => JavaPath = path);
        Session = session;
        _java = string.IsNullOrWhiteSpace(session.SavedSettings.JavaPath) || session.SavedSettings.JavaPath.Trim() == "自动选择" ? "" : session.SavedSettings.JavaPath;
        _directory = session.SavedSettings.GameDirectory;
        MemoryAllocation.LoadMemoryMb(session.SavedSettings.MemoryMb);
        MemoryAllocation.IsAutomatic = session.SavedSettings.AutomaticMemory; MemoryAllocation.OptimizeBeforeLaunch = session.SavedSettings.OptimizeMemory; _jvm = session.SavedSettings.JvmArguments;
        MemoryAllocation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MemoryAllocationViewModel.AllocatedMemoryGb) or nameof(MemoryAllocationViewModel.IsAutomatic) or nameof(MemoryAllocationViewModel.OptimizeBeforeLaunch)) Notice = "";
        };
        JavaDetection.UpdateSelection(JavaPath);
        Save = new(async _ => await SaveEditedAsync(), _ => !_saving);
        Restore = new(_ =>
        {
            JavaPath = session.SavedSettings.JavaPath; GameDirectory = session.SavedSettings.GameDirectory;
            MemoryAllocation.LoadMemoryMb(session.SavedSettings.MemoryMb);
        MemoryAllocation.IsAutomatic = session.SavedSettings.AutomaticMemory; MemoryAllocation.OptimizeBeforeLaunch = session.SavedSettings.OptimizeMemory;
            MemoryAllocation.IsAutomatic = session.SavedSettings.AutomaticMemory; MemoryAllocation.OptimizeBeforeLaunch = session.SavedSettings.OptimizeMemory;
            JvmArguments = session.SavedSettings.JvmArguments; JavaError = DirectoryError = ""; Notice = "已恢复已保存的设置。";
        });
        BrowseJava = new(_ => { var dialog = new OpenFileDialog { Filter = "Java|java.exe;javaw.exe" }; if (dialog.ShowDialog() == true) JavaPath = dialog.FileName; });
        ClearJava = new(_ => JavaPath = "", _ => !IsJavaAutoSelected);
        BrowseDirectory = new(_ => { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog() == true) GameDirectory = dialog.FolderName; });
        BrowseBackground = new(async _ =>
        {
            var dialog = new OpenFileDialog { Title = "选择背景图片", Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true, Multiselect = false };
            if (dialog.ShowDialog(System.Windows.Application.Current.MainWindow) != true) return;
            await ChangeBackgroundAsync(dialog.FileName);
        }, _ => !_importingBackground);
        ResetBackground = new(async _ => await ChangeBackgroundAsync(null), _ => !_importingBackground && session.HasBackgroundSelection);
    }
    /// <summary>通过原生文件选择器导入图片；操作期间避免重复导入与清理。</summary>
    private async Task ChangeBackgroundAsync(string? path)
    {
        _importingBackground = true; Changed(nameof(BackgroundActionText));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        try
        {
            if (path == null) await Session.ResetCustomBackgroundAsync();
            else await Session.SetCustomBackgroundAsync(path);
        }
        finally
        {
            _importingBackground = false; Changed(nameof(BackgroundActionText));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }
    /// <summary>验证目录格式和实际 Java 可执行文件后保存；错误保留在对应字段旁。</summary>
    private async Task SaveEditedAsync()
    {
        // 自动内存由启动时的实际可用内存决定，保留手动值供切换模式时使用。
        var memory = MemoryAllocation.IsAutomatic ? Session.SavedSettings.MemoryMb : MemoryAllocation.MemoryMb;
        DirectoryError = !Path.IsPathFullyQualified(GameDirectory.Trim()) ? "请输入完整的游戏目录路径。" : "";
        JavaError = !string.IsNullOrWhiteSpace(JavaPath) && !Path.IsPathFullyQualified(JavaPath.Trim()) ? "请输入完整路径，也可以留空。" : "";
        Notice = "";
        if (DirectoryError.Length + JavaError.Length > 0) return;
        _saving = true;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        try
        {
            if(!string.IsNullOrWhiteSpace(JavaPath))
            {
                try { await FMCore.Java.JavaReader.GetJavaInfoAsync(JavaPath.Trim()); }
                catch(Exception error){JavaError=Infrastructure.Runtime.RuntimeSupport.Error(error);return;}
            }
            try { _ = Path.GetFullPath(GameDirectory.Trim()); }
            catch(Exception error) when(error is ArgumentException or NotSupportedException or PathTooLongException)
            {DirectoryError="游戏目录路径无效，请重新选择。";return;}
            var saved = await Session.SaveGlobalSettingsAsync(new() { JavaPath = JavaPath.Trim(), GameDirectory = GameDirectory.Trim(), MemoryMb = memory, AutomaticMemory = MemoryAllocation.IsAutomatic, OptimizeMemory = MemoryAllocation.OptimizeBeforeLaunch, JvmArguments = JvmArguments });
            Notice = !saved ? "设置未保存，请检查程序目录的写入权限后重试。" : "已保存全局设置。";
        }
        catch(Exception error){Notice=Infrastructure.Runtime.RuntimeSupport.Error(error);}
        finally { _saving = false; System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
    }
}
