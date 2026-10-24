using System.IO;
namespace FMLauncher.Infrastructure.Configuration;

public sealed class AppPaths
{
    public string UserDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FMLauncher");
    public string SettingsFile => Path.Combine(UserDirectory, "settings.v2.json");
    public string AccountsDirectory => Path.Combine(UserDirectory, "accounts-v2");
    public string WallpaperCache => Path.Combine(UserDirectory, "wallpapers");
    public string LogsDirectory => Path.Combine(UserDirectory, "logs");
    public string LegacySettings => Path.Combine(DefaultGameRoot, "LauncherSetting.json");
    public string DefaultGameRoot
    {
        get
        {
            var nearby = Path.Combine(AppContext.BaseDirectory, ".minecraft");
            if (File.Exists(Path.Combine(nearby, "LauncherSetting.json"))) return nearby;
            // 升级 TFM 后输出目录会变化；只在兄弟输出目录中存在唯一旧配置时自动采用。
            var parent = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName;
            if (parent == null) return nearby;
            var candidates = new[] { "net9.0-windows", "net9.0", "net10.0", "net10.0-windows" }
                .Select(name => Path.Combine(parent, name, ".minecraft")).Where(path => File.Exists(Path.Combine(path, "LauncherSetting.json"))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return candidates.Length == 1 ? candidates[0] : nearby;
        }
    }
}

