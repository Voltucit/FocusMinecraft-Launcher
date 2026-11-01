using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace FocusMinecraftLauncher.Infrastructure.Authentication;

/// <summary>离线本地皮肤：按 CustomSkinLoader 的 LocalSkin 约定写入游戏目录，仅本机生效。</summary>
public static class LocalSkinStore
{
    private const string LoaderFolder = "CustomSkinLoader";
    private const string SkinFileName = "skins";

    /// <summary>CustomSkinLoader 读取的本地皮肤文件位置。</summary>
    public static string FileFor(string gameDirectory, string playerName) =>
        Path.Combine(gameDirectory, LoaderFolder, "LocalSkin", SkinFileName, playerName + ".png");

    /// <summary>游戏目录里是否放了 CustomSkinLoader；没有它本地皮肤不会被加载。</summary>
    public static bool HasLoader(string gameDirectory)
    {
        try
        {
            var mods = Path.Combine(gameDirectory, "mods");
            return Directory.Exists(mods) && Directory.EnumerateFiles(mods, "*CustomSkinLoader*.jar", SearchOption.TopDirectoryOnly).Any();
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>校验 PNG 皮肤尺寸；旧的 64×32 与 HD 皮肤都允许。</summary>
    public static void Validate(byte[] png)
    {
        using var stream = new MemoryStream(png, false);
        BitmapFrame frame;
        try { frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]; }
        catch (Exception error) when (error is NotSupportedException or ArgumentException or IOException or FileFormatException)
        { throw new InvalidDataException("无法读取这张图片，请选择 PNG 格式的皮肤文件。", error); }
        int width = frame.PixelWidth, height = frame.PixelHeight;
        if (width < 64 || height < 32 || width % 64 != 0 || height % 32 != 0)
            throw new InvalidDataException($"皮肤图片尺寸 {width}×{height} 不受支持，请使用 64×64、64×32 或对应的 HD 尺寸。");
    }

    /// <summary>写入所有目标游戏目录；返回实际写入的目录数。</summary>
    public static async Task<int> SaveAsync(IEnumerable<string> gameDirectories, string playerName, byte[] png, CancellationToken token)
    {
        if (playerName.Length == 0) throw new InvalidOperationException("当前账号没有可用的玩家名。");
        var saved = 0;
        foreach (var directory in Distinct(gameDirectories))
        {
            token.ThrowIfCancellationRequested();
            var path = FileFor(directory, playerName);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(temporary, png, token);
                File.Move(temporary, path, true);
                saved++;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return saved;
    }

    /// <summary>删除所有目标游戏目录里该玩家的本地皮肤；返回删除的文件数。</summary>
    public static int Remove(IEnumerable<string> gameDirectories, string playerName)
    {
        var removed = 0;
        foreach (var directory in Distinct(gameDirectories))
        {
            try
            {
                var path = FileFor(directory, playerName);
                if (!File.Exists(path)) continue;
                File.Delete(path);
                removed++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    private static IEnumerable<string> Distinct(IEnumerable<string> gameDirectories) =>
        gameDirectories.Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => Path.GetFullPath(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase);
}
