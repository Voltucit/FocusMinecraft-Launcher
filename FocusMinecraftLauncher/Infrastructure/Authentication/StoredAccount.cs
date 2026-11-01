using System.Text.Json;
using System.Text.Json.Serialization;

namespace FocusMinecraftLauncher.Infrastructure.Authentication;

/// <summary>账户资料与当前 Windows 用户加密的授权缓存；不含 Microsoft 密码或明文令牌。</summary>
public sealed class StoredAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = "Offline";
    public string Name { get; set; } = "";
    public Guid Uuid { get; set; }
    public string ProtectedSession { get; set; } = "";
    public DateTimeOffset? LastAuthenticatedAt { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalFields { get; set; }
}
