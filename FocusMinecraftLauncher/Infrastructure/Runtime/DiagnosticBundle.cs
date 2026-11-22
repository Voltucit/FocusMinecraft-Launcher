using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using FMCore.Java;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

internal sealed record DiagnosticContext(string LauncherLog, string? Java, int? RequiredJava, string? GameRoot,
    string? GameDirectory, IReadOnlyList<string> AccountValues);

internal sealed record DiagnosticFile(string Name, string Title, string Content);

internal static class DiagnosticBundle
{
    private const int MaxExcerptLength = 100_000;
    private const int MaxMods = 2000;

    internal static string DescribeJava(JavaInfo java) =>
        $"Java {java.Version}（{java.Architecture ?? "架构未知"}）";

    public static IReadOnlyList<DiagnosticFile> Prepare(LauncherError report, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var context = report.DiagnosticContext;
        var privatePaths = new (string? Path, string Label)[]
        {
            (context?.GameDirectory, "[游戏目录]"),
            (context?.GameRoot, "[游戏根目录]"),
            (AppContext.BaseDirectory, "[启动器目录]"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "[用户目录]"),
            (Path.GetTempPath(), "[临时目录]")
        };
        List<DiagnosticFile> files = [];
        void Add(string name, string title, string content)
        {
            token.ThrowIfCancellationRequested();
            var text = RuntimeSupport.SafeText(content);
            foreach (var (path, label) in privatePaths.Where(item => !string.IsNullOrWhiteSpace(item.Path))
                         .OrderByDescending(item => item.Path!.Length))
            {
                var directory = Path.TrimEndingDirectorySeparator(path!);
                // 根卷路径不能作为替换项，否则会误删日志中每一个同盘路径的盘符。
                if (directory == Path.GetPathRoot(directory)) continue;
                text = text.Replace(directory, label, StringComparison.OrdinalIgnoreCase)
                    .Replace(directory.Replace('\\', '/'), label, StringComparison.OrdinalIgnoreCase);
            }
            foreach (var value in context?.AccountValues ?? [])
            {
                text = Regex.Replace(text, $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(value)}(?![\p{{L}}\p{{N}}_])",
                    "[账号已隐藏]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            text = Regex.Replace(text, @"(?i)\b(?:[A-Z0-9._%+\-]+)@[A-Z0-9.\-]+\.[A-Z]{2,}\b", "[邮箱已隐藏]");
            text = Regex.Replace(text, @"\beyJ[\w-]+\.[\w-]+\.[\w-]+\b", "[令牌已隐藏]");
            if (text.Length > MaxExcerptLength)
                text = text[..20_000] + "\n\n[中段内容已省略]\n\n" + text[^(MaxExcerptLength - 20_000)..];
            files.Add(new(name, title, text));
        }

        Add("error-report.txt", "错误报告", report.BuildDiagnosticText());
        if (!string.IsNullOrWhiteSpace(context?.LauncherLog))
            Add("launcher-log.txt", "启动器日志", context.LauncherLog);
        if (report.GameEvidence.Length > 0)
            Add("game-log.txt", "本次游戏日志", report.GameEvidence);
        var java = context?.Java ?? "本次错误未记录 Java 版本。";
        var required = (context?.RequiredJava ?? report.Java?.RequiredMajor) is { } major ? $"需求版本：Java {major}\n" : "";
        Add("environment.txt", "环境与 Java",
            $"{RuntimeInformation.OSDescription}（{RuntimeInformation.OSArchitecture}）\n{RuntimeInformation.FrameworkDescription}\n\n{required}{java}");
        if (context?.GameDirectory is { Length: > 0 } directory)
            Add("mods.txt", "Mod 列表", ReadMods(directory, token));
        return files;
    }

    private static string ReadMods(string gameDirectory, CancellationToken token)
    {
        try
        {
            var mods = Path.Combine(gameDirectory, "mods");
            List<string> names = [];
            foreach (var path in Directory.EnumerateFiles(mods))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                    names.Add(name);
                if (names.Count > MaxMods) break;
            }
            if (names.Count == 0) return "这个实例当前没有 Mod。";
            names.Sort(StringComparer.OrdinalIgnoreCase);
            var list = string.Join(Environment.NewLine, names.Take(MaxMods));
            return "生成预览时的 Mod 文件名\n\n" + list + (names.Count > MaxMods ? "\n[其余条目已省略]" : "");
        }
        catch (DirectoryNotFoundException) { return "这个实例当前没有 mods 目录。"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "无法读取该实例的 Mod 列表。";
        }
    }

    public static async Task SaveAsync(string path, IReadOnlyList<DiagnosticFile> files, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var created = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                created = true;
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(file.Name, CompressionLevel.Optimal);
                    await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    await writer.WriteAsync(file.Content.AsMemory(), token).ConfigureAwait(false);
                }
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            created = false;
        }
        finally
        {
            try { if (created) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceWarning("诊断包临时文件清理失败：{0}", error.GetType().Name); }
        }
    }
}
