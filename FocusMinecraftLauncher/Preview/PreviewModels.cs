using FocusMinecraftLauncher.Common;
using System.Windows.Media;
using FocusMinecraftLauncher.Presentation;
using FocusMinecraftLauncher.Features.Home.Models;
namespace FocusMinecraftLauncher.Preview;
/// <summary>账户资料与运行时认证结果分离，读取配置不等于 Microsoft 认证成功。</summary>
public sealed class PreviewAccount : ObservableObject
{
    private bool _current, _valid, _restoring;
    private string _loginFailure = "需要重新登录";
    private string _warning = "";
    private ImageSource? _headImage;
    private bool _skinLoading;
    private string _skinMessage = "";
    public byte[]? SkinPng { get; private set; }
    public byte[]? CapePng { get; private set; }
    public bool IsSlimSkin { get; private set; }
    public bool IsSkinLoading => _skinLoading;
    public string SkinMessage => _skinMessage;
    public void SetSkinState(bool loading, string message) { _skinLoading = loading; _skinMessage = message; Refresh(); }
    public void ApplySkin(ImageSource? head, byte[]? png, bool slim = false, byte[]? cape = null)
    { _headImage = head; SkinPng = png; IsSlimSkin = slim; CapePng = cape; Refresh(); }
    public Infrastructure.Authentication.StoredAccount Stored { get; }
    public FMCore.Accounts.IMinecraftAccount? RuntimeAccount { get; private set; }
    public Guid Id => Stored.Id;
    public Guid Uuid => Stored.Uuid;
    public string Name => Stored.Name;
    public string Kind => IsMicrosoft ? "Microsoft 正版账号" : IsYggdrasil ? "外置账号" : "离线账号";
    /// <summary>账号卡片的类型标记：正版 / 离线 / 外置。</summary>
    public string KindLabel => IsMicrosoft ? "正版" : IsYggdrasil ? "外置" : "离线";
    public bool IsMicrosoft => Stored.Type == "Microsoft";
    public bool IsYggdrasil => Stored.Type == "Yggdrasil";
    /// <summary>需要验证服务的账号都归在“正版/在线”一侧，离线账号单独一栏。</summary>
    public bool IsOnline => IsMicrosoft || IsYggdrasil;
    public bool IsValid => _valid && (RuntimeAccount is not FMCore.Accounts.MicrosoftAccount microsoft || microsoft.ExpiresAt > DateTimeOffset.UtcNow);
    public bool IsRestoring => _restoring;
    public ImageSource? HeadImage { get => _headImage; set => Set(ref _headImage, value); }
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public string LoginState => IsRestoring ? "正在恢复登录" : IsMicrosoft ? IsValid ? "已登录" : _loginFailure : IsYggdrasil ? IsValid ? "已登录" : _loginFailure : IsValid ? "离线账号" : "账户资料不可用";
    /// <summary>状态用于界面着色：active 正常、expired 需要重新登录、offline 离线。</summary>
    public string StatusKey => !IsMicrosoft && !IsYggdrasil ? "offline" : !IsValid ? "expired" : "active";
    public string StatusText => IsRestoring ? "正在校验登录" : StatusKey switch
    {
        "active" => _warning.Length > 0 ? _warning : "已登录",
        "expired" => _loginFailure,
        _ => IsValid ? "离线账号" : "账户资料不可用"
    };
    public string LastLoginText => (IsMicrosoft || IsYggdrasil) && Stored.LastAuthenticatedAt is { } time
        ? "上次登录 " + time.ToLocalTime().ToString("MM-dd HH:mm") : "";
    public bool HasLastLogin => LastLoginText.Length > 0;
    public string UuidText => Uuid == Guid.Empty ? "" : Uuid.ToString("D");
    private bool _default;
    public bool IsDefault { get => _default; internal set { if (Set(ref _default, value)) Changed(nameof(DefaultLabel)); } }
    public string DefaultLabel => IsDefault ? "默认" : "";
    public string Label => Name + "（" + Kind + "）";
    public bool IsCurrent { get => _current; set { if (Set(ref _current, value)) Changed(nameof(CurrentLabel)); } }
    public string CurrentLabel => IsCurrent ? "当前使用" : "";
    public PreviewAccount(Infrastructure.Authentication.StoredAccount stored)
    {
        Stored = stored;
        if (stored.Id == Guid.Empty) stored.Id = Guid.NewGuid();
        if (stored.Type == "Offline")
        {
            var generated = FMCore.Accounts.OfflineAccount.Create(stored.Name);
            if (stored.Uuid == Guid.Empty) stored.Uuid = generated.Uuid;
            RuntimeAccount = generated with { Uuid = stored.Uuid };
            _valid = true;
        }
        _restoring = (IsMicrosoft && stored.ProtectedSession.Length > 0) || (IsYggdrasil && stored.ProtectedYggdrasil.Length > 0);
    }
    public void SetRestoring(bool value) { _restoring = value; Refresh(); }
    /// <summary>仅更改离线昵称；沿用账户标识和原 UUID，不重新生成存档身份。</summary>
    internal void RenameOffline(string name)
    {
        if (IsMicrosoft) throw new InvalidOperationException("正版账号请前往官网修改玩家名称。");
        var renamed = FMCore.Accounts.OfflineAccount.Create(name) with { Uuid = Stored.Uuid };
        Stored.Name = renamed.Username;
        RuntimeAccount = renamed; _valid = true;
        Refresh();
    }
    public void MarkLoginFailure(string message) { _valid = false; _restoring = false; _loginFailure = message; _warning = ""; RuntimeAccount = null; Refresh(); }
    /// <summary>网络或服务暂时不可用：保留登录态与令牌，只在状态行提示，下次再校验。</summary>
    internal void MarkUnverified(string message) { if (!IsMicrosoft && !IsYggdrasil || !_valid) return; _warning = message; Refresh(); }
    public void ApplyMicrosoft(FMCore.Accounts.MicrosoftAccount account, string encryptedCache)
    {
        Stored.Name = account.Username; Stored.Uuid = account.Uuid;
        Stored.ProtectedSession = encryptedCache; Stored.LastAuthenticatedAt = DateTimeOffset.UtcNow;
        RuntimeAccount = account; _valid = true; _restoring = false; _warning = ""; Refresh();
    }
    /// <summary>登录或恢复外置账号后写入名称、验证服务与加密令牌，界面与启动都读这一份。</summary>
    public void ApplyYggdrasil(FMCore.Accounts.YggdrasilAccount account, string encryptedCache)
    {
        Stored.Name = account.Username; Stored.Uuid = account.Uuid;
        Stored.YggdrasilServer = account.ApiRoot.ToString().TrimEnd('/');
        Stored.ProtectedYggdrasil = encryptedCache; Stored.LastAuthenticatedAt = DateTimeOffset.UtcNow;
        RuntimeAccount = account; _valid = true; _restoring = false; _warning = ""; Refresh();
    }
}
/// <summary>从实例进入资源下载时携带的筛选上下文：目标游戏版本与加载器（空串表示不限）。</summary>
public sealed record ResourceFilter(string GameVersion, string Loader);
/// <summary>从已安装 Mod 进入下载详情时的一次性请求；项目 ID、筛选上下文与本地已知名称一同传递，先出页面再补数据。</summary>
public sealed record ResourceDetailRequest(string ProjectId, ResourceFilter Filter, string? Name = null);
/// <summary>由核心读取的本地实例及启动器显示元数据。</summary>
public sealed class PreviewInstance(string id, string name, string version, string loader, string versionType = "release") : ObservableObject
{
    private string _name = name, _description = "", _category = "";
    private bool _favorite;
    private ImageSource? _icon, _fallbackIcon;
    public string Id { get; } = id;
    public string Name { get => _name; set => Set(ref _name, value); }
    private string _version = version, _metadataIssue = "", _loader = loader;
    public string Version { get => _version; set { if (Set(ref _version, value)) Changed(nameof(VersionText)); } }
    public string MetadataIssue { get => _metadataIssue; set { if (Set(ref _metadataIssue, value)) Changed(nameof(ReadinessText)); } }
    public string ReadinessText => MetadataIssue.Length > 0 ? MetadataIssue : "启动前将检查完整性";
    public string Loader
    {
        get => _loader;
        set
        {
            if (!Set(ref _loader, value)) return;
            Changed(nameof(SupportsMods)); Changed(nameof(DefaultIcon));
            Changed(nameof(ComponentsText)); Changed(nameof(DisplayIcon));
        }
    }
    public string VersionType { get; } = versionType;
    public string Description { get => _description; set => Set(ref _description, value); }
    public string Category { get => _category; set => Set(ref _category, value); }
    public bool IsFavorite { get => _favorite; set => Set(ref _favorite, value); }
    /// <summary>自定义或整合包图标优先；清除后回到当前加载器/版本类型的默认图标。</summary>
    public ImageSource? IconImage { get => _icon; set { if (Set(ref _icon, value)) Changed(nameof(DisplayIcon)); } }
    public ImageSource DefaultIcon => GameVersionIcons.Get(Loader, VersionType);
    public ImageSource? FallbackIconImage { get=>_fallbackIcon;set {if(Set(ref _fallbackIcon,value))Changed(nameof(DisplayIcon));} }
    public ImageSource DisplayIcon => IconImage ?? FallbackIconImage ?? DefaultIcon;
    public string VersionText => "Minecraft " + Version;
    public string Root { get; init; } = "";
    public string DirectoryPath { get; init; } = "";
    public string Folder => DirectoryPath.Length > 0 ? DirectoryPath : System.IO.Path.Combine(Root, "versions", Id);
    private VersionIsolationMode _isolation = VersionIsolationMode.All;
    public VersionIsolationMode IsolationMode
    {
        get => _isolation;
        set
        {
            if (!Set(ref _isolation, Enum.IsDefined(value) ? value : VersionIsolationMode.All)) return;
            Changed(nameof(UsesGameIsolation)); Changed(nameof(GameDirectory)); Changed(nameof(IsolationText)); RefreshOptiFineState();
        }
    }
    public bool UsesGameIsolation => IsolationMode == VersionIsolationMode.All || IsolationMode == VersionIsolationMode.ModdedOnly && SupportsMods;
    public string GameDirectory => UsesGameIsolation ? Folder : Root;
    public string IsolationText => UsesGameIsolation ? "按版本使用独立目录" : "使用共享游戏目录";
    public bool IsEditingContent { get; set; }

    public int? RequiredJava { get; set; }
    public string LoaderVersion { get; set; } = "";
    private string _optiFineVersion="";
    public string OptiFineVersion {get=>_optiFineVersion;private set {if(Set(ref _optiFineVersion,value))Changed(nameof(ComponentsText));}}
    public string ComponentsText=>Loader+(LoaderVersion.Length>0?" "+LoaderVersion:"")+(OptiFineVersion.Length>0?" / OptiFine "+OptiFineVersion:"");
    public void RefreshOptiFineState()
    {
        var component=Options.OptiFineMod;
        OptiFineVersion=component!=null&&System.IO.Path.GetFileName(component.FileName)==component.FileName&&System.IO.File.Exists(System.IO.Path.Combine(GameDirectory,"mods",component.FileName))?component.Edition:"";
    }
    public Infrastructure.Runtime.InstanceOptions Options { get; set; } = new();
    public bool SupportsMods => Loader is "Fabric" or "Forge" or "NeoForge";
    public void LoadIcon(string path)
    {
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit(); image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 128; image.UriSource = new Uri(path); image.EndInit(); image.Freeze(); IconImage = image;
    }
}
public sealed class PreviewVersion(string version, string type) : ObservableObject
{
    private bool _installed;
    public string Version { get; } = version;
    public string Type { get; } = type;
    public ImageSource IconImage => GameVersionIcons.Get("原版", Type);
    public string Label => "Minecraft " + Version;
    public bool IsInstalled { get => _installed; set { if (Set(ref _installed, value)) Changed(nameof(InstallState)); } }
    public string InstallState => IsInstalled ? "已安装" : "可安装";
}
public enum PreviewTaskKind { Launch, Install, Resource, Repair, Import, Export }
/// <summary>真实操作的共享状态；成功只能在对应核心方法成功返回后登记。</summary>
public sealed class PreviewTask(PreviewTaskKind kind, string name, string version) : ObservableObject
{
    public PreviewTaskKind Kind { get; } = kind;
    public string Name { get; } = name;
    public string Version { get; } = version;
    public string Root { get; init; } = "";
    public string InstanceId { get; init; } = "";
    public string State { get; set; } = "排队中";
    public string Stage { get; set; } = "等待执行";
    public string Error { get; set; } = "";
    public Infrastructure.Runtime.LauncherError? Failure { get; set; }
    public bool HasFailure => Failure != null;
    public long Bytes { get; set; }
    public long Total { get; set; }
    public double BytesPerSecond { get; set; }
    public int CompletedFiles { get; set; }
    public int TotalFiles { get; set; }
    private bool _runningGame;
    public bool IsRunningGame
    {
        get => _runningGame;
        set
        {
            if (!Set(ref _runningGame, value)) return;
            Changed(nameof(ShowProgress)); Changed(nameof(IsIndeterminate)); Changed(nameof(CancelText));
        }
    }
    public bool IsActive => State is "排队中" or "进行中";
    public bool CanRetry => State is "失败" or "已取消";
    public bool HasBytes => Total > 0 || Bytes > 0;
    /// <summary>游戏进程已启动后只保留运行状态和关闭操作，不再显示启动或下载进度。</summary>
    public bool ShowProgress => IsActive && !IsRunningGame;
    public bool IsIndeterminate => ShowProgress && Total <= 0 && TotalFiles<=0;
    public string CancelText => IsRunningGame ? "关闭游戏" : Kind == PreviewTaskKind.Launch ? "取消启动" : "取消";
    public double Percent => Total > 0 ? Math.Clamp(100d * Bytes / Total, 0, 100) : TotalFiles>0?Math.Clamp(100d*CompletedFiles/TotalFiles,0,100):0;
    public string DownloadLabel => Kind==PreviewTaskKind.Export&&TotalFiles>0
        ? $"{CompletedFiles} / {TotalFiles} 个文件"+(Total>0?$"，已处理 {Bytes/1048576d:0.0} / {Total/1048576d:0.0} MiB":"")
        : Total>0 ? $"{Bytes / 1048576d:0.0} / {Total / 1048576d:0.0} MiB" : Bytes>0?$"已下载 {Bytes/1048576d:0.0} MiB":"";
    public string Speed => BytesPerSecond > 0 && IsActive ? $"{BytesPerSecond / 1048576d:0.0} MiB/s" : "";
}
