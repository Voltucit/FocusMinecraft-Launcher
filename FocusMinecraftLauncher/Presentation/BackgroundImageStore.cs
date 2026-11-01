using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace FocusMinecraftLauncher.Presentation;

/// <summary>将选中的图片保存为程序目录内的独立副本；有界解码并释放源文件句柄。</summary>
public sealed class BackgroundImageStore(string configurationDirectory)
{
    private const int MaximumDecodeEdge = 2560;
    private readonly string _directory = Path.GetFullPath(Path.Combine(configurationDirectory, "Backgrounds"));
    public sealed record ImportedImage(BitmapSource Image, string RelativePath, string DisplayName);

    public Task<BitmapSource> LoadAsync(string relativePath, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(Resolve(relativePath));
        var image = Decode(stream);
        cancellationToken.ThrowIfCancellationRequested();
        return image;
    }, cancellationToken);

    /// <summary>先校验并解码，再在受管目录写入副本；不修改或移动用户选中的原图。</summary>
    public Task<ImportedImage> ImportAsync(string source, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp")) throw new NotSupportedException("不支持的图片格式。");
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var image = Decode(input);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_directory);
        var relativePath = "Backgrounds/" + Guid.NewGuid().ToString("N") + extension;
        var destination = Resolve(relativePath);
        var temporary = destination + ".tmp";
        try
        {
            input.Position = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
            return new ImportedImage(image, relativePath, Path.GetFileName(source));
        }
        finally { DeleteFile(temporary); }
    }, cancellationToken);

    /// <summary>配置成功保存后才清理旧副本；只清理本功能生成的文件，不删除用户原图。</summary>
    public void RemoveOwnedCopy(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return;
        try
        {
            var path = Resolve(relativePath);
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)) DeleteFile(path);
        }
        catch (ArgumentException error) { Trace.TraceWarning("背景副本未清理：{0}", error.GetType().Name); }
    }

    private string Resolve(string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new ArgumentException("背景图片应使用程序内相对路径。");
        var path = Path.GetFullPath(Path.Combine(configurationDirectory, relativePath));
        if (!string.Equals(Path.GetDirectoryName(path), _directory, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("背景图片不在受管目录中。");
        return path;
    }

    private static BitmapSource Decode(Stream stream)
    {
        // 只读尺寸后再限制长边；避免把相机大图完整解码到界面内存。
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        if (Math.Max(width, height) > MaximumDecodeEdge)
        {
            if (width >= height) image.DecodePixelWidth = MaximumDecodeEdge;
            else image.DecodePixelHeight = MaximumDecodeEdge;
        }
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static void DeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Trace.TraceWarning("背景副本清理失败：{0}", error.GetType().Name); }
    }
}
