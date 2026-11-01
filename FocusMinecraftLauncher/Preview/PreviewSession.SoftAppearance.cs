using FocusMinecraftLauncher.Infrastructure.Configuration;
using FocusMinecraftLauncher.Presentation;

namespace FocusMinecraftLauncher.Preview;

public sealed partial class PreviewSession
{
    private ThemeColorPreset _softColorPreset = UiTheme.SoftColorPresets[0];
    private bool _softNightMode;
    public bool IsSoftTheme => Theme == UiTheme.SoftThemeName;
    /// <summary>选择固定配色子预设，即时应用并只保存到当前主题的配置。</summary>
    public string SoftColorPresetId
    {
        get => _softColorPreset.Id;
        set
        {
            var preset = UiTheme.SoftColorPresets.FirstOrDefault(option => option.Id == value);
            if (preset == null || !Set(ref _softColorPreset, preset)) return;
            UiTheme.SetSoftAccent(preset.Color);
            if (!_restoringConfiguration)
                _configuration.ChangeLauncher(root =>
                {
                    var profile = LauncherConfiguration.Object(
                        LauncherConfiguration.Object(LauncherConfiguration.Object(root, "appearance"), "themes"), "SoftUI");
                    profile["colorPreset"] = preset.Id;
                    profile["accentColor"] = preset.Color;
                });
        }
    }
    /// <summary>柔和主题使用独立夜间偏好，不覆盖极简主题的模式。</summary>
    public bool SoftNightMode
    {
        get => _softNightMode;
        set
        {
            if (!Set(ref _softNightMode, value)) return;
            UiTheme.SetSoftNightMode(value);
            if (!_restoringConfiguration)
                _configuration.ChangeLauncher(root => LauncherConfiguration.Object(
                    LauncherConfiguration.Object(LauncherConfiguration.Object(root, "appearance"), "themes"), "SoftUI")["nightMode"] = value);
        }
    }

    private void RestoreSoftAppearance(System.Text.Json.Nodes.JsonObject root)
    {
        var profile = root["appearance"]?["themes"]?["SoftUI"];
        _softColorPreset = UiTheme.ResolveColorPreset(UiTheme.SoftColorPresets,
            LauncherConfiguration.Text(profile?["colorPreset"]), LauncherConfiguration.Text(profile?["accentColor"]));
        _softNightMode = LauncherConfiguration.Boolean(root["appearance"]?["themes"]?["SoftUI"]?["nightMode"], false);
        UiTheme.SetSoftAccent(_softColorPreset.Color);
        UiTheme.SetSoftNightMode(_softNightMode);
    }
}
