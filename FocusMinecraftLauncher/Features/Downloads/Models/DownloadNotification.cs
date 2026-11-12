namespace FocusMinecraftLauncher.Features.Downloads.Models;

/// <summary>一次独立的前端提示；重复文本也能重新触发显示，不表示真实下载完成。</summary>
public sealed class DownloadNotification(string message, bool isError, Action? showDetails = null)
{
    public string Message { get; } = message;
    public bool IsError { get; } = isError;
    public bool HasDetails => showDetails != null;
    public Common.UiCommand ShowDetails { get; } = new(_ => showDetails?.Invoke(), _ => showDetails != null);
}
