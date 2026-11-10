using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using Microsoft.Win32;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Infrastructure.Runtime;
using FocusMinecraftLauncher.Preview;
using FocusMinecraftLauncher.Features.Home.Models;
using FocusMinecraftLauncher.Presentation.ViewModels;
namespace FocusMinecraftLauncher.Features.Settings.ViewModels;

/// <summary>管理全局设置的编辑、校验与程序目录配置保存。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private string _java, _jvm, _authlib = "", _javaError = "", _notice = "";
    private string _assetsDirectory = "", _librariesDirectory = "";
    private string _gcMode = "Auto";
    private const int WallpaperPageSize = 12;
    private IReadOnlyList<WallpaperOption> _allWallpapers = [];
    private WallpaperChoiceViewModel? _selectedWallpaper;
    private string _wallpaperListStatus = "展开后读取本地壁纸";
    private bool _isWallpaperExpanded;
    private bool _isWallpaperPropertiesExpanded;
    private bool _suppressWallpaperApply;
    private bool _wallpapersLoaded;
    private bool _wallpapersLoading;
    private bool _saving, _importingBackground;
    private bool _initializing = true;
    private readonly SemaphoreSlim _javaSaveGate = new(1, 1);
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
            if (!_initializing) _ = SaveJavaPathAsync(path);
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool IsJavaAutoSelected => string.IsNullOrWhiteSpace(JavaPath);
    public string JavaDisplayText => IsJavaAutoSelected ? "自动选择" : JavaPath;
    public string AssetsDirectory { get => _assetsDirectory; set { if (Set(ref _assetsDirectory, value)) Notice = ""; } }
    public string LibrariesDirectory { get => _librariesDirectory; set { if (Set(ref _librariesDirectory, value)) Notice = ""; } }
    public UiCommand BrowseAssets { get; }
    public UiCommand ClearAssets { get; }
    public UiCommand BrowseLibraries { get; }
    public UiCommand ClearLibraries { get; }
    public MemoryAllocationViewModel MemoryAllocation { get; } = new();
    public JavaDetectionViewModel JavaDetection { get; }
    public string JvmArguments { get => _jvm; set { if (Set(ref _jvm, value)) Notice = ""; } }
    public UiCommand RefreshWallpaper { get; }
    public UiCommand ClearWallpaper { get; }
    public UiCommand RefreshWallpaperList { get; }
    public UiCommand LoadMoreWallpapers { get; }
    public UiCommand SelectWallpaper { get; }
    public UiCommand ResetWallpaperProperties { get; }
    public ObservableCollection<WallpaperChoiceViewModel> VisibleWallpapers { get; } = [];
    public ObservableCollection<WallpaperRowViewModel> WallpaperRows { get; } = [];
    public ObservableCollection<WallpaperPropertyEditorViewModel> WallpaperProperties { get; } = [];
    public bool HasWallpaperProperties => WallpaperProperties.Count > 0;
    public WallpaperChoiceViewModel? SelectedWallpaper
    {
        get => _selectedWallpaper;
        set
        {
            if (!Set(ref _selectedWallpaper, value)) return;
            foreach (WallpaperChoiceViewModel item in VisibleWallpapers)
                item.IsSelected = ReferenceEquals(item, value);
            if (_suppressWallpaperApply || value is null) return;
            _ = ApplySelectedWallpaperAsync(value);
        }
    }
    public string WallpaperListStatus { get => _wallpaperListStatus; private set => Set(ref _wallpaperListStatus, value); }
    public bool IsWallpaperExpanded
    {
        get => _isWallpaperExpanded;
        set
        {
            if (!Set(ref _isWallpaperExpanded, value) || !value) return;
            _ = LoadWallpapersAsync();
        }
    }
    public bool IsWallpaperPropertiesExpanded { get => _isWallpaperPropertiesExpanded; set => Set(ref _isWallpaperPropertiesExpanded, value); }
    public bool HasMoreWallpapers => VisibleWallpapers.Count < _allWallpapers.Count;
    public IReadOnlyList<GcOption> GcChoices { get; } =
    [
        new("Auto", "自动"),
        new("G1GC", "G1GC"),
        new("ZGC", "ZGC"),
        new("None", "不添加")
    ];
    public string GcMode
    {
        get => _gcMode;
        set
        {
            var normalized = GcChoices.Any(choice => string.Equals(choice.Value, value, StringComparison.OrdinalIgnoreCase)) ? value : "Auto";
            if (Set(ref _gcMode, normalized)) Notice = "";
        }
    }
    /// <summary>外置账号使用的 authlib-injector；留空表示由启动器下载并维护。</summary>
    public string AuthlibInjectorPath { get => _authlib; set { if (Set(ref _authlib, value)) { Notice = ""; System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } } }
    public string JavaError { get => _javaError; private set => Set(ref _javaError, value); }
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public IReadOnlyList<Presentation.ThemeColorPreset> FlatColorPresets => Presentation.UiTheme.FlatColorPresets;
    public IReadOnlyList<Presentation.ThemeColorPreset> SoftColorPresets => Presentation.UiTheme.SoftColorPresets;
    public UiCommand Save { get; }
    public UiCommand Restore { get; }
    public UiCommand BrowseJava { get; }
    public UiCommand ClearJava { get; }
    public UiCommand BrowseAuthlibInjector { get; }
    public UiCommand ClearAuthlibInjector { get; }
    public UiCommand BrowseBackground { get; }
    public UiCommand ResetBackground { get; }
    public SettingsViewModel(PreviewSession session)
    {
        JavaDetection = new(path => JavaPath = path, () => session.FileSource == FMCore.Downloads.DownloadSource.BmclApi, javas => _ = session.SaveJavaScanCacheAsync(javas));
        Session = session;
        _assetsDirectory = session.ResourceAssetsDirectory;
        _librariesDirectory = session.ResourceLibrariesDirectory;
        _isolation = session.SavedSettings.VersionIsolation;
        _java = string.IsNullOrWhiteSpace(session.SavedSettings.JavaPath) || session.SavedSettings.JavaPath.Trim() == "自动选择" ? "" : session.SavedSettings.JavaPath;
        _authlib = session.SavedSettings.AuthlibInjectorPath;
        MemoryAllocation.LoadMemoryMb(session.SavedSettings.MemoryMb);
        MemoryAllocation.IsAutomatic = session.SavedSettings.AutomaticMemory; MemoryAllocation.OptimizeBeforeLaunch = session.SavedSettings.OptimizeMemory; _jvm = session.SavedSettings.JvmArguments;
        GcMode = session.SavedSettings.GcMode;
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
            JvmArguments = session.SavedSettings.JvmArguments; GcMode = session.SavedSettings.GcMode; AuthlibInjectorPath = session.SavedSettings.AuthlibInjectorPath; JavaError = ""; Notice = "已恢复已保存的设置。";        });
        BrowseJava = new(_ => { var dialog = new OpenFileDialog { Filter = "Java|java.exe;javaw.exe" }; if (dialog.ShowDialog() == true) JavaPath = dialog.FileName; });
        ClearJava = new(_ => JavaPath = "", _ => !IsJavaAutoSelected);
        BrowseAssets = new(_ => { var dialog = new OpenFolderDialog { Title = "选择 assets 目录" }; if (dialog.ShowDialog() == true) AssetsDirectory = dialog.FolderName; });
        ClearAssets = new(_ => AssetsDirectory = "", _ => AssetsDirectory.Length > 0);
        BrowseLibraries = new(_ => { var dialog = new OpenFolderDialog { Title = "选择 libraries 目录" }; if (dialog.ShowDialog() == true) LibrariesDirectory = dialog.FolderName; });
        ClearLibraries = new(_ => LibrariesDirectory = "", _ => LibrariesDirectory.Length > 0);
        BrowseAuthlibInjector = new(_ => { var dialog = new OpenFileDialog { Filter = "authlib-injector|*.jar", Title = "选择 authlib-injector" }; if (dialog.ShowDialog() == true) AuthlibInjectorPath = dialog.FileName; });
        ClearAuthlibInjector = new(_ => AuthlibInjectorPath = "", _ => AuthlibInjectorPath.Length > 0);
        BrowseBackground = new(async _ =>
        {
            var dialog = new OpenFileDialog { Title = "选择背景图片", Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true, Multiselect = false };
            if (dialog.ShowDialog(System.Windows.Application.Current.MainWindow) != true) return;
            await ChangeBackgroundAsync(dialog.FileName);
        }, _ => !_importingBackground);
        ResetBackground = new(async _ => await ChangeBackgroundAsync(null), _ => !_importingBackground && session.HasBackgroundSelection);
        RefreshWallpaper = new(async _ => await Session.RefreshWallpaperAsync());
        ClearWallpaper = new(async _ =>
        {
            await Session.ClearWallpaperAsync();
            LoadWallpaperProperties();
        });
        RefreshWallpaperList = new(async _ => await LoadWallpapersAsync(true));
        LoadMoreWallpapers = new(_ => LoadMoreWallpapersCore(), _ => HasMoreWallpapers);
        SelectWallpaper = new(parameter =>
        {
            if (parameter is WallpaperChoiceViewModel choice) SelectedWallpaper = choice;
        });
        ResetWallpaperProperties = new(_ =>
        {
            foreach (WallpaperPropertyEditorViewModel editor in WallpaperProperties) editor.Reset();
        });
        Session.PropertyChanged += OnSessionPropertyChanged;
        _initializing = false;
    }

    /// <summary>Java 选择变化后只自动保存 Java 路径，不影响其他尚未点击保存的草稿。</summary>
    private async Task SaveJavaPathAsync(string path)
    {
        await _javaSaveGate.WaitAsync();
        try
        {
            if (!string.Equals(JavaPath, path, StringComparison.OrdinalIgnoreCase)) return;
            var saved = Session.SavedSettings.Copy();
            if (string.Equals(saved.JavaPath, path, StringComparison.OrdinalIgnoreCase)) return;
            saved.JavaPath = path;
            if (!await Session.SaveGlobalSettingsAsync(saved))
                Notice = "Java 路径未能自动保存，请检查程序目录的写入权限。";
        }
        catch (Exception error)
        {
            Notice = "Java 路径未能自动保存：" + RuntimeSupport.Error(error);
        }
        finally { _javaSaveGate.Release(); }
    }
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PreviewSession.WallpaperProject))
            LoadWallpaperProperties();
        if (e.PropertyName is nameof(PreviewSession.SelectedRoot))
        {
            AssetsDirectory = Session.ResourceAssetsDirectory;
            LibrariesDirectory = Session.ResourceLibrariesDirectory;
        }
    }
    private async Task ApplySelectedWallpaperAsync(WallpaperChoiceViewModel wallpaper)
    {
        await Session.UseWallpaperAsync(wallpaper.Option.ProjectPath);
        LoadWallpaperProperties();
    }
    private void LoadWallpaperProperties()
    {
        WallpaperProperties.Clear();
        foreach (WallpaperProperty property in Session.WallpaperProperties)
        {
            var editor = new WallpaperPropertyEditorViewModel(property, Session.GetWallpaperPropertyValue(property.Key));
            editor.ValueChanged += (key, value) => Session.SetWallpaperProperty(key, value);
            WallpaperProperties.Add(editor);
        }
        Changed(nameof(HasWallpaperProperties));
    }
    private async Task LoadWallpapersAsync(bool force = false)
    {
        if (_wallpapersLoading || (_wallpapersLoaded && !force)) return;
        _wallpapersLoading = true;
        WallpaperListStatus = "正在读取本地壁纸…";
        try
        {
            IReadOnlyList<WallpaperOption> wallpapers = await Task.Run(() => WallpaperEngineService.GetLocalWallpapers(force));
            _allWallpapers = wallpapers;
            VisibleWallpapers.Clear();
            WallpaperRows.Clear();
            AddNextWallpapers();
            _suppressWallpaperApply = true;
            try
            {
                SelectedWallpaper = VisibleWallpapers.FirstOrDefault(item =>
                        string.Equals(item.Option.ProjectPath, Session.WallpaperProject, StringComparison.OrdinalIgnoreCase))
                    ?? VisibleWallpapers.FirstOrDefault();
            }
            finally { _suppressWallpaperApply = false; }
            LoadWallpaperProperties();
            UpdateWallpaperStatus();
            _wallpapersLoaded = true;
        }
        catch (Exception error)
        {
            _allWallpapers = [];
            VisibleWallpapers.Clear();
            WallpaperRows.Clear();
            SelectedWallpaper = null;
            Changed(nameof(HasMoreWallpapers));
            WallpaperListStatus = "读取本地壁纸失败：" + Infrastructure.Runtime.RuntimeSupport.Error(error);
        }
        finally
        {
            _wallpapersLoading = false;
        }
    }
    private void LoadMoreWallpapersCore()
    {
        if (!HasMoreWallpapers) return;
        AddNextWallpapers();
        UpdateWallpaperStatus();
    }
    private void AddNextWallpapers()
    {
        int start = VisibleWallpapers.Count;
        foreach (WallpaperOption option in _allWallpapers.Skip(start).Take(WallpaperPageSize))
            VisibleWallpapers.Add(new WallpaperChoiceViewModel(option));
        for (int index = start; index < VisibleWallpapers.Count; index += 3)
            WallpaperRows.Add(new WallpaperRowViewModel(VisibleWallpapers.Skip(index).Take(3).ToArray()));
        Changed(nameof(HasMoreWallpapers));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
    private void UpdateWallpaperStatus()
    {
        WallpaperListStatus = _allWallpapers.Count == 0
            ? "未找到本地 Wallpaper Engine 壁纸"
            : $"已找到 {_allWallpapers.Count} 个壁纸，显示 {VisibleWallpapers.Count} 个";
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
    /// <summary>只检查路径格式；Java 是否可用留到启动时解析，避免 Java 未安装时无法保存设置。</summary>
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
            if(AuthlibInjectorPath.Trim().Length>0 && !File.Exists(AuthlibInjectorPath.Trim())){JavaError="authlib-injector 文件不存在，请重新选择或留空自动下载。";return;}
            await Session.SaveResourceDirectoriesAsync(AssetsDirectory, LibrariesDirectory);
            var saved = await Session.SaveGlobalSettingsAsync(new() { VersionIsolation = VersionIsolation, JavaPath = JavaPath.Trim(), GameDirectory = Session.SavedSettings.GameDirectory, MemoryMb = memory, AutomaticMemory = MemoryAllocation.IsAutomatic, OptimizeMemory = MemoryAllocation.OptimizeBeforeLaunch, JvmArguments = JvmArguments, GcMode = GcMode, AuthlibInjectorPath = AuthlibInjectorPath.Trim() });
            Notice = !saved ? "设置未保存，请检查程序目录的写入权限后重试。" : "已保存全局设置。";
        }
        catch(Exception error){Notice=Infrastructure.Runtime.RuntimeSupport.Error(error);}
        finally { _saving = false; System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
    }
}

public sealed record GcOption(string Value, string Label);
