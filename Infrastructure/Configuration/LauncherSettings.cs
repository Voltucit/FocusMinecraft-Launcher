using FMLauncher.Appearance.Themes;
using FMLauncher.Appearance.Wallpapers;
namespace FMLauncher.Infrastructure.Configuration;

/// <summary>启动器自有配置。凭据和游戏数据不进入此文档。</summary>
public sealed record LauncherSettings
{
    public int SchemaVersion { get; set; } = 2;
    public string GameRoot { get; set; } = "";
    public string VersionId { get; set; } = "";
    public string LegacyOfflineName { get; set; } = "";
    public Guid? AccountId { get; set; }
    // 仅保留旧配置兼容；认证不再读取此字段，实际应用标识来自开发者配置。
    public string MicrosoftClientId { get; set; } = "";
    public string JavaPath { get; set; } = "";
    public int? FallbackJavaMajor { get; set; }
    public int MinMemoryMb { get; set; }
    public int MaxMemoryMb { get; set; } = 4096;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public bool Fullscreen { get; set; }
    public bool KeepGamesOnExit { get; set; } = true;
    public string JvmArguments { get; set; } = "";
    public string GameArguments { get; set; } = "";
    public string ServerHost { get; set; } = "";
    public int ServerPort { get; set; } = 25565;
    public ThemeSettings Appearance { get; set; } = new();
    public WallpaperSettings Wallpaper { get; set; } = new();
}

