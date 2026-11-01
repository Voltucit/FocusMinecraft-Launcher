using System.Collections;
using System.Windows;
using System.Windows.Media;

namespace FocusMinecraftLauncher.Presentation;

public static partial class UiTheme
{
    public const string SoftThemeName = "柔和";
    public const string DefaultSoftAccent = "#6366F1";
    private static ResourceDictionary? _softResources;
    private static ResourceDictionary? _softNightResources;
    private static bool _softNightMode;
    private static bool _softApplied;
    private static string _softAccent = DefaultSoftAccent;

    public static bool TryNormalizeSoftAccent(string? value, out string normalized)
    {
        // 复用相同的六位不透明颜色校验，各主题的值和配置仍分别保存。
        var valid = TryNormalizeFlatAccent(value, out normalized);
        if (!valid) normalized = DefaultSoftAccent;
        return valid;
    }

    /// <summary>更新 Soft UI 自己的调色板，画刷配置完成并冻结后再交给 WPF 使用。</summary>
    public static void SetSoftAccent(string value)
    {
        _softAccent = TryNormalizeSoftAccent(value, out var normalized) ? normalized : DefaultSoftAccent;
        if (_softApplied) { ApplySoftAccent(); ApplyCheckBoxPalette(); }
    }

    /// <summary>柔和主题单独持有夜间偏好，切换时重建表面与控件资源。</summary>
    public static void SetSoftNightMode(bool enabled)
    {
        _softNightMode = enabled;
        if (_softApplied) Apply(SoftThemeName);
    }

    private static void ApplySoftPreset()
    {
        _softResources ??= new ResourceDictionary
        { Source = new Uri("pack://application:,,,/FocusMinecraftLauncher;component/Presentation/Resources/SoftUI.xaml") };
        foreach (DictionaryEntry entry in _softResources) Application.Current.Resources[entry.Key] = entry.Value;
        if (_softNightMode)
        {
            _softNightResources ??= new ResourceDictionary
            { Source = new Uri("pack://application:,,,/FocusMinecraftLauncher;component/Presentation/Resources/SoftUI.Night.xaml") };
            foreach (DictionaryEntry entry in _softNightResources) Application.Current.Resources[entry.Key] = entry.Value;
        }
        _softApplied = true;
        ApplySoftAccent();
    }

    /// <summary>卸载本主题覆盖项，其他主题的原始资源不作修改。</summary>
    private static void RemoveSoftPreset()
    {
        Application.Current.Resources["SoftEnabled"] = false;
        if (!_softApplied || _softResources == null) return;
        foreach (var key in _softResources.Keys) Application.Current.Resources.Remove(key);
        _softApplied = false;
    }

    private static Color SoftMix(Color first, Color second, double amount) => Color.FromRgb(
        (byte)Math.Round(first.R + (second.R - first.R) * amount),
        (byte)Math.Round(first.G + (second.G - first.G) * amount),
        (byte)Math.Round(first.B + (second.B - first.B) * amount));

    private static double SoftLuminance(Color color)
    {
        static double Linear(byte channel)
        { var value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }

    /// <summary>保持原色相，必要时逐步加深以满足小字号文字的对比度。</summary>
    private static Color SoftReadableShade(Color color, Color background, bool whiteText)
    {
        for (var step = 0; step <= 100; step++)
        {
            var shade = SoftMix(color, Colors.Black, step / 100d);
            var contrast = whiteText ? 1.05 / (SoftLuminance(shade) + .05)
                : (SoftLuminance(background) + .05) / (SoftLuminance(shade) + .05);
            if (contrast >= 4.5) return shade;
        }
        return Colors.Black;
    }

    private static void SoftResourceBrush(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }

    private static void ApplySoftAccent()
    {
        var accent = (Color)ColorConverter.ConvertFromString(_softAccent);
        var pale = _softNightMode ? SoftMix(Color.FromRgb(0x22, 0x26, 0x31), accent, .14)
            : _softAccent == DefaultSoftAccent ? Color.FromRgb(0xEE, 0xF2, 0xFF) : SoftMix(Colors.White, accent, .10);
        var fill = SoftReadableShade(accent, Colors.White, true);
        var text = _softNightMode ? SoftReadableTint(accent, pale)
            : SoftReadableShade(SoftMix(accent, Colors.Black, .12), pale, false);
        var hover = SoftMix(fill, Colors.Black, .06);
        var pressed = SoftMix(fill, Colors.Black, .12);
        foreach (var key in new[] { "PrimaryBackground", "AccentButtonBackground", "SliderThumbBackground" }) SoftResourceBrush(key, fill);
        foreach (var key in new[] { "PrimaryHover", "AccentButtonBackgroundPointerOver", "SliderThumbBackgroundPointerOver" }) SoftResourceBrush(key, hover);
        foreach (var key in new[] { "PrimaryPressed", "AccentButtonBackgroundPressed" }) SoftResourceBrush(key, pressed);
        foreach (var key in new[] { "SelectionBackground", "SoftIconBackground", "NavigationSelectedBackground" }) SoftResourceBrush(key, pale);
        foreach (var key in new[] { "AccentText", "SelectedText", "NavigationSelectedText", "FocusRing" }) SoftResourceBrush(key, text);
        SoftResourceBrush("SoftAccent", accent);
        SoftResourceBrush("SoftFocusRing", _softNightMode ? text : Color.FromArgb(0x73, accent.R, accent.G, accent.B));
        Application.Current.Resources["SoftShadowColor"] = _softNightMode ? Color.FromRgb(0x04, 0x06, 0x0B) : accent;
    }

    /// <summary>夜间文字逐步提亮并保留强调色色相，与选中表面达到正常文字对比度。</summary>
    private static Color SoftReadableTint(Color color, Color background)
    {
        for (var step = 0; step <= 100; step++)
        {
            var tint = SoftMix(color, Colors.White, step / 100d);
            if ((SoftLuminance(tint) + .05) / (SoftLuminance(background) + .05) >= 4.5) return tint;
        }
        return Colors.White;
    }
}
