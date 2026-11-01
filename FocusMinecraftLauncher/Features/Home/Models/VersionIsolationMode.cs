namespace FocusMinecraftLauncher.Features.Home.Models;
/// <summary>只决定存档、Mod 与游戏配置目录，不改变版本声明、客户端和公共依赖的安装位置。</summary>
public enum VersionIsolationMode { All, ModdedOnly, None }
public sealed record VersionIsolationOption(VersionIsolationMode Mode, string Name);
