using System.IO;
using System.Text.Json;
using FMCore.Authentication;
using FMCore.Java;
using FMCore.Launch;
using FMCore.Versions;
using FMLauncher.Infrastructure.Configuration;
namespace FMLauncher.Infrastructure.Logging;
/// <summary>只记录固定事件名、错误类型和结构化代码，不写异常正文、令牌、请求 URL 或参数。</summary>
public static class DiagnosticLog
{
    private static readonly object Gate = new();
    public static void Record(string operation, Exception error) => Record(operation, error switch
    {
        AuthenticationException e => "Authentication/" + e.Code,
        VersionException e => "Version/" + e.Code,
        JavaException e => "Java/" + e.Code,
        LaunchException e => "Launch/" + e.Code,
        _ => error.GetType().Name + "/" + error.HResult.ToString("X8")
    });
    public static void Record(string operation, string code)
    {
        try
        {
            lock (Gate)
            {
                var folder = new AppPaths().LogsDirectory; Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, "launcher-diagnostics.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, operation, code }) + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
