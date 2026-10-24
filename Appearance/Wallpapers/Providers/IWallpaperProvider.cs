namespace FMLauncher.Appearance.Wallpapers.Providers;

/// <summary>Provider 只获取有界图像字节，格式校验和缓存由 WallpaperManager 统一执行。</summary>
public interface IWallpaperProvider
{
    Task<byte[]?> LoadAsync(WallpaperSettings settings, string credentialsJson, CancellationToken cancellationToken);
}

