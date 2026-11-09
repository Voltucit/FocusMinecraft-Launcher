using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FMCore.Accounts;
using FocusMinecraftLauncher.Infrastructure.Runtime;

namespace FocusMinecraftLauncher.Infrastructure.Authentication;

/// <summary>只从 Minecraft 官方档案与纹理服务读取皮肤与披风，并支持切换当前披风；纹理请求不携带授权。</summary>
public sealed class OfficialSkinService(string configurationDirectory)
{
    private readonly string _cacheDirectory = Path.Combine(configurationDirectory, "Skins");
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false });
    static OfficialSkinService() => Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", CommunitySource.UserAgent);
    public sealed record SkinImage(BitmapSource? Head, byte[]? Png, bool CacheSaved = true,
        bool IsSlim = false, byte[]? CapePng = null, string CapeMessage = "");
    private sealed record AppearanceCache(byte[]? SkinPng, bool IsSlim, byte[]? CapePng);
    private sealed record TextureProfile(string? Skin, bool IsSlim, string? Cape);
    /// <summary>官方档案返回的披风条目；IsActive 表示当前正在使用的披风。</summary>
    public sealed record CapeEntry(string Id, string Alias, bool IsActive);

    /// <summary>加载 UUID 对应的本机缓存，读取完成后不占用文件句柄。</summary>
    public Task<SkinImage?> ReadCacheAsync(Guid uuid, CancellationToken cancellationToken) => Task.Run<SkinImage?>(async () =>
    {
        var appearancePath = AppearanceCachePath(uuid);
        if (File.Exists(appearancePath))
        {
            try
            {
                if (new FileInfo(appearancePath).Length > 3 * 1024 * 1024) throw new InvalidDataException("外观缓存过大。");
                var cache = JsonSerializer.Deserialize<AppearanceCache>(await File.ReadAllBytesAsync(appearancePath, cancellationToken).ConfigureAwait(false));
                if (cache != null)
                {
                    var skin = cache.SkinPng == null ? new SkinImage(null, null) : DecodeSkin(cache.SkinPng);
                    if (cache.CapePng != null) ValidateCape(cache.CapePng);
                    return skin with { IsSlim = cache.IsSlim, CapePng = cache.CapePng };
                }
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException or ArgumentException or FileFormatException)
            { System.Diagnostics.Trace.TraceWarning("外观缓存无效，尝试旧皮肤缓存：{0}", error.GetType().Name); }
        }
        var path = CachePath(uuid);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("皮肤缓存过大。");
        var data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return DecodeSkin(data);
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
        var slim = false;
        foreach (var skin in skins.EnumerateArray())
            if (skin.TryGetProperty("state", out var state) && state.GetString() == "ACTIVE")
            {
                if (skin.TryGetProperty("url", out var url)) address = url.GetString();
                if (string.IsNullOrEmpty(address)) throw new InvalidDataException("皮肤地址缺失。");
                slim = skin.TryGetProperty("variant", out var variant) && string.Equals(variant.GetString(), "SLIM", StringComparison.OrdinalIgnoreCase);
                break;
            }
        string? capeAddress = null;
        if (root.TryGetProperty("capes", out var capes) && capes.ValueKind == JsonValueKind.Array)
            foreach (var cape in capes.EnumerateArray())
                if (cape.TryGetProperty("state", out var state) && state.GetString() == "ACTIVE")
                { if (cape.TryGetProperty("url", out var url)) capeAddress = url.GetString(); break; }
        return await FetchAppearanceAsync(account.Uuid, new(address, slim, capeAddress), OfficialTexture, token, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>从外置验证服务的档案接口读取皮肤；纹理地址由服务在档案属性里给出。</summary>
    public async Task<SkinImage?> FetchYggdrasilAsync(Uri apiRoot, Guid uuid, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var root = apiRoot.ToString().TrimEnd('/') + "/";
        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(root + "sessionserver/session/minecraft/profile/" + uuid.ToString("N")));
        byte[] profile;
        try { profile = await ReadResponseAsync(profileRequest, 256 * 1024, timeout.Token).ConfigureAwait(false); }
        // 部分验证服务对没有皮肤或未注册的档案返回空响应。
        catch (JsonException) { return ClearSkinCache(uuid); }
        TextureProfile textures;
        using (var document = JsonDocument.Parse(profile)) textures = ReadYggdrasilTextures(document.RootElement);
        Uri Resolve(string address)
        {
            if (!Uri.TryCreate(new Uri(root), address, out var texture) || texture.Scheme is not ("http" or "https") || texture.UserInfo.Length != 0
                || (apiRoot.Scheme == Uri.UriSchemeHttps && texture.Scheme != Uri.UriSchemeHttps))
                throw new InvalidDataException("验证服务返回的纹理地址不受支持。");
            return texture;
        }
        return await FetchAppearanceAsync(uuid, textures, Resolve, timeout.Token, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>在 Yggdrasil 档案里找到皮肤纹理地址：properties 里的 textures 属性是 Base64 编码的 JSON。</summary>
    private static TextureProfile ReadYggdrasilTextures(JsonElement profile)
    {
        if (!profile.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Array) return new(null, false, null);
        foreach (var property in properties.EnumerateArray())
        {
            if (!property.TryGetProperty("name", out var name) || name.GetString() != "textures") continue;
            if (!property.TryGetProperty("value", out var value) || value.GetString() is not { Length: > 0 } encoded) continue;
            byte[] decoded;
            try { decoded = Convert.FromBase64String(encoded.Trim()); }
            catch (FormatException) { continue; }
            try
            {
                using var document = JsonDocument.Parse(decoded);
                if (!document.RootElement.TryGetProperty("textures", out var textures)) continue;
                string? skinAddress = null, capeAddress = null;
                var slim = false;
                if (textures.TryGetProperty("SKIN", out var skin))
                {
                    if (skin.TryGetProperty("url", out var url)) skinAddress = url.GetString();
                    slim = skin.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("model", out var model) && model.GetString() == "slim";
                }
                if (textures.TryGetProperty("CAPE", out var cape) && cape.TryGetProperty("url", out var capeUrl)) capeAddress = capeUrl.GetString();
                return new(skinAddress, slim, capeAddress);
            }
            catch (JsonException) { continue; }
        }
        return new(null, false, null);
    }

    /// <summary>没有可用皮肤时清掉旧缓存，避免继续显示上一次的皮肤。</summary>
    private SkinImage? ClearSkinCache(Guid uuid)
    {
        try { File.Delete(CachePath(uuid)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.TraceWarning("旧皮肤缓存未清理：{0}", error.GetType().Name); }
        try { File.Delete(AppearanceCachePath(uuid)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.TraceWarning("旧外观缓存未清理：{0}", error.GetType().Name); }
        return null;
    }

    private static Uri OfficialTexture(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var texture) || texture.Host != "textures.minecraft.net"
            || texture.Scheme is not ("http" or "https") || texture.UserInfo.Length != 0 || !texture.IsDefaultPort
            || !texture.AbsolutePath.StartsWith("/texture/", StringComparison.Ordinal))
            throw new InvalidDataException("纹理地址不是受支持的官方地址。");
        return new UriBuilder(texture) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
    }

    private async Task<SkinImage> FetchAppearanceAsync(Guid uuid, TextureProfile textures, Func<string, Uri> resolve,
        CancellationToken requestToken, CancellationToken cancellationToken)
    {
        var result = new SkinImage(null, null, IsSlim: textures.IsSlim);
        if (!string.IsNullOrEmpty(textures.Skin))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, resolve(textures.Skin));
            var png = await ReadResponseAsync(request, 1024 * 1024, requestToken).ConfigureAwait(false);
            result = await Task.Run(() => DecodeSkin(png) with { IsSlim = textures.IsSlim }, requestToken).ConfigureAwait(false);
        }
        if (!string.IsNullOrEmpty(textures.Cape))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, resolve(textures.Cape));
                var png = await ReadResponseAsync(request, 1024 * 1024, requestToken).ConfigureAwait(false);
                await Task.Run(() => ValidateCape(png), requestToken).ConfigureAwait(false);
                result = result with { CapePng = png };
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is HttpRequestException or IOException or OperationCanceledException or ArgumentException or InvalidOperationException or NotSupportedException or FileFormatException)
            {
                System.Diagnostics.Trace.TraceWarning("披风纹理读取失败：{0}", error.GetType().Name);
                result = result with { CapeMessage = "披风暂时无法读取，请刷新资料重试。" };
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            // 一份原子快照保存皮肤、模型和披风，避免新皮肤与旧披风混用；不保存授权或纹理 URL。
            var path = AppearanceCachePath(uuid);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var data = JsonSerializer.SerializeToUtf8Bytes(new AppearanceCache(result.Png, result.IsSlim, result.CapePng));
                await File.WriteAllBytesAsync(temporary, data, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path, true);
                try { File.Delete(CachePath(uuid)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.TraceWarning("旧皮肤缓存未清理：{0}", error.GetType().Name); }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { result = result with { CacheSaved = false }; }
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
    private string AppearanceCachePath(Guid uuid) => Path.Combine(_cacheDirectory, uuid.ToString("N") + ".appearance.json");
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
    public static SkinImage DecodeSkin(byte[] png)
    {
        if (png.Length > 1024 * 1024) throw new InvalidDataException("皮肤图片过大。");
        using var stream = new MemoryStream(png, false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var width = decoder.Frames[0].PixelWidth;
        var height = decoder.Frames[0].PixelHeight;
        if (decoder is not PngBitmapDecoder || width < 64 || width > 1024 || width % 64 != 0 || (height != width && height * 2 != width))
            throw new InvalidDataException("皮肤图片尺寸或格式无效。");
        var pixels = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Pbgra32, null, 0);
        var face = new byte[8 * 8 * 4]; var hat = new byte[face.Length];
        var scale = width / 64;
        var region = new byte[8 * scale * 8 * scale * 4];
        void ReadFace(int x, byte[] target)
        {
            pixels.CopyPixels(new Int32Rect(x * scale, 8 * scale, 8 * scale, 8 * scale), region, 8 * scale * 4, 0);
            for (var y = 0; y < 8; y++)
                for (var column = 0; column < 8; column++)
                    Buffer.BlockCopy(region, (y * scale * 8 * scale + column * scale) * 4, target, (y * 8 + column) * 4, 4);
        }
        ReadFace(8, face); ReadFace(40, hat);
        if (height * 2 == width)
        {
            var legacyHat = new byte[32 * scale * 32 * scale * 4];
            pixels.CopyPixels(new Int32Rect(32 * scale, 0, 32 * scale, 32 * scale), legacyHat, 32 * scale * 4, 0);
            if (Enumerable.Range(0, legacyHat.Length / 4).All(index => legacyHat[index * 4 + 3] == 255)) Array.Clear(hat);
        }
        for (var i = 0; i < face.Length; i += 4)
            for (var channel = 0; channel < 4; channel++)
                face[i + channel] = (byte)Math.Min(255, hat[i + channel] + (face[i + channel] * (255 - hat[i + 3]) + 127) / 255);
        var head = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Pbgra32, null, face, 32);
        head.Freeze();
        var slim = false;
        if (height == width)
        {
            bool Transparent(int x, int y, int w, int h)
            {
                var unused = new byte[w * scale * h * scale * 4];
                pixels.CopyPixels(new Int32Rect(x * scale, y * scale, w * scale, h * scale), unused, w * scale * 4, 0);
                return Enumerable.Range(0, unused.Length / 4).All(index => unused[index * 4 + 3] == 0);
            }
            slim = Transparent(54, 20, 2, 12) && Transparent(50, 16, 2, 4)
                && Transparent(46, 52, 2, 12) && Transparent(42, 48, 2, 4);
        }
        return new(head, png, IsSlim: slim);
    }

    private static void ValidateCape(byte[] png)
    {
        if (png.Length > 1024 * 1024) throw new InvalidDataException("披风图片过大。");
        using var stream = new MemoryStream(png, false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if (decoder is not PngBitmapDecoder || frame.PixelWidth < 64 || frame.PixelWidth > 1024 || frame.PixelWidth % 64 != 0 || frame.PixelHeight * 2 != frame.PixelWidth)
            throw new InvalidDataException("披风图片尺寸或格式无效。");
    }
}
