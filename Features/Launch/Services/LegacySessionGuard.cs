using System.Diagnostics;
using System.IO;
using System.Text.Json;
namespace FMLauncher.Features.Launch.Services;

/// <summary>跨旧版迁移时只读检查旧进程标记；不改写正在运行游戏的元数据或旧锁。</summary>
internal static class LegacySessionGuard
{
    public static void Check(string root, string version)
    {
        if (string.IsNullOrEmpty(version) || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || version is "." or "..") throw new InvalidDataException("游戏版本 ID 无效。");
        var file = Path.Combine(root, "versions", version, ".launcher-session.json");
        if (!File.Exists(file)) return;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length is 0 or > 4096) return;
            using var json = JsonDocument.Parse(stream);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return;
            var id = json.RootElement.GetProperty("Id").GetInt32();
            var ticks = json.RootElement.GetProperty("StartedUtc").GetInt64();
            try
            {
                using var process = Process.GetProcessById(id);
                if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks) throw new InvalidOperationException("旧版启动器留下的游戏仍在运行，请先关闭该游戏。");
            }
            catch (ArgumentException) { }
        }
        catch (IOException) { throw new InvalidOperationException("旧版游戏会话仍被占用，请先关闭旧游戏。"); }
        catch (JsonException) { }
        catch (KeyNotFoundException) { }
    }
}

