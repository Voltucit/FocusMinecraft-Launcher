using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FMCore.Java;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>下载官方 Java 运行时，安装到启动器自己的 Java 目录。下载源可跟随启动器的镜像设置。</summary>
public static class JavaBootstrap
{
    /// <summary>启动器与核心都只支持 Windows x64 的 8、16、17、21、25 五个官方运行时。</summary>
    public static bool CanInstall(int major) => JavaRuntimeInstaller.CanInstall(major);

    /// <summary>安装目录放在启动器 Java 扫描路径下，装完即可被自动检测发现。</summary>
    public static string Directory(int major) =>
        Path.Combine(AppContext.BaseDirectory, "FMLauncher", "Java", "java-" + major);

    public static async Task<JavaInfo> InstallAsync(int major, bool mirror, Action<JavaInstallProgress>? report, CancellationToken cancellationToken)
    {
        if (!CanInstall(major)) throw new NotSupportedException($"启动器不能自动安装 Java {major}，请手动安装后在设置中选择。");
        var result = await JavaRuntimeInstaller.InstallAsync(major, Directory(major), mirror, report, cancellationToken);
        return result.Java;
    }
}
