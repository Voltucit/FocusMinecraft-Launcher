using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using FMLauncher.Infrastructure.Persistence;
namespace FMLauncher.Appearance.Themes;

/// <summary>统一语义资源与 Fluent 主题。所有资源修改必须由 UI 线程调用。</summary>
public sealed class ThemeService : IDisposable
{
    private ThemeSettings _settings = new();
    public bool AnimationsEnabled => _settings.Animations;
    public string Layout => _settings.Layout;
    public event Action? Applied;
    public ThemeService() => SystemEvents.UserPreferenceChanged += SystemChanged;
    public static ThemeSettings Preset(string id) => id switch
    {
        "Immersive" => new() { Preset = id, Mode = "Dark", CardOpacity = 0.78, Accent = "#7DD3FC", Radius = 20 },
        "Anime" => new() { Preset = id, Accent = "#B18CE5", Radius = 24, CardOpacity = 0.80 },
        "Minimal" => new() { Preset = id, Mode = "Light", Accent = "#287D6B", Radius = 6, Material = "None", Layout = "Compact", Animations = false },
        _ => new()
    };
    public static void Validate(ThemeSettings value)
    {
        if (!new[] { "System", "Dark", "Light" }.Contains(value.Mode) ||
            !new[] { "None", "Mica", "Acrylic" }.Contains(value.Material) ||
            !new[] { "Spacious", "Compact" }.Contains(value.Layout) ||
            !Regex.IsMatch(value.Accent ?? "", "^#[0-9A-Fa-f]{6}$") ||
            !string.IsNullOrEmpty(value.Background) && !Regex.IsMatch(value.Background, "^#[0-9A-Fa-f]{6}$") ||
            !double.IsFinite(value.WallpaperOpacity) || value.WallpaperOpacity is < 0 or > 1 ||
            !double.IsFinite(value.CardOpacity) || value.CardOpacity is < 0.2 or > 1 ||
            !double.IsFinite(value.Radius) || value.Radius is < 0 or > 32 ||
            !double.IsFinite(value.Blur) || value.Blur is < 0 or > 40) throw new InvalidDataException("主题数据无效：请检查颜色、透明度、圆角及模糊范围。");
    }
    /// <summary>在 UI 线程应用已校验的主题快照，防止后续编辑影响系统主题回调。</summary>
    public void Apply(ThemeSettings value)
    {
        Validate(value); _settings = JsonSerializer.Deserialize<ThemeSettings>(JsonSerializer.Serialize(value))!;
        var dark = value.Mode == "Dark" || value.Mode == "System" && IsSystemDark();
        var theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        var material = Enum.Parse<WindowBackdropType>(value.Material);
        ApplicationThemeManager.Apply(theme, material);
        var accent = (Color)ColorConverter.ConvertFromString(value.Accent);
        ApplicationAccentColorManager.Apply(accent, theme, false);
        var resources = Application.Current.Resources;
        void Brush(string key, Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); resources[key] = brush; }
        Brush("Ink", dark ? Color.FromRgb(236, 242, 245) : Color.FromRgb(27, 42, 51));
        Brush("Muted", dark ? Color.FromRgb(165, 180, 190) : Color.FromRgb(84, 103, 115));
        Brush("Canvas", string.IsNullOrEmpty(value.Background) ? dark ? Color.FromRgb(16, 23, 29) : Color.FromRgb(242, 246, 248) : (Color)ColorConverter.ConvertFromString(value.Background));
        Brush("Surface", Color.FromArgb((byte)(value.CardOpacity * 255), dark ? (byte)24 : (byte)255, dark ? (byte)35 : (byte)255, dark ? (byte)43 : (byte)255));
        Brush("Line", dark ? Color.FromRgb(53, 69, 79) : Color.FromRgb(213, 223, 229));
        Brush("SurfaceSoft", Color.FromArgb((byte)(value.CardOpacity * 255), dark ? (byte)30 : (byte)246, dark ? (byte)43 : (byte)249, dark ? (byte)52 : (byte)251));
        Brush("AccentSoft", Color.FromArgb(32, accent.R, accent.G, accent.B));
        Brush("Accent", accent);
        Brush("OnAccent", (accent.R * 299 + accent.G * 587 + accent.B * 114) / 1000 > 155 ? Colors.Black : Colors.White);
        resources["CardRadius"] = new CornerRadius(value.Radius);
        resources["PagePadding"] = value.Layout == "Compact" ? new Thickness(16, 12, 16, 16) : new Thickness(24, 16, 24, 20);
        resources["WallpaperBlur"] = value.Blur;
        resources["WallpaperOpacity"] = value.WallpaperOpacity;
        foreach (Window window in Application.Current.Windows)
            if (window is FluentWindow fluent) fluent.WindowBackdropType = material;
        Applied?.Invoke();
    }
    private static bool IsSystemDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }
    private void SystemChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_settings.Mode == "System" && !Application.Current.Dispatcher.HasShutdownStarted)
            Application.Current.Dispatcher.BeginInvoke(new Action(() => Apply(_settings)));
    }
    /// <summary>导入有限 JSON 并拒绝未知字段，不实例化任何不可信界面或脚本。</summary>
    public ThemeSettings Import(string path)
    {
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("主题文件超过 64 KiB。");
        var value = JsonSerializer.Deserialize<ThemeSettings>(File.ReadAllText(path), new JsonSerializerOptions(AtomicJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidDataException("主题为空。");
        Validate(value); return value;
    }
    public void Export(string path, ThemeSettings value) { Validate(value); AtomicJson.Write(path, value); }
    public void ExtractAccent(BitmapSource source)
    {
        if (!_settings.ExtractAccent) return;
        var tiny = new TransformedBitmap(source, new ScaleTransform(32d / source.PixelWidth, 32d / source.PixelHeight));
        var pixels = new FormatConvertedBitmap(tiny, PixelFormats.Bgra32, null, 0);
        var data = new byte[pixels.PixelWidth * pixels.PixelHeight * 4];
        pixels.CopyPixels(data, pixels.PixelWidth * 4, 0);
        long r = 0, g = 0, b = 0; var count = data.Length / 4;
        for (var i = 0; i < data.Length; i += 4) { b += data[i]; g += data[i + 1]; r += data[i + 2]; }
        _settings.Accent = $"#{r / count:X2}{g / count:X2}{b / count:X2}"; Apply(_settings);
    }
    public void Dispose() => SystemEvents.UserPreferenceChanged -= SystemChanged;
}

