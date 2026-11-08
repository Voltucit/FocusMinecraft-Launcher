using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FocusMinecraftLauncher.Features.Settings.ViewModels;
using FocusMinecraftLauncher.Infrastructure.Runtime;

namespace FocusMinecraftLauncher.Features.Settings.Views;

/// <summary>按 Wallpaper Engine 的属性类型生成简单的原生编辑器。</summary>
public sealed class WallpaperPropertiesEditor : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(WallpaperPropertiesEditor),
        new PropertyMetadata(null, OnItemsSourceChanged));

    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    private readonly StackPanel _panel = new();
    private INotifyCollectionChanged? _observed;

    public WallpaperPropertiesEditor()
    {
        Content = _panel;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    private static void OnItemsSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var editor = (WallpaperPropertiesEditor)sender;
        if (editor._observed is not null) editor._observed.CollectionChanged -= editor.OnCollectionChanged;
        editor._observed = e.NewValue as INotifyCollectionChanged;
        if (editor._observed is not null) editor._observed.CollectionChanged += editor.OnCollectionChanged;
        editor.Rebuild();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        _panel.Children.Clear();
        if (ItemsSource is null) return;
        foreach (object item in ItemsSource)
        {
            if (item is not WallpaperPropertyEditorViewModel editor) continue;
            _panel.Children.Add(BuildRow(editor));
        }
    }

    private FrameworkElement BuildRow(WallpaperPropertyEditorViewModel editor)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock
        {
            Text = editor.Label,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            ToolTip = editor.Label
        };
        label.SetResourceReference(ForegroundProperty, "AppText");
        grid.Children.Add(label);

        FrameworkElement input = editor.Kind switch
        {
            WallpaperPropertyKind.Slider => BuildSlider(editor),
            WallpaperPropertyKind.Bool => BuildBool(editor),
            WallpaperPropertyKind.Combo => BuildCombo(editor),
            WallpaperPropertyKind.Color => BuildColor(editor),
            _ => BuildText(editor)
        };
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
        return grid;
    }

    private static FrameworkElement BuildSlider(WallpaperPropertyEditorViewModel editor)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        var slider = new Slider
        {
            Minimum = editor.Minimum,
            Maximum = editor.Maximum,
            Value = editor.NumberValue,
            SmallChange = Math.Max((editor.Maximum - editor.Minimum) / 100, 0.001),
            LargeChange = Math.Max((editor.Maximum - editor.Minimum) / 10, 0.01),
            IsMoveToPointEnabled = true,
            MinHeight = 32,
            VerticalAlignment = VerticalAlignment.Center
        };
        var value = new TextBlock
        {
            Text = editor.NumberValue.ToString("0.###", CultureInfo.InvariantCulture),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right
        };
        value.SetResourceReference(ForegroundProperty, "AppText");
        slider.ValueChanged += (_, e) =>
        {
            editor.NumberValue = e.NewValue;
            value.Text = e.NewValue.ToString("0.###", CultureInfo.InvariantCulture);
        };
        grid.Children.Add(slider);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    private static FrameworkElement BuildBool(WallpaperPropertyEditorViewModel editor)
    {
        var check = new CheckBox
        {
            IsChecked = editor.BoolValue,
            MinHeight = 32,
            VerticalAlignment = VerticalAlignment.Center
        };
        check.Checked += (_, _) => editor.BoolValue = true;
        check.Unchecked += (_, _) => editor.BoolValue = false;
        return check;
    }

    private static FrameworkElement BuildCombo(WallpaperPropertyEditorViewModel editor)
    {
        var combo = new ComboBox
        {
            ItemsSource = editor.Options,
            DisplayMemberPath = "Label",
            SelectedValuePath = "Value",
            MinHeight = 32
        };
        combo.SelectedValue = editor.Value;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedValue is string value) editor.TextValue = value;
        };
        return combo;
    }

    private static FrameworkElement BuildText(WallpaperPropertyEditorViewModel editor)
    {
        var text = new TextBox
        {
            Text = editor.TextValue,
            MinHeight = 32,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        text.TextChanged += (_, _) => editor.TextValue = text.Text;
        return text;
    }

    private static FrameworkElement BuildColor(WallpaperPropertyEditorViewModel editor)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var swatch = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Background = ParseColor(editor.Value)
        };
        swatch.SetResourceReference(BorderBrushProperty, "AppText");
        var text = new TextBox
        {
            Text = editor.Value,
            MinHeight = 32,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        text.TextChanged += (_, _) =>
        {
            editor.TextValue = text.Text;
            swatch.Background = ParseColor(text.Text);
        };
        grid.Children.Add(swatch);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private static Brush ParseColor(string value)
    {
        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double r)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double g)
            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
        {
            return new SolidColorBrush(Color.FromRgb(ToByte(r), ToByte(g), ToByte(b)));
        }
        if (value.StartsWith('#') && (value.Length == 7 || value.Length == 9))
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); }
            catch (Exception) { }
        }
        return Brushes.Transparent;

        static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
    }
}
