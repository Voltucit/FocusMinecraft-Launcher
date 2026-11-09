using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FocusMinecraftLauncher.Features.Multiplayer.Models;

namespace FocusMinecraftLauncher.Features.Multiplayer.Services;

internal sealed record LeaseQuery(string RoomId, string Nonce, string Proof, string PeerId = "", string AvatarPixels = "");
internal sealed record RoomAvatar(string PeerId, string IpAddress, string Pixels);
internal sealed record RoomPeer(string PeerId, string IpAddress);
internal sealed record LeaseReply(bool Closed, string Signature, RoomAvatar[]? Avatars = null, string AvatarSignature = "",
    string Admission = "", RoomPeer[]? Peers = null, string AdmissionSignature = "");
/// <summary>区分经过房主签名确认的正常解散与身份校验、连接中断等失败。</summary>
internal sealed class RoomEndedException(string message, bool isDissolved = false) : IOException(message)
{
    public bool IsDissolved { get; } = isDissolved;
}

/// <summary>EasyTier 是对等网络；额外用房主签名心跳表达房间生命周期，避免房主离开后成员仍停留在旧网络。</summary>
internal sealed class RoomLease : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _clients = [];
    private TcpListener? _listener;
    private Task? _acceptTask;
    private ECDsa? _hostKey;
    private volatile bool _closed;
    private readonly object _avatarLock = new();
    private readonly Dictionary<string, RoomAvatar> _avatars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string IpAddress, TcpClient Client)> _admissions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _visiblePeers = new(StringComparer.Ordinal);
    private NetworkMember[] _members = [];
    private string _localAvatarPixels = "";
    public RoomInvitation Room { get; private set; } = null!;

    public string LocalAvatarPixels
    {
        get => Volatile.Read(ref _localAvatarPixels);
        set => Volatile.Write(ref _localAvatarPixels, MemberAvatarCodec.Normalize(value));
    }

    /// <summary>用核心返回的成员 ID 和虚拟 IP 关联头像，离开的成员不保留头像，也不按昵称匹配账号。</summary>
    public NetworkMember[] UpdateMembers(NetworkMember[] members, RoomInvitation room)
    {
        lock (_avatarLock)
        {
            _members = members;
            foreach (var id in _admissions.Keys.Where(id => !members.Any(member => member.Id == id && member.IpAddress == _admissions[id].IpAddress)).ToArray())
            {
                var connection = _admissions[id].Client;
                _admissions.Remove(id);
                connection.Dispose();
            }
            foreach (var id in _avatars.Keys.Where(id => !members.Any(member => member.Id == id && member.IpAddress == _avatars[id].IpAddress)).ToArray())
                _avatars.Remove(id);
            var local = members.FirstOrDefault(member => member.IsLocal);
            if (local != null) SetAvatar(local.Id, local.IpAddress, LocalAvatarPixels);
            return VisibleMembers(room.Format == 2).Select(member => member with
            {
                AvatarPixels = _avatars.TryGetValue(member.Id, out var avatar) && avatar.IpAddress == member.IpAddress ? avatar.Pixels : ""
            }).ToArray();
        }
    }

    private IEnumerable<NetworkMember> VisibleMembers(bool admissionRequired) => _members.Where(member => !admissionRequired || member.IsLocal
        || (_hostKey != null ? _admissions.TryGetValue(member.Id, out var admission) && admission.IpAddress == member.IpAddress
            : _visiblePeers.TryGetValue(member.Id, out var ip) && ip == member.IpAddress));

    /// <summary>房主在同一把锁内分配名额；并发加入不会超额，同一成员重连不重复占位。</summary>
    private string AdmitMember(NetworkMember? member, TcpClient client)
    {
        if (member == null) return "pending";
        if (_admissions.TryGetValue(member.Id, out var existing))
        {
            if (!ReferenceEquals(existing.Client, client)) existing.Client.Dispose();
        }
        else if (_admissions.Count >= Room.MaxPlayers - 1) return "full";
        _admissions[member.Id] = (member.IpAddress, client);
        return "accepted";
    }

    private void ReceivePeers(RoomPeer[] peers)
    {
        lock (_avatarLock)
        {
            _visiblePeers.Clear();
            foreach (var peer in peers) _visiblePeers[peer.PeerId] = peer.IpAddress;
        }
    }

    private void SetAvatar(string peerId, string ipAddress, string pixels)
    {
        var valid = MemberAvatarCodec.Normalize(pixels);
        if (valid.Length == 0) _avatars.Remove(peerId);
        else _avatars[peerId] = new(peerId, ipAddress, valid);
    }

    private LeaseQuery CreateQuery(RoomInvitation room, string nonce, string proof)
    {
        lock (_avatarLock)
            return new(room.RoomId, nonce, proof, _members.FirstOrDefault(member => member.IsLocal)?.Id ?? "", LocalAvatarPixels);
    }

    private void ReceiveAvatars(RoomAvatar[]? avatars)
    {
        lock (_avatarLock)
        {
            _avatars.Clear();
            if (avatars is not { Length: <= RoomInvitation.MaximumPlayers }) return;
            foreach (var avatar in avatars)
                if (avatar != null && _members.Any(member => !member.IsLocal && member.Id == avatar.PeerId && member.IpAddress == avatar.IpAddress))
                    SetAvatar(avatar.PeerId, avatar.IpAddress, avatar.Pixels);
        }
    }

    public RoomInvitation StartHost(RoomInvitation room, string hostId)
    {
        if (room.Format == 2) RoomInvitation.ValidatePlayerLimit(room.MaxPlayers);
        _hostKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _listener = new TcpListener(IPAddress.Parse(room.HostIp), 0);
        _listener.Start(32);
        Room = room with { HostId = hostId, ControlPort = ((IPEndPoint)_listener.LocalEndpoint).Port, HostKey = Convert.ToBase64String(_hostKey.ExportSubjectPublicKeyInfo()) };
        _acceptTask = AcceptAsync();
        return Room;
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                lock (_clients)
                {
                    _clients.RemoveAll(x => x.IsCompleted);
                    // 满员后仍留出待检查连接，返回明确的“房间已满”；这些连接不占正式名额。
                    if (_clients.Count >= RoomInvitation.MaximumPlayers + 32) { client.Dispose(); continue; }
                    _clients.Add(ServeAsync(client));
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            var admittedPeerId = "";
            try
            {
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !RoomInvitation.IsRoomIp(remote.Address.ToString(), Room.Subnet)) return;
                using var wire = new NetworkWire(client.GetStream());
                while (!deadline.IsCancellationRequested)
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    var query = await wire.ReadAsync<LeaseQuery>(deadline.Token).ConfigureAwait(false);
                    if (query == null || query.RoomId != Room.RoomId || query.Nonce.Length != 64) return;
                    var expected = HMACSHA256.HashData(Convert.FromBase64String(Room.Secret), Encoding.UTF8.GetBytes(query.RoomId + ":" + query.Nonce));
                    if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(query.Proof))) return;
                    var closed = _closed;
                    var admission = closed ? "closed" : "accepted";
                    RoomAvatar[] avatars;
                    RoomPeer[] peers;
                    lock (_avatarLock)
                    {
                        var member = _members.FirstOrDefault(member => !member.IsLocal && member.IpAddress == remote.Address.ToString()
                            && (string.IsNullOrEmpty(query.PeerId) || member.Id == query.PeerId));
                        if (admittedPeerId.Length > 0 && member?.Id != admittedPeerId) return;
                        if (admittedPeerId.Length > 0 && (!_admissions.TryGetValue(admittedPeerId, out var current) || !ReferenceEquals(current.Client, client))) return;
                        if (!closed && Room.Format == 2)
                        {
                            admission = AdmitMember(member, client);
                            if (admission == "accepted") admittedPeerId = member!.Id;
                        }
                        // 只允许更新实际从该虚拟 IP 接入的成员，不能借头像消息替换房主或其他成员。
                        if (!closed && admission == "accepted" && member != null)
                            SetAvatar(member.Id, remote.Address.ToString(), query.AvatarPixels);
                        peers = VisibleMembers(Room.Format == 2).Select(member => new RoomPeer(member.Id, member.IpAddress)).ToArray();
                        avatars = _avatars.Values.Take(RoomInvitation.MaximumPlayers).ToArray();
                    }
                    byte[] signature, avatarSignature, admissionSignature;
                    lock (_hostKey!)
                    {
                        signature = _hostKey.SignData(Payload(Room.RoomId, query.Nonce, closed), HashAlgorithmName.SHA256);
                        avatarSignature = _hostKey.SignData(AvatarPayload(Room.RoomId, query.Nonce, avatars), HashAlgorithmName.SHA256);
                        admissionSignature = _hostKey.SignData(AdmissionPayload(Room, query.Nonce, closed, admission, peers), HashAlgorithmName.SHA256);
                    }
                    await wire.WriteAsync(new LeaseReply(closed, Convert.ToBase64String(signature), avatars, Convert.ToBase64String(avatarSignature),
                        admission, peers, Convert.ToBase64String(admissionSignature)), deadline.Token).ConfigureAwait(false);
                    if (closed || admission == "full") return;
                }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or FormatException or System.Text.Json.JsonException) { }
            finally
            {
                lock (_avatarLock)
                    if (_admissions.TryGetValue(admittedPeerId, out var admission) && ReferenceEquals(admission.Client, client))
                    {
                        _admissions.Remove(admittedPeerId);
                        _avatars.Remove(admittedPeerId);
                    }
            }
        }
    }

    /// <summary>每次验证新的挑战，成员无法凭共享网络密钥伪造房主的解散消息。</summary>
    public async Task WatchHostAsync(RoomInvitation room, Action<bool> connectionChanged, CancellationToken token)
    {
        using var hostKey = ECDsa.Create();
        hostKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(room.HostKey), out _);
        var lastReply = DateTime.UtcNow;
        var hadReply = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Parse(room.HostIp), room.ControlPort, timeout.Token).ConfigureAwait(false);
                using var wire = new NetworkWire(client.GetStream());
                while (!token.IsCancellationRequested)
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(8));
                    var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    var proof = Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(room.Secret), Encoding.UTF8.GetBytes(room.RoomId + ":" + nonce)));
                    await wire.WriteAsync(CreateQuery(room, nonce, proof), timeout.Token).ConfigureAwait(false);
                    var reply = await wire.ReadAsync<LeaseReply>(timeout.Token).ConfigureAwait(false) ?? throw new IOException();
                    if (!hostKey.VerifyData(Payload(room.RoomId, nonce, reply.Closed), Convert.FromBase64String(reply.Signature), HashAlgorithmName.SHA256))
                        throw new RoomEndedException("无法验证房主身份，已退出联机。");
                    if (room.Format == 2)
                    {
                        if (reply.Peers is not { } peers || peers.Length > room.MaxPlayers || string.IsNullOrEmpty(reply.AdmissionSignature)
                            || peers.Any(peer => peer == null || string.IsNullOrEmpty(peer.PeerId) || !RoomInvitation.IsRoomIp(peer.IpAddress, room.Subnet))
                            || peers.Select(peer => peer.PeerId).Distinct(StringComparer.Ordinal).Count() != peers.Length
                            || !hostKey.VerifyData(AdmissionPayload(room, nonce, reply.Closed, reply.Admission, peers), Convert.FromBase64String(reply.AdmissionSignature), HashAlgorithmName.SHA256))
                            throw new RoomEndedException("无法验证房间人数信息，请更新启动器并重新获取邀请。");
                        if (reply.Admission == "full")
                            throw new RoomEndedException($"房间已满（上限 {room.MaxPlayers} 人，包含房主）。请等待有成员退出后再加入。");
                        if (reply.Admission == "pending")
                        {
                            connectionChanged(false);
                            if (DateTime.UtcNow - lastReply > TimeSpan.FromSeconds(30)) throw new RoomEndedException("暂时无法确认加入资格，请稍后重新加入房间。");
                            await Task.Delay(2000, token).ConfigureAwait(false);
                            continue;
                        }
                        if (reply.Admission != (reply.Closed ? "closed" : "accepted"))
                            throw new RoomEndedException("房间加入状态无效，请重新获取邀请。");
                        ReceivePeers(peers);
                    }
                    if (reply.Closed) throw new RoomEndedException("房主已解散房间。", isDissolved: true);
                    // 保留原生命周期签名，旧客户端仍可联机；头像扩展缺失或无效时仅回退默认头像。
                    var verifiedAvatars = false;
                    if (reply.Avatars is { Length: <= RoomInvitation.MaximumPlayers } && !string.IsNullOrEmpty(reply.AvatarSignature))
                    {
                        try { verifiedAvatars = hostKey.VerifyData(AvatarPayload(room.RoomId, nonce, reply.Avatars), Convert.FromBase64String(reply.AvatarSignature), HashAlgorithmName.SHA256); }
                        catch (Exception error) when (error is FormatException or CryptographicException) { }
                    }
                    ReceiveAvatars(verifiedAvatars ? reply.Avatars : null);
                    lastReply = DateTime.UtcNow;
                    hadReply = true;
                    connectionChanged(true);
                    await Task.Delay(2000, token).ConfigureAwait(false);
                }
            }
            catch (RoomEndedException) { throw; }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or CryptographicException or FormatException or System.Text.Json.JsonException)
            {
                token.ThrowIfCancellationRequested();
                connectionChanged(false);
                if (DateTime.UtcNow - lastReply > TimeSpan.FromSeconds(30))
                    throw new RoomEndedException(hadReply ? "与房主的连接已中断，已退出联机。请重新获取邀请后加入。"
                        : "房间不可用：邀请可能已失效，或房主已离线。请向房主获取新的邀请信息。");
                await Task.Delay(1500, token).ConfigureAwait(false);
            }
        }
    }

    public async Task AnnounceCloseAsync()
    {
        if (_hostKey == null) return;
        _closed = true;
        await Task.Delay(2500).ConfigureAwait(false);
    }

    private static byte[] Payload(string room, string nonce, bool closed) => Encoding.UTF8.GetBytes(room + ":" + nonce + ":" + (closed ? "closed" : "open"));
    private static byte[] AvatarPayload(string room, string nonce, RoomAvatar[] avatars) => Encoding.UTF8.GetBytes(room + ":" + nonce + ":avatars:" + JsonSerializer.Serialize(avatars));
    private static byte[] AdmissionPayload(RoomInvitation room, string nonce, bool closed, string admission, RoomPeer[] peers)
        => JsonSerializer.SerializeToUtf8Bytes(new { room.RoomId, Nonce = nonce, room.MaxPlayers, Closed = closed, Admission = admission, Peers = peers });

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener?.Stop();
        if (_acceptTask != null) await _acceptTask.ConfigureAwait(false);
        Task[] clients; lock (_clients) clients = [.. _clients];
        await Task.WhenAll(clients).ConfigureAwait(false);
        _hostKey?.Dispose(); _stop.Dispose();
    }
}
