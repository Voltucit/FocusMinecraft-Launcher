using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Features.Downloads.Models;
using FocusMinecraftLauncher.Preview;

namespace FocusMinecraftLauncher.Features.Downloads.ViewModels;

public sealed class DependencyViewModel
{
    public ResourceProject Project { get; }
    public ResourceDependency Dependency { get; }
    public string Requirement => Dependency.Requirement;
    public string Kind => Dependency.Required ? "必需" : "可选";
    public string Compatibility => ResourceText.VersionSummary(Dependency.GameVersions) + (Dependency.Environment == null ? "" : " / " + Dependency.Environment);
    public UiCommand Open { get; }
    public DependencyViewModel(ResourceProject project, ResourceDependency dependency, Action<string, bool> open)
    { Project = project; Dependency = dependency; Open = new(_ => open(project.Id, false)); }
}

public sealed class ResourceFileRowViewModel : ObservableObject
{
    private readonly ResourceDetailViewModel _owner;
    public ResourceFileItem File { get; }
    public bool IsSelected => ReferenceEquals(_owner.SelectedFile, File);
    public UiCommand Select { get; }
    public UiCommand DownloadPreview { get; }
    public ResourceFileRowViewModel(ResourceDetailViewModel owner, ResourceFileItem file, Action<ResourceFileItem> preview)
    { _owner = owner; File = file; Select = new(_ => owner.SelectedFile = file); DownloadPreview = new(_ => { owner.SelectedFile = file; preview(file); }); }
}

public sealed class ResourceFileGroupViewModel(string key, string title, IEnumerable<ResourceFileRowViewModel> files, bool expanded) : ObservableObject
{
    private bool _expanded = expanded;
    public string Key { get; } = key;
    public string Title { get; } = title;
    public IReadOnlyList<ResourceFileRowViewModel> Files { get; } = files.ToArray();
    public string Count => Files.Count + " 个文件";
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
}

public sealed record ResourceLoaderTab(string Key, string Title);

/// <summary>资源详情和列表共用文件选择，按实际加载器筛选及游戏版本系列展示，不改变精确兼容声明。</summary>
public sealed class ResourceDetailViewModel : ObservableObject, IDisposable
{
    private readonly Action<ResourceFileItem> _preview;
    private readonly Action<string> _showImage;
    private ResourceProject? _boundProject;
    private int _boundFileCount;
    private bool _groupsBuilt;
    private bool _loading;
    private double _loadingPercent;
    private string _notice = "";
    private bool _retryVisible;
    private int _tab;
    private string _loader = "";
    private bool _applyingFilter;
    private readonly Dictionary<string, bool> _groupExpansion = [];
    public ResourceEntryViewModel Entry { get; }
    public ResourceProject Project => Entry.Project;
    public DownloadScrollPosition FileScroll { get; } = new();
    public DownloadScrollPosition IntroScroll { get; } = new();
    public DownloadScrollPosition DependencyScroll { get; } = new();
    public GameVersionFilterViewModel VersionFilter { get; }
    public string GameVersion => VersionFilter.SelectedVersion;
    public IReadOnlyList<ResourceLoaderTab> LoaderTabs { get; private set; }
    public bool HasLoaderNavigation => Project.UsesEnvironment && LoaderTabs.Count > 1;
    public string LoaderNavigationName => Project.Category == DownloadSection.Shaders ? "运行环境" : "加载器";
    public string SelectedLoader
    {
        get => _loader;
        set
        {
            var selected = LoaderTabs.Any(item => item.Key == value) ? value : "";
            if (Set(ref _loader, selected) && _groupsBuilt) BuildGroups();
        }
    }

    public ObservableCollection<ResourceFileGroupViewModel> Groups { get; } = [];
    public ObservableCollection<DependencyViewModel> Dependencies => Entry.Dependencies;
    public IReadOnlyList<GalleryImageViewModel> Gallery { get; private set; }
    public ResourceFileItem? SelectedFile { get => Entry.SelectedFile; set => Entry.SelectedFile = value; }
    public int SelectedTab { get => _tab; set { if (Set(ref _tab, value) && value == 2) EnsureGroups(); } }
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    /// <summary>失败的提示旁边给一个重试按钮；加载中的提示不出按钮。</summary>
    public bool ShowRetry => _retryVisible;
    /// <summary>详情读取中或失败时把原因写在页面顶部，失败也不再打断导航。</summary>
    public void ShowNotice(string text, bool failed = false)
    {
        if (_retryVisible != failed) { _retryVisible = failed; Changed(nameof(ShowRetry)); }
        Notice = text;
    }
    public void ClearNotice()
    {
        if (Notice.Length > 0) Notice = "";
        if (_retryVisible) { _retryVisible = false; Changed(nameof(ShowRetry)); }
    }
    public bool IsLoading { get => _loading; private set { if (Set(ref _loading, value)) Changed(nameof(CanRetryFiles)); } }
    public double LoadingPercent { get => _loadingPercent; private set => Set(ref _loadingPercent, value); }
    public string LoadingText => IsLoading ? $"正在加载资源详情（{LoadingPercent:0}%）" : "";
    public void BeginLoading()
    {
        LoadingPercent = 0; IsLoading = true; Changed(nameof(LoadingText));
    }
    public void ReportLoading(double percent)
    {
        LoadingPercent = Math.Clamp(percent, 0, 100); Changed(nameof(LoadingText));
    }
    public void FinishLoading()
    {
        LoadingPercent = 100; IsLoading = false; Changed(nameof(LoadingText));
    }
    public void StopLoading()
    {
        IsLoading = false; Changed(nameof(LoadingText));
    }
    public IEnumerable<ResourceFileItem> AvailableFiles => Project.Files;
    public bool HasFiles => Project.Files.Count > 0;
    /// <summary>读取结束但没有任何文件时提供重新读取入口。</summary>
    public bool CanRetryFiles => !HasFiles && !IsLoading;
    public bool HasGallery => Gallery.Count > 0;
    public bool HasDependencies => Dependencies.Count > 0;
    public string DependencyStatus => Entry.DependencyStatus;
    public UiCommand Back { get; }
    public UiCommand CopyName { get; }
    /// <summary>重新读取本项目详情；读不到时页面里给一个重试入口，而不是弹红色提示条。</summary>
    public UiCommand Retry { get; }

    public ResourceDetailViewModel(ResourceEntryViewModel entry, Action back, Action<ResourceFileItem> preview, Action<string> showImage, ResourceFilter? filter = null, Action? retry = null)
    {
        Entry = entry; _preview = preview; _showImage = showImage; _boundProject = Project; _boundFileCount = Project.Files.Count;
        LoaderTabs = BuildLoaderTabs();
        Back = new(_ => back());
        Retry = new(_ => retry?.Invoke(), _ => retry is not null);
        CopyName = new(_ =>
        {
            try { Clipboard.SetText(Project.Name); Notice = "名称已复制"; }
            catch (ExternalException) { Notice = "剪贴板正被占用，请重试。"; }
        });
        VersionFilter = new(Project.Files.SelectMany(file => file.GameVersions), () => { if (_groupsBuilt) BuildGroups(); });
        Gallery = BuildGallery();
        entry.PropertyChanged += EntryChanged;
        ApplyFilter(filter);
        RefreshDependencies();
    }
    /// <summary>每次进入详情均使用本次入口的可见条件，清除缓存详情遗留的版本与加载器限制。</summary>
    public void ApplyFilter(ResourceFilter? filter)
    {
        _applyingFilter = true;
        try
        {
            ClearNotice();
            SelectedLoader = Project.UsesEnvironment
                ? LoaderTabs.FirstOrDefault(tab => tab.Key.Equals(filter?.Loader, StringComparison.OrdinalIgnoreCase))?.Key ?? "" : "";
            VersionFilter.Select("全部");
            if (filter is { GameVersion.Length: > 0 } && filter.GameVersion != "全部")
            {
                var version = VersionFilter.Series.SelectMany(series => series.Versions)
                    .FirstOrDefault(choice => choice.Number.Equals(filter.GameVersion, StringComparison.OrdinalIgnoreCase));
                if (version != null) VersionFilter.Select(version.Number);
                else Notice = $"该项目没有标注兼容 Minecraft {filter.GameVersion} 的文件，已显示全部版本。";
            }
        }
        finally { _applyingFilter = false; }
        if (_groupsBuilt) BuildGroups();
    }
    private void EntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 简介译文与图标是异步补上的，整个项目刷新时让详情页跟着重建绑定。
        if (string.IsNullOrEmpty(e.PropertyName))
        {
            if (!ReferenceEquals(_boundProject, Project) || _boundFileCount != Project.Files.Count)
            {
                _boundProject = Project; _boundFileCount = Project.Files.Count;
                RefreshProject();
            }
            Changed(nameof(Project));
        }
        if (e.PropertyName is nameof(ResourceEntryViewModel.HasDependencies) or nameof(ResourceEntryViewModel.DependencyStatus)) RefreshDependencies();
        if (e.PropertyName != nameof(ResourceEntryViewModel.SelectedFile)) return;
        if (SelectedFile is { } selected)
        {
            var resetLoader = !MatchesLoader(selected);
            if (resetLoader) { _loader = ""; Changed(nameof(SelectedLoader)); }
            if (GameVersion != "全部" && !selected.GameVersions.Contains(GameVersion, StringComparer.OrdinalIgnoreCase)) VersionFilter.Select("全部");
            else if (resetLoader && _groupsBuilt) BuildGroups();
        }
        Changed(nameof(SelectedFile)); RefreshDependencies();
        foreach (var file in Groups.SelectMany(g => g.Files)) file.Refresh();
    }
    /// <summary>项目或文件列表补取完成后重建加载器、版本、图库；版本分组只在打开对应标签页时构建。</summary>
    private void RefreshProject()
    {
        LoaderTabs = BuildLoaderTabs(); Changed(nameof(LoaderTabs)); Changed(nameof(HasLoaderNavigation));
        if (!LoaderTabs.Any(tab => tab.Key == _loader)) { _loader = ""; Changed(nameof(SelectedLoader)); }
        VersionFilter.UpdateVersions(Project.Files.SelectMany(file => file.GameVersions));
        Gallery = BuildGallery(); Changed(nameof(Gallery)); Changed(nameof(HasGallery));
        Changed(nameof(HasFiles)); Changed(nameof(AvailableFiles)); Changed(nameof(CanRetryFiles));
        if (_groupsBuilt) BuildGroups();
    }
    private void EnsureGroups()
    {
        if (!_groupsBuilt) BuildGroups();
    }
    private IReadOnlyList<ResourceLoaderTab> BuildLoaderTabs() =>
        [new("", "全部"), .. (Project.UsesEnvironment
            ? Project.Files.SelectMany(FileLoaders).Where(loader => !string.IsNullOrWhiteSpace(loader)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(LoaderOrder).ThenBy(LoaderTitle, StringComparer.OrdinalIgnoreCase).Select(loader => new ResourceLoaderTab(loader, LoaderTitle(loader)))
            : Enumerable.Empty<ResourceLoaderTab>())];
    private IReadOnlyList<GalleryImageViewModel> BuildGallery() =>
        Project.Gallery.Select((key, i) => new GalleryImageViewModel(key, "资源图片 " + (i + 1), _showImage)).ToArray();
    /// <summary>加载器作为子导航，版本按系列归组；同一文件支持多个补丁版本时，在同一系列内只展示一次。</summary>
    private void BuildGroups()
    {
        if (_applyingFilter) return;
        _groupsBuilt = true;
        foreach (var group in Groups) _groupExpansion[group.Key] = group.IsExpanded;
        Groups.Clear();
        var files = Project.Files.Where(file => MatchesLoader(file)
            && (GameVersion == "全部" || file.GameVersions.Contains(GameVersion, StringComparer.OrdinalIgnoreCase)));
        var rows = files.SelectMany(file => (GameVersion == "全部" ? file.GameVersions : [GameVersion])
            .Select(MinecraftVersionOrder.Series).DefaultIfEmpty("未提供").Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(series => (File: file, Series: series)));
        foreach (var group in rows.OrderByDescending(row => row.Series, MinecraftVersionOrder.Instance).GroupBy(row => row.Series))
        {
            var key = SelectedLoader + "|" + group.Key;
            var title = GameVersion != "全部" ? "Minecraft " + GameVersion : group.Key switch
            { "其他" => "其他版本", "未提供" => "兼容版本未提供", _ => "Minecraft " + group.Key + " 系列" };
            var items = group.Select(row => row.File).DistinctBy(file => file.Id).OrderByDescending(file => file.Date).ThenBy(file => file.FileName)
                .Select(file => new ResourceFileRowViewModel(this, file, _preview));
            Groups.Add(new(key, title, items, _groupExpansion.GetValueOrDefault(key, false)));
        }
        if (SelectedFile == null || !Groups.SelectMany(group => group.Files).Any(row => ReferenceEquals(row.File, SelectedFile)))
            SelectedFile = Groups.FirstOrDefault()?.Files.FirstOrDefault()?.File;
        Changed(nameof(HasFiles)); Changed(nameof(AvailableFiles)); Changed(nameof(CanRetryFiles)); FileScroll.Reset();
    }
    private bool MatchesLoader(ResourceFileItem file) => SelectedLoader.Length == 0
        || FileLoaders(file).Contains(SelectedLoader, StringComparer.OrdinalIgnoreCase);
    private static IEnumerable<string> FileLoaders(ResourceFileItem file) => file.Loaders.Length > 0 ? file.Loaders
        : file.Environment?.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
    private static int LoaderOrder(string loader) => loader.ToLowerInvariant() switch
    { "fabric" => 0, "forge" => 1, "neoforge" => 2, "quilt" => 3, "iris" => 4, "optifine" => 5, _ => 6 };
    private static string LoaderTitle(string loader) => loader.ToLowerInvariant() switch
    { "fabric" => "Fabric", "forge" => "Forge", "neoforge" => "NeoForge", "quilt" => "Quilt", "iris" => "Iris", "optifine" => "OptiFine", "liteloader" => "LiteLoader", _ => loader };
    private void RefreshDependencies()
    {
        Changed(nameof(DependencyStatus)); Changed(nameof(HasDependencies));
    }
    public void Dispose() => Entry.PropertyChanged -= EntryChanged;
}

public sealed class GalleryImageViewModel : ObservableObject
{
    private System.Windows.Media.ImageSource? _image;
    public System.Windows.Media.ImageSource Image => _image ?? ResourceArtwork.Get(null);
    public string Title { get; }
    public UiCommand Open { get; }
    public GalleryImageViewModel(string key,string title,Action<string> show)
    {Title=title;Open=new(_=>show(key));_ = LoadAsync(key);}
    private async Task LoadAsync(string key){_image=await RemoteArtwork.LoadAsync(key);Changed(nameof(Image));}
}
