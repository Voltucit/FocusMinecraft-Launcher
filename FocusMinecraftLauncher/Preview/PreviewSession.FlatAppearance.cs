using FocusMinecraftLauncher.Infrastructure.Configuration;
using FocusMinecraftLauncher.Presentation;

namespace FocusMinecraftLauncher.Preview;

public sealed partial class PreviewSession
{
    private ThemeColorPreset _flatColorPreset = UiTheme.FlatColorPresets[0];
    private bool _flatNightMode;
    public bool IsFlatTheme => Theme == UiTheme.FlatThemeName;
    /// <summary>选择固定配色子预设，即时应用并只保存到当前主题的配置。</summary>
    public string FlatColorPresetId
    {
        get => _flatColorPreset.Id;
        set
        {
            var preset = UiTheme.FlatColorPresets.FirstOrDefault(option => option.Id == value);
            if (preset == null || !Set(ref _flatColorPreset, preset)) return;
            UiTheme.SetFlatAccent(preset.Color);
            if (!_restoringConfiguration)
                _configuration.ChangeLauncher(root =>
                {
                    var profile = LauncherConfiguration.Object(
                        LauncherConfiguration.Object(LauncherConfiguration.Object(root, "appearance"), "themes"), "MinimalistFlat");
                    profile["colorPreset"] = preset.Id;
                    profile["accentColor"] = preset.Color;
                });
        }
    }
    /// <summary>极简主题的夜间模式即时生效，并只保存到自身配置。</summary>
    public bool FlatNightMode
    {
        get => _flatNightMode;
        set
        {
            if (!Set(ref _flatNightMode, value)) return;
            UiTheme.SetFlatNightMode(value);
            if (!_restoringConfiguration)
                _configuration.ChangeLauncher(root => LauncherConfiguration.Object(
                    LauncherConfiguration.Object(LauncherConfiguration.Object(root, "appearance"), "themes"), "MinimalistFlat")["nightMode"] = value);
        }
    }

    private void RestoreFlatAppearance(System.Text.Json.Nodes.JsonObject root)
    {
        var profile = root["appearance"]?["themes"]?["MinimalistFlat"];
        _flatColorPreset = UiTheme.ResolveColorPreset(UiTheme.FlatColorPresets,
            LauncherConfiguration.Text(profile?["colorPreset"]), LauncherConfiguration.Text(profile?["accentColor"]));
        _flatNightMode = LauncherConfiguration.Boolean(root["appearance"]?["themes"]?["MinimalistFlat"]?["nightMode"], false);
        UiTheme.SetFlatAccent(_flatColorPreset.Color);
        UiTheme.SetFlatNightMode(_flatNightMode);
    }
}
