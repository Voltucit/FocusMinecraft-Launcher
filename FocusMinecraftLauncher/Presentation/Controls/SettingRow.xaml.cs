using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
namespace FocusMinecraftLauncher.Presentation.Controls;

/// <summary>统一设置行；空间不足时把编辑区放到标签下方，避免整体缩放。</summary>
[ContentProperty(nameof(Editor))]
public partial class SettingRow : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(""));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(""));
    public static readonly DependencyProperty EditorProperty = DependencyProperty.Register(nameof(Editor), typeof(object), typeof(SettingRow));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public object Editor { get => GetValue(EditorProperty); set => SetValue(EditorProperty, value); }
    public SettingRow() => InitializeComponent();
    private void ResizeRow(object sender, SizeChangedEventArgs e)
    {
        var compact = ActualWidth < 620;
        LabelColumn.Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(220);
        Grid.SetColumn(EditorHost, compact ? 0 : 1); Grid.SetRow(EditorHost, compact ? 1 : 0);
        Grid.SetColumnSpan(EditorHost, compact ? 2 : 1); Grid.SetColumnSpan(Labels, compact ? 2 : 1);
        EditorHost.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness();
    }
}
