using FMCore.Versions;
using FMLauncher.CoreIntegration.Models;
namespace FMLauncher.CoreIntegration.Adapters;

/// <summary>本地枚举和在线目录使用同一公开版本 API，保留无法解析的本地版本供维护者定位。</summary>
public sealed class VersionAdapter
{
    public async Task<IReadOnlyList<VersionItem>> InstalledAsync(string root, CancellationToken ct)
        => (await GameCoreUtil.GetGameCoresAsync(root, ct)).Select(v => new VersionItem(v.Id, v.Type ?? "本地", installed: true, errorCode: v.ErrorCode?.ToString(), minecraftVersion: v.MinecraftVersion, loader: v.Loaders.Count == 0 ? null : string.Join(" / ", v.Loaders.Select(l => l.Name + " " + l.Version)))).ToArray();
    public async Task<IReadOnlyList<VersionItem>> AvailableAsync(string root, CancellationToken ct)
        => (await GameCoreUtil.GetAvailableVersionsAsync(root, VersionCachePolicy.Refresh, ct)).Versions.OrderByDescending(v => v.ReleaseTime).Select(v => new VersionItem(v.Id, v.Type, v.ReleaseTime)).ToArray();
    public async Task<int?> RequiredJavaAsync(string root, string version, CancellationToken ct)
        => (await GameCoreUtil.GetGameCoreAsync(version, root, ct)).JavaRequirement?.MajorVersion;
}

