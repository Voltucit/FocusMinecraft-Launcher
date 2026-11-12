using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Infrastructure.Runtime;
using FocusMinecraftLauncher.Presentation.Dialogs;

namespace FocusMinecraftLauncher.Preview;

public sealed partial class PreviewSession
{
    public UiCommand ShowTaskError { get; } = new(p =>
    {
        if (p is PreviewTask { Failure: { } report }) ErrorDetailsDialog.Show(report);
    }, p => p is PreviewTask { Failure: not null });

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
        await report.SaveAsync();
        Log(report.BuildText());
        NotifyError(report);
        return report;
    }

    private void NotifyError(LauncherError report)
    {
        var reason = report.Reason.Length > 180 ? report.Reason[..180] + "…" : report.Reason;
        var message = report.Action + "在“" + report.Stage + "”失败：" + reason;
        TaskNotification?.Invoke(message, true);
        // 旧的下载页订阅会转发全局消息，最后设置带详情的通知，避免被普通提示覆盖。
        GlobalNotification = new(message, true, () => ErrorDetailsDialog.Show(report));
    }

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
        task.State = "失败";
        task.Stage = "失败于：" + report.Stage;
        task.Error = report.Summary;
        task.Failure = null;
        task.Refresh(); Signal();
        await report.SaveAsync();
        task.Failure = report;
        task.Refresh(); Signal();
        Log(report.BuildText());
        if (notify) NotifyError(report);
    }
}
