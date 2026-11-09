using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FocusMinecraftLauncher.Features.Multiplayer.Models;

namespace FocusMinecraftLauncher.Features.Multiplayer.Services;

/// <summary>同一启动器的无界面提权工作模式，仅管理已验证的 EasyTier；不初始化账号、壁纸或游戏服务。</summary>
public static class EasyTierWorker
{
    public const string Argument = "--fmlauncher-network-worker";
    public const int PipeProtocol = 1;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 4 || args[0] != Argument || !args[1].StartsWith("FMLauncher.Network.", StringComparison.Ordinal)
            || args[1].Length > 100 || !int.TryParse(args[2], out var parentId) || !long.TryParse(args[3], out var parentTicks)) return 2;
        using var stop = new CancellationTokenSource();
        // 访问限制由服务端 User SID ACL 与双方进程校验负责，不使用比较 Owner SID 的 CurrentUserOnly。
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        NetworkWire? wire = null;
        Process? parent = null;
        Task? monitor = null;
        var stage = "连接启动器";
        var helloSent = false;
        try
        {
            await pipe.ConnectAsync(30000, stop.Token).ConfigureAwait(false);
            wire = new NetworkWire(pipe);
            stage = "验证通信身份";
            NetworkWindows.VerifyPipeProcess(pipe.SafePipeHandle, parentId, true);
            NetworkWindows.VerifyPipeServerUser(pipe);
            stage = "验证 Windows 权限";
            if (!NetworkWindows.IsAdministrator()) throw new UnauthorizedAccessException("尚未获得管理员权限，请允许本次 Windows 授权。");
            stage = "验证启动器进程";
            parent = Process.GetProcessById(parentId);
            if (parent.StartTime.ToUniversalTime().Ticks != parentTicks || !string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("启动器进程身份已变化，请关闭后重新打开启动器。");
            await wire.WriteAsync(new WorkerHello(PipeProtocol, ""), stop.Token).ConfigureAwait(false);
            helloSent = true;
            stage = "读取联机配置";
            using var initial = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var request = await wire.ReadAsync<WorkerRequest>(initial.Token).ConfigureAwait(false);
            if (request is not { Kind: "start", Room: not null } || request.PlayerName.Length is < 1 or > 48)
                throw new InvalidDataException("联机启动请求不完整，请重新创建或加入房间。");
            RoomInvitation.ValidateNode(request.SharedNode);
            if (request.Hosting)
            {
                if (request.Room.Format != 2) throw new InvalidDataException("房间人数配置不完整，请重新创建房间。");
                RoomInvitation.ValidatePlayerLimit(request.Room.MaxPlayers);
            }
            if (!request.Hosting) _ = RoomInvitation.Parse(request.Room.Encode());
            stage = "启动联机核心";
            await using var lease = new RoomLease { LocalAvatarPixels = request.AvatarPixels };
            monitor = MonitorParentAsync(parent, wire, stop, lease);
            await RunNetworkAsync(request, wire, lease, stop.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (RoomEndedException ended)
        {
            // 房主主动解散属于正常结束；仅连接中断、身份验证失败等情况填写错误字段。
            if (!ended.IsDissolved) Trace.TraceWarning("联机已结束：{0}", ended.Message);
            if (wire != null)
            {
                using var report = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await wire.WriteAsync(new NetworkSnapshot(ended.IsDissolved ? ended.Message : "联机已结束",
                        ended.IsDissolved ? "" : ended.Message, false, true, "", [], RoomDissolved: ended.IsDissolved), report.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
            }
            return ended.IsDissolved ? 0 : 3;
        }
        catch (Exception error)
        {
            var detail = error switch
            {
                UnauthorizedAccessException => error.Message,
                System.ComponentModel.Win32Exception win32 => win32.Message + $"（Windows 错误 {win32.NativeErrorCode}）",
                JsonException => "联机消息格式错误，请确认启动器文件完整。",
                _ => Describe(error)
            };
            var message = stage + "失败：" + detail;
            // 仅记录阶段、异常及错误码，不记录邀请、配置正文或网络密钥。
            Trace.TraceError("联机后台 {0}：{1} / 0x{2:X8}：{3}", stage, error.GetType().Name, error.HResult, detail);
            if (wire != null)
            {
                using var report = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    if (helloSent) await wire.WriteAsync(new NetworkSnapshot("联机已结束", message, false, true, "", []), report.Token).ConfigureAwait(false);
                    else await wire.WriteAsync(new WorkerHello(PipeProtocol, message), report.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
            }
            return 3;
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try { wire?.Dispose(); }
            finally { pipe.Dispose(); }
            if (monitor != null)
            {
                try { await monitor.ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or OperationCanceledException or InvalidOperationException) { }
            }
            parent?.Dispose();
        }
    }

    private static async Task MonitorParentAsync(Process parent, NetworkWire wire, CancellationTokenSource stop, RoomLease lease)
    {
        async Task ReadCommands()
        {
            try
            {
                while (await wire.ReadAsync<WorkerRequest>(stop.Token).ConfigureAwait(false) is { } command)
                {
                    if (command.Kind == "stop") break;
                    if (command.Kind == "avatar") lease.LocalAvatarPixels = command.AvatarPixels;
                }
            }
            finally { await stop.CancelAsync().ConfigureAwait(false); }
        }
        var read = ReadCommands(); var exit = parent.WaitForExitAsync(stop.Token);
        await Task.WhenAny(read, exit).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        try { await read.ConfigureAwait(false); } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        try { await exit.ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    private static async Task RunNetworkAsync(WorkerRequest request, NetworkWire wire, RoomLease lease, CancellationToken token)
    {
        string? work = null; Process? core = null;
        using var job = new NetworkProcessJob();
        using var firewall = new NetworkFirewall();
        using var guestStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? guestWatch = null;
        try
        {
            var room = request.Room!;
            CheckSubnet(room.Subnet);
            work = await EasyTierComponents.PrepareWorkerAsync(token).ConfigureAwait(false);
            var corePath = Path.Combine(work, "easytier-core.exe");
            var cliPath = Path.Combine(work, "easytier-cli.exe");
            firewall.AllowCore(corePath);
            var rpc = FreeLocalPort();
            var config = Path.Combine(work, "network.toml");
            await File.WriteAllTextAsync(config, BuildConfig(room, request), new UTF8Encoding(false), token).ConfigureAwait(false);
            var start = new ProcessStartInfo(corePath) { WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "--disable-env-parsing", "--config-file", config, "--rpc-portal", "127.0.0.1:" + rpc, "--rpc-portal-whitelist", "127.0.0.1/32" }) start.ArgumentList.Add(argument);
            core = Process.Start(start) ?? throw new IOException("无法创建联机核心进程。");
            job.Add(core);
            var output = DrainAsync(core.StandardOutput); var errors = DrainAsync(core.StandardError);
            var since = DateTime.UtcNow; var lastQuery = since;
            bool hostReady = false, guestConnected = false, wasReady = false;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (core.HasExited) throw new IOException($"联机核心已退出（退出码 {core.ExitCode}）。请检查虚拟网卡与 Windows 权限。");
                if (guestWatch is { IsCompleted: true }) await guestWatch.ConfigureAwait(false);
                NetworkMember[] members;
                try
                {
                    members = await ReadPeersAsync(cliPath, work, rpc, room, job, token).ConfigureAwait(false);
                    lastQuery = DateTime.UtcNow;
                }
                catch (Exception e) when (e is IOException or JsonException or SocketException or OperationCanceledException)
                {
                    token.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow - lastQuery > TimeSpan.FromSeconds(30)) throw new IOException("无法读取联机核心状态，请退出后重试。", e);
                    await wire.WriteAsync(new NetworkSnapshot("正在连接", "", false, false, "", [], MaxPlayers: room.Format == 2 ? room.MaxPlayers : 0), token).ConfigureAwait(false);
                    await Task.Delay(1500, token).ConfigureAwait(false); continue;
                }
                var local = members.FirstOrDefault(x => x.IsLocal);
                if (local != null && request.Hosting && !hostReady && await ConnectorReadyAsync(cliPath, work, rpc, job, token).ConfigureAwait(false))
                {
                    if (local.IpAddress != room.HostIp) throw new IOException("房主虚拟 IP 与房间配置不一致。");
                    room = lease.StartHost(room, local.Id);
                    firewall.AllowRoom(Environment.ProcessPath!, room.HostIp, room.ControlPort, room.Subnet);
                    hostReady = true;
                    members = members.Select(x => x with { IsHost = x.Id == room.HostId }).ToArray();
                }
                members = lease.UpdateMembers(members, room);
                if (local != null && !request.Hosting && guestWatch == null)
                    guestWatch = lease.WatchHostAsync(room, connected => Volatile.Write(ref guestConnected, connected), guestStop.Token);
                var ready = request.Hosting ? hostReady : Volatile.Read(ref guestConnected);
                TimeSpan connectTimeout = request.Hosting && room.NetworkName.Length == 0
                    ? TimeSpan.FromSeconds(15)
                    : TimeSpan.FromSeconds(30);
                if (!ready && !wasReady && DateTime.UtcNow - since > connectTimeout)
                    throw new IOException(request.Hosting && room.NetworkName.Length == 0
                        ? "连接超时。如果这是私有节点，请先在“设置 → 其他”填写网络名称和密码；公益节点请确认节点可用。"
                        : "连接超时。请确认共享节点可用、房主仍在线，并检查私有节点的网络名称和密码。");
                var status = !ready ? request.Hosting ? "正在连接共享节点" : wasReady ? "正在重新连接房主" : "正在连接房主" : members.Count(x => !x.IsLocal) == 0 ? "等待玩家加入" : "已连接";
                if (!ready && request.Hosting && room.NetworkName.Length == 0 && DateTime.UtcNow - since > TimeSpan.FromSeconds(5))
                    status = "正在连接共享节点（私有节点需填写网络名称和密码）";
                wasReady |= ready;
                await wire.WriteAsync(new NetworkSnapshot(status, "", ready, false, hostReady ? room.Encode() : "", members,
                    MaxPlayers: room.Format == 2 ? room.MaxPlayers : 0), token).ConfigureAwait(false);
                await Task.Delay(2000, token).ConfigureAwait(false);
            }
        }
        finally
        {
            await lease.AnnounceCloseAsync().ConfigureAwait(false);
            await guestStop.CancelAsync().ConfigureAwait(false);
            if (guestWatch != null) try { await guestWatch.ConfigureAwait(false); } catch (Exception e) when (e is IOException or OperationCanceledException) { }
            if (core != null)
            {
                if (!core.HasExited) { core.Kill(true); await core.WaitForExitAsync().ConfigureAwait(false); }
                core.Dispose();
            }
            job.Dispose();
            if (work != null) EasyTierComponents.DeleteWorkerDirectory(work);
        }
    }

    private static string BuildConfig(RoomInvitation room, WorkerRequest request)
    {
        static string Quote(string value) => JsonSerializer.Serialize(value);
        var ip = request.Hosting ? room.HostIp + "/24" : room.Subnet;
        return $"""
            instance_id = "{Guid.NewGuid()}"
            instance_name = "FMLauncher"
            hostname = {Quote(request.PlayerName)}
            ipv4 = {Quote(ip)}
            dhcp = {(!request.Hosting).ToString().ToLowerInvariant()}
            listeners = ["tcp://0.0.0.0:0", "udp://0.0.0.0:0"]
            exit_nodes = []
            [network_identity]
            network_name = {Quote(room.NetworkName.Length > 0 ? room.NetworkName : $"FMLauncher-{room.RoomId}")}
            network_secret = {Quote(room.Secret)}
            [[peer]]
            uri = {Quote(request.SharedNode)}
            [flags]
            enable_encryption = true
            enable_exit_node = false
            accept_dns = false
            relay_network_whitelist = ""
            latency_first = true
            """;
    }

    private static async Task<NetworkMember[]> ReadPeersAsync(string cli, string work, int rpc, RoomInvitation room, NetworkProcessJob job, CancellationToken token)
    {
        using var document = await QueryAsync(cli, work, rpc, "peer", job, token).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("联机状态格式异常。");
        var members = new List<NetworkMember>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            static string Text(JsonElement element, string key) => element.TryGetProperty(key, out var value) ? value.ToString() : "";
            var ip = Text(item, "ipv4");
            if (!RoomInvitation.IsRoomIp(ip, room.Subnet)) continue;
            var id = Text(item, "id"); var name = Text(item, "hostname");
            name = new string(name.Where(x => !char.IsControl(x)).Take(48).ToArray());
            var cost = Text(item, "cost"); var local = cost == "Local";
            var latency = Text(item, "lat_ms");
            var measured = !local && double.TryParse(latency, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds) && milliseconds > 0;
            members.Add(new(id, string.IsNullOrWhiteSpace(name) ? "未命名成员" : name, ip, local, id == room.HostId,
                local ? "本机" : cost == "p2p" ? "P2P 直连" : "中继连接", measured ? latency + " ms" : "未测量"));
        }
        return members.ToArray();
    }

    private static async Task<bool> ConnectorReadyAsync(string cli, string work, int rpc, NetworkProcessJob job, CancellationToken token)
    {
        using var document = await QueryAsync(cli, work, rpc, "connector", job, token).ConfigureAwait(false);
        // 官方 ConnectorStatus：CONNECTED = 0，JSON 由 CLI 原样输出 Vec<Connector>。
        return document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.EnumerateArray().Any(x =>
            x.TryGetProperty("status", out var status) && (status.ToString() == "0" || status.ToString() == "CONNECTED"));
    }

    private static async Task<JsonDocument> QueryAsync(string cli, string work, int rpc, string command, NetworkProcessJob job, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var info = new ProcessStartInfo(cli) { WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "--rpc-portal", "127.0.0.1:" + rpc, "--output", "json", command }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("无法读取联机状态。");
        job.Add(process);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = DrainAsync(process.StandardError);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var json = await output.ConfigureAwait(false); await errors.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("联机核心尚未就绪。");
            return JsonDocument.Parse(json);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().ConfigureAwait(false); } }
    }

    private static async Task DrainAsync(StreamReader reader)
    { var buffer = new char[4096]; while (await reader.ReadAsync(buffer).ConfigureAwait(false) != 0) { } }
    private static int FreeLocalPort()
    { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }

    internal static void CheckSubnet(string subnet)
    {
        var address = IPAddress.Parse(subnet.Split('/')[0]).GetAddressBytes();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up))
        foreach (var entry in adapter.GetIPProperties().UnicastAddresses.Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork))
        {
            var mask = entry.IPv4Mask.GetAddressBytes(); var ip = entry.Address.GetAddressBytes();
            if (Enumerable.Range(0, 4).All(i => (ip[i] & mask[i]) == (address[i] & mask[i])))
                throw new IOException("房间虚拟网段与本机现有网络冲突，请让房主重新创建房间。");
        }
    }

    internal static string Describe(Exception error) => error switch
    {
        OperationCanceledException => "已取消联机。",
        UnauthorizedAccessException => "无法访问联机组件，请检查目录权限，并允许本次 Windows 管理员授权。",
        System.ComponentModel.Win32Exception { NativeErrorCode: 1223 } => "已取消 Windows 管理员授权。",
        System.ComponentModel.Win32Exception => "无法启动联机后台，请检查 Windows 权限与安全软件。",
        System.Runtime.InteropServices.COMException => "无法设置本次联机的防火墙规则，请检查 Windows 防火墙服务。",
        FormatException or InvalidDataException => error.Message,
        IOException => error.Message,
        _ => "联机失败，请退出房间后重试。"
    };
}
