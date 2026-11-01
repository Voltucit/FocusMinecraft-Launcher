using FocusMinecraftLauncher.Common;

namespace FocusMinecraftLauncher.Features.Home.Models;

/// <summary>版本选择页的已保存游戏目录；实际版本由共享目录服务读取。</summary>
public sealed class InstanceFolderOption(string name, string fullPath, bool usesPreviewInstances = false) : ObservableObject
{
    private string _fullPath = fullPath;
    public string Name { get; } = name;
    public string FullPath { get => _fullPath; internal set => Set(ref _fullPath, value); }
    public bool UsesPreviewInstances { get; } = usesPreviewInstances;
}
