using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FocusMinecraftLauncher.Navigation;

public enum MainPage { Home, Accounts, Downloads, Settings, More }

public sealed record NavigationItem(MainPage Page, string Title, string IconData);

/// <summary>仅持有导航选择及侧栏展开状态，不执行账号、下载或游戏业务。</summary>
public sealed class NavigationViewModel : INotifyPropertyChanged
{
    public Preview.PreviewSession Session { get; } = new();
    private NavigationItem _selectedItem;
    private bool _isExpanded = true;

    public IReadOnlyList<NavigationItem> Items { get; } =
    [
        new(MainPage.Home, "主页", "M3,9 L10,3 17,9 V17 H12 V12 H8 V17 H3 Z"),
        new(MainPage.Accounts, "账号", "M14,6 A4,4 0 1 1 6,6 A4,4 0 1 1 14,6 M3,18 C3,11 17,11 17,18"),
        new(MainPage.Downloads, "下载", "M10,2 V13 M5,8 L10,13 15,8 M3,13 V18 H17 V13"),
        new(MainPage.Settings, "设置", "M3,5 H7 M11,5 H17 M3,15 H11 M15,15 H17 M7,2 V8 M11,12 V18"),
        new(MainPage.More, "更多", "M4,10 L4,10.01 M10,10 L10,10.01 M16,10 L16,10.01")
    ];

    public NavigationViewModel() => _selectedItem = Items[0];

    public NavigationItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (value == null || ReferenceEquals(_selectedItem, value)) return;
            _selectedItem = value;
            Changed();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            Changed();
            Changed(nameof(ToggleLabel));
        }
    }

    public string ToggleLabel => IsExpanded ? "收起导航" : "展开导航";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
