using System.IO;
using System.Text.Json.Nodes;
using FMCore.Installer;
using FMCore.Logger;
using FMCore.Versions;
using FocusMinecraftLauncher.Infrastructure.Runtime;

namespace FocusMinecraftLauncher.Preview;

public sealed partial class PreviewSession
{
    /// <summary>检查、补全并将旧的继承实例转换为独立版本；同目录写入串行，取消或失败后不继续启动。</summary>
    public async Task EnsureFilesAsync(PreviewInstance instance, PreviewTask task, CancellationToken token)
    {
        if (!_rootWriters.TryGetValue(instance.Root, out var writer)) _rootWriters[instance.Root] = writer = new(1, 1);
        await writer.WaitAsync(token);
        var changed = false;
        try
        {
            var reader = new MinecraftVersionReader();
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
                    await new MinecraftInstaller(parentId, instance.Root, DownloadConcurrency) { Source = FileSource }
                        .InstallAsync(p => Report(task, "恢复游戏配置：" + p.Message, p.Download), token);
                    changed = true;
                    versions = await reader.GetVersionsAsync(instance.Root, token);
                    parent = versions.Single(v => v.Id == parentId);
                }
                current = parent;
            }
            // 完整继承校验、合并和客户端复制均交给核心，保留核心对循环和损坏配置的错误。
            var chain = reader.GetInheritanceChain(version, versions);
            var profile = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(version.Directory, Path.GetFileName(version.Directory) + ".json"), token));
            var convert = version.InheritsFrom != null || profile?["jar"] != null;
            if (convert)
            {
                ModpackArchive.EnsureInstanceWritePath(instance);
                // 整合包把完整启动信息写进自己的版本文件时没有继承链，此时不能把整合包名当成游戏版本存下来。
                var gameVersion = InstanceStore.IsMinecraftVersion(instance.Version) ? instance.Version : chain.Last().Id;
                instance.Options = await InstanceStore.UpdateAsync(instance, options =>
                {
                    if (chain.Count > 1 || string.IsNullOrWhiteSpace(options.GameVersion)) options.GameVersion = gameVersion;
                });
            }

            await CheckAndCompleteFilesAsync(instance, task, token);
            if (convert)
            {
                Report(task, "转换为独立版本");
                await new MinecraftVersionConverter(instance.Id, instance.Root).MakeIndependentAsync(token);
                changed = true;
                Log(instance.Id + "：已由 FMCore 转换为独立版本，存档、Mod 和设置保留在原处。");
            }
            token.ThrowIfCancellationRequested();
            if (changed || instance.MetadataIssue.Length > 0)
            {
                versions = await reader.GetVersionsAsync(instance.Root, token);
                var updated = await InstanceStore.ReadVersionAsync(versions.Single(v => v.Id == instance.Id), versions, instance.Root, token);
                instance.Version = updated.Version; instance.RequiredJava = updated.RequiredJava; instance.LoaderVersion = updated.LoaderVersion;
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

    /// <summary>核心负责离线检查及联网补全；资源映射目录与最终启动的隔离策略保持一致。</summary>
    private async Task CheckAndCompleteFilesAsync(PreviewInstance instance, PreviewTask task, CancellationToken token)
    {
        Report(task, "检查完整性");
        var checker = new GameFileChecker(instance.Id, instance.Root) { GameDirectory = instance.GameDirectory };
        var check = await checker.CheckAsync(p => Report(task, $"检查完整性 {p.CheckedFiles}/{p.TotalKnownFiles}"), token);
        token.ThrowIfCancellationRequested();
        if (check.FileContentVerified) return;

        Log(instance.Id + $"：文件检查发现缺失 {check.MissingFiles} 项，内容不符 {check.InvalidFiles} 项，校验声明不足 {check.UnverifiableFiles} 项，交由核心补全并校验。");
        // MetadataMissing 不是损坏；在线补全器正常返回即完成校验，不用同一份不足的本地声明再次拦截。
        var completed = await new GameFileCompleter(instance.Id, instance.Root, DownloadConcurrency)
        { Source = FileSource, GameDirectory = instance.GameDirectory }.CompleteAsync(p =>
        {
            var stage = p.Stage is GameFileCompletionStage.CompletingClientAndLibraries or GameFileCompletionStage.CompletingResources
                ? "补全并校验文件" : "检查完整性";
            Report(task, stage + "：" + p.Message, p.Download);
        }, token);
        token.ThrowIfCancellationRequested();
        Log(instance.Id + $"：核心文件校验完成，共 {completed.TotalFiles} 个文件，下载 {completed.DownloadedFiles} 个，复用 {completed.ReusedFiles} 个，映射 {completed.MappedFiles} 个。");
    }
}
