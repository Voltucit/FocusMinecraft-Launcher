using System.IO;
using System.Text.Json;
namespace FMLauncher.Infrastructure.Persistence;

/// <summary>在同目录写临时文件并原子替换，避免中断留下半份配置。</summary>
public static class AtomicJson
{
    public static JsonSerializerOptions Options { get; } = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value, Options)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

