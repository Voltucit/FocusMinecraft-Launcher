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

public sealed class ResourceFileGroupViewModel(string title, IEnumerable<ResourceFileRowViewModel> files, bool expanded) : ObservableObject
{
    private bool _expanded = expanded;
    public string Title { get; } = title;
    public IReadOnlyList<ResourceFileRowViewModel> Files { get; } = files.ToArray();
    public string Count => Files.Count + " 个文件";
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
}

/// <summary>资源详情和列表共用文件选择，前置列表只取当前文件声明的本地资料。</summary>
public sealed class ResourceDetailViewModel : ObservableObject, IDisposable
{
    private readonly Action<ResourceFileItem> _preview;
    private string _notice = "";
    private int _tab;
    private readonly Dictionary<string, bool> _groupExpansion = [];
    public ResourceEntryViewModel Entry { get; }
    public ResourceProject Project => Entry.Project;
    public DownloadScrollPosition FileScroll { get; } = new();
    public DownloadScrollPosition IntroScroll { get; } = new();
    public DownloadScrollPosition DependencyScroll { get; } = new();
    public GameVersionFilterViewModel VersionFilter { get; }
    public string GameVersion => VersionFilter.SelectedVersion;
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
        if(SelectedFile != null && GameVersion != "全部" && !SelectedFile.GameVersions.Contains(GameVersion)) VersionFilter.Select("全部");
        Changed(nameof(SelectedFile)); RefreshDependencies();
        foreach (var file in Groups.SelectMany(g => g.Files)) file.Refresh();
    }
    private void BuildGroups()
    {
        foreach (var group in Groups) _groupExpansion[group.Title] = group.IsExpanded;
        Groups.Clear();
        var rows = Project.Files.Where(f => GameVersion == "全部" || f.GameVersions.Contains(GameVersion))
            .SelectMany(file => file.GameVersions.Where(v => GameVersion == "全部" || v == GameVersion).Select(v => (file, version: v, title: "Minecraft " + v + (file.Environment == null ? "" : " / " + file.Environment))));
        foreach (var group in rows.OrderByDescending(row => row.version, MinecraftVersionOrder.Instance).ThenBy(row => row.file.Environment).GroupBy(row => row.title))
            Groups.Add(new(group.Key, group.Select(row => new ResourceFileRowViewModel(this, row.file, _preview)), _groupExpansion.GetValueOrDefault(group.Key, false)));
        if (SelectedFile == null || !Groups.SelectMany(g => g.Files).Any(row => ReferenceEquals(row.File, SelectedFile)))
            SelectedFile = Groups.FirstOrDefault()?.Files.FirstOrDefault()?.File;
        Changed(nameof(HasFiles)); Changed(nameof(AvailableFiles)); FileScroll.Reset();
    }
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
