using System.Diagnostics;
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
        var java = candidates.Where(j => !required.HasValue || j.MajorVersion == required).OrderByDescending(j => j.MajorVersion).FirstOrDefault();
        return java?.Executable ?? throw new InvalidOperationException(required.HasValue ? $"未找到 Java {required}，请在设置中选择对应的 Java。" : "未找到可用的 Java，请先安装 Java 并在设置中选择。");
    }
    /// <summary>自动选择逐个调用核心的公开配置检查，不复制核心的 Java 兼容性规则。</summary>
    public static async Task<(FMCore.Launch.MinecraftLauncher Launcher,string JavaPath)> SelectCheckedLauncherAsync(PreviewInstance instance,FMCore.Accounts.IMinecraftAccount account,InstanceOptions settings,PreviewSettings global,CancellationToken token,IReadOnlyList<JavaInfo>? javaCandidates=null)
    {
        var ordered=javaCandidates??await ReadLaunchJavaCandidatesAsync(instance,settings,global,token);
        string reason="";
        var memory=MemoryMb(settings.GlobalMemory?global.AutomaticMemory:settings.AutomaticMemory,settings.GlobalMemory?global.MemoryMb:settings.MemoryMb);
        foreach(var java in ordered)
        {
            token.ThrowIfCancellationRequested();
            var launcher=new FMCore.Launch.MinecraftLauncher(new()
            {
                Account=account,
                GameCoreConfig=new(){Root=instance.Root,Version=instance.Id,GameDirectory=instance.Folder,GameArguments=Arguments(settings.GlobalArguments?"":settings.GameArguments)},
                JavaConfig=new(){JavaPath=java.Executable,MaxMemoryMb=memory,JvmArguments=Arguments(settings.GlobalArguments?global.JvmArguments:settings.JvmArguments)},
                GameWindowConfig=settings.GlobalWindow?null:new(){Width=settings.Width,Height=settings.Height,Fullscreen=settings.Fullscreen}
            });
            var check=await launcher.CheckAsync(cancellationToken:token);
            if(check.ConfigurationVerified)return(launcher,java.Executable);
            reason=check.Issue!.Message;
        }
        throw new InvalidOperationException("启动配置检查未通过："+reason);
    }
    /// <summary>补全游戏文件前先检查本机 Java；兼容性最终交给核心配置检查，不复制版本兼容规则。</summary>
    public static async Task<IReadOnlyList<JavaInfo>> ReadLaunchJavaCandidatesAsync(PreviewInstance instance,InstanceOptions settings,PreviewSettings global,CancellationToken token)
    {
        var preferred=settings.GlobalJava?global.JavaPath:settings.JavaPath;
        var candidates=string.IsNullOrWhiteSpace(preferred)
            ?await JavaReader.GetJavasAsync([Path.Combine(AppContext.BaseDirectory,"FMLauncher","Java")],cancellationToken:token)
            :new[]{await JavaReader.GetJavaInfoAsync(preferred,token)};
        var result=candidates.Where(java=>!string.IsNullOrWhiteSpace(preferred)||!instance.RequiredJava.HasValue||java.MajorVersion==instance.RequiredJava)
            .OrderByDescending(java=>java.MajorVersion).ToArray();
        if(result.Length==0)throw new InvalidOperationException(instance.RequiredJava.HasValue?$"未找到 Java {instance.RequiredJava}，请在设置中选择 Java。":"未找到可用的 Java，请先安装并选择 Java。");
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
    public static (double Total, double Available) Memory()
    {
        var state = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref state)) throw new IOException("无法读取系统内存。");
        return (state.TotalPhysical / 1073741824d, state.AvailablePhysical / 1073741824d);
    }
    public static int MemoryMb(bool automatic, int configured)
    {
        if (!automatic) return configured;
        var reading = Memory();
        return (int)Math.Clamp(Math.Floor(Math.Min(reading.Total * .4, reading.Available * .65) * 2) * 512, 1024, 12288);
    }
    public static void ReleaseLauncherMemory() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended; }
}
