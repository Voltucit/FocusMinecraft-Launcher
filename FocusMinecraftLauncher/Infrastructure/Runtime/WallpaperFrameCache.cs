using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>缓存 Scene 首次成功渲染的抓帧，用作列表缩略图。</summary>
public static class WallpaperFrameCache
{
    private static string Root => Path.Combine(CommunityCache.Root, "WallpaperFrames");

    public static string? Find(string projectPath)
    {
        string path = PathFor(projectPath);
        return File.Exists(path) ? path : null;
    }

    public static string? FindUsable(string projectPath)
    {
        string path = PathFor(projectPath);
        if (!File.Exists(path)) return null;
        if (IsUsable(path)) return path;
        try { File.Delete(path); } catch (Exception) { }
        return null;
    }

    public static string PathFor(string projectPath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(projectPath)));
        return Path.Combine(Root, Convert.ToHexString(hash).ToLowerInvariant() + ".png");
    }

    public static string CreateTemporaryPath()
    {
        Directory.CreateDirectory(Root);
        return Path.Combine(Root, Guid.NewGuid().ToString("N") + ".tmp");
    }

    public static bool Commit(string temporary, string projectPath)
    {
        if (!IsUsable(temporary))
        {
            try { File.Delete(temporary); } catch (Exception) { }
            return false;
        }
        string target = PathFor(projectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(temporary, target, true);
        Trim();
        return true;
    }

    /// <summary>拒收透明或全黑帧；这类帧通常是 WebView 首帧尚未出画。</summary>
    public static bool IsUsable(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 32;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int width = converted.PixelWidth, height = converted.PixelHeight;
            if (width == 0 || height == 0) return false;
            byte[] pixels = new byte[width * height * 4];
            converted.CopyPixels(pixels, width * 4, 0);
            int visible = 0, brightest = 0;
            for (int index = 0; index < pixels.Length; index += 4)
            {
                int alpha = pixels[index + 3];
                if (alpha < 8) continue;
                visible++;
                int luma = (pixels[index] * 30 + pixels[index + 1] * 59 + pixels[index + 2] * 11) / 100;
                if (luma > brightest) brightest = luma;
            }
            return visible > pixels.Length / 4 / 100 && brightest >= 8;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Trim()
    {
        const int maxFiles = 80;
        const long maxBytes = 120L * 1024 * 1024;
        try
        {
            var files = new DirectoryInfo(Root).EnumerateFiles("*.png")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();
            long total = files.Sum(file => file.Length);
            for (int index = 0; index < files.Length; index++)
            {
                bool overCount = files.Length - index > maxFiles;
                bool overSize = total > maxBytes;
                if (!overCount && !overSize) break;
                total -= files[^(index + 1)].Length;
                try { files[^(index + 1)].Delete(); } catch (Exception) { }
            }
        }
        catch (Exception) { }
    }
}
