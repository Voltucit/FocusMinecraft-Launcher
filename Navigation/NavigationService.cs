using System.Collections.ObjectModel;
using FMLauncher.Common;
using FMLauncher.Infrastructure;
using Wpf.Ui.Controls;
namespace FMLauncher.Navigation;
public sealed record NavigationEntry(string Key, string Section, string Label, SymbolRegular Icon, string Description);
/// <summary>仅管理 UI 位置、返回栈和分区记忆，不调用核心或执行文件任务；入口均由 UI 线程调用。</summary>
public sealed class NavigationService(LauncherServices services) : ObservableObject
{
    private static readonly NavigationEntry[] Pages =
    [
        new("Home", "Start", "开始游戏", SymbolRegular.Home24, "选择账号与版本，继续你的世界"),
        new("Versions", "Start", "版本选择", SymbolRegular.Cube24, "管理已安装的 Minecraft 版本"),
        new("Downloads", "Download", "原版与加载器", SymbolRegular.ArrowDownload24, "选择 Minecraft 与兼容的加载器"),
        new("Mods", "Download", "Mod 资源", SymbolRegular.Apps24, "搜索社区资源与查看简介"),
        new("Modpacks", "Download", "整合包清单", SymbolRegular.Box24, "读取本地整合包的现有清单信息"),
        new("Tasks", "Download", "任务管理", SymbolRegular.ArrowDownload24, "查看当前文件任务与操作记录"),
        new("GameSettings", "Settings", "启动设置", SymbolRegular.Options24, "Java、内存、窗口与启动参数"),
        new("Appearance", "Settings", "个性化", SymbolRegular.PaintBrush24, "主题、壁纸与属于你的界面"),
        new("Java", "Settings", "Java 环境", SymbolRegular.Code24, "管理已安装环境与运行时下载"),
        new("Settings", "Settings", "游戏目录", SymbolRegular.Settings24, "选择游戏的保存位置"),
        new("Diagnostics", "More", "诊断日志", SymbolRegular.Options24, "游戏维护与问题定位"),
        new("About", "More", "关于启动器", SymbolRegular.Info24, "FMLauncher 与项目说明")
    ];
    private readonly Stack<string> _history = new();
    private readonly Dictionary<string, string> _lastPages = new()
    { ["Start"] = "Home", ["Download"] = "Downloads", ["Settings"] = "GameSettings", ["More"] = "About" };
    private NavigationEntry? _current, _selected;
    private bool _changing;
    public LauncherServices Services => services;
    public ObservableCollection<NavigationEntry> Items { get; } = [];
    public NavigationEntry? Current => _current;
    public string Section => _current?.Section ?? "Start";
    public string SectionTitle => Section switch { "Download" => "发现与下载", "Settings" => "偏好设置", "More" => "关于与帮助", _ => "我的游戏" };
    public bool CanGoBack => _history.Count > 0;
    public NavigationEntry? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value) && !_changing && value != null) Open(value.Key, false); }
    }
    public event Action<NavigationEntry, bool>? Navigated;
    /// <summary>一级分区记忆最后访问页面，切换分区时开启新的返回路径。</summary>
    public void SwitchSection(string section)
    { if (_lastPages.TryGetValue(section, out var page) && Section != section) Open(page, false); }
    /// <summary>快捷入口压入返回栈，侧栏选择作为新起点；相同页面不重复导航。</summary>
    public void Open(string key, bool remember = true)
    {
        var entry = Pages.FirstOrDefault(p => p.Key == key);
        if (entry == null || _current?.Key == key) return;
        if (remember && _history.TryPeek(out var previous) && previous == key) { Back(); return; }
        if (remember && _current != null) _history.Push(_current.Key);
        else if (!remember) _history.Clear();
        Present(entry, false);
    }
    public void Back()
    { if (_history.TryPop(out var key)) Present(Pages.First(p => p.Key == key), true); }
    private void Present(NavigationEntry entry, bool back)
    {
        _changing = true;
        try
        {
            var sectionChanged = _current?.Section != entry.Section;
            _current = entry; _lastPages[entry.Section] = entry.Key;
            if (sectionChanged)
            {
                Items.Clear();
                foreach (var item in Pages.Where(p => p.Section == entry.Section)) Items.Add(item);
            }
            Selected = entry;
            Changed(nameof(Current)); Changed(nameof(Section)); Changed(nameof(SectionTitle)); Changed(nameof(CanGoBack));
        }
        finally { _changing = false; }
        Navigated?.Invoke(entry, back);
    }
}
