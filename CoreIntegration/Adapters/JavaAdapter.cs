using FMCore.Java;
using FMLauncher.CoreIntegration.Models;
namespace FMLauncher.CoreIntegration.Adapters;

/// <summary>检测、兼容性与安装复用 SDK；只有用户明确点击安装时才调用下载入口。</summary>
public sealed class JavaAdapter
{
    private IReadOnlyList<JavaRuntimeVersion> _available = [];
    /// <summary>异步探测已有 Java，不下载运行环境，避免外部进程探测阻塞 UI。</summary>
    public async Task<IReadOnlyList<JavaItem>> DetectAsync(CancellationToken ct)
        => (await JavaUtil.GetJavasAsync(cancellationToken: ct)).Select(Map).ToArray();
    public async Task<JavaItem> InspectAsync(string path, CancellationToken ct) => Map(await JavaUtil.GetJavaInfoAsync(path, cancellationToken: ct));
    private static JavaItem Map(JavaRuntime java) => new(java.ExecutablePath, java.Version.Major, java.Version.Text, java.Architecture.ToString());
    /// <summary>查询官方精确包列表；此操作不会安装 Java。</summary>
    public async Task<IReadOnlyList<RuntimeItem>> AvailableAsync(int major, string distribution, CancellationToken ct)
    {
        _available = await JavaRuntimeUtil.GetAvailableVersionsAsync(major, new() { Distribution = distribution == "Zulu" ? JavaDistribution.Zulu : JavaDistribution.Temurin, Architecture = JavaArchitecture.X64 }, ct);
        return _available.Select(v => new RuntimeItem(v.Id, v.FullVersion, v.Distribution.ToString(), v.Size)).ToArray();
    }
    /// <summary>仅安装本轮查询返回的精确包，并把取消和进度传给 SDK。</summary>
    public async Task<JavaItem> InstallAsync(string id, IProgress<WorkProgress> progress, CancellationToken ct)
    {
        var version = _available.FirstOrDefault(v => v.Id == id) ?? throw new InvalidOperationException("请先查询并选择准确的 Java 包。");
        return Map(await JavaRuntimeUtil.InstallAsync(version, progress: new InlineProgress<JavaRuntimeProgress>(p => progress.Report(FMLauncher.CoreIntegration.Mapping.ProgressMapping.Map(p.Stage.ToString(), p.CompletedFiles, p.TotalFiles, p.Download))), cancellationToken: ct));
    }
}

