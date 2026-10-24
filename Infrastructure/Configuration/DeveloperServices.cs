using System.Text.Json;
namespace FMLauncher.Infrastructure.Configuration;
/// <summary>开发者公共应用标识，与用户设置及壁纸凭据隔离；不加载任何 Client Secret 或私人服务密钥。</summary>
public sealed class DeveloperServices
{
#if DEBUG
    public const string EnvironmentName = "development";
#else
    public const string EnvironmentName = "production";
#endif
    public string? MicrosoftClientId { get; private init; }
    public bool MicrosoftLoginConfigured => Guid.TryParse(MicrosoftClientId, out var id) && id != Guid.Empty;
    /// <summary>按构建环境读取嵌入的公开配置；缺失或无效时保留未配置状态，不借用其他启动器标识。</summary>
    public static DeveloperServices Load()
    {
        try
        {
            using var input = typeof(DeveloperServices).Assembly.GetManifestResourceStream("FMLauncher.Developer.services." + EnvironmentName + ".json");
            if (input == null) return new();
            using var json = JsonDocument.Parse(input);
            return new() { MicrosoftClientId = json.RootElement.TryGetProperty("MicrosoftPublicClientId", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null };
        }
        catch (JsonException) { return new(); }
    }
}
