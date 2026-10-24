using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
namespace FMLauncher.Infrastructure.Networking;

/// <summary>只连接公网 HTTPS；在实际连接时验证 DNS，避免重定向、代理或 DNS 重绑定访问本机和内网。</summary>
public sealed class PublicImageClient : IDisposable
{
    private readonly HttpClient _client;
    public PublicImageClient()
    {
        _client = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                if (addresses.Length == 0 || addresses.Any(address => !IsPublic(address))) throw new InvalidDataException("壁纸 API 只能连接公网地址。");
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try { await socket.ConnectAsync(address, context.DnsEndPoint.Port, ct); return new NetworkStream(socket, true); }
                    catch (SocketException) { socket.Dispose(); }
                    catch { socket.Dispose(); throw; }
                }
                throw new HttpRequestException("无法连接壁纸服务。");
            }
        })
        { Timeout = Timeout.InfiniteTimeSpan };
    }
    public static Uri Validate(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || uri.Port != 443 || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("壁纸地址必须为无用户信息的 HTTPS 默认端口地址。");
        if (IPAddress.TryParse(uri.Host, out var ip) && !IsPublic(ip)) throw new InvalidDataException("壁纸 API 不允许访问本机或内网。");
        return uri;
    }
    private static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 100 && b[1] is >= 64 and <= 127 || b[0] == 198 && b[1] is 18 or 19);
        // 只允许 IPv6 全球单播，排除本地、组播、隧道及文档保留前缀。
        return (b[0] & 0xE0) == 0x20 && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) && !(b[0] == 0x20 && b[1] == 0x02) && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0);
    }
    /// <summary>限制完整响应读取；机密请求头仅发给初始主机，重定向必须由用户配置最终 URL。</summary>
    public async Task<byte[]> GetAsync(Uri uri, IReadOnlyDictionary<string, string>? headers, int limit, CancellationToken ct)
    {
        Validate(uri.AbsoluteUri);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("FMLauncher/2.0");
        if (headers != null)
            foreach (var (key, value) in headers)
            {
                if (key.Equals("Host", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || key.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                    key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')) || value.Contains('\r') || value.Contains('\n') || value.Length > 4096 ||
                    !request.Headers.TryAddWithoutValidation(key, value)) throw new InvalidDataException("请求头格式无效或不受支持。");
            }
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)response.StatusCode is >= 300 and < 400) throw new InvalidDataException("壁纸服务返回重定向，请将 API 配置为最终 HTTPS 地址。");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("壁纸响应超过大小限制。");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("壁纸响应超过大小限制。");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return output.ToArray();
    }
    public void Dispose() => _client.Dispose();
}

