using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FMCore.Installer;
using FMCore.Logger;
using FMCore.Versions;
using FocusMinecraftLauncher.Infrastructure.Runtime;

namespace FocusMinecraftLauncher.Preview;

public sealed partial class PreviewSession
{
    public void StartFileRepair(PreviewInstance instance)
    {
        if (IsBusy || _cleaningStorage) return;
        Enqueue(PreviewTaskKind.Repair, instance.Name + "文件检查", instance.Version, instance.Root, instance.Id,
            (task, token) => EnsureFilesAsync(instance, task, token));
    }

    /// <summary>检查游戏文件和继承关系。同目录写入串行，取消或失败后不继续启动。</summary>
    public async Task EnsureFilesAsync(PreviewInstance instance, PreviewTask task, CancellationToken token,
        Architecture? targetArchitecture = null)
    {
        task.RecoveryInstance = instance;
        if (!_rootWriters.TryGetValue(instance.Root, out var writer)) _rootWriters[instance.Root] = writer = new(1, 1);
        await writer.WaitAsync(token);
        var changed = false;
        try
        {
            var reader = new MinecraftVersionReader();
            if (await NeedsParentVersionCheckAsync(instance, token))
            {
                var versions = await reader.GetVersionsAsync(instance.Root, token);
                var version = versions.Single(v => v.Id == instance.Id);
                var current = version;
                var visited = new HashSet<string>(StringComparer.Ordinal);
                // 只恢复实际声明且在官方清单中存在的父版本，不根据实例名称猜测游戏版本。
                while (current.InheritsFrom is { } parentId && visited.Add(current.Id))
                {
                    var parent = versions.SingleOrDefault(v => v.Id == parentId);
                    if (parent == null)
                    {
                        Report(task, "检查缺少的游戏配置");
                        var official = await MinecraftInstaller.GetVersionsAsync(token);
                        if (!official.Any(v => v.Id == parentId))
                            throw new InvalidOperationException($"缺少自定义父版本 {parentId}，请先恢复该版本，再检查并补全文件。");
                        Report(task, "恢复官方游戏版本 " + parentId);
                        await new MinecraftInstaller(parentId, instance.Root, DownloadConcurrency, targetArchitecture) { Source = FileSource }
                            .InstallAsync(p => Report(task, "恢复游戏配置：" + p.Message, p.Download), token);
                        changed = true;
                        MarkDependencyVersion(instance.Root, parentId);
                        versions = await reader.GetVersionsAsync(instance.Root, token);
                        parent = versions.Single(v => v.Id == parentId);
                    }
                    current = parent;
                }
            }

            // FMCore 能直接读取继承链和 jar 引用，检查时保留现有版本配置。
            await CheckAndCompleteFilesAsync(instance, task, token, targetArchitecture);
            token.ThrowIfCancellationRequested();
            if (changed || instance.MetadataIssue.Length > 0)
            {
                var versions = await reader.GetVersionsAsync(instance.Root, token);
                var updated = await InstanceStore.ReadVersionAsync(versions.Single(v => v.Id == instance.Id), versions, instance.Root, token);
                instance.Version = updated.Version; instance.RequiredJava = updated.RequiredJava; instance.LoaderVersion = updated.LoaderVersion;
                instance.PrefersLegacyJava8 = updated.PrefersLegacyJava8;
                instance.MetadataIssue = updated.MetadataIssue; instance.Options = updated.Options;
                Signal();
            }
            Report(task, "检查完整性完成");
        }
        finally
        {
            writer.Release();
            // 恢复父版本也可能先成功；即使后续步骤失败，列表仍重新读取实际落盘状态。
            if (changed && string.Equals(instance.Root, SelectedRoot, StringComparison.OrdinalIgnoreCase)) await RefreshInstancesAsync();
        }
    }

    private static async Task<bool> NeedsParentVersionCheckAsync(PreviewInstance instance, CancellationToken token)
    {
        // 独立版本无需先遍历所有版本。路径或声明不明确时仍由核心解析版本与继承关系。
        var folder = Path.TrimEndingDirectorySeparator(instance.Folder);
        var profilePath = Path.Combine(folder, Path.GetFileName(folder) + ".json");
        try
        {
            var profile = JsonNode.Parse(await File.ReadAllTextAsync(profilePath, token));
            return profile is not JsonObject data
                || data["id"] is not JsonValue id || !id.TryGetValue<string>(out var profileId) || profileId != instance.Id
                || data["inheritsFrom"] != null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return true;
        }
    }

    /// <summary>核心负责离线检查及联网补全；资源映射目录与最终启动的隔离策略保持一致。</summary>
    private async Task CheckAndCompleteFilesAsync(PreviewInstance instance, PreviewTask task, CancellationToken token,
        Architecture? targetArchitecture = null)
    {
        Report(task, "检查完整性");
        var checker = new GameFileChecker(instance.Id, instance.Root, targetArchitecture) { GameDirectory = instance.GameDirectory };
        var lastProgressUpdate = Environment.TickCount64;
        var lastFileTotal = 0;
        var baseFileTotal = 0;
        var check = await checker.CheckAsync(progress =>
        {
            var total = progress.TotalKnownFiles;
            if (total <= 0) return;
            var now = Environment.TickCount64;
            // 文件计数每次变化会被当成新阶段，需在进入界面调度前限频。
            if (progress.FilePath != null && total == lastFileTotal && now - lastProgressUpdate < 100) return;
            lastFileTotal = total;
            lastProgressUpdate = now;
            if (baseFileTotal == 0) baseFileTotal = total;
            // 资源索引解析后才知道资源总数，分别显示基础文件与资源文件的进度。
            var checkingResources = total > baseFileTotal;
            var previousFiles = checkingResources ? baseFileTotal : 0;
            var completedFiles = progress.CheckedFiles - previousFiles;
            var phaseTotal = total - previousFiles;
            var phase = checkingResources ? "资源文件" : "基础文件";
            Report(task, $"检查完整性：{phase} {completedFiles}/{phaseTotal}", files: (completedFiles, phaseTotal));
        }, token);
        token.ThrowIfCancellationRequested();
        if (check.FileContentVerified) return;
        // PCL2 等外部安装可能缺少校验声明，但文件本身是完整的；只有实际缺失或内容不符时才强制补全。
        if (check.MissingFiles == 0 && check.InvalidFiles == 0)
        {
            Log(instance.Id + $"：{check.UnverifiableFiles} 项缺少校验声明，未发现缺失或内容不符，跳过强制补全。");
            Report(task, "检查完整性完成");
            return;
        }

        Log(instance.Id + $"：文件检查发现缺失 {check.MissingFiles} 项，内容不符 {check.InvalidFiles} 项，校验声明不足 {check.UnverifiableFiles} 项，交由核心补全并校验。");
        // MetadataMissing 不是损坏；在线补全器正常返回即完成校验，不用同一份不足的本地声明再次拦截。
        var completed = await new GameFileCompleter(instance.Id, instance.Root, DownloadConcurrency, targetArchitecture: targetArchitecture)
        { Source = FileSource, GameDirectory = instance.GameDirectory, ReuseIncompleteLegacyLibraries = true }.CompleteAsync(p =>
        {
            var stage = p.Stage is GameFileCompletionStage.CompletingClientAndLibraries or GameFileCompletionStage.CompletingResources
                ? "补全并校验文件" : "检查完整性";
            Report(task, stage + "：" + p.Message, p.Download);
        }, token);
        token.ThrowIfCancellationRequested();
        Log(instance.Id + $"：核心文件校验完成，共 {completed.TotalFiles} 个文件，下载 {completed.DownloadedFiles} 个，复用 {completed.ReusedFiles} 个，映射 {completed.MappedFiles} 个。"
            + (completed.UnverifiedLegacyLibraries > 0
                ? $"其中 {completed.UnverifiedLegacyLibraries} 项旧版依赖缺少完整校验声明，已保留本地文件。" : ""));
    }
}
