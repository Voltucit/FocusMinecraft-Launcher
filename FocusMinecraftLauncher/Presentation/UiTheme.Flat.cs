using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FocusMinecraftLauncher.Presentation;

public static partial class UiTheme
{
    public const string FlatThemeName = "极简";
    public const string DefaultFlatAccent = "#FF3366";
    private static ResourceDictionary? _flatResources;
    private static ResourceDictionary? _flatNightResources;
    private static bool _flatNightMode;
    private static bool _flatApplied;
    private static string _flatAccent = DefaultFlatAccent;

    /// <summary>只接受不透明的六位十六进制颜色，避免透明强调色破坏扁平主题。</summary>
    public static bool TryNormalizeFlatAccent(string? value, out string normalized)
    {
        var text = value?.Trim() ?? "";
        normalized = DefaultFlatAccent;
        if (text.Length != 7 || text[0] != '#' || !uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return false;
        normalized = "#" + rgb.ToString("X6", CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>保存当前外观会话的强调色；配置写入仍使用项目原有的外观存储。</summary>
    public static void SetFlatAccent(string value)
    {
        _flatAccent = TryNormalizeFlatAccent(value, out var normalized) ? normalized : DefaultFlatAccent;
        if (_flatApplied) { ApplyFlatAccent(); ApplyCheckBoxPalette(); }
    }

    /// <summary>独立切换极简夜间配色；重新应用主题，确保日间资源不残留。</summary>
    public static void SetFlatNightMode(bool enabled)
    {
        _flatNightMode = enabled;
        if (_flatApplied) Apply(FlatThemeName);
    }

    private static void ApplyFlatPreset()
    {
        _flatResources ??= new ResourceDictionary
        { Source = new Uri("pack://application:,,,/FocusMinecraftLauncher;component/Presentation/Resources/MinimalistFlat.xaml") };
        foreach (DictionaryEntry entry in _flatResources)
            Application.Current.Resources[entry.Key] = entry.Value;
        if (_flatNightMode)
        {
            _flatNightResources ??= new ResourceDictionary
            { Source = new Uri("pack://application:,,,/FocusMinecraftLauncher;component/Presentation/Resources/MinimalistFlat.Night.xaml") };
            foreach (DictionaryEntry entry in _flatNightResources) Application.Current.Resources[entry.Key] = entry.Value;
        }
        _flatApplied = true;
        ApplyFlatAccent();
    }

    /// <summary>卸载本预设的覆盖项，已有主题的资源字典保持原样。</summary>
    private static void RemoveFlatPreset()
    {
        Application.Current.Resources["FlatEnabled"] = false;
        if (!_flatApplied || _flatResources == null) return;
        foreach (var key in _flatResources.Keys) Application.Current.Resources.Remove(key);
        _flatApplied = false;
    }

    private static SolidColorBrush FlatBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>按 WCAG 相对亮度在黑白文字中选择更高对比度的一种，颜色在发布后不再修改。</summary>
    private static void ApplyFlatAccent()
    {
        var color = (Color)ColorConverter.ConvertFromString(_flatAccent);
        static double Linear(byte component)
        {
            var value = component / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        var luminance = .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        var foreground = (luminance + .05) / .05 >= 1.05 / (luminance + .05) ? Colors.Black : Colors.White;
        var accent = FlatBrush(color);
        foreach (var key in new[] { "PrimaryBackground", "FlatAccent", "AccentButtonBackground", "SliderThumbBackground" })
            Application.Current.Resources[key] = accent;
        // 焦点色按当前纸色校验；日夜模式均避免自定义强调色与背景融为一体。
        var background = _flatNightMode ? Color.FromRgb(0x11, 0x11, 0x13) : Colors.White;
        var backgroundLuminance = .2126 * Linear(background.R) + .7152 * Linear(background.G) + .0722 * Linear(background.B);
        var contrast = (Math.Max(luminance, backgroundLuminance) + .05) / (Math.Min(luminance, backgroundLuminance) + .05);
        Application.Current.Resources["FocusRing"] = contrast >= 3 ? accent : FlatBrush(_flatNightMode ? Colors.White : Colors.Black);
        var text = FlatBrush(foreground);
        Application.Current.Resources["PrimaryForeground"] = text;
        Application.Current.Resources["AccentButtonForeground"] = text;
    }
}
