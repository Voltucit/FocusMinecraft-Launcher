using FMCore.Downloader;
using FMLauncher.Infrastructure.Logging;
using FMLauncher.CoreIntegration.Models;
namespace FMLauncher.CoreIntegration.Mapping;

internal static class ProgressMapping
{
    public static WorkProgress Map(string stage, int completed = 0, int total = 0, DownloadProgress? download = null)
        => new(PlayerText.Stage(stage), completed, total, download?.BytesReceived ?? 0, download?.RelativePath ?? "", download?.Attempt ?? 0, download?.TotalBytes);
}

