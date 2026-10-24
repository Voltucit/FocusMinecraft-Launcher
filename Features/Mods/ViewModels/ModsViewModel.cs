using System.Collections.ObjectModel;
using FMLauncher.Common;
using FMLauncher.CoreIntegration.Adapters;
using FMLauncher.CoreIntegration.Models;
using FMLauncher.Features.Versions.ViewModels;
namespace FMLauncher.Features.Mods.ViewModels;

/// <summary>资源查询和版本 mods 目录管理，不向 SDK 固定的独立实例目录安装内容。</summary>
public sealed class ModsViewModel : PageViewModel
{
    private string _source = "Modrinth", _query = "", _game = "", _loader = "";
    private ResourceItem? _selected;
    private int _offset;
    public ObservableCollection<ResourceItem> Results { get; } = [];
    public VersionsViewModel Targets { get; }
    public string Source { get => _source; set { Set(ref _source, value); _offset = 0; Results.Clear(); Selected = null; } }
    public string Query { get => _query; set => Set(ref _query, value); }
    public string Game { get => _game; set => Set(ref _game, value); }
    public string Loader { get => _loader; set => Set(ref _loader, value); }
    public ResourceItem? Selected { get => _selected; set => Set(ref _selected, value); }
    public AsyncCommand Search { get; }
    public AsyncCommand Next { get; }
    public AsyncCommand Previous { get; }
    public ModsViewModel(ResourceAdapter adapter, VersionsViewModel targets)
    {
        Targets = targets;
        Search = Command(async ct => { _offset = 0; await SearchAsync(ct); });
        Next = Command(async ct => { _offset += 20; await SearchAsync(ct); });
        Previous = Command(async ct => { _offset = Math.Max(0, _offset - 20); await SearchAsync(ct); });
        async Task SearchAsync(CancellationToken ct)
        {
            var items = await adapter.SearchAsync(Query, Source, _offset, Game, Loader, ct);
            Results.Clear(); foreach (var item in items) Results.Add(item);
            Selected = Results.FirstOrDefault(); Status = $"第 {_offset / 20 + 1} 页 · {items.Count} 项。下载内容请自行放入所选版本的 mods 目录。";
        }
    }
}
