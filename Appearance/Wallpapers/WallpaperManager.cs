using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using FMLauncher.Appearance.Wallpapers.Providers;
using FMLauncher.Infrastructure.Configuration;
using FMLauncher.Infrastructure.Networking;
using FMLauncher.Infrastructure.Persistence;
namespace FMLauncher.Appearance.Wallpapers;

/// <summary>串行刷新、限额解码、历史缓存及失败回退；计时调度由应用层调用。</summary>
public sealed class WallpaperManager(AppPaths paths, SecretStore secrets) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PublicImageClient _client = new();
    private readonly LocalWallpaperProvider _localProvider = new();
    public BitmapSource? Current { get; private set; }
    public string Status { get; private set; } = "";
    public event Action? Changed;
    private string StatePath => Path.Combine(paths.WallpaperCache, "state.json");
    public IReadOnlyList<string> History => Directory.Exists(paths.WallpaperCache) ? Directory.EnumerateFiles(paths.WallpaperCache, "*.image").OrderByDescending(File.GetLastWriteTimeUtc).Take(50).ToArray() : [];
    /// <summary>按更新策略串行取图，先验证再提交缓存；失败保留当前图或最近有效缓存。</summary>
    public async Task RefreshAsync(WallpaperSettings settings, bool force, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (settings.Source == "None") { Current = null; Status = ""; Changed?.Invoke(); return; }
            DateTimeOffset last = default;
            if (File.Exists(StatePath)) try { last = System.Text.Json.JsonSerializer.Deserialize<DateTimeOffset>(await File.ReadAllTextAsync(StatePath, ct)); } catch (System.Text.Json.JsonException) { }
            var due = settings.Frequency switch { "Startup" => force, "Manual" => force, "Interval" => DateTimeOffset.UtcNow - last >= TimeSpan.FromMinutes(Math.Clamp(settings.IntervalMinutes, 5, 10080)), _ => last.LocalDateTime.Date != DateTime.Now.Date };
            var history = History;
            if (!force && !due && history.Count > 0) { Current = await LoadImageAsync(history[0], ct); Changed?.Invoke(); return; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 3, 120)));
            IWallpaperProvider provider = settings.Source is "DirectApi" or "JsonApi" ? new ApiWallpaperProvider(_client) : _localProvider;
            var bytes = await provider.LoadAsync(settings, secrets.Read("wallpaper"), timeout.Token);
            if (bytes == null) return;
            var image = await Task.Run(() => Decode(bytes), timeout.Token);
            Directory.CreateDirectory(paths.WallpaperCache);
            var file = Path.Combine(paths.WallpaperCache, Convert.ToHexString(SHA256.HashData(bytes)) + ".image");
            try { await File.WriteAllBytesAsync(file + ".tmp", bytes, timeout.Token); File.Move(file + ".tmp", file, true); }
            finally { if (File.Exists(file + ".tmp")) File.Delete(file + ".tmp"); }
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
            AtomicJson.Write(StatePath, DateTimeOffset.UtcNow);
            Current = image; Status = "壁纸已更新";
            // UI 只展示前 50 项，但清理必须枚举所有缓存，否则第 51 张会永久遗漏。
            await Task.Run(() =>
            {
                foreach (var old in Directory.EnumerateFiles(paths.WallpaperCache, "*.image").OrderByDescending(File.GetLastWriteTimeUtc).Skip(Math.Clamp(settings.CacheCount, 1, 50)))
                { ct.ThrowIfCancellationRequested(); File.Delete(old); }
            }, ct);
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Status = "壁纸更新失败，保留当前图片或最近缓存。";
            if (Current == null)
                foreach (var file in History)
                    try { Current = await LoadImageAsync(file, ct); break; } catch (Exception e) when (e is IOException or NotSupportedException or System.IO.FileFormatException) { }
            Changed?.Invoke();
        }
        finally { _gate.Release(); }
    }
    private static BitmapSource Decode(byte[] bytes)
    {
        if (bytes.Length > 20 * 1024 * 1024) throw new InvalidDataException("图片超过 20 MiB。");
        var jpeg = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
        var png = bytes.Length > 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var bmp = bytes.Length > 2 && bytes[0] == 66 && bytes[1] == 77;
        var webp = bytes.Length > 12 && System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP";
        if (!jpeg && !png && !bmp && !webp) throw new InvalidDataException("只支持 JPEG、PNG、BMP 或系统可解码的 WebP。");
        using var stream = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count != 1) throw new InvalidDataException("只支持单帧静态壁纸。");
        var frame = decoder.Frames[0];
        if (frame.PixelWidth < 1 || frame.PixelHeight < 1 || (long)frame.PixelWidth * frame.PixelHeight > 32000000) throw new InvalidDataException("图片分辨率超过限制。");
        stream.Position = 0;
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = Math.Min(2560, frame.PixelWidth); bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }
    /// <summary>只选择当前缓存清单中的图片，不按外部输入访问任意文件。</summary>
    public async Task SelectHistoryAsync(string file, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!History.Contains(file)) throw new InvalidDataException("壁纸历史已失效。");
            Current = await LoadImageAsync(file, ct);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow); Changed?.Invoke();
        }
        finally { _gate.Release(); }
    }
    /// <summary>仅删除缓存根目录的壁纸文件，不删除本地图片来源或游戏文件。</summary>
    public async Task ClearAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { if (Directory.Exists(paths.WallpaperCache)) foreach (var file in Directory.EnumerateFiles(paths.WallpaperCache, "*.image")) { ct.ThrowIfCancellationRequested(); File.Delete(file); } if (File.Exists(StatePath)) File.Delete(StatePath); }
        finally { _gate.Release(); }
    }
    public void Dispose() => _client.Dispose();
    private static async Task<BitmapSource> LoadImageAsync(string path, CancellationToken ct)
    {
        var bytes = await ImageFile.ReadAsync(path, ct);
        return await Task.Run(() => Decode(bytes), ct);
    }
}

