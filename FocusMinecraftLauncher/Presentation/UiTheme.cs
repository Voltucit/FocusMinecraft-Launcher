using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Wpf.Ui.Controls;
namespace FocusMinecraftLauncher.Presentation;

/// <summary>集中管理毛玻璃与纯色主题，统一 WPF UI 资源；高对比度模式使用系统颜色。</summary>
public static partial class UiTheme
{
    public const string GlassThemeName = "模糊(默认)";
    public static void Apply(string theme, double glassTransparency = 50, bool hasCustomBackground = false)
    {
        RemoveSoftPreset();
        RemoveFlatPreset();
        theme = NormalizeTheme(theme);
        var glass = theme == GlassThemeName;
        var dark = glass || (theme == FlatThemeName && _flatNightMode) || (theme == SoftThemeName && _softNightMode);
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
            dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light,
            Wpf.Ui.Controls.WindowBackdropType.None, false);
        var colors = new Dictionary<string, string>
        {
            ["AppBackground"] = dark ? "#111111" : "#FCFCFC",
            ["SidebarBackground"] = dark ? "#191919" : "#F9F9F9",
            ["Surface"] = dark ? "#191919" : "#FFFFFF",
            ["ControlHover"] = dark ? "#222222" : "#F0F0F0",
            ["ControlPressed"] = dark ? "#313131" : "#E0E0E0",
            ["SelectionBackground"] = dark ? "#2A2A2A" : "#E8E8E8",
            ["AppText"] = dark ? "#EEEEEE" : "#202020",
            ["SelectedText"] = dark ? "#EEEEEE" : "#202020",
            ["MutedText"] = dark ? "#B4B4B4" : "#646464",
            ["BorderBrush"] = dark ? "#3A3A3A" : "#D9D9D9",
            ["SubtleBorder"] = dark ? "#2A2A2A" : "#E8E8E8",
            ["FocusRing"] = dark ? "#B4B4B4" : "#646464",
            ["PrimaryBackground"] = dark ? "#EEEEEE" : "#202020",
            ["PrimaryForeground"] = dark ? "#202020" : "#FCFCFC",
            ["AccentText"] = dark ? "#60C4A0" : "#208368",
            ["ErrorText"] = dark ? "#FF9592" : "#CE2C31",
            ["CaptionDangerHover"] = "#E5484D",
            ["CaptionDangerPressed"] = "#CE2C31",
            ["CaptionDangerForeground"] = "#FFFFFF"
        };
        if (glass)
        {
            foreach (var (key, value) in new Dictionary<string, string>
            {
                ["AppBackground"] = "#08090D", ["SidebarBackground"] = "#6608090D",
                ["Surface"] = "#B312131A", ["ControlHover"] = "#E6232535",
                ["ControlPressed"] = "#F02B2D40", ["SelectionBackground"] = "#337C5CFF",
                ["AppText"] = "#FFFFFF", ["SelectedText"] = "#FFFFFF", ["MutedText"] = "#B3FFFFFF",
                ["BorderBrush"] = "#33FFFFFF", ["SubtleBorder"] = "#1AFFFFFF",
                ["FocusRing"] = "#7C5CFF", ["PrimaryBackground"] = "#7C5CFF",
                ["PrimaryForeground"] = "#FFFFFF", ["AccentText"] = "#B5A4FF"
            }) colors[key] = value;
        }
        colors["TitleBarBackground"] = glass ? "#6608090D" : colors["AppBackground"];
        colors["ContentScrim"] = glass ? "#8012131A" : colors["AppBackground"];
        colors["CaptionText"] = glass ? "#80FFFFFF" : colors["MutedText"];
        colors["PrimaryHover"] = glass ? "#7354ED" : colors["PrimaryBackground"];
        colors["PrimaryPressed"] = glass ? "#6949D8" : colors["PrimaryBackground"];
        colors["PopupSurface"] = colors["Surface"];
        // 图片只作为背景氛围；日间用浅遮罩、夜间用深遮罩，面板与控件继续使用各主题表面。
        colors["BackgroundImageScrim"] = glass ? "#3308090D" : dark ? "#A608090D" : "#B3FFFFFF";
        // 壁纸本身已经提供画面，遮罩只负责前景可读性；不能沿用图片那种高不透明度浅色遮罩。
        colors["WallpaperScrim"] = glass ? "#2608090D" : dark ? "#4D08090D" : "#1F000000";
        foreach (var (key, value) in colors)
        {
            // 先完成画刷配置再发布到资源中；WPF 使用资源后可能冻结画刷，不能再修改它。
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value))
            {
                Opacity = glass && key == "PrimaryBackground" ? 0.94 : 1d
            };
            Application.Current.Resources[key] = brush;
        }
        if (SystemParameters.HighContrast)
        {
            foreach (var key in new[] { "AppBackground", "SidebarBackground", "Surface", "PopupSurface", "TitleBarBackground", "ContentScrim" }) Application.Current.Resources[key] = SystemColors.WindowBrush;
            foreach (var key in new[] { "AppText", "MutedText", "CaptionText", "FocusRing", "BorderBrush", "SubtleBorder", "AccentText", "ErrorText" }) Application.Current.Resources[key] = SystemColors.WindowTextBrush;
            foreach (var key in new[] { "SelectionBackground", "PrimaryBackground" }) Application.Current.Resources[key] = SystemColors.HighlightBrush;
            foreach (var key in new[] { "SelectedText", "PrimaryForeground" }) Application.Current.Resources[key] = SystemColors.HighlightTextBrush;
            Application.Current.Resources["ControlHover"] = SystemColors.ControlBrush;
            Application.Current.Resources["ControlPressed"] = SystemColors.ControlDarkBrush;
            Application.Current.Resources["CaptionDangerHover"] = SystemColors.HighlightBrush;
            Application.Current.Resources["CaptionDangerPressed"] = SystemColors.HighlightBrush;
            Application.Current.Resources["CaptionDangerForeground"] = SystemColors.HighlightTextBrush;
            Application.Current.Resources["PrimaryHover"] = SystemColors.HighlightBrush;
            Application.Current.Resources["PrimaryPressed"] = SystemColors.HighlightBrush;
        }
        if (theme == SoftThemeName && !SystemParameters.HighContrast) ApplySoftPreset();
        Application.Current.Resources["SoftEnabled"] = theme == SoftThemeName && !SystemParameters.HighContrast;
        if (theme == FlatThemeName && !SystemParameters.HighContrast) ApplyFlatPreset();
        Application.Current.Resources["FlatEnabled"] = theme == FlatThemeName && !SystemParameters.HighContrast;
        if (theme != FlatThemeName || SystemParameters.HighContrast)
        {
            Application.Current.Resources["NavigationSelectedBackground"] = Application.Current.Resources["SelectionBackground"];
            Application.Current.Resources["NavigationSelectedText"] = Application.Current.Resources["SelectedText"];
        }
        if (theme != SoftThemeName || SystemParameters.HighContrast)
        {
            Application.Current.Resources["SoftIconBackground"] = Application.Current.Resources["ControlHover"];
            Application.Current.Resources["IconCornerRadius"] = Application.Current.Resources["Radius12"];
        }
        // 复用控件库的模板与状态，只覆盖调色板，避免复制其验证、编辑和无障碍逻辑。
        foreach (var (libraryKey, appKey) in new Dictionary<string, string>
        {
            ["ButtonBackground"]="Surface", ["ButtonForeground"]="AppText",
            ["ButtonBackgroundPointerOver"]="ControlHover", ["ButtonBackgroundPressed"]="ControlPressed",
            ["ButtonForegroundPressed"]="AppText", ["ButtonBorderBrushPressed"]="BorderBrush",
            ["ControlElevationBorderBrush"]="BorderBrush", ["ControlStrokeColorDefaultBrush"]="BorderBrush",
            ["TextControlBackground"]="Surface", ["TextControlBackgroundPointerOver"]="ControlHover",
            ["TextControlBackgroundFocused"]="Surface", ["TextControlForeground"]="AppText",
            ["TextControlElevationBorderBrush"]="BorderBrush", ["TextControlFocusedBorderBrush"]="FocusRing",
            ["TextControlPlaceholderForeground"]="CaptionText", ["TextControlForegroundDisabled"]="CaptionText",
            ["AccentButtonBackground"]="PrimaryBackground", ["AccentButtonForeground"]="PrimaryForeground",
            ["AccentButtonBackgroundPointerOver"]="PrimaryHover", ["AccentButtonBackgroundPressed"]="PrimaryPressed",
            ["AccentButtonForegroundPointerOver"]="PrimaryForeground", ["AccentButtonForegroundPressed"]="PrimaryForeground",
            ["SliderThumbBackground"]="PrimaryBackground", ["SliderThumbBackgroundPointerOver"]="PrimaryHover",
            ["SliderOuterThumbBackground"]="Surface", ["SliderTrackFill"]="BorderBrush",
            ["SliderTrackFillPointerOver"]="MutedText"
        }) Application.Current.Resources[libraryKey] = Application.Current.Resources[appKey];
        ApplyCheckBoxPalette();
        ApplyGlassTransparency(theme, glassTransparency, hasCustomBackground);
        if (Application.Current.MainWindow is FluentWindow window) ApplyWindowMaterial(window, theme, hasCustomBackground);
    }

    /// <summary>已移除或无效的主题统一回到默认模糊主题，不再载入废弃资源。</summary>
    public static string NormalizeTheme(string? theme) => theme is FlatThemeName or SoftThemeName ? theme : GlassThemeName;

    /// <summary>复用 WPF UI 勾选框模板，把状态画刷接入当前主题；选色后同样即时更新。</summary>
    private static void ApplyCheckBoxPalette()
    {
        foreach (var (libraryKey, appKey) in new Dictionary<string, string>
        {
            ["CheckBoxForeground"] = "AppText", ["CheckBoxBackground"] = "Surface", ["CheckBoxBorderBrush"] = "BorderBrush",
            ["CheckBoxCheckBackgroundFillChecked"] = "PrimaryBackground", ["CheckBoxCheckBorderBrush"] = "PrimaryBackground",
            ["CheckBoxCheckGlyphForeground"] = "PrimaryForeground",
            ["CheckBoxCheckBackgroundFillCheckedPointerOver"] = _flatApplied ? "PrimaryBackground" : "PrimaryHover",
            ["CheckBoxCheckBackgroundFillCheckedPressed"] = _flatApplied ? "PrimaryBackground" : "PrimaryPressed",
            ["CheckBoxCheckBackgroundFillUncheckedPointerOver"] = "ControlHover",
            ["CheckBoxCheckBackgroundFillUncheckedPressed"] = "ControlPressed"
        }) Application.Current.Resources[libraryKey] = Application.Current.Resources[appKey];
    }

    /// <summary>仅更新背景遮罩画刷，不改变文字、控件透明度或系统 Acrylic 的模糊程度。</summary>
    public static void ApplyGlassTransparency(string theme, double percentage, bool hasCustomBackground = false)
    {
        if (theme != GlassThemeName || SystemParameters.HighContrast) return;
        if (hasCustomBackground)
        {
            // 用户图片亮度不固定，保留稳定的阅读遮罩；模糊只改变图片效果，不改变遮罩透明度。
            Application.Current.Resources["ContentScrim"] = new SolidColorBrush(Color.FromRgb(0x12, 0x13, 0x1A)) { Opacity = 0.68 };
            foreach (var key in new[] { "SidebarBackground", "TitleBarBackground" })
                Application.Current.Resources[key] = new SolidColorBrush(Color.FromRgb(0x08, 0x09, 0x0D)) { Opacity = 0.58 };
            return;
        }
        var transparency = Math.Clamp(percentage, 0, 100) / 100d;
        // 每次创建配置完整的新画刷，避免修改已被 WPF 冻结的共享资源。
        Application.Current.Resources["ContentScrim"] = new SolidColorBrush(Color.FromRgb(0x12, 0x13, 0x1A))
        { Opacity = 1 - transparency };
        var chromeOpacity = Math.Max(0, 1 - transparency * 1.2);
        Application.Current.Resources["SidebarBackground"] = new SolidColorBrush(Color.FromRgb(0x08, 0x09, 0x0D))
        { Opacity = chromeOpacity };
        Application.Current.Resources["TitleBarBackground"] = new SolidColorBrush(Color.FromRgb(0x08, 0x09, 0x0D))
        { Opacity = chromeOpacity };
    }

    /// <summary>未选自定义图时复用 WPF UI 系统 Acrylic；选图时停用系统背板，由独立背景图控件负责模糊。</summary>
    public static void ApplyWindowMaterial(FluentWindow window, string theme, bool hasCustomBackground = false)
    {
        if (!window.IsLoaded) return;
        if (theme == GlassThemeName && !hasCustomBackground && !SystemParameters.HighContrast && WindowBackdrop.IsSupported(WindowBackdropType.Acrylic))
        {
            window.WindowBackdropType = WindowBackdropType.Acrylic;
            if (WindowBackdrop.RemoveBackground(window)
                && WindowBackdrop.ApplyBackdrop(new WindowInteropHelper(window).Handle, WindowBackdropType.Acrylic))
                return;
        }
        window.WindowBackdropType = WindowBackdropType.None;
        WindowBackdrop.RemoveBackdrop(window);
        window.SetResourceReference(Control.BackgroundProperty, "AppBackground");
    }
}
