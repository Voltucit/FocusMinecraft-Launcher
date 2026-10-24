using FMLauncher.Common;
namespace FMLauncher.CoreIntegration.Models;
/// <summary>版本展示快照；技术错误码仅供诊断，玩家界面使用中文标签。</summary>
public sealed class VersionItem(string id, string type, DateTimeOffset? released = null,
    bool installed = false, string? errorCode = null, string? minecraftVersion = null, string? loader = null) : ObservableObject
{
    private bool _installed = installed;
    public string Id { get; } = id;
    public string Type { get; } = type;
    public DateTimeOffset? Released { get; } = released;
    public string? ErrorCode { get; } = errorCode;
    public string? MinecraftVersion { get; } = minecraftVersion;
    public string? Loader { get; } = loader;
    public bool IsInstalled { get => _installed; set { if (Set(ref _installed, value)) { Changed(nameof(InstallState)); Changed(nameof(Label)); } } }
    public string TypeLabel => Loader ?? (Type switch
    { "release" => "正式版", "snapshot" => "快照版", "old_beta" => "远古 Beta", "old_alpha" => "远古 Alpha", _ => "本地版本" });
    public string Summary => ErrorCode != null ? "版本信息需要检查" : string.IsNullOrWhiteSpace(MinecraftVersion) ? TypeLabel : $"Minecraft {MinecraftVersion} · {TypeLabel}";
    public string InstallState => IsInstalled ? "已安装" : "可安装";
    public string Label => $"{Id} · {Summary}";
    public bool SameMetadata(VersionItem other) => Id == other.Id && Type == other.Type && Released == other.Released &&
        ErrorCode == other.ErrorCode && MinecraftVersion == other.MinecraftVersion && Loader == other.Loader;
}
