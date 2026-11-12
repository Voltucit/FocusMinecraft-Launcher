using System.IO;
using System.Text;
using FocusMinecraftLauncher.Preview;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>按明确的失败签名分析日志。普通组件名称、警告及退出码本身不作为根因证据。</summary>
public static class GameLogs
{
    private const int TailLines = 500;
    private const int MaxReadBytes = 512 * 1024;
    public static string LogPath(PreviewInstance instance) => Path.Combine(instance.GameDirectory, "logs", "latest.log");
    public static string CrashFolder(PreviewInstance instance) => Path.Combine(instance.GameDirectory, "crash-reports");
    public static IReadOnlyList<FileInfo> Crashes(PreviewInstance instance)
    {
        var folder = CrashFolder(instance);
        if (!Directory.Exists(folder)) return [];
        return new DirectoryInfo(folder).EnumerateFiles("*.txt").OrderByDescending(file => file.LastWriteTimeUtc).Take(20).ToArray();
    }
    public static async Task<string> ReadTailAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
        var start = Math.Max(0, stream.Length - MaxReadBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        if (start > 0) await reader.ReadLineAsync(token);
        var lines = new Queue<string>();
        while (await reader.ReadLineAsync(token) is { } line)
        {
            lines.Enqueue(line);
            if (lines.Count > TailLines) lines.Dequeue();
        }
        return string.Join(Environment.NewLine, lines);
    }
    public static Task<string> ReadLatestAsync(PreviewInstance instance, CancellationToken token) => ReadTailAsync(LogPath(instance), token);

    public static async Task<GameExitException> DescribeExitAsync(PreviewInstance instance, int exitCode, DateTime startedUtc, string output, CancellationToken token)
    {
        var evidence = new StringBuilder("=== 本次游戏进程输出 ===\n" + output);
        async Task AppendNewFile(string path, bool includeHeader = false)
        {
            // 不使用上一次启动的日志解释本次崩溃；保留 1 秒文件时间精度余量。
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < startedUtc.AddSeconds(-1)) return;
            evidence.AppendLine().AppendLine("=== " + path + " ===");
            if (includeHeader)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                evidence.AppendLine("--- 报告开头 ---");
                for (var index = 0; index < 120 && await reader.ReadLineAsync(token) is { } line; index++)
                    evidence.AppendLine(line);
                evidence.AppendLine("--- 报告末尾 ---");
            }
            evidence.AppendLine(await ReadTailAsync(path, token));
        }
        try
        {
            await AppendNewFile(LogPath(instance));
            if (Crashes(instance).FirstOrDefault(file => file.LastWriteTimeUtc >= startedUtc.AddSeconds(-1)) is { } crash)
                await AppendNewFile(crash.FullName, includeHeader: true);
            if (Directory.Exists(instance.GameDirectory))
            {
                var native = new DirectoryInfo(instance.GameDirectory).EnumerateFiles("hs_err_pid*.log")
                    .Where(file => file.LastWriteTimeUtc >= startedUtc.AddSeconds(-1)).OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
                if (native != null) await AppendNewFile(native.FullName, includeHeader: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { evidence.AppendLine().AppendLine("附加日志读取失败：" + error.Message); }
        var text = evidence.ToString();
        var findings = Analyze(text).Take(3).ToArray();
        var message = $"游戏异常退出（退出码 {exitCode}）。";
        var advice = "打开游戏的“日志”页面查看完整日志，或复制本次错误信息用于反馈；退出码本身不能确定崩溃原因。";
        if (findings.Length > 0)
        {
            message += "\n日志提示的可能原因：" + string.Join("；", findings.Select(item => item.Title)) + "。";
            advice = string.Join(Environment.NewLine, findings.Select(item => item.Advice));
        }
        else message += "尚未从本次日志中识别出明确原因。";
        var analysis = string.Join(Environment.NewLine, findings.Select(item => item.Detail));
        return new GameExitException(message, advice, text, analysis);
    }

    public static IReadOnlyList<LogFinding> Analyze(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var findings = new List<LogFinding>();
        void Add(string title, string detail, string advice, params string[] signatures)
        {
            var evidence = text.Split('\n').FirstOrDefault(line => signatures.Any(key => line.Contains(key, StringComparison.OrdinalIgnoreCase)))?.Trim();
            if (evidence == null) return;
            if (evidence.Length > 500) evidence = evidence[..500] + "…";
            findings.Add(new(title, detail + "\n日志依据：" + RuntimeSupport.SafeText(evidence), advice));
        }
        Add("Java 版本不匹配", "游戏或 Mod 需要的 Java 比当前使用的版本新。",
            "在游戏的“启动设置”中选择该游戏或整合包要求的 Java 版本，再重试。",
            "UnsupportedClassVersionError", "has been compiled by a more recent version of the Java Runtime");
        Add("Java 启动参数无效", "Java 拒绝了当前启动参数。",
            "检查“启动设置”的高级 JVM 参数，移除不受当前 Java 支持的参数；保留修改前的参数以便恢复。",
            "Unrecognized VM option", "Unrecognized option:", "Improperly specified VM option");
        Add("Java 无法找到启动文件或主类", "Java 未能加载游戏入口。",
            "检查并补全游戏文件，核对高级启动参数中的主类和文件路径，并检查文件是否被安全软件隔离。",
            "Unable to access jarfile", "Could not find or load main class");
        Add("内存不足或内存分配失败", "日志记录了内存耗尽或无法预留内存。",
            "堆内存不足时适当增加游戏内存；无法预留内存时减少分配，并关闭占用较高的程序。检查 Java 是否为 64 位。",
            "java.lang.OutOfMemoryError", "Could not reserve enough space", "There is insufficient memory for the Java Runtime", "Native memory allocation (malloc) failed");
        Add("缺少前置 Mod", "加载器明确报告缺少必需依赖。",
            "按加载器输出补齐指定前置 Mod，并确保游戏、加载器与前置版本满足要求。",
            "Missing or unsupported mandatory dependencies", "which is missing", "Missing dependency");
        Add("Mod 依赖或版本冲突", "加载器无法得到兼容的 Mod 组合。",
            "查看加载器输出中的 Mod 名称、要求版本及建议方案，调整对应 Mod；先备份再修改整合包。",
            "Incompatible mod set", "Incompatible mods found!", "Mod resolution failed", "A potential solution has been determined");
        Add("重复安装 Mod", "加载器检测到重复 Mod。",
            "在 Mod 管理中保留与游戏匹配的一份文件，移除重复版本。",
            "Duplicate mods", "DuplicateModsFoundException", "found a duplicate mod");
        Add("Mixin 注入失败", "可能涉及 Mod 冲突、版本不匹配或缺少依赖，不能仅凭这一条确定责任 Mod。",
            "查看日志中的 Mixin 配置及 Mod 名称，优先排查最近添加或更新的 Mod，并核对依赖与版本。",
            "Mixin apply failed", "InvalidMixinException", "MixinTransformerError");
        Add("显卡驱动或 OpenGL 初始化失败", "日志明确报告图形接口初始化失败。",
            "安装显卡厂商驱动并检查硬件是否支持该游戏所需的 OpenGL；双显卡设备可检查游戏的显卡选择。",
            "Pixel format not accelerated", "GLFW error 65542", "GLFW error 65543", "The driver does not appear to support OpenGL", "Couldn't set pixel format", "Failed to create the GLFW window");
        Add("Mod 配置文件解析失败", "日志指出 Mod 配置文件无法读取。",
            "根据日志中指出的配置路径检查格式；先备份该配置，再按照对应 Mod 的说明修复。",
            "Failed loading config file", "com.electronwill.nightconfig.core.io.ParsingException");
        Add("类或方法加载失败", "可能是组件缺失、文件损坏或 Mod 与加载器版本不兼容。",
            "先检查并补全受管游戏文件，再核对相关 Mod 与加载器版本；具体类名见日志。",
            "java.lang.NoSuchMethodError", "java.lang.NoClassDefFoundError", "java.lang.ClassNotFoundException");
        Add("Java 虚拟机发生本机崩溃", "需要结合 hs_err 报告中的 Problematic frame 判断，不能仅凭退出码确定原因。",
            "查看技术详情中的 hs_err 报告与崩溃模块；核对 Java 版本，若模块指向显卡驱动则更新驱动。",
            "A fatal error has been detected by the Java Runtime Environment");
        return findings;
    }
}

public sealed record LogFinding(string Title, string Detail, string Advice);

public sealed class GameExitException(string message, string advice, string evidence, string analysis) : InvalidOperationException(message)
{
    public string Advice { get; } = advice;
    public string Analysis { get; } = analysis;
    public override string ToString() => base.ToString() + Environment.NewLine + evidence;
}
