namespace FocusMinecraftLauncher.Preview;

public sealed partial class PreviewSession
{
    public bool IsInstallingLauncherUpdate { get; private set; }
    /// <summary>空闲时锁定游戏操作，避免更新交接期间开始新的下载、删除或游戏进程。</summary>
    public bool TryBeginLauncherUpdate()
    {
        if (IsBusy || IsLoading || IsAuthenticating) return false;
        IsInstallingLauncherUpdate = true; Signal(); return true;
    }
    public void EndLauncherUpdate() { IsInstallingLauncherUpdate = false; Signal(); }
}
