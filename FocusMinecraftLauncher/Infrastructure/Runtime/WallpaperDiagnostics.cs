using System.IO;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>把壁纸渲染错误写入程序目录，方便定位 WebView2 / WebWallGL 问题。</summary>
public static class WallpaperDiagnostics
{
    private static readonly object Gate = new();
    public static string LogPath { get; } = Path.Combine(AppContext.BaseDirectory, "FMLauncher", "Logs", "wallpaper.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception) { }
    }
}
