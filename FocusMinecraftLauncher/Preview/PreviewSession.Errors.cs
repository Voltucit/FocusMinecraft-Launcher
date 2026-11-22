using System.IO;
using System.Windows;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Features.Settings.Views;
using FocusMinecraftLauncher.Infrastructure.Runtime;
using FocusMinecraftLauncher.Presentation.Dialogs;

namespace FocusMinecraftLauncher.Preview;

public sealed partial class PreviewSession
{
    public UiCommand ShowTaskError { get; }

    private string SanitizeError(string text)
    {
        // 同时清除当前会话中不带参数名的裸令牌，再做字段和 URL 参数脱敏。
        foreach (var account in Accounts)
            if (account.RuntimeAccount?.AccessToken is { Length: > 1 } token)
                text = text.Replace(token, "[已隐藏令牌]", StringComparison.Ordinal);
        return RuntimeSupport.SafeText(text);
    }

    public async Task<LauncherError> ReportErrorAsync(Exception error, string action, string stage, string context = "")
    {
        var report = new LauncherError(error, action, stage, SanitizeError, context);
        report.DiagnosticContext = CaptureDiagnosticContext(null);
        await report.SaveAsync();
        Log(report.BuildText());
        NotifyError(report);
        return report;
    }

    private void NotifyError(LauncherError report, PreviewTask? task = null)
    {
        var reason = report.Reason.Length > 180 ? report.Reason[..180] + "…" : report.Reason;
        var message = report.Action + "在“" + report.Stage + "”失败：" + reason;
        TaskNotification?.Invoke(message, true);
        // 旧的下载页订阅会转发全局消息，最后设置带详情的通知，避免被普通提示覆盖。
        GlobalNotification = new(message, true, () => ShowError(report, task));
    }

    private void ShowError(LauncherError report, PreviewTask? task)
    {
        List<ErrorRecoveryAction> actions = [];
        if (report.Recovery == RecoveryHint.DiskSpace)
            actions.Add(new("打开磁盘占用与缓存清理", () => DiskUsageDialog.Show(this, task?.Root)));
        if (task != null && ReferenceEquals(task.Failure, report))
        {
            bool CanRepeat() => ReferenceEquals(task.Failure, report) && CanRetry(task);
            if (report.Java is { } java)
            {
                actions.Add(new("选择或下载 Java 并重试",
                    () => Retry(task, new(java.Instance, java.RequiredMajor, report.Reason)), CanRepeat));
            }
            if (task.Kind is PreviewTaskKind.Launch && task.RecoveryInstance is { } instance
                && (report.Recovery == RecoveryHint.MissingGameEntry ||
                    (report.Recovery is RecoveryHint.LocalFile or RecoveryHint.Integrity && IsGameFileStage(report.Stage)))
                && File.Exists(Path.Combine(instance.Root, "versions", instance.Id, instance.Id + ".json")))
            {
                actions.Add(new("检查并补全此游戏的文件", () => StartFileRepair(instance),
                    () => ReferenceEquals(task.Failure, report) && !IsBusy && !_cleaningStorage));
            }
            if (report.Recovery is RecoveryHint.Network or RecoveryHint.Integrity && IsDownloadTask(task, report.Stage))
            {
                if (report.Java == null) actions.Add(new("重试任务", () => Retry(task), CanRepeat));
                if (Application.Current.MainWindow is MainWindow window)
                    actions.Add(new("打开下载源设置", window.OpenDownloadSettings));
            }
            else if (report.Recovery == RecoveryHint.DiskSpace && report.Java == null
                     && task.Kind is not PreviewTaskKind.Launch)
            {
                actions.Add(new("重试任务", () => Retry(task), CanRepeat));
            }
        }
        ErrorDetailsDialog.Show(report, actions);
    }

    private static bool IsGameFileStage(string stage) =>
        stage.StartsWith("检查游戏文件", StringComparison.Ordinal)
        || stage.StartsWith("检查完整性", StringComparison.Ordinal)
        || stage.StartsWith("补全并校验文件", StringComparison.Ordinal)
        || stage.StartsWith("恢复游戏配置", StringComparison.Ordinal);

    private static bool IsDownloadTask(PreviewTask task, string stage) =>
        task.Kind is PreviewTaskKind.Resource or PreviewTaskKind.Install or PreviewTaskKind.Repair
        || task.Kind == PreviewTaskKind.Import &&
            (task.Installation?.Origin != InstallationOrigin.LocalPack || !stage.Contains("整合包归档", StringComparison.Ordinal))
        || task.Kind == PreviewTaskKind.Launch && (IsGameFileStage(stage) || stage.StartsWith("下载 Java", StringComparison.Ordinal));

    private async Task SetTaskFailureAsync(PreviewTask task, Exception error, string? stage = null, bool notify = true)
    {
        stage ??= _taskStages.TryGetValue(task, out var tracked) ? tracked : task.Stage;
        var operation = task.Kind switch
        {
            PreviewTaskKind.Launch => "启动游戏", PreviewTaskKind.Install => "安装游戏",
            PreviewTaskKind.Import => "导入整合包", PreviewTaskKind.Export => "导出整合包",
            PreviewTaskKind.Repair => "检查并补全文件", _ => "下载资源"
        };
        var report = new LauncherError(error, operation + "：" + task.Name, stage, SanitizeError,
            $"游戏版本：{task.Version}\n游戏根目录：{task.Root}\n版本标识：{task.InstanceId}");
        report.DiagnosticContext = CaptureDiagnosticContext(task);
        task.State = "失败";
        task.Stage = "失败于：" + report.Stage;
        task.Error = report.Summary;
        task.Failure = null;
        task.Refresh(); Signal();
        await report.SaveAsync();
        task.Failure = report;
        task.Refresh(); Signal();
        Log(report.BuildText());
        if (notify) NotifyError(report, task);
    }

    private DiagnosticContext CaptureDiagnosticContext(PreviewTask? task)
    {
        var log = string.Join(Environment.NewLine, Logs.Take(120).Reverse().Concat(_logBuffer.ToArray()).TakeLast(120));
        var instance = task?.RecoveryInstance;
        if (instance == null && task is { Root.Length: > 0, InstanceId.Length: > 0 })
            instance = Instances.FirstOrDefault(item => item.Id == task.InstanceId
                && string.Equals(item.Root, task.Root, StringComparison.OrdinalIgnoreCase));
        return new(SanitizeError(log), task?.DiagnosticJava, instance?.RequiredJava, task?.Root, instance?.GameDirectory,
            Accounts.SelectMany(account => new[] { account.Name, account.Uuid.ToString("D"), account.Uuid.ToString("N") })
                .Where(value => value.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
