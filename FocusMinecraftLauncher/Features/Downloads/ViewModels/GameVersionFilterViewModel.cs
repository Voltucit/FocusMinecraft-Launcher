using System.Windows.Input;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Features.Downloads.Models;

namespace FocusMinecraftLauncher.Features.Downloads.ViewModels;

/// <summary>只从本地兼容版本集合生成单选项；不接受手动输入，也不查询版本服务。</summary>
public sealed class GameVersionFilterViewModel : ObservableObject
{
    private readonly Action _selectionChanged;
    private bool _expanded;
    private string _selected = "全部";
    public string SelectedVersion => _selected;
    public string SelectionText => IsAllSelected ? "全部版本" : SelectedVersion;
    public bool IsAllSelected => _selected == "全部";
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
    public IReadOnlyList<GameVersionSeriesViewModel> Series { get; }
    public UiCommand SelectAll { get; }

    public GameVersionFilterViewModel(IEnumerable<string> versions, Action selectionChanged)
    {
        _selectionChanged = selectionChanged;
        Series = versions.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(version => version, MinecraftVersionOrder.Instance)
            .GroupBy(MinecraftVersionOrder.Series)
            .Select(group => new GameVersionSeriesViewModel(group.Key,
                group.Select(version => new GameVersionChoiceViewModel(version, this)))).ToArray();
        SelectAll = new(_ => Select("全部"));
    }

    public void Select(string version)
    {
        if (version != "全部" && !Series.SelectMany(series => series.Versions).Any(item => item.Number == version)) return;
        if (!Set(ref _selected, version, nameof(SelectedVersion))) return;
        Changed(nameof(SelectionText)); Changed(nameof(IsAllSelected));
        foreach (var choice in Series.SelectMany(series => series.Versions)) choice.Refresh();
        _selectionChanged();
        CommandManager.InvalidateRequerySuggested();
    }
}

public sealed class GameVersionSeriesViewModel(string title, IEnumerable<GameVersionChoiceViewModel> versions) : ObservableObject
{
    private bool _expanded;
    public string Title { get; } = title;
    public IReadOnlyList<GameVersionChoiceViewModel> Versions { get; } = versions.ToArray();
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
}

public sealed class GameVersionChoiceViewModel(string number, GameVersionFilterViewModel owner) : ObservableObject
{
    public string Number { get; } = number;
    public bool IsSelected => owner.SelectedVersion == Number;
    public UiCommand Select { get; } = new(_ => owner.Select(number));
}

