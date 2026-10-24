namespace FMLauncher.Appearance.Themes;

/// <summary>主题只包含受限制的数据，不加载第三方 XAML、程序集或脚本。</summary>
public sealed class ThemeSettings
{
    public string Preset { get; set; } = "Fluent";
    public string Mode { get; set; } = "System";
    public string Accent { get; set; } = "#58C6A5";
    public string Background { get; set; } = "";
    public double WallpaperOpacity { get; set; } = 0.55;
    public double CardOpacity { get; set; } = 0.90;
    public double Radius { get; set; } = 16;
    public string Material { get; set; } = "Mica";
    public double Blur { get; set; }
    public string Layout { get; set; } = "Spacious";
    public bool Animations { get; set; } = true;
    public bool ExtractAccent { get; set; }
}

