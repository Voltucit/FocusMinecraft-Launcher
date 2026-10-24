using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using FMLauncher.Common;
using FMLauncher.Appearance.Themes;
using FMLauncher.Appearance.Wallpapers;
using FMLauncher.Infrastructure.Configuration;
using FMLauncher.Infrastructure.Persistence;
namespace FMLauncher.Appearance.ViewModels;

/// <summary>预览与保存分开；主题 JSON 从不携带壁纸地址、请求头或 API 凭据。</summary>
public sealed class AppearanceViewModel : PageViewModel
{
    private ThemeSettings _draft;
    private string _preset = "Fluent", _name = "我的主题", _apiKey = "", _parameters = "{}", _headers = "{}";
    private string? _history, _saved;
    private readonly string _themeDirectory;
    public ThemeSettings Draft { get => _draft; private set => Set(ref _draft, value); }
    public WallpaperSettings Wallpaper { get; }
    public string Preset { get => _preset; set => Set(ref _preset, value); }
    public string ThemeName { get => _name; set => Set(ref _name, value); }
    public string ApiKey { get => _apiKey; set => Set(ref _apiKey, value); }
    public string Parameters { get => _parameters; set => Set(ref _parameters, value); }
    public string Headers { get => _headers; set => Set(ref _headers, value); }
    public string? SelectedHistory { get => _history; set => Set(ref _history, value); }
    public string? SelectedSaved { get => _saved; set => Set(ref _saved, value); }
    public ObservableCollection<string> History { get; } = [];
    public ObservableCollection<string> SavedThemes { get; } = [];
    public AsyncCommand ApplyPreset { get; }
    public AsyncCommand Preview { get; }
    public AsyncCommand Revert { get; }
    public AsyncCommand Reset { get; }
    public AsyncCommand Save { get; }
    public AsyncCommand Import { get; }
    public AsyncCommand Export { get; }
    public AsyncCommand SaveNamed { get; }
    public AsyncCommand LoadNamed { get; }
    public AsyncCommand BrowseImage { get; }
    public AsyncCommand BrowseFolder { get; }
    public AsyncCommand SaveWallpaper { get; }
    public AsyncCommand RefreshWallpaper { get; }
    public AsyncCommand UseHistory { get; }
    public AsyncCommand ClearCache { get; }
    public AppearanceViewModel(ThemeService themes, WallpaperManager manager, SettingsStore settings, SecretStore secrets, AppPaths paths)
    {
        _draft = Clone(settings.Current.Appearance);
        Wallpaper = JsonSerializer.Deserialize<WallpaperSettings>(JsonSerializer.Serialize(settings.Current.Wallpaper))!;
        _themeDirectory = Path.Combine(paths.UserDirectory, "themes");
        ApplyPreset = Command(ct => { Draft = ThemeService.Preset(Preset); themes.Apply(Draft); return Task.CompletedTask; });
        Preview = Command(ct => { themes.Apply(Draft); Status = "预览中；点击保存后持久生效。"; return Task.CompletedTask; });
        Revert = Command(ct => { Draft = Clone(settings.Current.Appearance); themes.Apply(Draft); return Task.CompletedTask; });
        Reset = Command(ct => { Draft = new(); themes.Apply(Draft); return Task.CompletedTask; });
        Save = Command(ct => { ThemeService.Validate(Draft); settings.Current.Appearance = Clone(Draft); settings.Save(); themes.Apply(settings.Current.Appearance); Status = "主题已保存。"; return Task.CompletedTask; });
        Import = Command(ct => { var dialog = new OpenFileDialog { Filter = "主题 JSON|*.json" }; if (dialog.ShowDialog() == true) { Draft = themes.Import(dialog.FileName); themes.Apply(Draft); } return Task.CompletedTask; });
        Export = Command(ct => { var dialog = new SaveFileDialog { Filter = "主题 JSON|*.json", FileName = "theme.json" }; if (dialog.ShowDialog() == true) themes.Export(dialog.FileName, Draft); return Task.CompletedTask; });
        SaveNamed = Command(ct =>
        {
            if (ThemeName.Length is < 1 or > 64 || ThemeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || ThemeName is "." or "..") throw new InvalidDataException("主题名称无效。");
            Directory.CreateDirectory(_themeDirectory); themes.Export(Path.Combine(_themeDirectory, ThemeName + ".json"), Draft); RefreshSaved(); return Task.CompletedTask;
        });
        LoadNamed = Command(ct => { var name = SelectedSaved ?? throw new InvalidOperationException("请选择自定义主题。"); if (!SavedThemes.Contains(name)) throw new InvalidDataException("主题不存在。"); Draft = themes.Import(Path.Combine(_themeDirectory, name)); themes.Apply(Draft); return Task.CompletedTask; });
        BrowseImage = Command(ct => { var dialog = new OpenFileDialog { Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.webp" }; if (dialog.ShowDialog() == true) { Wallpaper.LocalPath = dialog.FileName; Wallpaper.Source = "Local"; Changed(nameof(Wallpaper)); } return Task.CompletedTask; });
        BrowseFolder = Command(ct => { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog() == true) { Wallpaper.LocalPath = dialog.FolderName; Wallpaper.Source = "Folder"; Changed(nameof(Wallpaper)); } return Task.CompletedTask; });
        SaveWallpaper = Command(ct => { SaveWallpaperSettings(); Status = "壁纸设置及加密参数已保存。"; return Task.CompletedTask; });
        RefreshWallpaper = Command(async ct => { SaveWallpaperSettings(); await manager.RefreshAsync(Wallpaper, true, ct); RefreshHistory(); Status = manager.Status; });
        UseHistory = Command(async ct => { await manager.SelectHistoryAsync(SelectedHistory ?? throw new InvalidOperationException("请选择历史壁纸。"), ct); });
        ClearCache = Command(async ct => { await manager.ClearAsync(ct); RefreshHistory(); Status = "磁盘缓存已清理，当前解码图片仍保留。"; });
        RefreshHistory(); RefreshSaved();
        try
        {
            var credential = secrets.Read("wallpaper");
            if (!string.IsNullOrWhiteSpace(credential))
            {
                using var json = JsonDocument.Parse(credential);
                if (json.RootElement.TryGetProperty("parameters", out var p)) Parameters = p.GetRawText();
                if (json.RootElement.TryGetProperty("headers", out var h)) Headers = h.GetRawText();
                if (json.RootElement.TryGetProperty("key", out var k)) ApiKey = k.GetString() ?? "";
            }
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or JsonException or UnauthorizedAccessException)
        { Status = "壁纸凭据无法读取，原文件已保留；重新输入并保存可更新凭据。"; }
        void RefreshHistory() { History.Clear(); foreach (var item in manager.History) History.Add(item); }
        void RefreshSaved() { SavedThemes.Clear(); if (Directory.Exists(_themeDirectory)) foreach (var item in Directory.EnumerateFiles(_themeDirectory, "*.json").Take(100)) SavedThemes.Add(Path.GetFileName(item)); }
        void SaveWallpaperSettings()
        {
            // URL 查询可能包含 Key，必须在保存之前拒绝，参数只能进入 DPAPI 字段。
            if (!string.IsNullOrWhiteSpace(Wallpaper.ApiUrl))
            {
                var uri = FMLauncher.Infrastructure.Networking.PublicImageClient.Validate(Wallpaper.ApiUrl);
                if (!string.IsNullOrEmpty(uri.Query)) throw new InvalidDataException("请把 API URL 的查询参数移到下方加密请求参数字段。");
            }
            var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(Parameters) ?? new();
            var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(Headers) ?? new();
            if (parameters.Count > 24 || headers.Count > 24 || parameters.Concat(headers).Any(pair => pair.Key.Length > 128 || pair.Value == null || pair.Value.Length > 4096) || ApiKey.Length > 4096)
                throw new InvalidDataException("API 参数或请求头超出数量和长度限制。");
            secrets.Write("wallpaper", JsonSerializer.Serialize(new { parameters, headers, key = ApiKey }));
            settings.Current.Wallpaper = JsonSerializer.Deserialize<WallpaperSettings>(JsonSerializer.Serialize(Wallpaper))!;
            settings.Save();
        }
    }
    private static ThemeSettings Clone(ThemeSettings value) => JsonSerializer.Deserialize<ThemeSettings>(JsonSerializer.Serialize(value))!;
}

