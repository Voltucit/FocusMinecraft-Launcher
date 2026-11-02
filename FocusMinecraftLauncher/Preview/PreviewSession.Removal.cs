using System.IO;
using FMCore.Versions;
using FocusMinecraftLauncher.Infrastructure.Runtime;
using Microsoft.VisualBasic.FileIO;

namespace FocusMinecraftLauncher.Preview;

public sealed partial class PreviewSession
{
    private bool _removingInstance;
    private Task? _instanceRemoval;
    public bool IsRemovingInstance => _removingInstance;

    /// <summary>确认后将当前版本目录交给 Windows 回收站；共享工作目录与其他游戏不在删除范围内。</summary>
    public Task RemoveInstanceAsync(PreviewInstance instance)
    {
        if (IsBusy || IsLoading || !Instances.Contains(instance))
            throw new InvalidOperationException("当前无法删除游戏，请等待正在执行的操作完成后重试。");
        _removingInstance = true;
        _instanceRemoval = RecycleInstanceAsync(instance);
        Signal();
        return _instanceRemoval;
    }

    private async Task RecycleInstanceAsync(PreviewInstance instance)
    {
        try
        {
            // 验证最终绝对路径及目录链接，始终使用版本目录而不是可能共享的 GameDirectory。
            ModpackArchive.EnsureInstanceWritePath(instance);
            var folder = Path.GetFullPath(instance.Folder);
            if (File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("不能通过目录链接删除游戏。");
            var reader = new MinecraftVersionReader();
            var all = await reader.GetVersionsAsync(instance.Root);
            if (all.Any(v => v.Id != instance.Id && reader.GetInheritanceChain(v, all).Any(parent => parent.Id == instance.Id)))
                throw new InvalidOperationException("其他游戏仍依赖此版本，请先在这些游戏中执行“检查并补全文件”，完成独立转换后再删除。");

            await Task.Run(() => FileSystem.DeleteDirectory(folder, UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin, UICancelOption.ThrowException));
            if (Directory.Exists(folder)) throw new IOException("游戏文件夹仍然存在，未完成删除。");

            var key = InstanceStore.Key(instance.Root, instance.Id);
            foreach (var item in Instances.Where(item => string.Equals(InstanceStore.Key(item.Root, item.Id), key, StringComparison.OrdinalIgnoreCase)).ToArray())
                Instances.Remove(item);
            if (CurrentInstance is { } selected && string.Equals(InstanceStore.Key(selected.Root, selected.Id), key, StringComparison.OrdinalIgnoreCase))
                CurrentInstance = null;
            if (string.Equals(instance.Root, SelectedRoot, StringComparison.OrdinalIgnoreCase)) await RefreshInstancesAsync();
            NotifyTask(instance.Name + " 删除成功，已移入回收站", false);
        }
        finally
        {
            _removingInstance = false;
            Signal();
        }
    }
}
