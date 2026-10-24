using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using FMLauncher.Common;
using FMLauncher.CoreIntegration.Adapters;
using FMLauncher.CoreIntegration.Models;
using FMLauncher.Infrastructure.Configuration;
namespace FMLauncher.Features.Instances.Services;

public sealed record InstancesChanged(string Root, string? InstalledVersion, Guid? OperationId);

/// <summary>共享本地版本状态。只调用核心查询；不解析、写入或迁移游戏元数据。</summary>
public sealed class InstanceCatalog : ObservableObject, IDisposable
{
    private readonly VersionAdapter _adapter;
    private readonly SettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    private readonly ObservableCollection<VersionItem> _items = [];
    private readonly Dictionary<string, int> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _selections = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _completed = [];
    private readonly Queue<Guid> _completionOrder = new();
    private readonly SemaphoreSlim _queries = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private string _root;
    private VersionItem? _selected;
    private bool _refreshing;
    private long _rootEpoch;
    public ReadOnlyObservableCollection<VersionItem> Versions { get; }
    public string Root => _root;
    public VersionItem? Selected => _selected;
    public bool IsRefreshing { get => _refreshing; private set => Set(ref _refreshing, value); }
    public event Action<InstancesChanged>? CatalogChanged;
    public InstanceCatalog(VersionAdapter adapter, SettingsStore settings, Dispatcher dispatcher)
    {
        _adapter = adapter; _settings = settings; _dispatcher = dispatcher;
        _root = Normalize(settings.Current.GameRoot); _selections[_root] = settings.Current.VersionId;
        Versions = new(_items);
    }
    private static string Normalize(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    private static bool SameRoot(string left, string right) => StringComparer.OrdinalIgnoreCase.Equals(Normalize(left), Normalize(right));
    private Task<T> UiAsync<T>(Func<T> work) => _dispatcher.CheckAccess() ? Task.FromResult(work()) : _dispatcher.InvokeAsync(work, DispatcherPriority.DataBind, _lifetime.Token).Task;

    /// <summary>明确选择已有版本，保存成功后才发布选择，避免配置失败造成显示和启动目标不一致。</summary>
    public void Select(VersionItem version)
    {
        _dispatcher.VerifyAccess();
        if (!SameRoot(_root, _settings.Current.GameRoot)) return;
        var item = _items.FirstOrDefault(v => v.Id == version.Id);
        if (item == null) return;
        if (_selected?.Id == item.Id && ReferenceEquals(_selected, item)) return;
        var previous = _settings.Current.VersionId;
        _settings.Current.VersionId = item.Id;
        try { _settings.Save(); } catch { _settings.Current.VersionId = previous; throw; }
        _selections[_root] = item.Id;
        Set(ref _selected, item, nameof(Selected));
    }
    /// <summary>切目录立即失效旧视图；旧目录或旧请求的晚到结果不能覆盖新选择。</summary>
    public async Task ChangeRootAsync(string root, CancellationToken ct)
    {
        var normalized = Normalize(root);
        await UiAsync(() =>
        {
            if (!SameRoot(_root, normalized))
            {
                _selections[_root] = _selected?.Id;
                _root = normalized; _rootEpoch++; _items.Clear();
                Set(ref _selected, null, nameof(Selected)); IsRefreshing = _pending.GetValueOrDefault(_root) > 0; Changed(nameof(Root));
                CatalogChanged?.Invoke(new(_root, null, null));
            }
            return true;
        });
        await RefreshRootAsync(normalized, null, null, ct);
    }
    public Task RefreshAsync(CancellationToken ct) => RefreshRootAsync(_root, null, null, ct);

    /// <summary>仅在安装器真正返回成功后调用；操作 ID 去重，刷新不使用已完成安装命令的取消令牌。</summary>
    public async Task InstallationSucceededAsync(string root, string version, Guid operationId)
    {
        var first = await UiAsync(() =>
        {
            if (!_completed.Add(operationId)) return false;
            _completionOrder.Enqueue(operationId);
            while (_completionOrder.Count > 256) _completed.Remove(_completionOrder.Dequeue());
            return true;
        });
        if (first) await RefreshRootAsync(Normalize(root), version, operationId, _lifetime.Token);
    }
    /// <summary>查询和发布共用串行门，避免两个成功安装互相覆盖；集合发布全部在 UI 线程执行。</summary>
    private async Task RefreshRootAsync(string root, string? installed, Guid? operationId, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var token = linked.Token;
        var request = await UiAsync(() =>
        {
            _pending[root] = _pending.GetValueOrDefault(root) + 1;
            if (SameRoot(root, _root)) IsRefreshing = true;
            return _rootEpoch;
        });
        var entered = false;
        try
        {
            await _queries.WaitAsync(token); entered = true;
            var snapshot = await _adapter.InstalledAsync(root, token).ConfigureAwait(false);
            await UiAsync(() =>
            {
                if (!SameRoot(root, _root) || !SameRoot(root, _settings.Current.GameRoot) || request != _rootEpoch)
                { CatalogChanged?.Invoke(new(root, installed, operationId)); return false; }
                var selection = _selected?.Id ?? _selections.GetValueOrDefault(root);
                var old = _items.ToDictionary(v => v.Id, StringComparer.OrdinalIgnoreCase);
                var desired = snapshot.Select(incoming => old.TryGetValue(incoming.Id, out var existing) && existing.SameMetadata(incoming) ? existing : incoming).ToArray();
                var retained = desired.ToHashSet();
                foreach (var stale in _items.Where(v => !retained.Contains(v)).ToArray()) _items.Remove(stale);
                for (var index = 0; index < desired.Length; index++)
                {
                    var item = desired[index]; var found = _items.IndexOf(item);
                    if (found >= 0) { if (found != index) _items.Move(found, index); }
                    else _items.Insert(index, item);
                }
                var next = _items.FirstOrDefault(v => v.Id == selection) ?? _items.FirstOrDefault(v => v.Id == installed) ?? _items.FirstOrDefault();
                if (_settings.Current.VersionId != (next?.Id ?? ""))
                {
                    var previous = _settings.Current.VersionId; _settings.Current.VersionId = next?.Id ?? "";
                    try { _settings.Save(); } catch { _settings.Current.VersionId = previous; throw; }
                }
                Set(ref _selected, next, nameof(Selected));
                _selections[root] = next?.Id;
                CatalogChanged?.Invoke(new(root, installed, operationId));
                return true;
            });
        }
        finally
        {
            if (entered) _queries.Release();
            await UiAsync(() =>
            {
                _pending[root] = Math.Max(0, _pending.GetValueOrDefault(root) - 1);
                if (SameRoot(root, _root)) IsRefreshing = _pending[root] > 0;
                return true;
            });
        }
    }
    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); _queries.Dispose(); }
}
