using System.Text.Json;
using System.Text.RegularExpressions;

namespace FMLauncher.UpdateShared;

public static class UpdatePaths
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "FMLauncher", ".minecraft", "versions", "libraries", "assets", "saves", "mods", "config", "resourcepacks", "shaderpacks", "screenshots", "logs", "crash-reports" };
    /// <summary>根目录已包含分隔符，不能重复追加，否则会把盘符下的正常路径误判为越界。</summary>
    private static string DirectoryPrefix(string path) => System.IO.Path.EndsInDirectorySeparator(path)
        ? path : path + System.IO.Path.DirectorySeparatorChar;
    public static string Cache(string root) => System.IO.Path.Combine(System.IO.Path.GetFullPath(root), "FMLauncher", "Updates");
    public static void ValidateProgramPath(string relative)
    {
        if (new[] { ".pem", ".key", ".pfx", ".p12" }.Contains(System.IO.Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包不能包含签名密钥或证书私钥文件。");
        var parts = relative.Split('/');
        if (parts.Length == 0 || Reserved.Contains(parts[0]) || relative.Contains('\\') || parts.Any(p => p.Length == 0 || p is "." or ".."
            || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(['<', '>', ':', '"', '|', '?', '*', '\0']) >= 0
            || p.Any(char.IsControl) || Regex.IsMatch(p.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)))
            throw new InvalidDataException("更新清单包含不允许替换的路径。");
        if (parts.Any(p => p.Equals("Launcher_config.json", StringComparison.OrdinalIgnoreCase) || p.Equals("account_config.json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("更新包不能覆盖用户配置。");
    }
    /// <summary>拒绝越界和目录链接；更新时只访问已签名清单中的程序文件。</summary>
    public static string ProgramFile(string root, string relative, IEnumerable<string>? protectedDirectories = null)
    {
        ValidateProgramPath(relative);
        var fullRoot = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (!path.StartsWith(DirectoryPrefix(fullRoot), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新路径越界。");
        foreach (var protectedPath in protectedDirectories ?? [])
        {
            var directory = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(protectedPath));
            if (!directory.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) && (path.Equals(directory, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(DirectoryPrefix(directory), StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("更新文件与游戏目录冲突。");
        }
        RejectLinks(fullRoot, path); return path;
    }
    public static void RejectLinks(string root, string path)
    {
        var current = System.IO.Path.GetFullPath(root);
        var full = System.IO.Path.GetFullPath(path);
        if (!full.Equals(current, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(DirectoryPrefix(current), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新工作路径越界。");
        void Check(string value) { if ((File.Exists(value) || Directory.Exists(value)) && File.GetAttributes(value).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("不能通过目录链接执行更新。"); }
        Check(current);
        foreach (var part in System.IO.Path.GetRelativePath(current, full).Split(System.IO.Path.DirectorySeparatorChar))
        { if (part == ".") continue; current = System.IO.Path.Combine(current, part); Check(current); }
    }
    public static string Job(string root, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("更新会话标识无效。");
        var path = System.IO.Path.Combine(Cache(root), id); RejectLinks(root, path); return path;
    }
    public static void AtomicBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void Save(string path, UpdateJournal journal) => AtomicBytes(path, JsonSerializer.SerializeToUtf8Bytes(journal, UpdateProduct.Json));
    public static UpdateJournal ReadJournal(string path)
    {
        var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllBytes(path), UpdateProduct.Json) ?? throw new InvalidDataException("更新记录无效。");
        var expected = System.IO.Path.Combine(Job(journal.Root, journal.Id), "job.json");
        if (!System.IO.Path.GetFullPath(path).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新记录位置无效。");
        return journal;
    }
}

public sealed class UpdateJournal
{
    public string Id { get; set; } = "";
    public string Root { get; set; } = "";
    public string Version { get; set; } = "";
    public string Phase { get; set; } = "Prepared";
    public string Error { get; set; } = "";
    public int ParentPid { get; set; }
    public long ParentStartedUtcTicks { get; set; }
    public string ParentExecutable { get; set; } = "";
    public int RecoveryPid { get; set; }
    public long RecoveryStartedUtcTicks { get; set; }
    public List<string> ProtectedDirectories { get; set; } = [];
    public List<UpdateChange> Changes { get; set; } = [];
    public bool InstalledIndexTouched { get; set; }
    public bool HadInstalledIndex { get; set; }
}
public sealed record InstalledRelease(string Manifest, string Signature);
public sealed class UpdateChange
{
    public string Path { get; set; } = "";
    public bool HadOriginal { get; set; }
    public bool Touched { get; set; }
    public bool RemoveOnly { get; set; }
}
