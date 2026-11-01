using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Features.Downloads.Models;

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
    private string _notice = "";
    private int _tab;
    private string _loader = "";
    private readonly Dictionary<string, bool> _groupExpansion = [];
    public ResourceEntryViewModel Entry { get; }
    public ResourceProject Project => Entry.Project;
    public DownloadScrollPosition FileScroll { get; } = new();
    public DownloadScrollPosition IntroScroll { get; } = new();
    public DownloadScrollPosition DependencyScroll { get; } = new();
    public GameVersionFilterViewModel VersionFilter { get; }
    public string GameVersion => VersionFilter.SelectedVersion;
    public IReadOnlyList<ResourceLoaderTab> LoaderTabs { get; }
    public bool HasLoaderNavigation => Project.UsesEnvironment && LoaderTabs.Count > 1;
    public string LoaderNavigationName => Project.Category == DownloadSection.Shaders ? "运行环境" : "加载器";
    public string SelectedLoader
    {
        get => _loader;
        set
        {
            var selected = LoaderTabs.Any(item => item.Key == value) ? value : "";
            if (Set(ref _loader, selected)) BuildGroups();
        }
    }

    public ObservableCollection<ResourceFileGroupViewModel> Groups { get; } = [];
    public ObservableCollection<DependencyViewModel> Dependencies => Entry.Dependencies;
    public IReadOnlyList<GalleryImageViewModel> Gallery { get; }
    public ResourceFileItem? SelectedFile { get => Entry.SelectedFile; set => Entry.SelectedFile = value; }
    public int SelectedTab { get => _tab; set => Set(ref _tab, value); }
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public IEnumerable<ResourceFileItem> AvailableFiles => Groups.SelectMany(g => g.Files).Select(row => row.File).DistinctBy(file => file.Id);
    public bool HasFiles => Groups.Count > 0;
    public bool HasGallery => Gallery.Count > 0;
    public bool HasDependencies => Dependencies.Count > 0;
    public string DependencyStatus => Entry.DependencyStatus;
    public UiCommand Back { get; }
    public UiCommand CopyName { get; }

    public ResourceDetailViewModel(ResourceEntryViewModel entry, Action back, Action<ResourceFileItem> preview, Action<string> showImage)
    {
        Entry = entry; _preview = preview;
        LoaderTabs = [new("", "全部"), .. (Project.UsesEnvironment
            ? Project.Files.SelectMany(FileLoaders).Where(loader => !string.IsNullOrWhiteSpace(loader)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(LoaderOrder).ThenBy(LoaderTitle, StringComparer.OrdinalIgnoreCase).Select(loader => new ResourceLoaderTab(loader, LoaderTitle(loader)))
            : Enumerable.Empty<ResourceLoaderTab>())];
        Back = new(_ => back());
        CopyName = new(_ =>
        {
            try { Clipboard.SetText(Project.Name); Notice = "名称已复制"; }
            catch (ExternalException) { Notice = "剪贴板正被占用，请重试。"; }
        });
        VersionFilter = new(Project.Files.SelectMany(file => file.GameVersions), BuildGroups);
        Gallery = Project.Gallery.Select((key, i) => new GalleryImageViewModel(key, "资源图片 " + (i + 1), showImage)).ToArray();
        entry.PropertyChanged += EntryChanged;
        BuildGroups(); RefreshDependencies();
    }
    private void EntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ResourceEntryViewModel.HasDependencies) or nameof(ResourceEntryViewModel.DependencyStatus)) RefreshDependencies();
        if (e.PropertyName != nameof(ResourceEntryViewModel.SelectedFile)) return;
        if (SelectedFile is { } selected)
        {
            var resetLoader = !MatchesLoader(selected);
            if (resetLoader) { _loader = ""; Changed(nameof(SelectedLoader)); }
            if (GameVersion != "全部" && !selected.GameVersions.Contains(GameVersion, StringComparer.OrdinalIgnoreCase)) VersionFilter.Select("全部");
            else if (resetLoader) BuildGroups();
        }
        Changed(nameof(SelectedFile)); RefreshDependencies();
        foreach (var file in Groups.SelectMany(g => g.Files)) file.Refresh();
    }
    /// <summary>加载器作为子导航，版本按系列归组；同一文件支持多个补丁版本时，在同一系列内只展示一次。</summary>
    private void BuildGroups()
    {
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
        Changed(nameof(HasFiles)); Changed(nameof(AvailableFiles)); FileScroll.Reset();
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
