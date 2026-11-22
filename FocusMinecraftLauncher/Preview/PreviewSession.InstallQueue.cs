using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Features.Downloads.Models;
using FocusMinecraftLauncher.Features.Home.Models;
using FocusMinecraftLauncher.Infrastructure.Runtime;

namespace FocusMinecraftLauncher.Preview;

internal enum InstallationOrigin { Game, LocalPack, Modrinth, CurseForge }

// 仅保存重建任务所需的信息。在线资源记录平台编号，由平台重新提供下载地址。
internal sealed record PendingInstallation
{
    public InstallationOrigin Origin { get; init; }
    public string Name { get; init; } = "";
    public string Root { get; init; } = "";
    public string TargetId { get; init; } = "";
    public string Game { get; init; } = "";
    public string Loader { get; init; } = "";
    public string LoaderVersion { get; init; } = "";
    public string OptiFine { get; init; } = "";
    public string ProjectId { get; init; } = "";
    public string VersionId { get; init; } = "";
    public string FileName { get; init; } = "";
    public string ArchivePath { get; init; } = "";
    public string ArchiveSha256 { get; init; } = "";
    public VersionIsolationMode Isolation { get; init; }
    public bool ComponentsInstalled { get; init; }
}

public sealed partial class PreviewSession
{
    private const string InstallationQueueKey = "pendingInstallations";

    public UiCommand ForgetInstallation { get; private set; } = null!;

    private void RestorePendingInstallations()
    {
        ForgetInstallation = new(async item =>
        {
            if (item is not PreviewTask { CanForgetInstallation: true } task || _operations.ContainsKey(task)) return;
            task.ForgettingInstallation = true;
            task.Refresh();
            Signal();
            try
            {
                await SaveInstallationQueueAsync();
                Tasks.Remove(task);
                _retryOperations.Remove(task);
                _executions.Remove(task);
                if (ReferenceEquals(_active, task)) _active = null;
            }
            catch (IOException error)
            {
                task.ForgettingInstallation = false;
                UpdateInstallationQueue();
                NotifyTask(error.Message, true);
            }
            finally
            {
                task.ForgettingInstallation = false;
                task.Refresh();
                Signal();
            }
        }, item => item is PreviewTask { CanForgetInstallation: true } task && !_operations.ContainsKey(task));

        if (_configuration.ReadLauncher()[InstallationQueueKey] is not JsonArray records) return;
        foreach (var record in records)
        {
            try
            {
                var plan = record.Deserialize<PendingInstallation>()
                    ?? throw new InvalidDataException("安装记录为空。");
                ValidateInstallationRecord(plan);
                if (plan.TargetId.Length > 0 && IsInstallationTargetReserved(plan.Root, plan.TargetId)) continue;
                var item = new PreviewTask(InstallationKind(plan), plan.Name, plan.Game)
                {
                    Root = plan.Root, InstanceId = plan.TargetId, Installation = plan,
                    State = "待继续", Stage = "上次安装未完成"
                };
                _retryOperations[item] = RunInstallationAsync;
                Tasks.Add(item);
            }
            catch (Exception error) when (error is JsonException or IOException or ArgumentException or NotSupportedException)
            { Log("无法读取待继续的安装记录：" + RuntimeSupport.Error(error)); }
        }
    }

    private static PreviewTaskKind InstallationKind(PendingInstallation plan) =>
        plan.Origin == InstallationOrigin.Game ? PreviewTaskKind.Install : PreviewTaskKind.Import;

    private PreviewTask EnqueueInstallation(PendingInstallation plan)
    {
        ValidateInstallationRecord(plan);
        return EnqueueCore(InstallationKind(plan), plan.Name, plan.Game, plan.Root, plan.TargetId,
            RunInstallationAsync, plan);
    }

    private void UpdateInstallationQueue()
    {
        var queue = JsonSerializer.SerializeToNode(Tasks
            .Where(task => task.Installation != null && !task.ForgettingInstallation)
            .Select(task => task.Installation).ToArray());
        var incomplete = new JsonArray(_incomplete.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray());
        _configuration.ChangeLauncher(document =>
        {
            document[InstallationQueueKey] = queue;
            document["incompleteInstallations"] = incomplete;
        });
    }

    private async Task SaveInstallationQueueAsync()
    {
        UpdateInstallationQueue();
        if (!await _configuration.FlushAsync())
            throw new IOException("无法保存安装记录。请检查启动器配置目录的写入权限后重试。");
    }

    private bool IsInstallationTargetReserved(string root, string id, PreviewTask? except = null) =>
        Tasks.Any(task => task != except && (task.IsActive || task.Installation != null) && task.InstanceId.Length > 0
            && string.Equals(InstanceStore.Key(task.Root, task.InstanceId), InstanceStore.Key(root, id), StringComparison.OrdinalIgnoreCase));

    private static bool IsFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name is not ("." or "..") && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static void ValidateInstallationRecord(PendingInstallation plan)
    {
        if (!Enum.IsDefined(plan.Origin) || !Enum.IsDefined(plan.Isolation) || string.IsNullOrWhiteSpace(plan.Name)
            || string.IsNullOrWhiteSpace(plan.Root) || !Path.IsPathFullyQualified(plan.Root)
            || plan.TargetId == null || (plan.TargetId.Length > 0 && !IsFileName(plan.TargetId))
            || plan.Game == null || plan.ArchivePath == null || plan.ArchiveSha256 == null)
            throw new InvalidDataException("安装记录缺少有效的目标目录。");

        if ((plan.Origin == InstallationOrigin.Game && (!IsFileName(plan.TargetId) || !IsFileName(plan.Game)
                || plan.Loader == null || plan.LoaderVersion == null || plan.OptiFine == null))
            || (plan.Origin == InstallationOrigin.LocalPack && (!IsFileName(plan.TargetId) || !Path.IsPathFullyQualified(plan.ArchivePath)))
            || (plan.Origin is InstallationOrigin.Modrinth or InstallationOrigin.CurseForge
                && (!IsFileName(plan.FileName) || !IsFileName(plan.ProjectId) || !IsFileName(plan.VersionId))))
            throw new InvalidDataException("安装记录缺少有效的资源或版本信息。");
    }

    private void ValidateInstallationTarget(PendingInstallation plan)
    {
        var target = ModpackArchive.SafePath(plan.Root, "versions/" + plan.TargetId);
        if (!Directory.Exists(target) || _incomplete.Contains(InstanceStore.Key(plan.Root, plan.TargetId))) return;
        // 允许把自动补装的原版父版本登记为正式的纯原版游戏。
        if (plan.Origin == InstallationOrigin.Game && plan.Loader is ("" or "原版")
            && plan.TargetId.Equals(plan.Game, StringComparison.OrdinalIgnoreCase)
            && !File.Exists(Path.Combine(target, InstanceStore.FileName))) return;
        throw new IOException("目标游戏目录已被占用。请在任务记录中移除此记录，再用其他名称安装。");
    }

    private async Task RunInstallationAsync(PreviewTask task, CancellationToken token)
    {
        var plan = task.Installation ?? throw new InvalidOperationException("安装记录已移除。");
        if (plan.Isolation != SavedSettings.VersionIsolation)
        {
            var mode = plan.Isolation switch
            {
                VersionIsolationMode.All => "全部隔离",
                VersionIsolationMode.ModdedOnly => "仅 Mod 版本隔离",
                _ => "不隔离"
            };
            throw new InvalidOperationException($"版本隔离设置已改变。请将“版本隔离”切回“{mode}”后继续。");
        }
        if (plan.TargetId.Length > 0 && IsInstallationTargetReserved(plan.Root, plan.TargetId, task))
            throw new InvalidOperationException("目标游戏正在处理其他任务，请稍后继续。");
        if (plan.Origin == InstallationOrigin.Game)
            await RunGameInstallationAsync(plan, task, token);
        else
            await RunPackInstallationAsync(plan, task, token);
    }

    private void QueueOnlinePack(ResourceFileItem file, string root, string version, InstallationOrigin origin)
    {
        var plan = new PendingInstallation
        {
            Origin = origin, Root = root, Name = file.FileName, FileName = file.FileName,
            ProjectId = file.ProjectId, VersionId = version, Isolation = SavedSettings.VersionIsolation
        };
        if (Tasks.Any(item => item.Installation is { } old && old.Origin == origin && old.ProjectId == plan.ProjectId
            && old.VersionId == plan.VersionId && old.FileName == plan.FileName
            && string.Equals(old.Root, root, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("这个整合包已有任务，请在任务记录中查看。");
        EnqueueInstallation(plan);
    }

    private string OnlineArchivePath(PendingInstallation plan) => ModpackArchive.SafePath(_configuration.ConfigDirectory,
        $"Downloads/InstallQueue/{plan.Origin}/{plan.ProjectId}/{plan.VersionId}/{plan.FileName}");

    private async Task RunPackInstallationAsync(PendingInstallation plan, PreviewTask task, CancellationToken token)
    {
        var path = plan.Origin == InstallationOrigin.LocalPack ? plan.ArchivePath : OnlineArchivePath(plan);
        if (plan.Origin != InstallationOrigin.LocalPack && (plan.ArchiveSha256.Length == 0 || !File.Exists(path)))
            await DownloadQueuedPackAsync(plan, path, task, token);

        Report(task, "校验整合包文件");
        await using var archive = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(archive, token));
        if (plan.ArchiveSha256.Length > 0 && !string.Equals(hash, plan.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("整合包文件已变化。请放回原文件，或移除此记录后重新导入。");
        if (plan.ArchiveSha256.Length == 0)
        {
            plan = plan with { ArchiveSha256 = hash };
            task.Installation = plan;
            await SaveInstallationQueueAsync();
        }

        Report(task, "读取整合包清单");
        var manifest = await Task.Run(() => ModpackArchive.ReadPreview(path), token);
        if (plan.Game.Length > 0 && manifest.Minecraft != plan.Game)
            throw new InvalidDataException("整合包的游戏版本与原安装记录不同。");
        PackImportRequest request;
        if (plan.TargetId.Length == 0)
        {
            Report(task, "等待确认整合包安装");
            var confirmed = await PrepareModpackImportAsync(path, plan.Root, token, manifest);
            if (confirmed == null) throw new UserCanceledOperationException("已取消导入整合包。");
            request = confirmed;
        }
        else request = new(manifest, plan.Root, plan.TargetId);
        token.ThrowIfCancellationRequested();
        if (plan.TargetId != request.InstanceName)
        {
            plan = plan with { TargetId = request.InstanceName, Game = manifest.Minecraft, ArchiveSha256 = hash };
            task.Installation = plan;
            task.InstanceId = plan.TargetId;
            await SaveInstallationQueueAsync();
        }
        string? icon = null;
        if (plan.Origin == InstallationOrigin.Modrinth && !File.Exists(Path.Combine(plan.Root, "versions", plan.TargetId, "fmlauncher.pack-icon.png")))
        {
            Report(task, "读取整合包封面");
            try
            {
                var project = await CommunitySource.NewClient().GetProjectAsync(plan.ProjectId, token);
                icon = project.IconUrl;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { Log("读取整合包图标失败：" + RuntimeSupport.Error(error)); }
        }
        await InstallModpackAsync(path, request, task, token, plan.ProjectId, plan.VersionId, icon);
    }

    private async Task DownloadQueuedPackAsync(PendingInstallation plan, string path, PreviewTask task, CancellationToken token)
    {
        task.DownloadSourceSetting = CommunitySource.Current.Name;
        Report(task, "读取整合包下载信息");
        if (plan.Origin == InstallationOrigin.Modrinth)
        {
            var client = CommunitySource.NewClient();
            var version = await client.GetVersionAsync(plan.VersionId, token);
            var file = version.Files.SingleOrDefault(file => file.Filename == plan.FileName);
            if (version.ProjectId != plan.ProjectId || file == null)
                throw new InvalidDataException("Modrinth 未返回原任务中的整合包文件。");
            await client.DownloadAsync(file, path, progress => Report(task, "下载整合包", file: progress), token);
        }
        else
        {
            var projectId = plan.ProjectId.StartsWith("cf-", StringComparison.Ordinal) ? plan.ProjectId[3..] : plan.ProjectId;
            var versionId = plan.VersionId.StartsWith("cf:", StringComparison.Ordinal) ? plan.VersionId[3..] : plan.VersionId;
            if (!long.TryParse(projectId, out var project) || project <= 0 || !long.TryParse(versionId, out var version) || version <= 0)
                throw new InvalidDataException("CurseForge 整合包编号无效。");
            var file = await new CurseForgeClient().GetFileAsync(project, version, token);
            if (file.Id != version || file.FileName != plan.FileName)
                throw new InvalidDataException("CurseForge 未返回原任务中的整合包文件。");
            var request = CurseForgeRequest(CurseForgeItem(project, file, DownloadSection.Modpacks), path);
            await FMCore.Downloads.FileDownloader.DownloadAsync(request, progress => Report(task, "下载整合包", file: progress), token);
        }
    }
}
