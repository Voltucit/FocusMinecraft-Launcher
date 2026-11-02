using System.IO;
using System.Text;
using FocusMinecraftLauncher.Preview;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>读取最近一次启动日志与崩溃报告，并按常见签名给出可执行的结论。</summary>
public static class GameLogs
{
    private const int TailLines = 500;
    public static string LogPath(PreviewInstance instance) => Path.Combine(instance.GameDirectory, "logs", "latest.log");
    public static string CrashFolder(PreviewInstance instance) => Path.Combine(instance.GameDirectory, "crash-reports");
    public static IReadOnlyList<FileInfo> Crashes(PreviewInstance instance)
    {
        var folder = CrashFolder(instance);
        if (!Directory.Exists(folder)) return [];
        return new DirectoryInfo(folder).EnumerateFiles("*.txt").OrderByDescending(file => file.LastWriteTimeUtc).Take(20).ToArray();
    }
    /// <summary>只取尾部若干行：日志开头是加载器横幅，真正的原因都在末尾。</summary>
    public static async Task<string> ReadTailAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new List<string>(TailLines);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(token) is { } line)
        {
            buffer.Add(line);
            if (buffer.Count > TailLines) buffer.RemoveAt(0);
        }
        return string.Join(Environment.NewLine, buffer);
    }
    public static async Task<string> ReadLatestAsync(PreviewInstance instance, CancellationToken token) =>
        await ReadTailAsync(LogPath(instance), token);
    public static IReadOnlyList<LogFinding> Analyze(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var findings = new List<LogFinding>();
        void Add(string title, string detail, string advice)
        {
            if (!findings.Any(item => item.Title == title)) findings.Add(new(title, detail, advice));
        }
        if (Contains(text, "UnsupportedClassVersionError", "class file version", "has been compiled by a more recent version"))
            Add("Java 版本太低", "游戏或 Mod 需要的 Java 比当前使用的版本新。",
                "到「设置」→ 游戏，选择与该版本匹配的 Java（1.18 以上通常用 17 或 21）；也可以换一个更高的 Java 再试。");
        if (Contains(text, "OutOfMemoryError", "Java heap space", "GC overhead limit exceeded"))
            Add("内存不足", "游戏分配到的内存不够用了。",
                "到「设置」→ 游戏调高内存占用，关掉自动分配后手动给出更大值；巨型整合包通常需要 6 GB 以上。");
        if (Contains(text, "Missing or unsupported mandatory dependencies", "which is missing", "Missing dependency", "Mod resolution failed", "Incompatible mod set"))
            Add("缺少前置 Mod", "有 Mod 声明了必需的依赖，但对应文件不在 mods 里。",
                "在 Mod 管理里找到提示中提到的 Mod，点「下载新 Mod」补齐它要求的前置；版本要匹配当前游戏版本。");
        if (Contains(text, "Duplicate mods", "DuplicateModsFoundException", "found a duplicate mod"))
            Add("Mod 重复", "同一个 Mod 装了两份（常见于整合包自带的与手动放入的）。",
                "在 Mod 管理里按名称搜索，保留一份并删除其余重复文件。");
        if (Contains(text, "Mixin apply failed", "InvalidMixinException", "MixinTransformerError", "Mixin transformation of", "was not transformed"))
            Add("Mod 与游戏版本不匹配", "MixIn 注入失败，通常是 Mod 声明的版本与当前游戏或加载器不一致。",
                "逐个禁用最近安装的 Mod 再启动，确认是哪一个；在下载页按当前版本重新下载对应版本。");
        if (Contains(text, "Pixel format not accelerated", "GLFW error", "Failed to create window", "OpenGL"))
            Add("显卡驱动或 OpenGL 异常", "创建窗口或初始化图形接口失败。",
                "更新显卡驱动；笔记本双显卡时在驱动面板里把游戏指定为独显；关闭强制使用软件渲染的选项。");
        if (Contains(text, "NoSuchMethodError", "NoClassDefFoundError", "ClassNotFoundException"))
            Add("类加载失败", "游戏或 Mod 调用了不存在的类，通常是版本错配或文件不完整。",
                "先「检查并补全文件」，再确认 Mod 版本与游戏一致；仍失败时禁用最近安装的 Mod。");
        if (Contains(text, "The game crashed whilst", "An unexpected issue occurred"))
            Add("游戏崩溃", "Minecraft 自己捕获到致命错误，具体原因见下方日志与崩溃报告。",
                "查看崩溃报告里第一条 Caused by，按提示处理；也可以在启动设置里换一个 Java 版本重试。");
        return findings;
    }
    private static bool Contains(string text, params string[] keys) =>
        keys.Any(key => text.Contains(key, StringComparison.OrdinalIgnoreCase));
}

public sealed record LogFinding(string Title, string Detail, string Advice);
