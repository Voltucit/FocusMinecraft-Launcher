using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media.Imaging;
using FocusMinecraftLauncher.Infrastructure.Configuration;
using FocusMinecraftLauncher.Infrastructure.Runtime;
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
    private bool _wallpaperEnabled, _wallpaperLoading, _notifyWallpaperReady, _wallpaperStaticMode;
    private int _wallpaperRequestRevision, _wallpaperRefreshVersion;
    private CancellationTokenSource? _wallpaperRead;
    private string _wallpaperKind = "", _wallpaperPath = "", _wallpaperPreview = "", _wallpaperName = "", _wallpaperError = "", _wallpaperProject = "";
    private IReadOnlyList<WallpaperProperty> _wallpaperProperties = [];
    private Dictionary<string, string> _wallpaperPropertyValues = new(StringComparer.Ordinal);
    private string _wallpaperPropertiesJson = "{}";
    private BackgroundImageStore BackgroundImages => ((App)Application.Current).BackgroundImages;
    public BitmapSource? CustomBackgroundImage => ActiveBackground.Image;
    public bool HasCustomBackgroundImage => CustomBackgroundImage != null;
    public bool IsBlurTheme => Theme == UiTheme.GlassThemeName;
    public bool ShowCustomBackgroundImage => HasCustomBackgroundImage && !SystemParameters.HighContrast;
    public bool ShowImageBackground => ShowCustomBackgroundImage && !HasWallpaper;
    public bool WallpaperEnabled => _wallpaperEnabled && !SystemParameters.HighContrast;
    public bool WallpaperStaticMode
    {
        get => _wallpaperStaticMode;
        set
        {
            if (!Set(ref _wallpaperStaticMode, value)) return;
            _wallpaperRefreshVersion++;
            _wallpaperError = "";
            _configuration.ChangeLauncher(root => LauncherConfiguration.Object(root, "appearance")["wallpaperStaticMode"] = value);
            NotifyWallpaper();
        }
    }
    public bool HasWallpaper => WallpaperEnabled && _wallpaperKind.Length > 0 && File.Exists(_wallpaperPath);
    public bool IsWallpaperLoading => _wallpaperLoading;
    public string WallpaperKind => _wallpaperKind;
    public string WallpaperPath => _wallpaperPath;
    public string WallpaperPreviewPath => _wallpaperPreview;
    public string WallpaperProject => _wallpaperProject;
    public string WallpaperName => _wallpaperLoading ? "正在读取壁纸…" : !_wallpaperEnabled ? "未启用"
        : _wallpaperName.Length > 0 ? _wallpaperName : "Wallpaper Engine 壁纸";
    public string WallpaperError => _wallpaperError;
    public IReadOnlyList<WallpaperProperty> WallpaperProperties => _wallpaperProperties;
    public string WallpaperPropertiesJson => _wallpaperPropertiesJson;
    public int WallpaperRefreshVersion => _wallpaperRefreshVersion;
    public bool HasBackgroundSelection => ActiveBackground.Path.Length > 0;
    public bool CanAdjustBackgroundBlur => (ShowCustomBackgroundImage || HasWallpaper) && !DynamicBlurEnabled;
    public string BackgroundImageName => ActiveBackground.Loading ? "正在读取背景…" : HasBackgroundSelection
        ? ActiveBackground.Name.Length > 0 ? ActiveBackground.Name : "自定义图片" : IsBlurTheme ? "系统毛玻璃" : "默认背景";
    public string BackgroundError => ActiveBackground.Error;
    public string BackgroundBlurHint => SystemParameters.HighContrast ? "高对比度模式下停用" : !HasCustomBackgroundImage && !HasWallpaper ? "选择背景图片或壁纸后可调整"
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
            Changed(nameof(BackgroundBlur)); Changed(nameof(BackgroundBlurRadius)); Changed(nameof(DynamicBlurHint));
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
        _wallpaperEnabled = LauncherConfiguration.Boolean(root["appearance"]?["wallpaperEnabled"], false);
        _wallpaperStaticMode = LauncherConfiguration.Boolean(root["appearance"]?["wallpaperStaticMode"], false);
        _wallpaperProject = LauncherConfiguration.Text(root["appearance"]?["wallpaperProject"]);
        ActiveBackground.Image = ((App)Application.Current).TakeInitialBackground();
        ActiveBackground.Error = ((App)Application.Current).InitialBackgroundError;
        ActiveBackground.Loaded = true;
        if (_wallpaperEnabled) _ = RefreshWallpaperAsync();
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
            if (HasWallpaper) await ClearWallpaperAsync();
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
    /// <summary>读取当前桌面壁纸；场景与网页交给播放器，只有实际出画后才提示应用成功。</summary>
    public Task UseWallpaperEngineAsync() => LoadWallpaperAsync(WallpaperEngineService.GetCurrentAsync, true);
    /// <summary>在后台解析本地项目，快速切换时取消并忽略上一次选择。</summary>
    public Task UseWallpaperAsync(string projectPath) => LoadWallpaperAsync(
        token => Task.Run(() => WallpaperEngineService.ResolvePath(projectPath, token), token), true);
    /// <summary>重新读取当前项目；失败时保留选择，并在设置页显示原因。</summary>
    public Task RefreshWallpaperAsync() => !_wallpaperEnabled ? Task.CompletedTask : LoadWallpaperAsync(
        token => _wallpaperProject.Length > 0
            ? Task.Run(() => WallpaperEngineService.ResolvePath(_wallpaperProject, token), token)
            : WallpaperEngineService.GetCurrentAsync(token), false);

    private async Task LoadWallpaperAsync(Func<CancellationToken, Task<WallpaperDescriptor>> read, bool notify)
    {
        _wallpaperRead?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _wallpaperRead = cancellation;
        int revision = ++_wallpaperRequestRevision;
        _wallpaperLoading = true; _wallpaperError = ""; _notifyWallpaperReady = false;
        NotifyWallpaper();
        try
        {
            var wallpaper = await read(cancellation.Token);
            if (revision != _wallpaperRequestRevision || cancellation.IsCancellationRequested) return;
            _notifyWallpaperReady = notify;
            ApplyWallpaper(wallpaper);
            _wallpaperEnabled = true;
            NotifyWallpaper(); RefreshPersonalization();
            await SaveWallpaperEnabledAsync(true);
            if (revision != _wallpaperRequestRevision || cancellation.IsCancellationRequested) return;
            if (HasCustomBackgroundImage) await ResetCustomBackgroundAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (revision == _wallpaperRequestRevision)
            {
                _notifyWallpaperReady = false;
                _wallpaperError = Infrastructure.Runtime.RuntimeSupport.Error(error);
                WallpaperDiagnostics.Write(error.ToString());
            }
        }
        finally
        {
            if (revision == _wallpaperRequestRevision)
            {
                _wallpaperLoading = false;
                NotifyWallpaper(); RefreshPersonalization();
            }
            if (ReferenceEquals(_wallpaperRead, cancellation)) _wallpaperRead = null;
        }
    }
    public async Task ClearWallpaperAsync()
    {
        _wallpaperRequestRevision++;
        _wallpaperRead?.Cancel(); _wallpaperRead = null;
        _wallpaperEnabled = _wallpaperLoading = _notifyWallpaperReady = false;
        _wallpaperError = "";
        ClearWallpaperFields();
        NotifyWallpaper(); RefreshPersonalization();
        await SaveWallpaperEnabledAsync(false);
    }
    private void ApplyWallpaper(WallpaperDescriptor wallpaper)
    {
        _wallpaperRefreshVersion++;
        _wallpaperKind = wallpaper.Kind.ToString();
        _wallpaperPath = wallpaper.Path;
        _wallpaperPreview = wallpaper.PreviewPath;
        _wallpaperName = wallpaper.Title;
        _wallpaperProject = wallpaper.ProjectPath;
        _wallpaperProperties = wallpaper.Properties ?? [];
        _wallpaperPropertyValues = LoadWallpaperPropertyValues(wallpaper.ProjectPath);
        foreach (WallpaperProperty property in _wallpaperProperties)
        {
            if (!_wallpaperPropertyValues.ContainsKey(property.Key))
                _wallpaperPropertyValues[property.Key] = property.DefaultValue;
        }
        RebuildWallpaperPropertiesJson();
        _wallpaperError = "";
    }
    private void ClearWallpaperFields()
    {
        _wallpaperRefreshVersion++;
        _wallpaperKind = _wallpaperPath = _wallpaperPreview = _wallpaperName = _wallpaperProject = "";
        _wallpaperProperties = [];
        _wallpaperPropertyValues.Clear();
        _wallpaperPropertiesJson = "{}";
    }
    /// <summary>仅接收当前壁纸的渲染结果，解析成功不等于播放器已成功显示。</summary>
    public void ReportWallpaperRenderState(int version, string error)
    {
        if (!_wallpaperEnabled || version != _wallpaperRefreshVersion) return;
        bool notify = _notifyWallpaperReady;
        _notifyWallpaperReady = false;
        _wallpaperError = error;
        Changed(nameof(WallpaperError));
        if (error.Length > 0) SetGlobalNotification(error, true);
        else if (notify) SetGlobalNotification("成功应用 " + _wallpaperName, false);
    }
    public string GetWallpaperPropertyValue(string key) =>
        _wallpaperPropertyValues.TryGetValue(key, out string? value) ? value : "";
    public void SetWallpaperProperty(string key, string value)
    {
        if (_wallpaperProject.Length == 0) return;
        _wallpaperPropertyValues[key] = value;
        RebuildWallpaperPropertiesJson();
        _configuration.ChangeLauncher(root =>
        {
            JsonObject appearance = LauncherConfiguration.Object(root, "appearance");
            JsonObject all = LauncherConfiguration.Object(appearance, "wallpaperProperties");
            JsonObject current = LauncherConfiguration.Object(all, _wallpaperProject);
            current[key] = value;
        });
        Changed(nameof(WallpaperPropertiesJson));
    }
    private Dictionary<string, string> LoadWallpaperPropertyValues(string projectPath)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (projectPath.Length == 0) return values;
        try
        {
            JsonObject appearance = LauncherConfiguration.Object(_configuration.ReadLauncher(), "appearance");
            if (appearance["wallpaperProperties"] is JsonObject all
                && all[projectPath] is JsonObject saved)
            {
                foreach ((string key, JsonNode? node) in saved)
                {
                    string value = LauncherConfiguration.Text(node);
                    if (value.Length > 0) values[key] = value;
                }
            }
        }
        catch (Exception) { }
        return values;
    }
    private void RebuildWallpaperPropertiesJson()
    {
        var result = new JsonObject();
        foreach (WallpaperProperty property in _wallpaperProperties)
        {
            string raw = _wallpaperPropertyValues.TryGetValue(property.Key, out string? value) ? value : property.DefaultValue;
            result[property.Key] = property.Kind switch
            {
                WallpaperPropertyKind.Slider => double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number)
                    ? JsonValue.Create(number) : JsonValue.Create(0d),
                WallpaperPropertyKind.Bool => bool.TryParse(raw, out bool flag) ? JsonValue.Create(flag) : JsonValue.Create(false),
                _ => JsonValue.Create(raw)
            };
        }
        _wallpaperPropertiesJson = result.ToJsonString();
    }
    private async Task SaveWallpaperEnabledAsync(bool enabled)
    {
        _configuration.ChangeLauncher(root =>
        {
            JsonObject appearance = LauncherConfiguration.Object(root, "appearance");
            appearance["wallpaperEnabled"] = enabled;
            appearance["wallpaperProject"] = _wallpaperProject;
        });
        await _configuration.FlushAsync();
    }
    private void NotifyWallpaper()
    {
        Changed(nameof(WallpaperEnabled)); Changed(nameof(WallpaperStaticMode)); Changed(nameof(HasWallpaper)); Changed(nameof(IsWallpaperLoading));
        Changed(nameof(WallpaperKind)); Changed(nameof(WallpaperPath)); Changed(nameof(WallpaperPreviewPath));
        Changed(nameof(WallpaperProject)); Changed(nameof(WallpaperName)); Changed(nameof(WallpaperError)); Changed(nameof(ShowImageBackground));
        Changed(nameof(WallpaperProperties)); Changed(nameof(WallpaperPropertiesJson)); Changed(nameof(WallpaperRefreshVersion));
        Changed(nameof(CanAdjustBackgroundBlur)); Changed(nameof(BackgroundBlurHint));
    }
    private void RefreshPersonalization()
    {
        Changed(nameof(CanAdjustBackgroundBlur)); Changed(nameof(BackgroundBlurHint)); Changed(nameof(ShowImageBackground));
        UiTheme.Apply(Theme, GlassTransparency, HasCustomBackgroundImage || HasWallpaper); Signal();
    }
    internal static bool IsBackgroundError(Exception error) => error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException;
}
