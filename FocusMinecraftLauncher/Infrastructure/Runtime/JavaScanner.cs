using System.Diagnostics;
using System.IO;
using FMCore.Java;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>
/// 在核心的探测范围之外补齐常见安装位置：各发行版默认目录、IDE 下载目录、包管理器目录和 Mojang 运行时。
/// 核心只认 JAVA_HOME、PATH、注册表与 ~/.jdks，安装到 Program Files 或由启动器下载的 Java 会漏掉。
/// </summary>
public static class JavaScanner
{
    private const int MaxDepth = 3;
    private const int MaxDirectories = 3000;
    private const int MaxMilliseconds = 2500;
    private const int MaxCandidates = 64;
    private static readonly string[] Vendors =
        ["Java", "Eclipse Adoptium", "Eclipse Foundation", "Zulu", "Amazon Corretto", "Microsoft",
         "BellSoft", "AdoptOpenJDK", "Semeru", "SapMachine", "IBM", "JetBrains", "Azul"];
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        { "WindowsApps", "$Recycle.Bin", "System Volume Information", "node_modules", "Config.Msi", "Recovery", "MSOCache" };

    /// <summary>先取核心已识别的 Java，再补扫额外目录；同一可执行文件只保留一份。</summary>
    public static async Task<IReadOnlyList<JavaInfo>> GetJavasAsync(CancellationToken cancellationToken = default)
    {
        Dictionary<string, JavaInfo> found = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            // 核心覆盖 JAVA_HOME、PATH、注册表与 ~/.jdks；它探测失败的项交给下面的补扫重试。
            foreach (JavaInfo java in await JavaReader.GetJavasAsync(null, null, cancellationToken).ConfigureAwait(false))
                found[java.Executable] = java;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Trace.TraceWarning("核心 Java 探测失败：{0}", error.Message);
        }

        string[] pending = FindCandidates().Where(path => !found.ContainsKey(path)).ToArray();
        using SemaphoreSlim gate = new(6);
        Task[] probes = [.. pending.Select(async path =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                JavaInfo java = await JavaReader.GetJavaInfoAsync(path, timeout.Token).ConfigureAwait(false);
                lock (found) found[java.Executable] = java;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                // 单个可执行文件不可用（损坏、权限、32 位不兼容）不影响其余结果。
                Trace.TraceWarning("Java 探测失败：{0}；{1}", path, error.Message);
            }
            finally { gate.Release(); }
        })];
        await Task.WhenAll(probes).ConfigureAwait(false);

        return [.. found.Values
            .OrderByDescending(java => java.MajorVersion)
            .ThenByDescending(java => java.Version, StringComparer.OrdinalIgnoreCase)
            .ThenBy(java => java.Executable, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>所有候选可执行文件；只做路径筛选，真正可用性由探测决定。</summary>
    private static IEnumerable<string> FindCandidates()
    {
        List<string> files = [];
        ScanBudget budget = new();
        foreach (string root in Roots())
        {
            files.Add(Path.Combine(root, "bin", "java.exe"));
            files.AddRange(FindNested(root, budget));
        }
        files.AddRange(FindMojangRuntimes());
        return files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxCandidates);
    }

    /// <summary>限定深度与总耗时的广度优先搜索，避免在 Program Files 这类目录里无界遍历。</summary>
    private static IEnumerable<string> FindNested(string root, ScanBudget budget)
    {
        Queue<(string Path, int Depth)> pending = new();
        pending.Enqueue((root, 0));
        while (pending.Count > 0 && budget.TryEnter())
        {
            (string directory, int depth) = pending.Dequeue();
            foreach (string child in SafeChildren(directory))
            {
                if (Skipped.Contains(Path.GetFileName(child)) || IsLink(child)) continue;
                string java = Path.Combine(child, "bin", "java.exe");
                if (File.Exists(java)) yield return java;
                if (depth + 1 < MaxDepth) pending.Enqueue((child, depth + 1));
            }
        }
    }

    /// <summary>Mojang 运行时是 runtime\名称\平台\名称\bin\java.exe，比普通安装多一层。</summary>
    private static IEnumerable<string> FindMojangRuntimes()
    {
        string runtime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime");
        if (!Directory.Exists(runtime)) yield break;
        foreach (string launcher in SafeChildren(runtime))
        foreach (string platform in SafeChildren(launcher))
        foreach (string name in SafeChildren(platform))
        {
            string java = Path.Combine(name, "bin", "java.exe");
            if (File.Exists(java)) yield return java;
        }
    }

    /// <summary>只返回真实存在的目录，避免把不存在的路径交给核心去解析。</summary>
    private static IEnumerable<string> Roots()
    {
        List<string> roots = [];
        string[] programs =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        ];
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string program in programs.Where(path => path.Length > 0))
        foreach (string vendor in Vendors)
            roots.Add(Path.Combine(program, vendor));
        foreach (string vendor in Vendors)
            roots.Add(Path.Combine(local, "Programs", vendor));
        roots.Add(Path.Combine(profile, ".jdks"));
        roots.Add(Path.Combine(profile, ".gradle", "jdks"));
        roots.Add(Path.Combine(profile, "scoop", "apps"));
        roots.Add(Path.Combine(profile, "jdks"));
        roots.Add(Path.Combine(AppContext.BaseDirectory, "FMLauncher", "Java"));
        return roots.Where(Directory.Exists);
    }

    private static IEnumerable<string> SafeChildren(string directory)
    {
        try { return Directory.EnumerateDirectories(directory).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool IsLink(string directory)
    {
        try { return File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>所有根目录共享的次数与耗时上限，保证启动时的自动检测不会拖慢界面。</summary>
    private sealed class ScanBudget
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _directories;
        public bool TryEnter()
        {
            if (_directories >= MaxDirectories || _clock.ElapsedMilliseconds >= MaxMilliseconds) return false;
            _directories++;
            return true;
        }
    }
}
