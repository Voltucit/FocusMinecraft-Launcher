using FocusMinecraftLauncher.Common;

namespace FocusMinecraftLauncher.Features.Home.Models;

/// <summary>版本选择页的已保存游戏目录；实际版本由共享目录服务读取。</summary>
public sealed class InstanceFolderOption(string name, string fullPath, bool usesPreviewInstances = false, bool canRemove = true) : ObservableObject
{
    private string _fullPath = fullPath;
    public string Name { get; } = name;
    public string FullPath { get => _fullPath; internal set => Set(ref _fullPath, value); }
    public bool UsesPreviewInstances { get; } = usesPreviewInstances;
    /// <summary>当前游戏目录与自动发现的官方目录不提供删除；删除只移除记录，不动磁盘文件。</summary>
    public bool CanRemove { get; } = canRemove;
}
