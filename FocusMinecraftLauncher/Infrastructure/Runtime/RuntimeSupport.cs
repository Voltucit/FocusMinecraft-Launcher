using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using FMCore.Java;
using FocusMinecraftLauncher.Preview;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

public static class RuntimeSupport
{
    /// <summary>按 Windows 命令行的引号和反斜杠规则拆分用户参数；不会启动 shell。</summary>
    public static string[] Arguments(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var pointer = CommandLineToArgvW("launcher " + text, out var count);
        if (pointer == IntPtr.Zero) throw new ArgumentException("参数格式无效。");
        try { return Enumerable.Range(1, count - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(pointer); }
    }
    public static async Task<string> SelectJavaAsync(string preferred, int? required, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(preferred)) return (await JavaReader.GetJavaInfoAsync(preferred, token)).Executable;
        var candidates = await JavaReader.GetJavasAsync([Path.Combine(AppContext.BaseDirectory, "FMLauncher", "Java")], cancellationToken: token);
        // 缺少目标版本时优先落到更高的版本上：精确版本排在前面，其余按版本从高到低尝试。
        var java = candidates.Where(j => !required.HasValue || j.MajorVersion >= required)
            .OrderBy(j => j.MajorVersion == required ? 0 : 1).ThenByDescending(j => j.MajorVersion).FirstOrDefault();
        return java?.Executable ?? throw new InvalidOperationException(required.HasValue ? $"未找到 Java {required} 或更高版本，请在设置中选择可用的 Java。" : "未找到可用的 Java，请先安装 Java 并在设置中选择。");
    }
    /// <summary>自动选择逐个调用核心的公开配置检查，不复制核心的 Java 兼容性规则。</summary>
    public static async Task<(FMCore.Launch.MinecraftLauncher Launcher,string JavaPath)> SelectCheckedLauncherAsync(PreviewInstance instance,FMCore.Accounts.IMinecraftAccount account,InstanceOptions settings,PreviewSettings global,CancellationToken token,IReadOnlyList<JavaInfo>? javaCandidates=null)
    {
        var ordered=javaCandidates??await ReadLaunchJavaCandidatesAsync(instance,settings,global,token);
        string reason="";
        var memory=MemoryMb(settings.GlobalMemory?global.AutomaticMemory:settings.AutomaticMemory,settings.GlobalMemory?global.MemoryMb:settings.MemoryMb,ModCount(instance));
        var jvmArguments = LaunchJvmArguments(instance, account, settings.GlobalArguments ? global.JvmArguments : settings.JvmArguments);
        foreach(var java in ordered)
        {
            token.ThrowIfCancellationRequested();
            var launcher=new FMCore.Launch.MinecraftLauncher(new()
            {
                Account=account,
                GameCoreConfig=new(){Root=instance.Root,Version=instance.Id,GameDirectory=instance.GameDirectory,GameArguments=Arguments(settings.GlobalArguments?"":settings.GameArguments)},
                JavaConfig=new(){JavaPath=java.Executable,MaxMemoryMb=memory,JvmArguments=jvmArguments},
                GameWindowConfig=settings.GlobalWindow?null:new(){Width=settings.Width,Height=settings.Height,Fullscreen=settings.Fullscreen}
            });
            var check=await launcher.CheckAsync(cancellationToken:token);
            if(check.ConfigurationVerified)return(launcher,java.Executable);
            reason=check.Issue!.Message;
        }
        throw new InvalidOperationException("启动配置检查未通过："+reason);
    }
    /// <summary>仅为 1.16.4、1.16.5 的离线身份启用旧 Authlib 的离线回退，保留正版和自定义认证配置。</summary>
    private static string[] LaunchJvmArguments(PreviewInstance instance, FMCore.Accounts.IMinecraftAccount account, string configuredArguments)
    {
        var arguments = Arguments(configuredArguments);
        if (account is not FMCore.Accounts.OfflineAccount || instance.Version is not ("1.16.4" or "1.16.5")) return arguments;
        if (arguments.Any(argument => argument.StartsWith("-Dminecraft.api.", StringComparison.OrdinalIgnoreCase)
            || argument.StartsWith("-javaagent:", StringComparison.OrdinalIgnoreCase)
                && argument.Contains("authlib-injector", StringComparison.OrdinalIgnoreCase))) return arguments;

        // 旧 Authlib 将未认证的权限响应误读为禁止多人游戏。不可解析的保留域名让它进入内置离线回退；
        // 三个基础 host 必须同时设置，services host 单独设置会被忽略。只传给本次游戏进程，不写入配置。
        // 参考：https://github.com/FabricMC/fabric-loom/issues/915
        return [.. arguments,
            "-Dminecraft.api.auth.host=https://offline.invalid",
            "-Dminecraft.api.account.host=https://offline.invalid",
            "-Dminecraft.api.session.host=https://offline.invalid",
            "-Dminecraft.api.services.host=https://offline.invalid"];
    }
    /// <summary>补全游戏文件前先检查本机 Java；兼容性最终交给核心配置检查，不复制版本兼容规则。</summary>
    public static async Task<IReadOnlyList<JavaInfo>> ReadLaunchJavaCandidatesAsync(PreviewInstance instance,InstanceOptions settings,PreviewSettings global,CancellationToken token)
    {
        var preferred=settings.GlobalJava?global.JavaPath:settings.JavaPath;
        var candidates=string.IsNullOrWhiteSpace(preferred)
            ?await JavaReader.GetJavasAsync([Path.Combine(AppContext.BaseDirectory,"FMLauncher","Java")],cancellationToken:token)
            :new[]{await JavaReader.GetJavaInfoAsync(preferred,token)};
        // 手动指定时尊重用户选择；自动检测缺少目标版本时回退到更高版本，再把兼容性交给核心配置检查。
        var target = instance.RequiredJava;
        var result=candidates.Where(java=>!string.IsNullOrWhiteSpace(preferred)||!target.HasValue||java.MajorVersion>=target.Value)
            .OrderBy(java=>string.IsNullOrWhiteSpace(preferred)&&target.HasValue&&java.MajorVersion!=target.Value?1:0)
            .ThenByDescending(java=>java.MajorVersion).ToArray();
        // 结果可能为空：由调用方决定是提示下载 Java，还是直接报告缺少运行时。
        return result;
    }
    public static void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("文件夹尚不存在。");
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }
    /// <summary>日志过滤认证参数，不记录访问令牌、登录密码或启动器的完整命令行。</summary>
    public static string SafeText(string text)
    {
        text = Regex.Replace(text, @"(?i)(access[_-]?token|refresh[_-]?token|authorization|password|client_secret)([\s""'=:\t]+)([^\s,""}]+)", "$1$2[已隐藏]");
        return Regex.Replace(text, @"(?i)Bearer\s+[^\s""']+", "Bearer [已隐藏]");
    }
    public static string Error(Exception error)
    {
        var text=error switch
        {
            OperationCanceledException=>"操作已取消。",
            UnauthorizedAccessException=>"无法写入所选位置，请检查文件权限。",
            HttpRequestException {StatusCode:System.Net.HttpStatusCode.TooManyRequests}=>"请求过于频繁，请稍后重试。",
            HttpRequestException=>"连接服务失败，请检查网络后重试。",
            DirectoryNotFoundException=>"所选文件夹不存在，请重新选择。",
            FileNotFoundException=>"找不到所需文件，请检查文件是否已被移动，或重新安装。",
            TimeoutException=>"操作超时，请稍后重试。",
            _=>SafeText(error.GetBaseException().Message)
        };
        return Regex.Replace(text,@"(?i)\b[A-Z]:[\\/][^\r\n""<>|；，。]*","相关文件");
    }
    /// <summary>把字节数写成 1.2 GB / 340 MB 这类短文本，用于进度与列表。</summary>
    public static string FormatSize(long bytes)
    {
        const double kilo = 1024, mega = kilo * 1024, giga = mega * 1024;
        return bytes switch
        {
            >= (long)giga => (bytes / giga).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
            >= (long)mega => (bytes / mega).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
            >= (long)kilo => (bytes / kilo).ToString("0", CultureInfo.InvariantCulture) + " KB",
            _ => bytes + " B"
        };
    }
    public static (double Total, double Available) Memory()
    {
        var state = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref state)) throw new IOException("无法读取系统内存。");
        return (state.TotalPhysical / 1073741824d, state.AvailablePhysical / 1073741824d);
    }
    /// <summary>统计实例中启用的 Mod 数量；目录不可读时按 0 计，不影响启动流程。</summary>
    public static int ModCount(PreviewInstance instance)
    {
        try
        {
            var directory = Path.Combine(instance.GameDirectory, "mods");
            if (!Directory.Exists(directory)) return 0;
            return Directory.EnumerateFiles(directory, "*.jar", SearchOption.TopDirectoryOnly)
                .Count(file => !file.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)
                    && !File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint));
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }
    /// <summary>自动分配：先按 Mod 数量估算需求，再取本机总内存与当前可用内存允许的最大值。</summary>
    public static int AutomaticMemoryMb(double totalGb, double availableGb, int modCount)
    {
        // 原版基线 2 GB，每个启用的 Mod 追加 96 MB，需求上限 16 GB。
        var need = Math.Clamp(2048d + Math.Max(0, modCount) * 96d, 2048d, 16384d);
        var total = totalGb * 1024d;
        var available = availableGb * 1024d;
        // 总内存预留系统 15%（不低于 2 GB）；可用内存只取 85%，其余留给 JVM 自身与瞬时波动。
        var cap = Math.Min(total - Math.Max(2048d, total * .15), available * .85);
        var target = Math.Min(need, cap);
        return (int)Math.Clamp(Math.Floor(target / 256d) * 256d, 512d, 16384d);
    }
    public static int MemoryMb(bool automatic, int configured, int modCount = 0)
    {
        if (!automatic) return configured;
        var reading = Memory();
        return AutomaticMemoryMb(reading.Total, reading.Available, modCount);
    }
    /// <summary>当前进程是否以管理员身份运行：此时 Windows 会拦截来自资源管理器的拖放。</summary>
    public static bool IsElevated { get; } = ReadElevation();
    private static bool ReadElevation()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception) { return false; }
    }
    public static void ReleaseLauncherMemory() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended; }
}
