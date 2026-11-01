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
    private string _java, _jvm, _javaError = "", _notice = "";
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
    private VersionIsolationMode _isolation;
    public VersionIsolationMode VersionIsolation { get => _isolation; set { if (Set(ref _isolation, value)) Notice = ""; } }
    public IReadOnlyList<VersionIsolationOption> IsolationOptions { get; } =
    [new(VersionIsolationMode.All, "全部隔离"), new(VersionIsolationMode.ModdedOnly, "仅 Mod 版本隔离"), new(VersionIsolationMode.None, "不隔离")];
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
    public string JvmArguments { get => _jvm; set { if (Set(ref _jvm, value)) Notice = ""; } }
    public string JavaError { get => _javaError; private set => Set(ref _javaError, value); }
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public IReadOnlyList<Presentation.ThemeColorPreset> FlatColorPresets => Presentation.UiTheme.FlatColorPresets;
    public IReadOnlyList<Presentation.ThemeColorPreset> SoftColorPresets => Presentation.UiTheme.SoftColorPresets;
    public UiCommand Save { get; }
    public UiCommand Restore { get; }
    public UiCommand BrowseJava { get; }
    public UiCommand ClearJava { get; }
    public UiCommand BrowseBackground { get; }
    public UiCommand ResetBackground { get; }
    public SettingsViewModel(PreviewSession session)
    {
        JavaDetection = new(path => JavaPath = path);
        Session = session;
        _isolation = session.SavedSettings.VersionIsolation;
        _java = string.IsNullOrWhiteSpace(session.SavedSettings.JavaPath) || session.SavedSettings.JavaPath.Trim() == "自动选择" ? "" : session.SavedSettings.JavaPath;
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
            JavaPath = session.SavedSettings.JavaPath; VersionIsolation = session.SavedSettings.VersionIsolation;
            MemoryAllocation.LoadMemoryMb(session.SavedSettings.MemoryMb);
        MemoryAllocation.IsAutomatic = session.SavedSettings.AutomaticMemory; MemoryAllocation.OptimizeBeforeLaunch = session.SavedSettings.OptimizeMemory;
            MemoryAllocation.IsAutomatic = session.SavedSettings.AutomaticMemory; MemoryAllocation.OptimizeBeforeLaunch = session.SavedSettings.OptimizeMemory;
            JvmArguments = session.SavedSettings.JvmArguments; JavaError = ""; Notice = "已恢复已保存的设置。";
        });
        BrowseJava = new(_ => { var dialog = new OpenFileDialog { Filter = "Java|java.exe;javaw.exe" }; if (dialog.ShowDialog() == true) JavaPath = dialog.FileName; });
        ClearJava = new(_ => JavaPath = "", _ => !IsJavaAutoSelected);
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
    /// <summary>验证实际 Java 可执行文件后保存；错误保留在对应字段旁。</summary>
    private async Task SaveEditedAsync()
    {
        // 自动内存由启动时的实际可用内存决定，保留手动值供切换模式时使用。
        var memory = MemoryAllocation.IsAutomatic ? Session.SavedSettings.MemoryMb : MemoryAllocation.MemoryMb;
        JavaError = !string.IsNullOrWhiteSpace(JavaPath) && !Path.IsPathFullyQualified(JavaPath.Trim()) ? "请输入完整路径，也可以留空。" : "";
        Notice = "";
        if (JavaError.Length > 0) return;
        _saving = true;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        try
        {
            if(!string.IsNullOrWhiteSpace(JavaPath))
            {
                try { await FMCore.Java.JavaReader.GetJavaInfoAsync(JavaPath.Trim()); }
                catch(Exception error){JavaError=Infrastructure.Runtime.RuntimeSupport.Error(error);return;}
            }
            var saved = await Session.SaveGlobalSettingsAsync(new() { VersionIsolation = VersionIsolation, JavaPath = JavaPath.Trim(), GameDirectory = Session.SavedSettings.GameDirectory, MemoryMb = memory, AutomaticMemory = MemoryAllocation.IsAutomatic, OptimizeMemory = MemoryAllocation.OptimizeBeforeLaunch, JvmArguments = JvmArguments });
            Notice = !saved ? "设置未保存，请检查程序目录的写入权限后重试。" : "已保存全局设置。";
        }
        catch(Exception error){Notice=Infrastructure.Runtime.RuntimeSupport.Error(error);}
        finally { _saving = false; System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
    }
}
