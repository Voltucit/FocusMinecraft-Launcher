using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FMCore.Downloads;
using FMCore.Java;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>
/// 官方 Java 运行时安装器：逻辑与 FMCore.Java.JavaInstaller 一致，额外支持 BMCLAPI 镜像
/// （核心的安装器把清单地址写死为 Mojang，无法从启动器侧切换源）。
/// </summary>
public static class JavaRuntimeInstaller
{
    private const string OfficialRoot = "https://launchermeta.mojang.com";
    private const string MirrorRoot = "https://bmclapi2.bangbang93.com";
    private const string CatalogPath = "/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    private const int MaximumMetadataBytes = 16 * 1024 * 1024;

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
        MaxResponseContentBufferSize = MaximumMetadataBytes
    };

    /// <summary>核心只提供 Windows x64 的 8、16、17、21、25 五个官方运行时。</summary>
    public static bool CanInstall(int major) =>
        OperatingSystem.IsWindows()
        && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64
        && major is 8 or 16 or 17 or 21 or 25;

    public static string Component(int major) => major switch
    {
        8 => "jre-legacy",
        16 => "java-runtime-alpha",
        17 => "java-runtime-gamma",
        21 => "java-runtime-delta",
        25 => "java-runtime-epsilon",
        _ => throw new NotSupportedException("当前支持安装 Java 8、16、17、21 或 25。")
    };

    public static async Task<JavaInstallResult> InstallAsync(int major, string installDirectory, bool mirror, Action<JavaInstallProgress>? report, CancellationToken cancellationToken)
    {
        if (!CanInstall(major)) throw new NotSupportedException($"启动器不能自动安装 Java {major}，请手动安装后在设置中选择。");

        installDirectory = Path.GetFullPath(installDirectory);
        Directory.CreateDirectory(installDirectory);
        const string stage = "读取 Java 运行时清单";
        report?.Invoke(new JavaInstallProgress(JavaInstallStage.ReadingMetadata, stage));

        var catalog = await ReadJsonAsync(Rewrite(OfficialRoot + CatalogPath, mirror), 0, null, cancellationToken);
        if (catalog["windows-x64"]?[Component(major)] is not JsonArray runtimes
            || runtimes.Count == 0
            || runtimes[0]?["manifest"]?["url"]?.GetValue<string>() is not { Length: > 0 } manifestUrl)
            throw new NotSupportedException($"官方清单没有可用的 Java {major} Windows x64 运行时。");

        var manifest = await ReadJsonAsync(Rewrite(manifestUrl, mirror), 1, catalog["windows-x64"]![Component(major)]![0]!["manifest"]!.AsObject(), cancellationToken);
        if (manifest["files"] is not JsonObject entries) throw new InvalidDataException("Java 文件清单缺少 files 对象。");

        var requests = new List<DownloadRequest>();
        var directories = new List<string>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(installDirectory, entry.Key.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(installDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Java 文件路径越出安装目录：{entry.Key}");
            if (entry.Value is not JsonObject file) throw new InvalidDataException($"Java 文件声明无效：{entry.Key}");
            switch (file["type"]?.GetValue<string>())
            {
                case "directory":
                    directories.Add(target);
                    break;
                case "file":
                    if (file["downloads"]?["raw"] is not JsonObject raw) throw new InvalidDataException($"Java 文件缺少原始下载声明：{entry.Key}");
                    var url = raw["url"]?.GetValue<string>() ?? throw new InvalidDataException($"Java 文件缺少下载地址：{entry.Key}");
                    requests.Add(new DownloadRequest
                    {
                        Url = Rewrite(url, mirror),
                        FilePath = target,
                        Size = raw["size"]?.GetValue<long>() ?? 0,
                        Sha1 = raw["sha1"]?.GetValue<string>() ?? "",
                        ReplaceInvalidFiles = false
                    });
                    break;
                default:
                    throw new NotSupportedException($"当前不支持 Java 清单中的 {file["type"]} 项：{entry.Key}");
            }
        }

        foreach (var directory in directories) Directory.CreateDirectory(directory);

        report?.Invoke(new JavaInstallProgress(JavaInstallStage.DownloadingFiles, $"下载 Java {major} 运行时文件"));
        var results = await FileDownloader.DownloadManyAsync(requests, 4,
            progress => report?.Invoke(new JavaInstallProgress(JavaInstallStage.DownloadingFiles, $"下载 Java {major} 运行时文件", progress)),
            cancellationToken);

        report?.Invoke(new JavaInstallProgress(JavaInstallStage.VerifyingJava, "检查已安装的 Java 版本"));
        var executable = Path.Combine(installDirectory, "bin", "java.exe");
        var java = await JavaReader.GetJavaInfoAsync(executable, cancellationToken);
        if (java.MajorVersion != major || java.Architecture != "x64")
            throw new InvalidDataException($"安装结果不符合要求：Java {java.Version}，架构 {java.Architecture ?? "未知"}。");

        report?.Invoke(new JavaInstallProgress(JavaInstallStage.Completed, $"Java {java.Version} 安装完成"));
        return new JavaInstallResult(java, installDirectory, results.Count, results.Count(file => !file.AlreadyPresent), results.Count(file => file.AlreadyPresent));
    }

    /// <summary>镜像模式下把 Mojang 的分发域名换成 BMCLAPI 的同路径地址；其它地址原样返回。</summary>
    private static string Rewrite(string url, bool mirror)
    {
        if (!mirror) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return url;
        return parsed.Host.ToLowerInvariant() switch
        {
            "launchermeta.mojang.com" or "piston-meta.mojang.com" or "piston-data.mojang.com" or "launcher.mojang.com"
                => MirrorRoot + parsed.PathAndQuery,
            _ => url
        };
    }

    /// <summary>official 版本会校验声明的 SHA-1；镜像返回的清单经重新序列化，只做结构校验。</summary>
    private static async Task<JsonObject> ReadJsonAsync(string url, int depth, JsonObject? declaration, CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.LongLength > MaximumMetadataBytes) throw new InvalidDataException("Java 元数据超出允许的大小。");
        if (depth == 1 && declaration is not null && !url.Contains(MirrorRoot, StringComparison.OrdinalIgnoreCase))
        {
            var sha1 = declaration["sha1"]?.GetValue<string>();
            var size = declaration["size"]?.GetValue<long>() ?? 0;
            if (sha1 is not { Length: 40 } || size != bytes.LongLength
                || !string.Equals(Convert.ToHexString(SHA1.HashData(bytes)), sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Java 文件清单的大小或 SHA-1 校验失败。");
        }
        return JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException("Java 元数据必须是 JSON 对象。");
    }
}
