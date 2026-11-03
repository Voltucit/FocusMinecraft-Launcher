using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>外置账号启动需要的 authlib-injector：默认由启动器下载并校验，设置里也可以指定现成的文件。</summary>
public static class AuthlibInjectorService
{
    private const string MetadataAddress = "https://authlib-injector.yushi.moe/artifact/latest.json";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string ManagedDirectory => Path.Combine(AppContext.BaseDirectory, "FMLauncher", "authlib-injector");
    public static string ManagedPath => Path.Combine(ManagedDirectory, "authlib-injector.jar");

    /// <summary>返回可用的 JAR 路径：设置里给了路径就用它，否则缺文件时下载并按发布页的 SHA-256 校验。</summary>
    public static async Task<string> EnsureAsync(string configured, Action<string>? report, CancellationToken cancellationToken)
    {
        configured = configured.Trim();
        if (configured.Length > 0)
        {
            if (File.Exists(configured)) return Path.GetFullPath(configured);
            throw new FileNotFoundException("设置里指定的 authlib-injector 文件不存在。", configured);
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(ManagedPath)) return ManagedPath;
            report?.Invoke("下载 authlib-injector");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            using var document = JsonDocument.Parse(await http.GetStringAsync(MetadataAddress, cancellationToken));
            var download = document.RootElement.GetProperty("download_url").GetString()
                ?? throw new InvalidDataException("authlib-injector 发布信息不完整。");
            var expected = document.RootElement.TryGetProperty("checksums", out var checksums)
                && checksums.TryGetProperty("sha256", out var sha) ? sha.GetString() ?? "" : "";

            var bytes = await http.GetByteArrayAsync(download, cancellationToken);
            if (expected.Length == 64 && !Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("authlib-injector 下载内容校验失败，请重试。");

            Directory.CreateDirectory(ManagedDirectory);
            var temporary = ManagedPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, ManagedPath, true);
            return ManagedPath;
        }
        finally { Gate.Release(); }
    }
}
