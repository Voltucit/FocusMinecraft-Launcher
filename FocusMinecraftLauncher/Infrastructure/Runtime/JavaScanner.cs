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
    private const int MaxDeepDirectories = 120000;
    private const int MaxDeepMilliseconds = 45000;
    private static readonly string[] Vendors =
        ["Java", "Eclipse Adoptium", "Eclipse Foundation", "Zulu", "Amazon Corretto", "Microsoft",
         "BellSoft", "AdoptOpenJDK", "Semeru", "SapMachine", "IBM", "JetBrains", "Azul"];
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        { "WindowsApps", "$Recycle.Bin", "System Volume Information", "node_modules", "Config.Msi", "Recovery", "MSOCache" };
    private static readonly HashSet<string> DeepSkipped = new(Skipped, StringComparer.OrdinalIgnoreCase)
        { "Windows", "WinSxS", "System32", "SysWOW64", ".git", ".vs", "obj", "packages", "__pycache__", "Cache" };
    private static IReadOnlyList<JavaInfo> _cached = [];

    /// <summary>最近一次快速或全盘扫描的结果；自动选择需要复用它，避免又退回核心的有限探测范围。</summary>
    public static IReadOnlyList<JavaInfo> Cached => Volatile.Read(ref _cached);

    /// <summary>从配置恢复上次扫描结果；只接受仍存在的可执行文件，避免启动时马上执行所有旧路径。</summary>
    public static void LoadCached(IEnumerable<JavaInfo> values)
        => Volatile.Write(ref _cached, values.Where(java => File.Exists(java.Executable)).ToArray());

    /// <summary>新安装或刚验证的 Java 立即进入缓存，避免下一次启动仍使用旧扫描结果。</summary>
    public static void Remember(JavaInfo java)
    {
        if (!File.Exists(java.Executable)) return;
        List<JavaInfo> values = [.. Cached.Where(item => !string.Equals(item.Executable, java.Executable, StringComparison.OrdinalIgnoreCase)), java];
        Volatile.Write(ref _cached, [.. values
            .OrderByDescending(item => item.MajorVersion)
            .ThenByDescending(item => item.Version, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Executable, StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>先取核心已识别的 Java，再补扫额外目录；同一可执行文件只保留一份。</summary>
    public static Task<IReadOnlyList<JavaInfo>> GetJavasAsync(CancellationToken cancellationToken = default)
        => GetJavasAsync(false, null, cancellationToken);

    /// <summary>deep 为 true 时遍历所有固定磁盘；仅在用户主动选择全盘扫描时使用。</summary>
    public static async Task<IReadOnlyList<JavaInfo>> GetJavasAsync(bool deep, IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        Dictionary<string, JavaInfo> found = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            // 先沿用上次扫描结果，再补充核心探测；自动选择不应因为重启而退回有限范围。
            foreach (JavaInfo java in Cached) if (File.Exists(java.Executable)) found[java.Executable] = java;
            // 核心覆盖 JAVA_HOME、PATH、注册表与 ~/.jdks；它探测失败的项交给下面的补扫重试。
            foreach (JavaInfo java in await JavaReader.GetJavasAsync(null, null, cancellationToken).ConfigureAwait(false))
                found[java.Executable] = java;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Trace.TraceWarning("核心 Java 探测失败：{0}", error.Message);
        }

        string[] pending = await Task.Run(() => FindCandidates(deep, progress, cancellationToken)
            .Where(path => !found.ContainsKey(path)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(deep ? MaxCandidates * 4 : MaxCandidates).ToArray(), cancellationToken).ConfigureAwait(false);
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

        IReadOnlyList<JavaInfo> result = [.. found.Values
            .OrderByDescending(java => java.MajorVersion)
            .ThenByDescending(java => java.Version, StringComparer.OrdinalIgnoreCase)
            .ThenBy(java => java.Executable, StringComparer.OrdinalIgnoreCase)];
        Volatile.Write(ref _cached, result);
        return result;
    }

    /// <summary>所有候选可执行文件；只做路径筛选，真正可用性由探测决定。</summary>
    private static IEnumerable<string> FindCandidates(bool deep, IProgress<string>? progress, CancellationToken token)
    {
        List<string> files = [];
        ScanBudget budget = new();
        foreach (string root in Roots())
        {
            files.Add(Path.Combine(root, "bin", "java.exe"));
            files.AddRange(FindNested(root, budget));
        }
        files.AddRange(FindMojangRuntimes());
        if (deep) files.AddRange(FindAllFixedDrives(progress, token));
        return files.Where(File.Exists);
    }

    /// <summary>仅在用户主动全盘扫描时遍历固定磁盘；跳过系统目录、链接和常见缓存，限制总目录数与耗时。</summary>
    private static IEnumerable<string> FindAllFixedDrives(IProgress<string>? progress, CancellationToken token)
    {
        Stack<string> pending = new();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try { if (drive.DriveType == DriveType.Fixed && drive.IsReady) pending.Push(drive.RootDirectory.FullName); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        int visited = 0;
        Stopwatch clock = Stopwatch.StartNew();
        while (pending.Count > 0 && visited < MaxDeepDirectories && clock.ElapsedMilliseconds < MaxDeepMilliseconds)
        {
            token.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            visited++;
            if (visited % 1500 == 0) progress?.Report($"已扫描 {visited} 个文件夹…");
            string java = Path.Combine(directory, "bin", "java.exe");
            if (IsJavaHome(directory)) yield return java;
            foreach (string child in SafeChildren(directory))
            {
                if (DeepSkipped.Contains(Path.GetFileName(child)) || IsLink(child)) continue;
                pending.Push(child);
            }
        }
        progress?.Report(visited >= MaxDeepDirectories || clock.ElapsedMilliseconds >= MaxDeepMilliseconds
            ? "已达到全盘扫描上限，可继续使用手动选择。"
            : "全盘扫描完成。");
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

    private static bool IsJavaHome(string directory)
    {
        if (!File.Exists(Path.Combine(directory, "bin", "java.exe"))) return false;
        return File.Exists(Path.Combine(directory, "release"))
            || File.Exists(Path.Combine(directory, "bin", "java.dll"))
            || File.Exists(Path.Combine(directory, "lib", "modules"));
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
