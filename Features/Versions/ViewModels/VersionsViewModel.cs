using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using FMLauncher.Common;
using FMLauncher.Features.Instances.Services;
using FMLauncher.CoreIntegration.Adapters;
using FMLauncher.CoreIntegration.Models;
using FMLauncher.Infrastructure.Configuration;
namespace FMLauncher.Features.Versions.ViewModels;

/// <summary>只管理共享根目录下的本地版本，不创建 SDK 独立实例目录。</summary>
public sealed class VersionsViewModel : PageViewModel
{
    private readonly InstanceCatalog _catalog;
    private readonly SettingsStore _settings;
    private VersionItem? _selected;
    public ReadOnlyObservableCollection<VersionItem> Versions => _catalog.Versions;
    public VersionItem? Selected { get => _selected; set { if (value != null || !_catalog.IsRefreshing) Set(ref _selected, value); } }
    public AsyncCommand Reload { get; }
    public AsyncCommand Use { get; }
    public AsyncCommand OpenFolder { get; }
    public AsyncCommand OpenMods { get; }
    public VersionsViewModel(InstanceCatalog catalog, SettingsStore settings)
    {
        _catalog = catalog; _settings = settings; catalog.CatalogChanged += OnCatalogChanged;
        Reload = Command(ReloadAsync);
        Use = Command(ct => { catalog.Select(Require()); Status = "已选择这个版本，可以直接开始游戏。"; return Task.CompletedTask; });
        OpenFolder = Command(ct => { Open(false); return Task.CompletedTask; });
        OpenMods = Command(ct => { Open(true); return Task.CompletedTask; });
        void Open(bool mods)
        {
            var version = Require().Id;
            var root = Path.GetFullPath(Path.Combine(settings.Current.GameRoot, "versions"));
            var path = Path.GetFullPath(Path.Combine(root, version));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("版本目录越界。");
            if (!Directory.Exists(path)) throw new InvalidDataException("版本目录不存在，请刷新列表。");
            if (mods) { path = Path.Combine(path, "mods"); Directory.CreateDirectory(path); }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }
    public VersionItem Require() => Selected ?? throw new InvalidOperationException("请先选择已安装版本。");
    public async Task ReloadAsync(CancellationToken ct)
    {
        await _catalog.RefreshAsync(ct);
        Status = "游戏列表已更新。";
    }
    private void OnCatalogChanged(InstancesChanged change)
    {
        if (!change.Root.Equals(_catalog.Root, StringComparison.OrdinalIgnoreCase)) return;
        var id = Selected?.Id;
        Set(ref _selected, Versions.FirstOrDefault(v => v.Id == id) ?? _catalog.Selected, nameof(Selected));
    }
    public override void Dispose() { _catalog.CatalogChanged -= OnCatalogChanged; base.Dispose(); }
}
