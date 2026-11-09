using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;

namespace FocusMinecraftLauncher.Features.Multiplayer.Models;

public sealed record MultiplayerMember(string Id, string Name, string Role, ImageSource? Avatar, string IpAddress, string ConnectionText)
{
    public bool HasIpAddress => IPAddress.TryParse(IpAddress, out _);
    public string IpText => HasIpAddress ? IpAddress : "待获取";
}

public sealed record NetworkMember(string Id, string Name, string IpAddress, bool IsLocal, bool IsHost, string Connection, string Latency, string AvatarPixels = "");
public sealed record NetworkSnapshot(string Status, string Error, bool Ready, bool Ended, string Invitation, NetworkMember[] Members, bool RoomDissolved = false, int MaxPlayers = 0);
public sealed record WorkerHello(int Protocol, string Error);
public sealed record WorkerRequest(string Kind, string PlayerName = "", bool Hosting = false, RoomInvitation? Room = null, string SharedNode = "", string AvatarPixels = "");

/// <summary>邀请只描述虚拟网络，不携带游戏版本、游戏端口或账号凭据；网络密钥不得写入日志。</summary>
public sealed record RoomInvitation(int Format, string RoomId, string Name, string Secret, string Subnet,
    string HostIp, int ControlPort, string HostKey, string HostId, string SharedNode, int MaxPlayers = 128,
    string NetworkName = "")
{
    public const int MaximumLength = 8192;
    public const int MinimumPlayers = 2;
    public const int MaximumPlayers = 128;
    public const int DefaultPlayers = 8;
    public string Encode() => "FMROOM" + Format + "." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static RoomInvitation Parse(string text)
    {
        try
        {
            text = text.Trim();
            if (text.Length > MaximumLength || !(text.StartsWith("FMROOM1.", StringComparison.Ordinal) || text.StartsWith("FMROOM2.", StringComparison.Ordinal))) throw new FormatException();
            var encoded = text[8..].Replace('-', '+').Replace('_', '/');
            var value = JsonSerializer.Deserialize<RoomInvitation>(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='))) ?? throw new FormatException();
            value = value with { NetworkName = value.NetworkName ?? "" };
            if (value.Format is not (1 or 2) || text[6] - '0' != value.Format || !Guid.TryParseExact(value.RoomId, "N", out _) || string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 48 || value.Name.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(value.Secret) || value.Secret.Length > 128 || value.Secret.Any(char.IsControl) || value.ControlPort is < 1024 or > 65535 || string.IsNullOrWhiteSpace(value.HostId)
                || value.NetworkName.Length > 64 || value.NetworkName.Any(char.IsControl)
                || !IsRoomIp(value.HostIp, value.Subnet)) throw new FormatException();
            ValidateNode(value.SharedNode);
            if (value.Format == 2) ValidatePlayerLimit(value.MaxPlayers);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(value.HostKey), out _);
            if (key.KeySize != 256) throw new FormatException();
            return value;
        }
        catch (Exception e) when (e is FormatException or JsonException or CryptographicException or ArgumentException or NullReferenceException)
        { throw new FormatException("邀请信息无效，请完整复制房主提供的邀请信息。", e); }
    }

    /// <summary>人数包含房主，并与当前成员、头像同步容量保持一致。</summary>
    public static void ValidatePlayerLimit(int count)
    {
        if (count is < MinimumPlayers or > MaximumPlayers)
            throw new FormatException($"人数上限应为 {MinimumPlayers}–{MaximumPlayers} 人，包含房主。");
    }

    public static bool IsRoomIp(string ip, string subnet)
    {
        if (!IPAddress.TryParse(ip.Split('/')[0], out var address) || !IPAddress.TryParse(subnet.Split('/')[0], out var network) || !subnet.EndsWith("/24", StringComparison.Ordinal)) return false;
        var a = address.GetAddressBytes(); var b = network.GetAddressBytes();
        return a.Length == 4 && b.Length == 4 && b[0] == 10 && b[3] == 0 && a[0] == b[0] && a[1] == b[1] && a[2] == b[2] && a[3] is > 0 and < 255;
    }

    /// <summary>手动填写节点时允许省略 TCP 协议；空值用于清除配置，不提供任何预设地址。</summary>
    public static string NormalizeNode(string node)
    {
        var address = node.Trim();
        if (address.Length == 0) return "";
        if (!address.Contains("://", StringComparison.Ordinal)) address = "tcp://" + address;
        ValidateNode(address);
        return address;
    }

    public static void ValidateNode(string node)
    {
        if (node.Length > 256 || !Uri.TryCreate(node, UriKind.Absolute, out var uri) || uri.Scheme is not ("tcp" or "udp" or "wss")
            || string.IsNullOrWhiteSpace(uri.Host) || uri.Port is < 1 or > 65535 || uri.UserInfo.Length > 0 || uri.IsLoopback
            || uri.Fragment.Length > 0 || uri.Query.Length > 0) throw new FormatException("请配置有效的共享节点地址，例如 tcp://节点域名:端口。");
    }
}
