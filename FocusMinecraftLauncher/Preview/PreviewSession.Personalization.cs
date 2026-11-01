using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media.Imaging;
using FocusMinecraftLauncher.Infrastructure.Configuration;
using FocusMinecraftLauncher.Presentation;

namespace FocusMinecraftLauncher.Preview;

public sealed partial class PreviewSession
{
    private sealed class ThemeBackground
    {
        public BitmapSource? Image;
        public string Path = "", Name = "", Error = "";
        public double Blur;
        public bool Loaded, Loading;
        public int Revision;
    }
    private readonly Dictionary<string, ThemeBackground> _themeBackgrounds = new()
    { ["Glass"] = new(), ["MinimalistFlat"] = new(), ["SoftUI"] = new() };
    private ThemeBackground ActiveBackground => _themeBackgrounds[ThemeKey(Theme)];
    private bool _dynamicBlurEnabled;
    private BackgroundImageStore BackgroundImages => ((App)Application.Current).BackgroundImages;
    public BitmapSource? CustomBackgroundImage => ActiveBackground.Image;
    public bool HasCustomBackgroundImage => CustomBackgroundImage != null;
    public bool IsBlurTheme => Theme == UiTheme.GlassThemeName;
    public bool ShowCustomBackgroundImage => HasCustomBackgroundImage && !SystemParameters.HighContrast;
    public bool HasBackgroundSelection => ActiveBackground.Path.Length > 0;
    public bool CanAdjustBackgroundBlur => ShowCustomBackgroundImage && !DynamicBlurEnabled;
    public string BackgroundImageName => ActiveBackground.Loading ? "正在读取背景…" : HasBackgroundSelection
        ? ActiveBackground.Name.Length > 0 ? ActiveBackground.Name : "自定义图片" : IsBlurTheme ? "系统毛玻璃" : "默认背景";
    public string BackgroundError => ActiveBackground.Error;
    public string BackgroundBlurHint => SystemParameters.HighContrast ? "高对比度模式下停用" : !HasCustomBackgroundImage ? "选择背景图片后可调整"
        : DynamicBlurEnabled ? "关闭动态模糊后可调整" : "";

    /// <summary>动态模糊仍只属于模糊主题，其他主题不会继承其效果或修改其参数。</summary>
    public bool DynamicBlurEnabled
    {
        get => IsBlurTheme && _dynamicBlurEnabled;
        set
        {
            if (!IsBlurTheme || !Set(ref _dynamicBlurEnabled, value)) return;
            Changed(nameof(DynamicBlurHint)); Changed(nameof(CanAdjustBackgroundBlur)); Changed(nameof(BackgroundBlurHint));
            _configuration.ChangeLauncher(root => BackgroundProfile(root, "Glass")["dynamicBlurEnabled"] = value);
        }
    }
    public string DynamicBlurHint => !DynamicBlurEnabled ? "" : SystemParameters.HighContrast ? "高对比度模式下暂停"
        : !MotionAllowed ? "界面动画已关闭，当前使用固定模糊度"
        : BackgroundBlur == 0 ? "模糊度为 0 时保持清晰" : BackgroundBlur == 100 ? "已达到最大模糊度" : "";
    /// <summary>即时调整当前主题自己的背景模糊度；只影响图片，不模糊前景控件。</summary>
    public double BackgroundBlur
    {
        get => ActiveBackground.Blur;
        set
        {
            if (DynamicBlurEnabled) return;
            var state = ActiveBackground;
            var key = ThemeKey(Theme);
            var blur = Math.Clamp(double.IsFinite(value) ? value : IsBlurTheme ? 50 : 0, 0, 100);
            if (!Set(ref state.Blur, blur)) return;
            Changed(nameof(BackgroundBlurRadius)); Changed(nameof(DynamicBlurHint));
            _configuration.ChangeLauncher(root => BackgroundProfile(root, key)["backgroundBlur"] = blur);
        }
    }
    public double BackgroundBlurRadius => BackgroundBlur / 2;
    private static JsonObject BackgroundProfile(JsonObject root, string key) => LauncherConfiguration.Object(
        LauncherConfiguration.Object(LauncherConfiguration.Object(root, "appearance"), "themes"), key);

    /// <summary>恢复各主题独立的图片与模糊度；新增主题参数默认保持原有清晰效果。</summary>
    private void RestorePersonalization(JsonObject root)
    {
        foreach (var (key, state) in _themeBackgrounds)
        {
            var profile = root["appearance"]?["themes"]?[key];
            state.Path = LauncherConfiguration.Text(profile?["backgroundImage"]);
            state.Name = LauncherConfiguration.Text(profile?["backgroundImageName"]);
            state.Blur = Math.Clamp(LauncherConfiguration.Number(profile?["backgroundBlur"], key == "Glass" ? 50 : 0), 0, 100);
            state.Loaded = state.Path.Length == 0;
        }
        var glass = root["appearance"]?["themes"]?["Glass"];
        _dynamicBlurEnabled = LauncherConfiguration.Boolean(glass?["dynamicBlurEnabled"], false);
        ActiveBackground.Image = ((App)Application.Current).TakeInitialBackground();
        ActiveBackground.Error = ((App)Application.Current).InitialBackgroundError;
        ActiveBackground.Loaded = true;
    }

    /// <summary>切换主题后按需读取该主题的背景，已有缓存不重复解码。</summary>
    private void SelectThemeBackground()
    {
        var state = ActiveBackground;
        if (!state.Loaded && !state.Loading) _ = LoadThemeBackgroundAsync(ThemeKey(Theme), state);
    }

    private async Task LoadThemeBackgroundAsync(string key, ThemeBackground state)
    {
        var revision = ++state.Revision;
        state.Loading = true;
        try
        {
            var image = await BackgroundImages.LoadAsync(state.Path, _lifetime.Token);
            if (state.Revision == revision) { state.Image = image; state.Error = ""; state.Loaded = true; }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (IsBackgroundError(error))
        {
            if (state.Revision == revision)
            { state.Image = null; state.Loaded = true; state.Error = "背景图片无法读取，请重新选择图片。"; }
            Trace.TraceWarning("主题背景读取失败：{0}", error.GetType().Name);
        }
        finally
        {
            if (state.Revision == revision)
            {
                state.Loading = false;
                if (!_lifetime.IsCancellationRequested && ThemeKey(Theme) == key) RefreshPersonalization();
            }
        }
    }

    /// <summary>捕获操作开始时的主题，异步导入期间切换主题也不会把图片保存到其他主题。</summary>
    public async Task SetCustomBackgroundAsync(string source)
    {
        var key = ThemeKey(Theme);
        var state = _themeBackgrounds[key];
        var revision = ++state.Revision;
        try
        {
            var imported = await BackgroundImages.ImportAsync(source, _lifetime.Token);
            if (_lifetime.IsCancellationRequested || state.Revision != revision)
            { RemoveUnusedBackground(imported.RelativePath); return; }
            var previous = state.Path;
            state.Image = imported.Image; state.Path = imported.RelativePath; state.Name = imported.DisplayName;
            state.Error = ""; state.Loaded = true; state.Loading = false;
            if (ThemeKey(Theme) == key) RefreshPersonalization();
            await SaveBackgroundSelectionAsync(key, state, previous, revision);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (IsBackgroundError(error))
        {
            if (state.Revision == revision)
            {
                state.Loading = false;
                state.Error = error is UnauthorizedAccessException ? "无法保存背景图片，请检查程序目录的写入权限。"
                    : error is IOException ? "图片读取或保存失败，请检查文件和程序目录。"
                    : "无法读取这张图片，请选择有效的 PNG、JPEG 或 BMP 图片。";
                if (ThemeKey(Theme) == key) Signal();
            }
            Trace.TraceWarning("背景图片导入失败：{0}", error.GetType().Name);
        }
    }

    /// <summary>仅恢复当前主题的默认背景，其他主题的图片和选择保持不变。</summary>
    public async Task ResetCustomBackgroundAsync()
    {
        var key = ThemeKey(Theme);
        var state = _themeBackgrounds[key];
        var previous = state.Path;
        var revision = ++state.Revision;
        state.Image = null; state.Path = state.Name = state.Error = "";
        state.Loaded = true; state.Loading = false;
        RefreshPersonalization();
        await SaveBackgroundSelectionAsync(key, state, previous, revision);
    }

    private async Task SaveBackgroundSelectionAsync(string key, ThemeBackground state, string previous, int revision)
    {
        var selected = state.Path;
        var name = state.Name;
        _configuration.ChangeLauncher(root =>
        {
            var profile = BackgroundProfile(root, key);
            profile["backgroundImage"] = selected; profile["backgroundImageName"] = name;
        });
        if (await _configuration.FlushAsync()) RemoveUnusedBackground(previous);
        else if (state.Revision == revision)
        {
            state.Error = "背景已应用，但设置未保存。请检查程序目录的写入权限。";
            if (ThemeKey(Theme) == key) Changed(nameof(BackgroundError));
        }
    }

    private void RemoveUnusedBackground(string path)
    {
        // 多个主题若引用相同副本，不能在重置其中一个主题时误删另一个主题的图片。
        if (!_themeBackgrounds.Values.Any(state => string.Equals(state.Path, path, StringComparison.OrdinalIgnoreCase)))
            BackgroundImages.RemoveOwnedCopy(path);
    }
    private void RefreshPersonalization()
    { UiTheme.Apply(Theme, GlassTransparency, HasCustomBackgroundImage); Signal(); }
    internal static bool IsBackgroundError(Exception error) => error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException;
}
