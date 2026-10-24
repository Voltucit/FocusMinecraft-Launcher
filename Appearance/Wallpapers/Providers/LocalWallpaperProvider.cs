using System.IO;
namespace FMLauncher.Appearance.Wallpapers.Providers;

public sealed class LocalWallpaperProvider : IWallpaperProvider
{
    private string? _previous;
    public async Task<byte[]?> LoadAsync(WallpaperSettings settings, string credentialsJson, CancellationToken cancellationToken)
    {
        if (settings.Source == "None") return null;
        var path = settings.LocalPath;
        if (settings.Source == "Folder")
        {
            var files = Directory.EnumerateFiles(path).Where(f => new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" }.Contains(Path.GetExtension(f).ToLowerInvariant())).Take(500).ToArray();
            if (files.Length == 0) throw new InvalidDataException("文件夹中没有支持的图片。");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            path = files[(Array.IndexOf(files, _previous) + 1) % files.Length];
            _previous = path;
        }
        if (!File.Exists(path) || new FileInfo(path).Length > 20 * 1024 * 1024) throw new InvalidDataException("本地图片不存在或超过 20 MiB。");
        return await ImageFile.ReadAsync(path, cancellationToken);
    }
}

