using System;
using FocusMinecraftLauncher.Infrastructure.Configuration;
using FMCore.Downloads;
using FMCore.ModsResources;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>社区资源源：默认走 MCIM 镜像，接口路径与官方一致，只替换主机名。</summary>
public static class CommunitySource
{
    public const string MirrorName = "MCIM 镜像源";
    public const string OfficialName = "仅使用官方源";
    /// <summary>镜像按「名称/版本号」识别接入方，注册用的 UA 与这里保持一致；版本号取自程序集，跟随发布走。</summary>
    public static string UserAgent { get; } = "FocusMinecraftLauncher/" + (typeof(CommunitySource).Assembly.GetName().Version is { } version
        ? version.ToString(3)
        : "2.0.7");

    private const string MirrorRoot = "https://mod.mcimirror.top";

    public static DownloadSource Mirror { get; } = new(MirrorName, Resolve);
    public static DownloadSource Official { get; } = DownloadSource.Official;

    /// <summary>当前选中的源，启动时按设置决定一次；没有设置或旧配置一律用镜像。</summary>
    public static DownloadSource Current { get; private set; } = Mirror;

    public static void Apply(LauncherConfiguration configuration)
    {
        var name = LauncherConfiguration.Text(configuration.ReadLauncher()["downloads"]?["communitySource"]);
        Current = name == OfficialName ? Official : Mirror;
    }

    public static ModrinthClient NewClient() => new(UserAgent) { Source = Current };

    /// <summary>官方地址到镜像地址的映射；未覆盖的主机返回 null，调用方保留原地址。</summary>
    private static string? Resolve(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url)) return null;
        return url.Host.ToLowerInvariant() switch
        {
            "api.modrinth.com" when MirrorsMetadata(url.AbsolutePath) => MirrorRoot + "/modrinth" + url.PathAndQuery,
            "cdn.modrinth.com" => MirrorRoot + url.PathAndQuery,
            _ => null
        };
    }

    /// <summary>只替换 MCIM 已提供的端点；作者团队等未镜像的补充接口保留官方地址。</summary>
    private static bool MirrorsMetadata(string path) => path.Trim('/').Split('/') switch
    {
        ["v2", "project", _] or ["v2", "project", _, "version"] => true,
        ["v2", "projects"] or ["v2", "search"] or ["v2", "versions"] => true,
        ["v2", "version", _] or ["v2", "version_file", _] or ["v2", "version_file", _, "update"] => true,
        ["v2", "version_files"] or ["v2", "version_files", "update"] => true,
        ["v2", "tag", "category" or "game_version" or "loader"] => true,
        _ => false
    };
}
