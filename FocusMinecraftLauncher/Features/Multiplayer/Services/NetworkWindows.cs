using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace FocusMinecraftLauncher.Features.Multiplayer.Services;

internal static class NetworkWindows
{
    /// <summary>显式按 User SID 限制管道访问，避免 .NET 10 的 CurrentUserOnly 比较 Owner SID 导致同账户提权后被拒绝。</summary>
    public static NamedPipeServerStream CreateUserPipeServer(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("无法读取当前 Windows 用户身份。");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096, security);
    }

    /// <summary>提权后台确认管道属于同一实际用户；仍然不允许换用其他 Windows 账户连接。</summary>
    public static void VerifyPipeServerUser(NamedPipeClientStream pipe)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        if (identity.User == null || !identity.User.Equals(owner))
            throw new UnauthorizedAccessException("联机后台与启动器的 Windows 账户不同，请使用同一账户进行管理员授权。");
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    public static void VerifyPipeProcess(SafePipeHandle pipe, int expected, bool server)
    {
        uint actual;
        var success = server ? GetNamedPipeServerProcessId(pipe, out actual) : GetNamedPipeClientProcessId(pipe, out actual);
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取联机通信进程身份。");
        if (actual != expected) throw new UnauthorizedAccessException("联机通信进程身份不匹配，已停止连接。");
    }
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>后台异常退出时由 Windows 一并结束所属核心和查询进程，避免残留虚拟网络。</summary>
internal sealed class NetworkProcessJob : IDisposable
{
    private readonly SafeFileHandle _job;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public UIntPtr Minimum, Maximum; public uint Active; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadCount, WriteCount, OtherCount, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob; }
    public NetworkProcessJob()
    {
        _job = CreateJobObject(IntPtr.Zero, null);
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (_job.IsInvalid || !SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        { _job.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }
    public void Add(Process process)
    {
        if (AssignProcessToJobObject(_job, process.Handle)) return;
        try { process.Kill(true); } catch (InvalidOperationException) { }
        throw new Win32Exception(Marshal.GetLastWin32Error(), "无法管理联机后台进程。");
    }
    public void Dispose() => _job.Dispose();
}

/// <summary>只放行本次联机核心及房间控制通道，退出时撤销；不关闭系统防火墙或修改游戏规则。</summary>
internal sealed class NetworkFirewall : IDisposable
{
    private readonly dynamic _policy;
    private readonly List<string> _names = [];
    public NetworkFirewall() => _policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
    public void AllowCore(string executable)
    { Add(executable, 6, "*", "*", "*"); Add(executable, 17, "*", "*", "*"); }
    public void AllowRoom(string executable, string ip, int port, string subnet) => Add(executable, 6, port.ToString(System.Globalization.CultureInfo.InvariantCulture), ip, subnet);
    private void Add(string executable, int protocol, string ports, string local, string remote)
    {
        dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
        var name = "FMLauncher-EasyTier-" + Guid.NewGuid().ToString("N");
        try
        {
            rule.Name = name; rule.Description = "FMLauncher 本次联机临时规则"; rule.ApplicationName = executable;
            rule.Protocol = protocol; rule.LocalPorts = ports; rule.LocalAddresses = local; rule.RemoteAddresses = remote;
            rule.Direction = 1; rule.Action = 1; rule.Enabled = true; rule.Profiles = int.MaxValue;
            _policy.Rules.Add(rule); _names.Add(name);
        }
        finally { Marshal.FinalReleaseComObject(rule); }
    }
    public void Dispose()
    {
        try { foreach (var name in _names) _policy.Rules.Remove(name); }
        finally { Marshal.FinalReleaseComObject(_policy); }
    }
}
