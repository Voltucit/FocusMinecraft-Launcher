using System.Collections.ObjectModel;
using FocusMinecraftLauncher.Common;
namespace FocusMinecraftLauncher.Features.Home.Models;

/// <summary>前端导出勾选树；父级汇总三态，勾选父级递归更新可选子项。</summary>
public sealed class ExportSelectionNode : ObservableObject
{
    private bool? _checked;
    private bool _updating, _expanded = true;
    public string Key { get; }
    public string Title { get; }
    public string Hint { get; }
    public bool CanToggle { get; }
    public bool Sensitive { get; }
    public ObservableCollection<ExportSelectionNode> Children { get; } = [];
    public ExportSelectionNode? Parent { get; private set; }
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
    public bool? IsChecked { get => _checked; set => Apply(value ?? true); }
    public ExportSelectionNode(string key, string title, bool selected = false, string hint = "", bool canToggle = true, bool sensitive = false)
    { Key=key;Title=title;_checked=selected;Hint=hint;CanToggle=canToggle;Sensitive=sensitive; }
    public ExportSelectionNode Add(ExportSelectionNode node)
    { node.Parent=this;Children.Add(node);Recalculate();return this; }
    public IEnumerable<ExportSelectionNode> Walk()
    { yield return this; foreach(var child in Children) foreach(var item in child.Walk()) yield return item; }
    private void Apply(bool value)
    {
        if (!CanToggle) return;
        _updating=true;
        foreach(var child in Children) child.Apply(value);
        _updating=false;
        if(Children.Count>0) Recalculate(); else { Set(ref _checked,value,nameof(IsChecked));Parent?.Recalculate(); }
    }
    private void Recalculate()
    {
        if(_updating || Children.Count==0)return;
        bool? value=Children.All(n=>n.IsChecked==true) ? true : Children.All(n=>n.IsChecked==false) ? false : null;
        Set(ref _checked,value,nameof(IsChecked));Parent?.Recalculate();
    }
}
