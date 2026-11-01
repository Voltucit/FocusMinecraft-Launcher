using System.IO;
using System.Windows.Input;
using System.Collections.Specialized;
using FocusMinecraftLauncher.Preview;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Features.Downloads.Models;
using FocusMinecraftLauncher.Infrastructure.Runtime;
namespace FocusMinecraftLauncher.Features.Downloads.ViewModels;

public sealed class VanillaVersionRowViewModel(GameVersionItem game, Action<GameVersionItem> configure) : ObservableObject
{
    private bool _installed;
    public GameVersionItem Game { get; } = game;
    public UiCommand Configure { get; } = new(_ => configure(game));
    public bool IsInstalled { get=>_installed;internal set {if(Set(ref _installed,value)){Changed(nameof(ActionText));Changed(nameof(AccessibleName));}} }
    public string ActionText => IsInstalled?"已安装":"安装";
    public string AccessibleName => "Minecraft "+Game.Number+"，"+ActionText;
}
public sealed class VanillaGroupViewModel(string title, IEnumerable<VanillaVersionRowViewModel> rows, bool expanded) : ObservableObject
{
    private bool _expanded = expanded;
    public string Title { get; } = title;
    public IReadOnlyList<VanillaVersionRowViewModel> Rows { get; } = rows.ToArray();
    public string Count => Rows.Count + " 个版本";
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
}
public sealed class VanillaCatalogViewModel : ObservableObject,IDisposable
{
    public IReadOnlyList<VanillaGroupViewModel> Groups { get; private set; } = [];
    public DownloadScrollPosition Scroll { get; } = new();
    private readonly Action<GameVersionItem> _configure;
    private readonly PreviewSession _session;
    public VanillaCatalogViewModel(PreviewSession session,Action<GameVersionItem> configure)
    {_session=session;_configure=configure;session.Instances.CollectionChanged+=InstancesChanged;}
    private void InstancesChanged(object? sender,NotifyCollectionChangedEventArgs e)=>UpdateInstalledStates();
    /// <summary>只使用当前游戏目录中安装成功的共享实例，不按下载百分比推断安装状态。</summary>
    private void UpdateInstalledStates()
    {
        var installed=_session.Instances.Where(instance=>string.Equals(instance.Root,_session.SelectedRoot,StringComparison.OrdinalIgnoreCase)).Select(instance=>instance.Version).ToHashSet(StringComparer.Ordinal);
        foreach(var row in Groups.SelectMany(group=>group.Rows))row.IsInstalled=installed.Contains(row.Game.Number);
    }
    public async Task LoadAsync(CancellationToken token)
    {
        var versions = await FMCore.Installer.MinecraftInstaller.GetVersionsAsync(token);
        var jokes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "1.RV-Pre1", "3D Shareware v1.34", "20w14infinite", "22w13oneblockatatime", "23w13a_or_b", "24w14potato", "25w14craftmine" };
        var games = versions.Select(v => new GameVersionItem(v.Id, v.Id, jokes.Contains(v.Id) ? "愚人节版" : v.Type switch { "release" => "正式版", "snapshot" => "预览版", _ => "远古版" }, v.Type, DateOnly.FromDateTime(v.ReleaseTime.Date))).ToArray();
        VanillaVersionRowViewModel Row(GameVersionItem game) => new(game, _configure);
        Groups = [new("最新版本", games.Where(g => g.Kind == "正式版").Take(1).Concat(games.Where(g => g.Kind == "预览版").Take(1)).Select(Row), true),
            ..new[] { "正式版", "预览版", "远古版", "愚人节版" }.Select(kind => new VanillaGroupViewModel(kind, games.Where(g => g.Kind == kind).Select(Row), false))];
        Changed(nameof(Groups));
        UpdateInstalledStates();
    }
    public void Dispose()=>_session.Instances.CollectionChanged-=InstancesChanged;
}
public sealed record ComponentVersionItem(string Version, string Date, string Stability, string[] CompatiblePrimaries)
{
    public string? ForgeBuild {get;init;}
    public bool IsOptiFine {get;init;}
    public string ForgeNote=>!IsOptiFine?"":ForgeBuild==null?"Forge 兼容信息未提供":ForgeBuild=="N/A"?"不支持 Forge":"官网标注 Forge "+ForgeBuild;
}
public sealed class ComponentChoiceViewModel : ObservableObject
{
    private readonly ComponentGroupViewModel _group;
    public ComponentVersionItem Option { get; }
    public bool IsSelected => ReferenceEquals(_group.Selected, Option);
    public bool CanChoose => _group.CanChoose(Option);
    public string Hint => _group.ChoiceHint(Option);
    public UiCommand Choose { get; }
    public ComponentChoiceViewModel(ComponentGroupViewModel group, ComponentVersionItem option)
    { _group = group; Option = option; Choose = new(_ => group.Choose(option), _ => CanChoose); }
}
public sealed class ComponentGroupViewModel : ObservableObject
{
    private readonly VanillaInstallViewModel _owner;
    private bool _expanded;
    public string Name { get; }
    /// <summary>使用程序内嵌的对应组件图标，不从网络加载图片。</summary>
    public Uri Icon { get; }
    public string Availability { get; private set; }
    public IReadOnlyList<ComponentChoiceViewModel> Choices { get; private set; }
    public ComponentVersionItem? Selected { get; internal set; }
    public bool HasOptions => Choices.Count > 0;
    public bool HasSelection => Selected != null;
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, HasOptions && value); }
    public string Status => !HasOptions ? Availability : Selected != null ? Selected.Version : Choices.All(c => !c.CanChoose) ? (Choices.Any(c=>c.Hint.Contains("未确认"))?"兼容性未确认":"不兼容") : "可以添加";
    public UiCommand Clear { get; }
    public ComponentGroupViewModel(VanillaInstallViewModel owner, string name, IReadOnlyList<ComponentVersionItem> options, string availability)
    {
        _owner = owner; Name = name; Availability = availability;
        var fileName = name == "OptiFine" ? "Optifine.png" : name.ToLowerInvariant() + ".png";
        Icon = new Uri("pack://application:,,,/FocusMinecraftLauncher;component/Assets/DownloadAssets/" + fileName);
        Choices = options.Select(option => new ComponentChoiceViewModel(this, option)).ToArray();
        Clear = new(_ => _owner.Select(this, null), _ => HasSelection);
    }
    /// <summary>Forge 与 OptiFine 使用真实兼容标注；未知组合不冒充不兼容或已支持。</summary>
    public bool CanChoose(ComponentVersionItem option)=>ChoiceHint(option).Length==0;
    public string ChoiceHint(ComponentVersionItem option)
    {
        if(Name=="OptiFine")
        {
            if(_owner.PrimaryName=="原版")return "";
            if(_owner.PrimaryName!="Forge")return "与当前加载器不兼容";
            var forge=_owner.Components.First(c=>c.Name=="Forge").Selected;
            return PairHint(option,forge?.Version??"");
        }
        if(_owner.PrimaryName!="原版"&&_owner.PrimaryName!=Name)return "主要加载器不能同时安装";
        var optiFine=_owner.Components.FirstOrDefault(c=>c.Name=="OptiFine")?.Selected;
        if(optiFine==null)return "";
        return Name=="Forge"?PairHint(optiFine,option.Version):"与当前 OptiFine 安装方式不兼容";
    }
    private string PairHint(ComponentVersionItem optiFine,string forge)
    {
        if(OptiFineForgeIntegration.IsKnownCombination(_owner.Game.Number,forge,optiFine.Version,optiFine.ForgeBuild))return "";
        if(optiFine.ForgeBuild=="N/A")return "此 OptiFine 版本未提供 Forge 支持";
        return optiFine.ForgeBuild==null?"此组合的兼容性未确认，未取得官网标注":"此组合的兼容性未确认；官网标注 Forge "+optiFine.ForgeBuild;
    }
    public void SetOptions(IReadOnlyList<ComponentVersionItem> options, string status)
    { var selected=Selected?.Version; Choices = options.Select(option => new ComponentChoiceViewModel(this, option)).ToArray(); Selected=options.FirstOrDefault(o=>o.Version==selected); Availability = status; Update(); }
    public void Choose(ComponentVersionItem option) => _owner.Select(this, ReferenceEquals(Selected, option) ? null : option);
    public void Update() { Refresh(); foreach(var choice in Choices) choice.Refresh(); CommandManager.InvalidateRequerySuggested(); }
}
/// <summary>真实查询核心支持的组件版本，选择状态与折叠状态互不影响。</summary>
public sealed class VanillaInstallViewModel : ObservableObject
{
    private string _name;
    public GameVersionItem Game { get; }
    public IReadOnlyList<ComponentGroupViewModel> Components { get; }
    public DownloadScrollPosition Scroll { get; } = new();
    public string Name { get => _name; set { if (Set(ref _name, value)) { Changed(nameof(NameError)); Changed(nameof(Summary)); CommandManager.InvalidateRequerySuggested(); } } }
    public string NameError
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name)) return "请输入安装名称。";
            if (Name.Length > 80 || Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "名称不可超过 80 个字符，且不能包含 \\ / : * ? \" < > |。";
            if (Name is "." or ".." || Name.EndsWith('.') || Name.EndsWith(' ')) return "名称不能以句点或空格结尾。";
            var stem = Name.Split('.')[0].ToUpperInvariant();
            return new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem) ? "名称不能使用 Windows 保留名称。" : "";
        }
    }
    public string PrimaryName => Components.FirstOrDefault(c => c.Name != "OptiFine" && c.Selected != null)?.Name ?? "原版";
    public string Composition => Components.Any(c => c.Selected != null) ? string.Join(" / ", Components.Where(c => c.Selected != null).Select(c => c.Name + " " + c.Selected!.Version)) : "纯原版";
    public string SuggestedName => Game.Number + string.Concat(Components.Where(component => component.Selected != null)
        .Select(component => "-" + component.Name + "_" + component.Selected!.Version.Replace(' ', '_')));
    public string Summary => Name + " / Minecraft " + Game.Number + " / " + Composition;
    public string CompatibilityError => Components.First(c=>c.Name=="OptiFine") is {Selected:{ } chosen} opti?opti.ChoiceHint(chosen):"";
    public UiCommand Back { get; }
    public UiCommand StartDownload { get; }
    public VanillaInstallViewModel(GameVersionItem game, Action back, Action<VanillaInstallViewModel> enqueue, FocusMinecraftLauncher.Preview.PreviewSession session)
    {
        Game = game; _name = game.Number; Back = new(_ => back());
        Components = new[] { "Forge", "NeoForge", "Fabric", "OptiFine" }.Select(kind => new ComponentGroupViewModel(this, kind, [], "正在查询版本")).ToArray();
        _ = LoadComponentsAsync(session);
        StartDownload = new(_ => enqueue(this), _ => NameError.Length == 0 && CompatibilityError.Length == 0);
    }
    public async Task RetryFailedQueriesAsync(FocusMinecraftLauncher.Preview.PreviewSession session)
    {if(Components.Any(g=>g.Availability.StartsWith("查询失败")))await LoadComponentsAsync(session);}
    private async Task LoadComponentsAsync(FocusMinecraftLauncher.Preview.PreviewSession session)
    {
        await Task.WhenAll(Components.Select(async group =>
        {
            try
            {
                IReadOnlyList<ComponentVersionItem> versions = group.Name switch
                {
                    "Fabric" => await WithDatesAsync("Fabric", (await FMCore.Modsloader.FabricInstaller.GetLoaderVersionsAsync(Game.Number, session.MetadataSource)).Select(v => new ComponentVersionItem(v.Version, "", v.Stable ? "稳定版" : "测试版", [])).ToArray()),
                    "Forge" => await WithDatesAsync("Forge", (await FMCore.Modsloader.ForgeInstaller.GetVersionsAsync(Game.Number, session.MetadataSource)).Select(v => new ComponentVersionItem(v, "", "", [])).ToArray()),
                    "NeoForge" => await WithDatesAsync("NeoForge", (await FMCore.Modsloader.NeoForgeInstaller.GetVersionsAsync(Game.Number, session.MetadataSource)).Select(v => new ComponentVersionItem(v, "", "", [])).ToArray()),
                    _ => await LoadOptiFineOptionsAsync(session)
                };
                group.SetOptions(versions, "当前游戏版本暂无可用版本");
                foreach(var component in Components)component.Update();Changed(nameof(CompatibilityError));
            }
            catch (Exception error) { group.SetOptions([], "查询失败，请重新进入此页重试"); session.Log(group.Name + "版本查询：" + FocusMinecraftLauncher.Infrastructure.Runtime.RuntimeSupport.Error(error)); }
        }));
    }
    /// <summary>发布日期只用于展示；查询失败时保留空日期，不影响版本选择。</summary>
    private async Task<IReadOnlyList<ComponentVersionItem>> WithDatesAsync(string kind, ComponentVersionItem[] items)
    {
        var dates = await LoaderReleaseDates.GetAsync(kind, Game.Number, items.Select(item => item.Version));
        return dates.Count == 0 ? items : items.Select(item => item with { Date = dates.GetValueOrDefault(item.Version) ?? item.Date }).ToArray();
    }
    private async Task<IReadOnlyList<ComponentVersionItem>> LoadOptiFineOptionsAsync(FocusMinecraftLauncher.Preview.PreviewSession session)
    {
        var versions=await FMCore.Modsloader.OptiFineOnlineInstaller.GetVersionsAsync(Game.Number);
        IReadOnlyDictionary<string,OptiFineForgeSupport>? support=null;
        try{support=await OptiFineForgeIntegration.GetSupportAsync();}
        catch(Exception error){session.Log("OptiFine Forge 兼容标注："+RuntimeSupport.Error(error));}
        return versions.Select(version=>
        {
            var item=support?.GetValueOrDefault(version.FileName);
            return new ComponentVersionItem(version.Edition,item?.Date??"",version.IsPreview?"测试版":"",["原版","Forge"]){ForgeBuild=item?.ForgeBuild,IsOptiFine=true};
        }).ToArray();
    }
    /// <summary>拒绝冲突选择；默认名称随组件更新，手动编辑的名称继续保留。</summary>
    internal void Select(ComponentGroupViewModel group, ComponentVersionItem? option)
    {
        if (option != null && !group.CanChoose(option)) return;
        var followSelection = Name == SuggestedName;
        group.Selected = option;
        if (followSelection) Name = SuggestedName;
        foreach (var component in Components) component.Update();
        Changed(nameof(PrimaryName)); Changed(nameof(Composition)); Changed(nameof(SuggestedName)); Changed(nameof(Summary)); Changed(nameof(CompatibilityError)); CommandManager.InvalidateRequerySuggested();
    }
}
