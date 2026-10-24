using FMCore.Launch;
using FMLauncher.Infrastructure.Configuration;
namespace FMLauncher.CoreIntegration.Mapping;

/// <summary>每行一个参数，避免自行按空格拆分而破坏带空格参数；校验和参数归一化由 SDK 完成。</summary>
internal static class LaunchSettingsMapping
{
    public static string[] Arguments(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public static JavaConfig Java(LauncherSettings s) => new()
    {
        JavaPath = string.IsNullOrWhiteSpace(s.JavaPath) ? null : s.JavaPath,
        MinMemoryMb = s.MinMemoryMb, MaxMemoryMb = s.MaxMemoryMb, Arguments = Arguments(s.JvmArguments),
        // 古老元数据缺少 Java 要求时，由用户明确声明回退主版本，不猜测或改用更高 Java。
        Selection = new FMCore.Java.JavaSelectionOptions { FallbackMajorVersion = s.FallbackJavaMajor }
    };
    public static GameWindowConfig Window(LauncherSettings s) => new() { Width = s.Width, Height = s.Height, Fullscreen = s.Fullscreen };
    public static GameServerConfig? Server(LauncherSettings s) => string.IsNullOrWhiteSpace(s.ServerHost) ? null : new() { Host = s.ServerHost.Trim(), Port = s.ServerPort };
}
