namespace FocusMinecraftLauncher.Features.More.Models;

/// <summary>特别鸣谢条目：头像、名字、介绍与个人主页。</summary>
public sealed record Acknowledgment(string Name, string Avatar, string Introduction, string Page)
{
    /// <summary>有头像地址时显示图片，否则留出占位圆。</summary>
    public bool HasAvatar => !string.IsNullOrWhiteSpace(Avatar);
    /// <summary>没有主页时不显示“主页”按钮。</summary>
    public bool HasPage => !string.IsNullOrWhiteSpace(Page);
}
