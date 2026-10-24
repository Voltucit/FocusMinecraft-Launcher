namespace FMLauncher.Appearance.Wallpapers;

public sealed class WallpaperSettings
{
    public string Source { get; set; } = "None";
    public string LocalPath { get; set; } = "";
    public string ApiUrl { get; set; } = "";
    public string JsonPath { get; set; } = "data.url";
    public string KeyHeader { get; set; } = "X-Api-Key";
    public string Frequency { get; set; } = "Daily";
    public int IntervalMinutes { get; set; } = 60;
    public int TimeoutSeconds { get; set; } = 20;
    public int CacheCount { get; set; } = 10;
}

