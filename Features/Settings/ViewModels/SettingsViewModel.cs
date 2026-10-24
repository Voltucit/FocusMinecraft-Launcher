using System.IO;
using Microsoft.Win32;
using FMLauncher.Common;
using FMLauncher.Infrastructure.Configuration;
namespace FMLauncher.Features.Settings.ViewModels;

public sealed class SettingsViewModel : PageViewModel
{
    private readonly SettingsStore _store;
    public LauncherSettings Settings { get; private set; }
    public void RefreshDraft() { Settings = _store.Snapshot(); Changed(nameof(Settings)); }
    public AsyncCommand Save { get; }
    public AsyncCommand BrowseRoot { get; }
    public AsyncCommand BrowseJava { get; }
    public AsyncCommand ImportLegacy { get; }
    public SettingsViewModel(SettingsStore settings, Func<bool> canChangeRoot, Func<CancellationToken, Task> reload)
    {
        _store = settings; Settings = settings.Snapshot();
        Save = Command(async ct =>
        {
            if (!Settings.GameRoot.Equals(settings.Current.GameRoot, StringComparison.OrdinalIgnoreCase) && !canChangeRoot()) throw new InvalidOperationException("请先停止游戏及当前文件任务，再更改游戏根目录。");
            settings.UpdatePreferences(Settings); await reload(ct);
            Status = "设置已保存。";
        });
        BrowseRoot = Command(ct => { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog() == true) { Settings.GameRoot = dialog.FolderName; Changed(nameof(Settings)); } return Task.CompletedTask; });
        BrowseJava = Command(ct => { var dialog = new OpenFileDialog { Filter = "Java|java.exe;javaw.exe" }; if (dialog.ShowDialog() == true) { Settings.JavaPath = dialog.FileName; Changed(nameof(Settings)); } return Task.CompletedTask; });
        ImportLegacy = Command(async ct =>
        {
            if (!canChangeRoot()) throw new InvalidOperationException("请先停止游戏及当前文件任务再导入旧配置。");
            var dialog = new OpenFolderDialog { Title = "选择含 LauncherSetting.json 的旧 .minecraft 目录" };
            if (dialog.ShowDialog() != true) return;
            settings.ImportFromGameRoot(dialog.FolderName); RefreshDraft(); await reload(ct);
            Status = settings.MigrationNotice + " 请重启以完成离线账号迁移及壁纸草稿刷新。";
        });
    }
}

