using System.IO;
using FMCore.Authentication;
using FMCore.Launch;
using FMCore.Versions;
using FMLauncher.CoreIntegration.Mapping;
using FMLauncher.Features.Launch.Models;
namespace FMLauncher.CoreIntegration.Adapters;

/// <summary>按版本目录构造启动对象。SDK 独立实例 API 目录固定，与用户要求冲突，因此不调用。</summary>
internal static class LaunchAdapter
{
    /// <summary>取得指定账号身份并返回启动对象，由后台宿主持有至游戏退出。</summary>
    public static async Task<MinecraftLauncher> CreateAsync(LaunchRequest request, AccountManager accounts, CancellationToken ct)
    {
        var s = request.Settings;
        var identity = await accounts.GetLaunchIdentityAsync(request.AccountId, cancellationToken: ct);
        // 显式指定工作目录，不能只设置 Isolate 后依赖 SDK 默认的 instances 路径。
        var config = new LaunchConfig
        {
            GameCore = new()
            {
                RootDirectory = s.GameRoot,
                VersionId = request.Version,
                Isolate = true,
                GameDirectory = Path.Combine("versions", request.Version),
                AutoInstall = false,
                CachePolicy = VersionCachePolicy.CacheOnly
            },
            Java = LaunchSettingsMapping.Java(s),
            Window = LaunchSettingsMapping.Window(s),
            Server = LaunchSettingsMapping.Server(s),
            Account = identity,
            GameArguments = LaunchSettingsMapping.Arguments(s.GameArguments),
            LauncherName = "FMLauncher",
            LauncherVersion = "2.0.0"
        };
        return new(config);
    }
}
