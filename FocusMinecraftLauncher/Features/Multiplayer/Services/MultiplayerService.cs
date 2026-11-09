using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Features.Multiplayer.Models;
using FocusMinecraftLauncher.Infrastructure.Configuration;
using FocusMinecraftLauncher.Presentation.Dialogs;

namespace FocusMinecraftLauncher.Features.Multiplayer.Services;

/// <summary>应用级联机会话；页面切换不创建第二份核心，组件下载、退出和卸载共用同一生命周期。</summary>
public sealed class MultiplayerService(LauncherConfiguration configuration) : ObservableObject, IDisposable
{
    private CancellationTokenSource? _operation;
    private NamedPipeServerStream? _pipe;
    private NetworkWire? _wire;
    private Process? _worker;
    private Task? _reader;
    private Task<bool>? _starting;
    private Task? _installation;
    private Task? _stopping;
    private bool _busy, _hasSession, _hosting, _preventStart, _installingComponents, _requestSent;
    private string _roomTitle = "", _notice = "", _componentState = "";
    private string _localAvatarPixels = "";
    private string _roomId = "";
    private NetworkSnapshot _snapshot = new("尚未联机", "", false, false, "", []);

    public bool IsBusy => _busy;
    public bool HasSession => _hasSession;
    public bool HasActivity => _busy || _hasSession || _worker != null || _stopping is { IsCompleted: false };
    public bool IsHosting => _hosting;
    public bool CanStart => !HasActivity && !_preventStart;
    public bool IsStopping => _stopping is { IsCompleted: false };
    public bool ComponentInstalled => EasyTierComponents.IsInstalled;
    public bool CanInstallComponents => CanStart && !ComponentInstalled;
    public bool IsInstallingComponents => _installingComponents;
    public bool CanCancelComponentInstallation => IsInstallingComponents && _operation is { IsCancellationRequested: false };
    public bool CanUninstall => !IsBusy && !IsStopping && Directory.Exists(EasyTierComponents.Root);
    public string ComponentStatus => _componentState.Length > 0 ? _componentState : ComponentInstalled ? "EasyTier " + EasyTierComponents.Version : "未下载";
    public string RoomTitle => _roomTitle;
    public string Notice => _notice;
    public NetworkSnapshot Snapshot => _snapshot;
    public int PreferredMaxPlayers
    {
        get
        {
            var count = LauncherConfiguration.Number(configuration.ReadLauncher()["multiplayer"]?["maxPlayers"], RoomInvitation.DefaultPlayers);
            return count is >= RoomInvitation.MinimumPlayers and <= RoomInvitation.MaximumPlayers && count == Math.Truncate(count)
                ? (int)count : RoomInvitation.DefaultPlayers;
        }
    }
    public string SharedNode => LauncherConfiguration.Text(configuration.ReadLauncher()["multiplayer"]?["sharedNode"]).Trim();
    public string SharedNodeNetworkName => LauncherConfiguration.Text(configuration.ReadLauncher()["multiplayer"]?["sharedNodeNetworkName"]).Trim();
    public string SharedNodePassword => LauncherConfiguration.Text(configuration.ReadLauncher()["multiplayer"]?["sharedNodePassword"]);
    public void PreventStart(bool value) { _preventStart = value; Publish(); }

    /// <summary>从设置页提前准备组件，不要求配置节点，也不启动核心、提权或建立网络。</summary>
    public Task InstallComponentsAsync()
    {
        if (!CanInstallComponents) return Task.CompletedTask;
        _busy = true; _installingComponents = true; _notice = "";
        _operation?.Dispose(); _operation = new(); Publish();
        _installation = InstallComponentsCoreAsync(_operation.Token);
        return _installation;
    }

    public void CancelComponentInstallation()
    {
        if (!CanCancelComponentInstallation) return;
        _operation?.Cancel(); Publish();
    }

    private async Task InstallComponentsCoreAsync(CancellationToken token)
    {
        try
        {
            _notice = await EnsureComponentsAsync(false, token) ? "联机组件已安装。" : "已取消组件安装。";
        }
        catch (OperationCanceledException) { _notice = "已取消组件安装。"; }
        catch (Exception error)
        {
            _notice = error is System.Net.Http.HttpRequestException ? "联机组件下载失败，请检查网络后重试。" : "组件安装失败：" + EasyTierWorker.Describe(error);
        }
        finally { _busy = false; _installingComponents = false; _componentState = ""; Publish(); }
    }

    /// <summary>设置页安装与联机前准备共用下载确认、真实进度和官方哈希校验。</summary>
    private async Task<bool> EnsureComponentsAsync(bool connectAfter, CancellationToken token)
    {
        _componentState = "正在检查联机组件"; Publish();
        if (await EasyTierComponents.VerifyAsync(token)) return true;
        token.ThrowIfCancellationRequested();
        var nextStep = connectAfter ? "下载完成后将请求 Windows 管理员授权以建立虚拟网络。" : "本次只准备组件，不建立联机连接。";
        if (!MessageDialog.Confirm("安装联机组件？", "需要从 EasyTier 官方 GitHub 下载独立联机组件（约 31 MB）。\n\n" + nextStep, "下载并安装", "取消")) return false;
        _componentState = "正在下载联机组件"; Publish();
        var throttle = Stopwatch.StartNew();
        var reporting = true;
        var progress = new Progress<(long Received, long? Total)>(value =>
        {
            if (!reporting || token.IsCancellationRequested || throttle.ElapsedMilliseconds < 150) return;
            throttle.Restart();
            var amount = (value.Received / 1048576d).ToString("0.0");
            _componentState = value.Total is > 0 ? $"正在下载 {amount} / {value.Total.Value / 1048576d:0.0} MB" : $"正在下载 {amount} MB";
            Publish();
        });
        try { await EasyTierComponents.DownloadAsync(progress, token); }
        finally { reporting = false; }
        return true;
    }

    /// <summary>保存用户提供的共享节点；私有节点额外保存网络名称和密码，公益节点留空。</summary>
    public async Task<bool> SaveSharedNodeAsync(string address, string networkName = "", string password = "")
    {
        var node = RoomInvitation.NormalizeNode(address);
        networkName = networkName.Trim();
        if ((networkName.Length == 0) != (password.Length == 0))
            throw new FormatException("私有节点需要同时填写网络名称和密码；公益节点请将两项都留空。");
        if (networkName.Length > 64 || networkName.Any(char.IsControl))
            throw new FormatException("节点网络名称最多 64 个字符，且不能包含控制字符。");
        if (password.Length > 128 || password.Any(char.IsControl))
            throw new FormatException("节点密码最多 128 个字符，且不能包含控制字符。");
        configuration.ChangeLauncher(root =>
        {
            var multiplayer = LauncherConfiguration.Object(root, "multiplayer");
            multiplayer["sharedNode"] = node;
            multiplayer["sharedNodeNetworkName"] = networkName;
            multiplayer["sharedNodePassword"] = password;
        });
        var saved = await configuration.FlushAsync();
        Publish();
        return saved;
    }

    /// <summary>设置页只验证节点端口可达；私有网络名称和密码在创建房间时由 EasyTier 实际校验。</summary>
    public async Task<string> TestSharedNodeAsync(string address, string networkName = "", string password = "", CancellationToken token = default)
    {
        var node = RoomInvitation.NormalizeNode(address);
        if (node.Length == 0) return "节点已清除。";
        await TestTcpAsync(node, token).ConfigureAwait(false);
        return "节点端口连接成功";
    }

    private static async Task TestTcpAsync(string node, CancellationToken token)
    {
        var uri = new Uri(node);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(uri.Host, timeout.Token);
            if (addresses.Length == 0) throw new IOException("节点域名没有可用地址。");
            if (uri.Scheme == "udp") return;
            Exception? last = null;
            foreach (IPAddress ip in addresses)
            {
                using var client = new TcpClient(ip.AddressFamily);
                try
                {
                    await client.ConnectAsync(ip, uri.Port, timeout.Token);
                    if (uri.Scheme == "wss")
                    {
                        using var ssl = new SslStream(client.GetStream(), false);
                        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = uri.Host }, timeout.Token);
                    }
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { last = error; }
            }
            throw new IOException("节点连接失败。", last);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("节点连接超时。");
        }
    }

    /// <summary>解析邀请并拒绝本机已确认解散的房间；不把房主暂时离线误记为永久失效。</summary>
    public RoomInvitation ReadInvitation(string invitation)
    {
        var room = RoomInvitation.Parse(invitation);
        if (configuration.ReadLauncher()["multiplayer"]?["dissolvedRooms"] is JsonArray rooms
            && rooms.Any(value => LauncherConfiguration.Text(value) == room.RoomId))
            throw new FormatException("这个房间已解散，请向房主获取新的邀请信息。");
        return room;
    }

    private void RememberDissolvedRoom(string roomId)
    {
        if (!Guid.TryParseExact(roomId, "N", out _)) return;
        // 仅保存无凭据的房间标识，沿用配置服务的合并、原子写入和保存失败反馈。
        configuration.ChangeLauncher(root =>
        {
            var multiplayer = LauncherConfiguration.Object(root, "multiplayer");
            if (multiplayer["dissolvedRooms"] is not JsonArray rooms) multiplayer["dissolvedRooms"] = rooms = new();
            if (!rooms.Any(value => LauncherConfiguration.Text(value) == roomId)) rooms.Add(roomId);
        });
    }

    /// <summary>用户点击创建或加入时，复用已准备的组件；缺失时确认下载，再请求提权建立连接。</summary>
    public Task<bool> StartAsync(string playerName, string roomName, string invitation, bool hosting, string avatarPixels = "", int maxPlayers = RoomInvitation.DefaultPlayers)
    {
        if (!CanStart) return Task.FromResult(false);
        _busy = true; _notice = ""; _hosting = hosting; _roomTitle = roomName.Trim();
        _roomId = "";
        _localAvatarPixels = MemberAvatarCodec.Normalize(avatarPixels); _requestSent = false;
        _operation?.Dispose(); _operation = new(); Publish();
        _starting = StartCoreAsync(playerName, roomName, invitation, hosting, maxPlayers, _operation.Token);
        return _starting;
    }

    /// <summary>账号头像延迟加载或刷新后同步到已有房间，不重新连接或改变本次联机身份。</summary>
    public void UpdateLocalAvatar(string avatarPixels)
    {
        var pixels = MemberAvatarCodec.Normalize(avatarPixels);
        if (_localAvatarPixels == pixels) return;
        _localAvatarPixels = pixels;
        if (_requestSent && _wire != null && !IsStopping)
            _ = SendAvatarAsync(_wire, pixels, _operation?.Token ?? CancellationToken.None);
    }

    private static async Task SendAvatarAsync(NetworkWire wire, string pixels, CancellationToken token)
    {
        try { await wire.WriteAsync(new WorkerRequest("avatar", AvatarPixels: pixels), token); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // 头像同步不覆盖联机主流程的错误；管道断开仍由会话读取循环处理。
            Trace.TraceWarning("联机头像同步未完成：{0}", error.GetType().Name);
        }
    }

    private async Task<bool> StartCoreAsync(string playerName, string roomName, string invitation, bool hosting, int maxPlayers, CancellationToken token)
    {
        try
        {
            RoomInvitation room;
            if (hosting)
            {
                RoomInvitation.ValidatePlayerLimit(maxPlayers);
                if (SharedNode.Length == 0) throw new FormatException("请先在“设置 → 其他”填写并保存共享节点地址。");
                RoomInvitation.ValidateNode(SharedNode);
                if ((SharedNodeNetworkName.Length == 0) != (SharedNodePassword.Length == 0))
                    throw new FormatException("私有节点需要同时填写网络名称和密码；公益节点请将两项都留空。");
                if (string.IsNullOrWhiteSpace(roomName) || roomName.Trim().Length > 48 || roomName.Any(char.IsControl)) throw new FormatException("请输入有效的房间名称，最多 48 个字符。");
                if (SharedNodeNetworkName.Length == 0 && SharedNodePassword.Length == 0)
                    _notice = "如果使用私有节点，请先在“设置 → 其他”填写网络名称和密码；私有网络凭据会在创建房间时校验。";
                room = NewRoom(roomName.Trim(), SharedNode, maxPlayers, SharedNodeNetworkName, SharedNodePassword);
                configuration.ChangeLauncher(root => LauncherConfiguration.Object(root, "multiplayer")["maxPlayers"] = maxPlayers);
            }
            else room = ReadInvitation(invitation);
            _roomId = room.RoomId;
            if (!await EnsureComponentsAsync(true, token)) return false;
            token.ThrowIfCancellationRequested();
            _componentState = "";
            _snapshot = new("等待 Windows 授权", "", false, false, "", [], MaxPlayers: room.Format == 2 ? room.MaxPlayers : 0);
            _hosting = hosting; _roomTitle = room.Name; _hasSession = true; Publish();
            var pipeName = "FMLauncher.Network." + Guid.NewGuid().ToString("N");
            _pipe = NetworkWindows.CreateUserPipeServer(pipeName);
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory, WindowStyle = ProcessWindowStyle.Hidden };
            using var current = Process.GetCurrentProcess();
            // 管道名和进程身份可以公开；网络密钥只通过当前用户专属管道传输。
            info.Arguments = $"{EasyTierWorker.Argument} {pipeName} {current.Id} {current.StartTime.ToUniversalTime().Ticks}";
            _worker = await Task.Run(() => Process.Start(info), CancellationToken.None) ?? throw new IOException("无法启动联机后台。");
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(token); connect.CancelAfter(TimeSpan.FromSeconds(45));
            var connected = _pipe.WaitForConnectionAsync(connect.Token);
            var exited = _worker.WaitForExitAsync(connect.Token);
            if (await Task.WhenAny(connected, exited) != connected) throw new IOException("联机后台未能启动，请使用当前 Windows 账户允许管理员授权。");
            await connected;
            NetworkWindows.VerifyPipeProcess(_pipe.SafePipeHandle, _worker.Id, false);
            _wire = new NetworkWire(_pipe);
            // 先收到后台身份校验完成的握手，再发送包含网络密钥的请求。
            var hello = await _wire.ReadAsync<WorkerHello>(connect.Token)
                ?? throw new IOException("联机后台在完成启动握手前退出，请确认使用同一 Windows 账户授权。");
            if (hello.Protocol != EasyTierWorker.PipeProtocol) throw new InvalidDataException("联机后台版本不匹配，请关闭启动器后重新打开。");
            if (hello.Error.Length > 0) throw new IOException(hello.Error);
            var sentAvatar = _localAvatarPixels;
            await _wire.WriteAsync(new WorkerRequest("start", playerName.Length > 48 ? playerName[..48] : playerName, hosting, room, room.SharedNode, sentAvatar), token);
            _requestSent = true;
            if (sentAvatar != _localAvatarPixels) await SendAvatarAsync(_wire, _localAvatarPixels, token);
            _snapshot = _snapshot with { Status = "正在连接" }; Publish();
            _reader = ReadSnapshotsAsync(_worker, _wire, _pipe);
            return true;
        }
        catch (Exception error)
        {
            Trace.TraceError("联机启动失败（{0}）：{1}\n{2}", error.GetType().Name, error.Message, error.StackTrace);
            _notice = error switch
            {
                System.Net.Http.HttpRequestException => "联机组件下载失败，请检查网络后重试。",
                ObjectDisposedException => "联机后台连接已关闭，请重新尝试。",
                IOException io when (io.HResult & 0xffff) is 109 or 232 or 233 => "联机后台在启动通信时退出，请确认使用同一 Windows 账户授权后重试。",
                _ => EasyTierWorker.Describe(error)
            };
            _snapshot = new("连接失败", _notice, false, true, "", []);
            _hasSession = false;
            CloseConnection();
            if (_worker != null)
            {
                try { await _worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35)); }
                catch (TimeoutException)
                {
                    _notice += "\n联机后台尚未退出，请稍后重试。";
                    _snapshot = _snapshot with { Error = _notice };
                }
                if (_worker.HasExited) { _worker.Dispose(); _worker = null; }
            }
            return false;
        }
        finally { _busy = false; _componentState = ""; Publish(); }
    }

    private async Task ReadSnapshotsAsync(Process worker, NetworkWire wire, NamedPipeServerStream pipe)
    {
        try
        {
            while (await wire.ReadAsync<NetworkSnapshot>(CancellationToken.None) is { } snapshot)
            {
                _snapshot = snapshot;
                if (snapshot.Ready) _notice = "";
                if (snapshot.Ended)
                {
                    _notice = snapshot.Error.Length > 0 ? snapshot.Error : snapshot.Status;
                    _hasSession = false;
                    if (snapshot.RoomDissolved) RememberDissolvedRoom(_roomId);
                }
                Publish();
                // 结束消息是终态，不再把后台随后正常关闭管道解释成连接故障。
                if (snapshot.Ended) break;
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or System.Text.Json.JsonException)
        { if (!IsStopping) { _notice = "联机后台连接已中断，正在退出联机。"; _hasSession = false; Publish(); } }
        finally
        {
            if (ReferenceEquals(_wire, wire)) { _wire = null; _requestSent = false; }
            if (ReferenceEquals(_pipe, pipe)) _pipe = null;
            try { wire.Dispose(); }
            finally { pipe.Dispose(); }
            try { await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { _notice = "联机后台尚未退出，请等待后重试。"; }
            if (worker.HasExited)
            {
                worker.Dispose(); if (ReferenceEquals(_worker, worker)) _worker = null;
                _hasSession = false;
            }
            Publish();
        }
    }

    /// <summary>等待组件下载、授权和核心退出结束；房主会先通知成员，退出失败不伪报成功。</summary>
    public Task StopAsync()
    {
        if (_stopping is { IsCompleted: false }) return _stopping;
        _stopping = StopAndNotifyAsync(); Publish(); return _stopping;
    }

    private async Task StopAndNotifyAsync()
    {
        try { await StopCoreAsync(); }
        finally { _stopping = null; Publish(); }
    }

    private async Task StopCoreAsync()
    {
        var installingComponents = IsInstallingComponents;
        _operation?.Cancel();
        if (_installation is { IsCompleted: false }) await _installation;
        if (_starting is { IsCompleted: false }) await _starting;
        var worker = _worker;
        if (worker != null)
        {
            try { if (_wire != null) await _wire.WriteAsync(new WorkerRequest("stop"), CancellationToken.None); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { CloseConnection(); }
            try
            {
                if (_reader != null) await _reader.WaitAsync(TimeSpan.FromSeconds(20));
                else { CloseConnection(); await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35)); }
            }
            catch (TimeoutException) { throw new IOException("联机后台仍在退出，请等待后重试；尚未卸载组件。"); }
            if (_worker != null)
            {
                if (!_worker.HasExited) throw new IOException("联机后台尚未退出，请稍后重试。");
                _worker.Dispose(); _worker = null;
            }
        }
        _operation?.Dispose(); _operation = null;
        CloseConnection(); _reader = null;
        if (_hosting) RememberDissolvedRoom(_roomId);
        _roomId = "";
        _hasSession = false; _snapshot = new("尚未联机", "", false, false, "", []);
        _notice = installingComponents ? "已取消组件安装。" : _hosting ? "房间已解散。" : "已退出房间。";
        Publish();
    }

    public async Task UninstallAsync()
    {
        if (!CanUninstall) return;
        var warning = HasActivity ? "当前仍在联机，继续会先退出联机，再删除联机组件。" : "删除独立下载的 EasyTier 文件，下次使用联机时会重新询问是否下载。";
        if (!MessageDialog.Confirm("卸载联机组件？", warning + "\n\n游戏文件与账号不受影响。", "卸载", "取消")) return;
        _preventStart = true; Publish();
        try
        {
            await StopAsync();
            _busy = true; _componentState = "正在卸载"; Publish();
            await Task.Run(EasyTierComponents.Uninstall);
            _notice = "联机组件已卸载。";
        }
        catch (Exception error) { _notice = "卸载失败：" + EasyTierWorker.Describe(error); }
        finally { _busy = false; _preventStart = false; _componentState = ""; Publish(); }
    }

    private static RoomInvitation NewRoom(string name, string node, int maxPlayers, string networkName, string password)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var subnet = $"10.{RandomNumberGenerator.GetInt32(128, 240)}.{RandomNumberGenerator.GetInt32(0, 256)}.0/24";
            try { EasyTierWorker.CheckSubnet(subnet); }
            catch (IOException) { continue; }
            string secret = password.Length > 0 ? password : Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            return new(2, Guid.NewGuid().ToString("N"), name, secret, subnet, subnet[..^4] + "1", 0, "", "", node, maxPlayers, networkName);
        }
        throw new IOException("无法分配不冲突的联机网段，请检查本机其他虚拟网络。");
    }

    public void NotifyComponentsChanged() => Publish();

    /// <summary>先释放消息包装再关闭管道，同时清除引用，供启动失败、退出与应用关闭共用。</summary>
    private void CloseConnection()
    {
        var wire = _wire; var pipe = _pipe;
        _wire = null; _pipe = null; _requestSent = false;
        try { wire?.Dispose(); }
        finally { pipe?.Dispose(); }
    }
    private void Publish()
    {
        void Notify() { Refresh(); CommandManager.InvalidateRequerySuggested(); }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        if (dispatcher.CheckAccess()) Notify(); else _ = dispatcher.InvokeAsync(Notify);
    }
    public void Dispose() { _operation?.Cancel(); CloseConnection(); }
}
