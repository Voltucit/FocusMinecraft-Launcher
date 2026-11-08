namespace FocusMinecraftLauncher.Features.Settings.ViewModels;

/// <summary>壁纸列表的一行，固定最多三个卡片，用于让 ListBox 只虚拟化行。</summary>
public sealed class WallpaperRowViewModel(IReadOnlyList<WallpaperChoiceViewModel> items)
{
    public IReadOnlyList<WallpaperChoiceViewModel> Items { get; } = items;
}
