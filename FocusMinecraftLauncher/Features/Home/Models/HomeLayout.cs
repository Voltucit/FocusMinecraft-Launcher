namespace FocusMinecraftLauncher.Features.Home.Models;

/// <summary>主页布局的稳定配置值，不使用中文显示名称作为存储键。</summary>
public enum HomeLayout { Classic, Minimal, Centered }

public sealed record HomeLayoutOption(HomeLayout Layout, string Name, string Description, bool IsDefault = false);
