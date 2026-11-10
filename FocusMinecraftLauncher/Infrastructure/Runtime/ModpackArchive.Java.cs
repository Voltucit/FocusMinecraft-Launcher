using System.IO.Compression;
using FMCore.Downloads;
using FocusMinecraftLauncher.Preview;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

public static partial class ModpackArchive
{
    /// <summary>先准备包内 Windows Java 的完整目录，供加载器安装使用；其余整合包内容仍按原流程导入。</summary>
    public static async Task<IReadOnlyList<string>> PrepareBundledJavaAsync(
        string archivePath, PackManifest manifest, PreviewInstance instance, InstanceOptions options,
        int concurrency, Action<DownloadBatchProgress> progress, CancellationToken token)
    {
        EnsureGameDirectoryWritePath(instance);
        string[] executables;
        using (var archive = ZipFile.OpenRead(archivePath))
        {
            var overrides = ReadOverrides(archive, instance, manifest);
            executables = overrides.Keys.Concat(manifest.Files.Where(file => file.Client == "required").Select(file => file.Path))
                .Select(path => path.Replace('\\', '/'))
                .Where(path => JavaHome(path) != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .GroupBy(path => JavaHome(path)!, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(path => path.EndsWith("/java.exe", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).First())
                .ToArray();
        }
        if (executables.Length == 0) return [];

        var homes = executables.Select(path => JavaHome(path)!).ToArray();
        bool InRuntime(string path)
        {
            path = path.Replace('\\', '/');
            return homes.Any(home => home.Length == 0 || path.StartsWith(home + "/", StringComparison.OrdinalIgnoreCase));
        }
        await InstallContentAsync(archivePath, manifest, instance, options, concurrency, progress, token, InRuntime);
        return executables.Select(path => SafePath(instance.GameDirectory, path)).ToArray();
    }

    private static string? JavaHome(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Length < 2 || !parts[^2].Equals("bin", StringComparison.OrdinalIgnoreCase)
            || !(parts[^1].Equals("java.exe", StringComparison.OrdinalIgnoreCase) || parts[^1].Equals("javaw.exe", StringComparison.OrdinalIgnoreCase)))
            return null;
        return string.Join('/', parts[..^2]);
    }
}
