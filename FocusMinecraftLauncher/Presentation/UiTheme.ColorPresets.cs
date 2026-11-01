namespace FocusMinecraftLauncher.Presentation;

/// <summary>固定配色子预设；Id 用于保存，Name 仅用于界面显示。</summary>
public sealed record ThemeColorPreset(string Id, string Name, string Color)
{
    public override string ToString() => Name;
}

public static partial class UiTheme
{
    public static IReadOnlyList<ThemeColorPreset> FlatColorPresets { get; } =
    [
        new("Rose", "玫红（默认）", DefaultFlatAccent), new("Blue", "蓝色", "#3E63DD"),
        new("Jade", "翠绿", "#29A383"), new("Orange", "橙色", "#F76B15"), new("Purple", "紫色", "#8E4EC6")
    ];
    public static IReadOnlyList<ThemeColorPreset> SoftColorPresets { get; } =
    [
        new("Indigo", "靛蓝（默认）", DefaultSoftAccent), new("Blue", "海蓝", "#3E63DD"),
        new("Teal", "青绿", "#12A594"), new("Rose", "玫瑰", "#D6409F"), new("Amber", "琥珀", "#FFC53D")
    ];

    /// <summary>优先恢复固定预设标识；旧颜色仅在与预设完全一致时沿用，否则使用默认预设。</summary>
    internal static ThemeColorPreset ResolveColorPreset(IReadOnlyList<ThemeColorPreset> presets, string id, string legacyColor)
        => presets.FirstOrDefault(preset => preset.Id == id)
            ?? presets.FirstOrDefault(preset => string.Equals(preset.Color, legacyColor.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? presets[0];
}
