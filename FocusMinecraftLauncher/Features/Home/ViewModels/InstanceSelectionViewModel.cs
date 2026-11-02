using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Features.Home.Models;
using FocusMinecraftLauncher.Preview;

namespace FocusMinecraftLauncher.Features.Home.ViewModels;

/// <summary>组织真实游戏目录和核心读取的版本列表；不同目录共用应用级实例状态。</summary>
public sealed class InstanceSelectionViewModel : ObservableObject, IDisposable
{
    private readonly InstanceFolderOption _currentFolder;
    private InstanceFolderOption _selectedFolder;
    private string _search = "", _notice = "";
    public PreviewSession Session { get; }
    public ObservableCollection<InstanceFolderOption> Folders { get; } = [];
    public IReadOnlyList<InstanceSelectionGroup> Groups { get; }
    public InstanceFolderOption SelectedFolder
    {
        get => _selectedFolder;
        set { if (value != null && Set(ref _selectedFolder, value)) { Notice = ""; _ = SelectRootAsync(); } }
    }
    public string Search { get => _search; set { if (Set(ref _search, value)) RefreshVersions(); } }
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public bool CanChangeSelection => !Session.IsEditingBlocked && !Session.IsLoading && !Session.IsAuthenticating;
    public bool IsLoading => Session.IsLoading;
    public bool HasError => !IsLoading && Session.InstanceError.Length > 0;
    // 读取期间保留已有条目，只在新结果到达后替换；否则列表会瞬间清空，页面闪出一帧空白。
    public bool ShowGroups => !HasError && Groups.Any(group => !group.Versions.IsEmpty);
    public string StateText => IsLoading ? (ShowGroups ? "" : "正在读取游戏版本")
        : HasError ? Session.InstanceError
        : ShowGroups ? ""
        : string.IsNullOrWhiteSpace(Search) ? "此文件夹中没有游戏版本" : "没有匹配的版本";
    public UiCommand ReturnHome { get; }
    public UiCommand InstallGame { get; }
    public UiCommand ChooseInstance { get; }
    public UiCommand AddFolder { get; }
    public UiCommand ImportModpack { get; }
    public UiCommand RemoveFolder { get; }

    public InstanceSelectionViewModel(PreviewSession session, Action returnHome)
    {
        Session = session;
        _currentFolder = new("当前文件夹", session.SavedSettings.GameDirectory, true, canRemove: false);
        _selectedFolder = SamePath(session.SelectedRoot, _currentFolder.FullPath) ? _currentFolder : new("已选文件夹", session.SelectedRoot, true);
        Folders.Add(_currentFolder);
        if (_selectedFolder != _currentFolder) Folders.Add(_selectedFolder);
        foreach(var path in session.GameFolders) if(!Folders.Any(f => SamePath(f.FullPath,path))) Folders.Add(new(Path.GetFileName(path),path,true));
        var officialPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        if (!SamePath(_currentFolder.FullPath, officialPath)) Folders.Add(new("官方启动器文件夹", officialPath, canRemove: false));
        var modVersions = new ListCollectionView(session.Instances)
            { Filter = item => item is PreviewInstance instance && Matches(instance) && SupportsMods(instance) };
        var regularVersions = new ListCollectionView(session.Instances)
            { Filter = item => item is PreviewInstance instance && Matches(instance) && !SupportsMods(instance) };
        Groups = [new("可安装 Mod", modVersions), new("常规版本", regularVersions)];
        ReturnHome = new(_ => returnHome());
        InstallGame = new(_ => session.OpenVersions(), _ => CanChangeSelection);
        ChooseInstance = new(item => { session.CurrentInstance = (PreviewInstance)item!; returnHome(); },
            item => item is PreviewInstance instance && CanChangeSelection && Matches(instance) && session.Instances.Contains(instance));
        AddFolder = new(_ => SelectFolder(), _ => CanChangeSelection);
        ImportModpack = new(async _ => await ImportAsync(), _ => CanChangeSelection);
        RemoveFolder = new(item =>
        {
            if (item is not InstanceFolderOption folder || !folder.CanRemove) return;
            if (ReferenceEquals(SelectedFolder, folder)) SelectedFolder = _currentFolder;
            Session.ForgetFolder(folder.FullPath);
            Folders.Remove(folder);
            Notice = "已从列表移除该文件夹，磁盘上的文件没有删除";
        }, item => item is InstanceFolderOption { CanRemove: true } && CanChangeSelection);
        session.Instances.CollectionChanged += InstancesChanged;
        session.PropertyChanged += SessionChanged;
    }

    /// <summary>进入页面时恢复当前版本的选中标记；保留搜索、文件夹及分组展开状态。</summary>
    public void Open()
    {
        _currentFolder.FullPath = Session.SavedSettings.GameDirectory;
        Notice = "";
        RefreshVersions();
        foreach (var group in Groups)
            group.SelectedInstance = Session.CurrentInstance is { } instance && group.Versions.Contains(instance) ? instance : null;
    }

    private bool Matches(PreviewInstance instance)
    {
        var query = Search.Trim();
        return instance.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || instance.Version.Contains(query, StringComparison.OrdinalIgnoreCase)
            || instance.Loader.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    // 分类使用已读取的加载器元数据，不按实例名称推断。
    private static bool SupportsMods(PreviewInstance instance) =>
        instance.Loader.Equals("Fabric", StringComparison.OrdinalIgnoreCase)
        || instance.Loader.Equals("Forge", StringComparison.OrdinalIgnoreCase)
        || instance.Loader.Equals("NeoForge", StringComparison.OrdinalIgnoreCase);

    /// <summary>保存用户选择的已有目录；重复路径直接选中，通过共享服务查询版本。</summary>
    private void SelectFolder()
    {
        var dialog = new OpenFolderDialog { Title = "添加已有游戏文件夹", Multiselect = false };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        var path = Path.TrimEndingDirectorySeparator(dialog.FolderName);
        var existing = Folders.FirstOrDefault(folder => SamePath(folder.FullPath, path));
        if (existing != null) { SelectedFolder = existing; Notice = "已选择列表中的文件夹"; return; }
        var name = Path.GetFileName(path);
        Session.RememberFolder(path);
        var added = new InstanceFolderOption(name.Length > 0 ? name : path, path, true);
        Folders.Add(added);
        SelectedFolder = added;
    }

    private async Task SelectRootAsync()
    {
        await Session.SelectRootAsync(SelectedFolder.FullPath);
        RefreshVersions();
    }
    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog { Title="选择整合包", Filter="Modrinth 整合包|*.mrpack;*.zip", CheckFileExists=true };
        if(dialog.ShowDialog(Application.Current.MainWindow)==true) await Session.ImportModpackAsync(dialog.FileName);
    }
    private static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);
    private void RefreshVersions()
    {
        foreach (var group in Groups) { group.Versions.Refresh(); group.RefreshSelection(); }
        UpdateState();
    }
    private void UpdateState()
    {
        Changed(nameof(CanChangeSelection)); Changed(nameof(IsLoading)); Changed(nameof(HasError));
        Changed(nameof(ShowGroups)); Changed(nameof(StateText));
    }
    private void InstancesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshVersions();
    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if(e.PropertyName == nameof(PreviewSession.SelectedRoot))
        {
            _currentFolder.FullPath = Session.SavedSettings.GameDirectory;
            var folder = Folders.FirstOrDefault(f => SamePath(f.FullPath, Session.SelectedRoot));
            if(folder == null)
            {
                folder = new(Path.GetFileName(Session.SelectedRoot), Session.SelectedRoot, true);
                Folders.Add(folder);
            }
            _selectedFolder = folder; Changed(nameof(SelectedFolder));
        }
        UpdateState();
    }
    public void Dispose()
    {
        Session.Instances.CollectionChanged -= InstancesChanged;
        Session.PropertyChanged -= SessionChanged;
    }
}

/// <summary>独立保存各分组的展开与候选选择状态；两个列表不共享 SelectedItem 绑定。</summary>
public sealed class InstanceSelectionGroup(string name, ICollectionView versions) : ObservableObject
{
    private bool _expanded = true;
    private PreviewInstance? _selected;
    public ICollectionView Versions { get; } = versions;
    public string Header => name + " (" + Versions.Cast<object>().Count() + ")";
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
    public PreviewInstance? SelectedInstance { get => _selected; set => Set(ref _selected, value); }
    public void RefreshSelection()
    {
        if (SelectedInstance != null && !Versions.Contains(SelectedInstance)) SelectedInstance = null;
        Changed(nameof(Header));
    }
}
