using FMLauncher.Infrastructure.Configuration;
namespace FMLauncher.Features.Launch.Models;

/// <summary>只传非机密设置、版本和账号 ID，凭据由宿主向 SDK 读取。</summary>
public sealed record LaunchRequest(LauncherSettings Settings, string Version, Guid AccountId);
