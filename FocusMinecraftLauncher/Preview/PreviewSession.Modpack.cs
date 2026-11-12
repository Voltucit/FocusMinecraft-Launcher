using System.IO;
using FocusMinecraftLauncher.Infrastructure.Runtime;
using FocusMinecraftLauncher.Presentation.Dialogs;

namespace FocusMinecraftLauncher.Preview;

public sealed partial class PreviewSession
{
    private async Task<PackManifest> ResolveManualPackFilesAsync(PackImportRequest request, PackManifest manifest,
        PreviewTask task, CancellationToken token, IReadOnlyList<PackFile>? unavailable = null)
    {
        var unavailablePaths = unavailable?.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var required = manifest.Files.Where(file => file.RequiresManualFile || unavailablePaths.Contains(file.Path)
            || request.ManualSources.ContainsKey(file.Path)).ToArray();
        if (required.Length == 0) return manifest;

        var instance = new PreviewInstance(request.InstanceName, manifest.Name, manifest.Minecraft, manifest.Loader)
            { Root = request.Root, IsolationMode = SavedSettings.VersionIsolation };
        ModpackArchive.EnsureGameDirectoryWritePath(instance);
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<PackFile>();
        Report(task, "检查需手动补齐的整合包资源");
        foreach (var file in required)
        {
            token.ThrowIfCancellationRequested();
            if (request.ManualSources.TryGetValue(file.Path, out var source) && await CanReuseAsync(file, source))
            { sources[file.Path] = source; continue; }
            // 重试时旧的手动文件失效，仍有自动地址的资源先重新尝试自动下载。
            if (!file.RequiresManualFile && !unavailablePaths.Contains(file.Path)) continue;
            var destination = ModpackArchive.SafePath(instance.GameDirectory, file.Path);
            if (await CanReuseAsync(file, destination)) sources[file.Path] = destination;
            else missing.Add(file);
        }
        if (missing.Count > 0)
        {
            Report(task, $"等待手动补齐 {missing.Count} 项整合包资源");
            var selected = ManualPackFilesDialog.Ask(missing, token);
            if (selected == null) throw new UserCanceledOperationException("已取消手动补齐整合包资源。");
            foreach (var item in selected) sources[item.Key] = item.Value;
        }
        token.ThrowIfCancellationRequested();
        foreach (var item in sources) request.ManualSources[item.Key] = item.Value;
        return manifest with { Files = manifest.Files.Select(file => sources.TryGetValue(file.Path, out var source)
            ? file with { SourcePath = source } : file).ToArray() };

        async Task<bool> CanReuseAsync(PackFile file, string path)
        {
            try { return await Task.Run(() => ModpackArchive.MatchesFileAsync(file, path, token), token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Log("整合包资源需要重新选择：" + file.FileName + "；" + RuntimeSupport.Error(error)); return false; }
        }
    }
}
