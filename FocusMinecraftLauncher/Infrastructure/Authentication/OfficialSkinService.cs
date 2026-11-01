using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FMCore.Accounts;

namespace FocusMinecraftLauncher.Infrastructure.Authentication;

/// <summary>只从 Minecraft 官方档案与纹理服务读取皮肤与披风，并支持切换当前披风；纹理请求不携带授权。</summary>
public sealed class OfficialSkinService(string configurationDirectory)
{
    private readonly string _cacheDirectory = Path.Combine(configurationDirectory, "Skins");
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false });
    public sealed record SkinImage(BitmapSource Head, byte[] Png, bool CacheSaved = true);
    /// <summary>官方档案返回的披风条目；IsActive 表示当前正在使用的披风。</summary>
    public sealed record CapeEntry(string Id, string Alias, bool IsActive);

    /// <summary>加载 UUID 对应的本机缓存，读取完成后不占用文件句柄。</summary>
    public Task<SkinImage?> ReadCacheAsync(Guid uuid, CancellationToken cancellationToken) => Task.Run<SkinImage?>(async () =>
    {
        var path = CachePath(uuid);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("皮肤缓存过大。");
        var data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Decode(data);
    }, cancellationToken);

    /// <summary>授权仅发送给官方档案地址；皮肤纹理请求不携带授权，禁止自动跳转。</summary>
    public async Task<SkinImage?> FetchAsync(MicrosoftAccount account, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var profileData = await ReadResponseAsync(profileRequest, 256 * 1024, token).ConfigureAwait(false);
        using var profile = JsonDocument.Parse(profileData);
        var root = profile.RootElement;
        if (!root.TryGetProperty("id", out var id) || !Guid.TryParse(id.GetString(), out var uuid) || uuid != account.Uuid)
            throw new InvalidDataException("皮肤档案与所选账号不一致。");
        if (!root.TryGetProperty("skins", out var skins) || skins.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("皮肤档案格式无效。");
        string? address = null;
        foreach (var skin in skins.EnumerateArray())
            if (skin.TryGetProperty("state", out var state) && state.GetString() == "ACTIVE")
            { if (skin.TryGetProperty("url", out var url)) address = url.GetString(); if (string.IsNullOrEmpty(address)) throw new InvalidDataException("皮肤地址缺失。"); break; }
        if (address == null)
        {
            // 已恢复默认皮肤时不继续保留之前的自定义皮肤缓存。
            try { File.Delete(CachePath(account.Uuid)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.TraceWarning("旧皮肤缓存未清理：{0}", error.GetType().Name); }
            return null;
        }
        if (!Uri.TryCreate(address, UriKind.Absolute, out var texture) || texture.Host != "textures.minecraft.net"
            || texture.Scheme is not ("http" or "https") || texture.UserInfo.Length != 0 || !texture.IsDefaultPort
            || !texture.AbsolutePath.StartsWith("/texture/", StringComparison.Ordinal))
            throw new InvalidDataException("皮肤纹理地址不是受支持的官方地址。");
        var secureTexture = new UriBuilder(texture) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        using var skinRequest = new HttpRequestMessage(HttpMethod.Get, secureTexture);
        var png = await ReadResponseAsync(skinRequest, 1024 * 1024, token).ConfigureAwait(false);
        var result = await Task.Run(() => Decode(png), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var temporary = CachePath(account.Uuid) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, png, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, CachePath(account.Uuid), true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return result with { CacheSaved = false }; }
        return result;
    }

    /// <summary>读取当前正版账号拥有的披风，仅向 Minecraft 官方档案发送授权。</summary>
    public async Task<IReadOnlyList<CapeEntry>> GetCapesAsync(MicrosoftAccount account,CancellationToken cancellationToken)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request=new HttpRequestMessage(HttpMethod.Get,"https://api.minecraftservices.com/minecraft/profile");
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",account.AccessToken);
        var data=await ReadResponseAsync(request,256*1024,timeout.Token);
        using var json=JsonDocument.Parse(data);
        if(!json.RootElement.TryGetProperty("id",out var id)||!Guid.TryParse(id.GetString(),out var uuid)||uuid!=account.Uuid)throw new InvalidDataException("披风档案与当前账号不一致。");
        if(!json.RootElement.TryGetProperty("capes",out var capes)||capes.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("披风档案格式无效。");
        var result=new List<CapeEntry>();
        foreach(var cape in capes.EnumerateArray())
        {
            var capeId=cape.TryGetProperty("id",out var value)?value.GetString():null;
            if(string.IsNullOrWhiteSpace(capeId))continue;
            var alias=(cape.TryGetProperty("alias",out var name)?name.GetString():null)??"未命名披风";
            var active=cape.TryGetProperty("state",out var state)&&state.GetString()=="ACTIVE";
            result.Add(new(capeId,alias,active));
        }
        return result;
    }

    /// <summary>切换当前披风；传 null 表示取消披风。请求只发往官方档案，并带上账号授权。</summary>
    public async Task SetCapeAsync(MicrosoftAccount account,string? capeId,CancellationToken cancellationToken)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);timeout.CancelAfter(TimeSpan.FromSeconds(20));
        const string address="https://api.minecraftservices.com/minecraft/profile/capes/active";
        using var request=capeId is null
            ? new HttpRequestMessage(HttpMethod.Delete,address)
            : new HttpRequestMessage(HttpMethod.Put,address){Content=new StringContent(JsonSerializer.Serialize(new{capeId}),Encoding.UTF8,"application/json")};
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",account.AccessToken);
        using var response=await Client.SendAsync(request,timeout.Token).ConfigureAwait(false);
        if(response.IsSuccessStatusCode)return;
        throw response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden=>new InvalidOperationException("登录已过期，请重新登录。"),
            System.Net.HttpStatusCode.TooManyRequests=>new InvalidOperationException("请求过于频繁，请稍后重试。"),
            _=>new InvalidOperationException("披风修改未通过，请稍后重试。")
        };
    }
    private string CachePath(Guid uuid) => Path.Combine(_cacheDirectory, uuid.ToString("N") + ".png");
    private static async Task<byte[]> ReadResponseAsync(HttpRequestMessage request, int maximumBytes, CancellationToken cancellationToken)
    {
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("皮肤服务响应过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var content = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (content.Length + count > maximumBytes) throw new InvalidDataException("皮肤服务响应过大。");
            content.Write(buffer, 0, count);
        }
        return content.ToArray();
    }

    /// <summary>兼容 64×32 与 64×64 皮肤，按透明度将帽子层覆盖到 8×8 面部上。</summary>
    private static SkinImage Decode(byte[] png)
    {
        if (png.Length > 1024 * 1024) throw new InvalidDataException("皮肤图片过大。");
        using var stream = new MemoryStream(png, false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder is not PngBitmapDecoder || decoder.Frames[0].PixelWidth != 64 || decoder.Frames[0].PixelHeight is not (32 or 64))
            throw new InvalidDataException("皮肤图片尺寸或格式无效。");
        var pixels = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Pbgra32, null, 0);
        var face = new byte[8 * 8 * 4]; var hat = new byte[face.Length];
        pixels.CopyPixels(new Int32Rect(8, 8, 8, 8), face, 32, 0);
        pixels.CopyPixels(new Int32Rect(40, 8, 8, 8), hat, 32, 0);
        for (var i = 0; i < face.Length; i += 4)
            for (var channel = 0; channel < 4; channel++)
                face[i + channel] = (byte)Math.Min(255, hat[i + channel] + (face[i + channel] * (255 - hat[i + 3]) + 127) / 255);
        var head = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Pbgra32, null, face, 32);
        head.Freeze();
        return new(head, png);
    }
}
