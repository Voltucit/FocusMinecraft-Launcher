using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>
/// 社区资源的磁盘缓存，统一放在启动器自己的 FMLauncher/Cache 下。
/// 键按哈希分两级目录，避免同目录堆积太多小文件；总量超过上限时按最后访问时间淘汰。
/// </summary>
public static class CommunityCache
{
    /// <summary>缓存总量上限：300 MB。</summary>
    public const long LimitBytes = 300L * 1024 * 1024;
    /// <summary>清理时一次退到 240 MB，避免每次写入都触发淘汰。</summary>
    private const long TrimToBytes = 240L * 1024 * 1024;
    private static readonly object Gate = new();

    public static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "FMLauncher", "Cache");
    private static bool _layoutReady;
    private static readonly object LayoutGate = new();

    public static string? ReadText(string key, TimeSpan lifetime)
    {
        var path = PathOf(key, ".txt");
        try
        {
            if (!IsFresh(path, lifetime)) return null;
            var text = File.ReadAllText(path, Encoding.UTF8);
            Touch(path);
            return text;
        }
        catch (Exception error) { Trace(error); return null; }
    }

    public static void WriteText(string key, string text) => Write(key, Encoding.UTF8.GetBytes(text), ".txt");

    public static byte[]? ReadBytes(string key, TimeSpan lifetime)
    {
        var path = PathOf(key, ".bin");
        try
        {
            if (!IsFresh(path, lifetime)) return null;
            var bytes = File.ReadAllBytes(path);
            Touch(path);
            return bytes;
        }
        catch (Exception error) { Trace(error); return null; }
    }

    public static void WriteBytes(string key, byte[] bytes) => Write(key, bytes, ".bin");
    public static void RemoveBytes(string key) => Remove(key, ".bin");

    /// <summary>当前占用字节数，供设置页显示。</summary>
    public static long Measure()
    {
        try
        {
            EnsureLayout();
            if (!Directory.Exists(Root)) return 0;
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                try { total += new FileInfo(file).Length; } catch (IOException) { }
            return total;
        }
        catch (Exception error) { Trace(error); return 0; }
    }

    public static string DescribeSize(long bytes) => bytes switch
    {
        <= 0 => "0 MB",
        < 1024 * 1024 => (bytes / 1024d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / 1024d / 1024d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB"
    };

    /// <summary>清空缓存目录，返回是否真的删掉了内容。</summary>
    public static bool Clear()
    {
        try
        {
            if (!Directory.Exists(Root)) return false;
            Directory.Delete(Root, true);
            return true;
        }
        catch (Exception error) { Trace(error); return false; }
    }

    private static void Remove(string key, string extension)
    {
        try
        {
            var path = PathOf(key, extension);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception error) { Trace(error); }
    }

    private static bool IsFresh(string path, TimeSpan lifetime)    {
        if (!File.Exists(path)) return false;
        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > lifetime)
        {
            try { File.Delete(path); } catch (IOException) { }
            return false;
        }
        return true;
    }

    private static void Touch(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (IOException) { }
    }

    private static string PathOf(string key, string extension)
    {
        EnsureLayout();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(Root, hash[..2], hash + extension);
    }
    /// <summary>旧版本把缓存放在版本号子目录里；升级后把内容移回 Cache 根，不再保留版本目录。</summary>
    private static void EnsureLayout()
    {
        if (_layoutReady) return;
        lock (LayoutGate)
        {
            if (_layoutReady) return;
            _layoutReady = true;
            try
            {
                if (!Directory.Exists(Root)) return;
                foreach (var version in Directory.EnumerateDirectories(Root)
                    .Where(path => Version.TryParse(Path.GetFileName(path), out _)).ToArray())
                {
                    foreach (var file in Directory.EnumerateFiles(version, "*", SearchOption.AllDirectories).ToArray())
                    {
                        var target = Path.Combine(Root, Path.GetRelativePath(version, file));
                        var folder = Path.GetDirectoryName(target);
                        if (folder is null) continue;
                        Directory.CreateDirectory(folder);
                        File.Move(file, target, true);
                    }
                    Directory.Delete(version, true);
                }
            }
            catch (Exception error) { Trace(error); }
        }
    }

    private static void Write(string key, byte[] payload, string extension)
    {
        try
        {
            var path = PathOf(key, extension);
            var directory = Path.GetDirectoryName(path);
            if (directory is null) return;
            Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, payload);
            File.Move(temporary, path, true);
            Enforce();
        }
        catch (Exception error) { Trace(error); }
    }

    /// <summary>超过上限就按最后写入时间删到 240 MB；任何一步失败都不影响正常使用。</summary>
    private static void Enforce()
    {
        lock (Gate)
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                var files = new List<(string Path, long Length, DateTime At)>();
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        files.Add((file, info.Length, info.LastWriteTimeUtc));
                    }
                    catch (IOException) { }
                }
                var total = files.Sum(item => item.Length);
                if (total <= LimitBytes) return;
                foreach (var item in files.OrderBy(item => item.At))
                {
                    if (total <= TrimToBytes) break;
                    try { File.Delete(item.Path); total -= item.Length; } catch (IOException) { }
                }
            }
            catch (Exception error) { Trace(error); }
        }
    }

    private static void Trace(Exception error) =>
        System.Diagnostics.Trace.TraceWarning("磁盘缓存操作失败（{0}）：{1}", error.GetType().Name, error.Message);
}
