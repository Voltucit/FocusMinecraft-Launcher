using System.Collections.ObjectModel;
using FMLauncher.Common;
using FMLauncher.Infrastructure.Logging;
using FMLauncher.Features.Instances.Services;
using FMLauncher.CoreIntegration.Adapters;
using FMLauncher.CoreIntegration.Models;
using FMLauncher.Features.Downloads.Services;
using FMLauncher.Infrastructure.Configuration;
namespace FMLauncher.Features.Downloads.ViewModels;

public sealed class DownloadsViewModel : PageViewModel
{
    private readonly InstanceCatalog _instances;
    private VersionItem? _selected;
    private string _kind = "Vanilla";
    private string? _loader;
    private string _filter = "release";
    private string _profileName = "";
    public string ProfileName { get => _profileName; set => Set(ref _profileName, value); }
    private IReadOnlyList<VersionItem> _catalog = [];
    public ObservableCollection<VersionItem> Versions { get; } = [];
    public ObservableCollection<string> Loaders { get; } = [];
    public VersionItem? Selected { get => _selected; set => Set(ref _selected, value); }
    public string Kind { get => _kind; set { Set(ref _kind, value); Loaders.Clear(); Loader = null; } }
    public string? Loader { get => _loader; set => Set(ref _loader, value); }
    public string Filter { get => _filter; set { Set(ref _filter, value); ApplyFilter(); } }
    public OperationService Operations { get; }
    public AsyncCommand Reload { get; }
    public AsyncCommand QueryLoaders { get; }
    public AsyncCommand Install { get; }
    public AsyncCommand Cancel { get; }
    public DownloadsViewModel(VersionAdapter versions, InstallationAdapter installer, SettingsStore settings, OperationService operations, InstanceCatalog instances)
    {
        Operations = operations; _instances = instances; instances.CatalogChanged += OnInstancesChanged;
        Reload = Command(async ct => { _catalog = await versions.AvailableAsync(settings.Current.GameRoot, ct); ApplyFilter(); Status = $"已读取 {_catalog.Count} 个版本，按发布时间排序。"; });
        QueryLoaders = Command(async ct =>
        {
            var game = Selected?.Id ?? throw new InvalidOperationException("请先选择 Minecraft 版本。");
            var items = await installer.LoadersAsync(settings.Current.GameRoot, game, Kind, ct);
            Loaders.Clear(); foreach (var item in items) Loaders.Add(item);
            Loader = Loaders.FirstOrDefault(); Status = items.Count == 0 && Kind != "Vanilla" ? "该组合没有可用的加载器版本。" : "组件列表已更新。";
        });
        Install = Command(async ct =>
        {
            var version = Selected?.Id ?? throw new InvalidOperationException("请先选择游戏版本。");
            if (Kind != "Vanilla" && Loader == null) throw new InvalidOperationException("请先查询并选择加载器版本。");
            var snapshot = settings.Snapshot(); var kind = Kind; var loader = Loader; var name = ProfileName;
            var installed = await operations.RunAsync("安装 " + version, async (p, token) =>
            {
                return await installer.InstallAsync(snapshot, version, kind, loader, name, p, token);
            }, ct);
            // SDK await 成功才发布完成；列表刷新失败不能把已完成的安装误报成安装失败。
            try
            {
                await instances.InstallationSucceededAsync(snapshot.GameRoot, installed, Guid.NewGuid());
                Status = "安装完成，游戏列表已自动更新。";
            }
            catch (OperationCanceledException) { Status = "安装已完成。"; }
            catch (Exception ex) { DiagnosticLog.Record("RefreshAfterInstall", ex); Status = "安装已完成，但游戏列表暂时无法更新，请检查游戏目录是否可访问。"; }
        });
        Cancel = Command(ct => { operations.Cancel(); return Task.CompletedTask; });
    }
    private void OnInstancesChanged(InstancesChanged change)
    { if (change.Root.Equals(_instances.Root, StringComparison.OrdinalIgnoreCase)) UpdateInstalledState(); }
    private void UpdateInstalledState()
    {
        var installed = _instances.Versions.Select(v => v.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _catalog) item.IsInstalled = installed.Contains(item.Id);
    }
    public override void Dispose() { _instances.CatalogChanged -= OnInstancesChanged; base.Dispose(); }
    private void ApplyFilter()
    {
        var selected = Selected?.Id;
        Versions.Clear(); foreach (var item in _catalog.Where(v => Filter == "all" || v.Type == Filter)) Versions.Add(item);
        Selected = Versions.FirstOrDefault(v => v.Id == selected) ?? Versions.FirstOrDefault();
        UpdateInstalledState();
    }
}

