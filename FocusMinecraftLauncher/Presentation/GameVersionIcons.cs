using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FocusMinecraftLauncher.Presentation;

/// <summary>按已有加载器与版本类型选择默认图标，不根据版本号推断版本新旧或安装状态。</summary>
public static class GameVersionIcons
{
    private static readonly IReadOnlyDictionary<string, ImageSource> Images = new Dictionary<string, ImageSource>
    {
        ["NormalVersion"] = Load("NormalVersion"), ["PreviewVersion"] = Load("PreviewVersion"),
        ["OldVersion"] = Load("OldVersion"), ["ErrorVersion"] = Load("ErrorVersion"),
        ["FabricVersion"] = Load("FabricVersion"), ["ForgeVersion"] = Load("ForgeVersion"),
        ["NeoForgeVersion"] = Load("NeoForgeVersion")
    };

    /// <summary>异常版本使用错误图标；正常版本优先显示加载器，原版按官方版本类型选择。</summary>
    public static ImageSource Get(string loader, string versionType)
    {
        var type = versionType.Trim().ToLowerInvariant();
        if (type == "error") return Images["ErrorVersion"];
        var key = loader.Trim().ToLowerInvariant() switch
        {
            "fabric" => "FabricVersion", "forge" => "ForgeVersion", "neoforge" => "NeoForgeVersion",
            _ => type switch
            {
                "snapshot" or "快照版" or "预览版" => "PreviewVersion",
                "old_alpha" or "old_beta" => "OldVersion",
                _ => "NormalVersion"
            }
        };
        return Images[key];
    }

    private static ImageSource Load(string name)
    {
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 128;
        image.UriSource = new Uri($"pack://application:,,,/FocusMinecraftLauncher;component/Assets/GamesIcon/{name}.png");
        image.EndInit(); image.Freeze();
        return image;
    }
}
