using System.IO;
using System.Text.Json;
using FMLauncher.Infrastructure.Networking;
namespace FMLauncher.Appearance.Wallpapers.Providers;

public sealed class ApiWallpaperProvider(PublicImageClient client) : IWallpaperProvider
{
    /// <summary>参数、请求头和 Key 整体来自 DPAPI；JSON 路径支持 data.url 与 data.0.url，不执行表达式。</summary>
    public async Task<byte[]?> LoadAsync(WallpaperSettings settings, string credentialsJson, CancellationToken cancellationToken)
    {
        using var secret = JsonDocument.Parse(string.IsNullOrWhiteSpace(credentialsJson) ? "{}" : credentialsJson);
        var uri = PublicImageClient.Validate(settings.ApiUrl);
        if (!string.IsNullOrEmpty(uri.Query)) throw new InvalidDataException("请把 URL 查询参数放入加密请求参数字段。");
        Dictionary<string, string> ReadMap(string name)
        {
            if (!secret.RootElement.TryGetProperty(name, out var field)) return new();
            var map = field.Deserialize<Dictionary<string, string>>() ?? new();
            if (map.Count > 24 || map.Any(v => v.Key.Length > 128 || v.Value.Length > 4096)) throw new InvalidDataException("API 参数数量或长度超出限制。");
            return map;
        }
        var query = ReadMap("parameters"); var headers = ReadMap("headers");
        if (secret.RootElement.TryGetProperty("key", out var key) && !string.IsNullOrEmpty(key.GetString())) headers[settings.KeyHeader] = key.GetString()!;
        if (query.Count > 0) uri = new UriBuilder(uri) { Query = string.Join("&", query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))) }.Uri;
        if (settings.Source == "JsonApi")
        {
            var jsonBytes = await client.GetAsync(uri, headers, 1024 * 1024, cancellationToken);
            using var json = JsonDocument.Parse(jsonBytes, new() { MaxDepth = 32 });
            var node = json.RootElement;
            var parts = settings.JsonPath.Split('.');
            if (parts.Length > 16 || settings.JsonPath.Length > 256) throw new InvalidDataException("JSON 字段路径过长。");
            foreach (var part in parts)
            {
                if (node.ValueKind == JsonValueKind.Array && int.TryParse(part, out var index) && index >= 0 && index < node.GetArrayLength()) node = node[index];
                else if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(part, out var child)) node = child;
                else throw new InvalidDataException("JSON 字段路径未找到图片地址。");
            }
            if (node.ValueKind != JsonValueKind.String) throw new InvalidDataException("JSON 目标字段必须为图片 URL 字符串。");
            uri = PublicImageClient.Validate(node.GetString() ?? "");
            // 图片可能来自不同 CDN；绝不能把 API Key 或自定义头转发给 JSON 返回的地址。
            headers = new();
        }
        return await client.GetAsync(uri, headers, 20 * 1024 * 1024, cancellationToken);
    }
}

