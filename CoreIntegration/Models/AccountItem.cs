namespace FMLauncher.CoreIntegration.Models;

public sealed record AccountItem(Guid Id, string Name, string Kind, bool IsDefault, Guid Uuid, bool Entitled)
{
    public string KindLabel => Kind == "Offline" ? "离线账号" : "Microsoft 正版账号";
    public string Label => $"{Name} · {KindLabel}" + (IsDefault ? " · 默认" : "");
}
