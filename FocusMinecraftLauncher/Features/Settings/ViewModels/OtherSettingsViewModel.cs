using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Infrastructure.Configuration;

namespace FocusMinecraftLauncher.Features.Settings.ViewModels;

public sealed record CloseBehaviorOption(LauncherCloseBehavior Value, string Label);

/// <summary>管理退出行为、下载、资源显示及联机设置。</summary>
public sealed class OtherSettingsViewModel : ObservableObject
{
    public FocusMinecraftLauncher.Features.More.ViewModels.LauncherUpdateViewModel Updates => ((App)System.Windows.Application.Current).Updates;
    private const string PreferOfficial = "官方源";
    private const string AutomaticSource = "自动选择";
    private const string ProjectAndFileName = "资源名称 + 原文件名";
    private string _fileDownloadSource = AutomaticSource;
    private string _versionListSource = PreferOfficial;
    private int _maximumThreads = 16;
    private string _resourceSource = FocusMinecraftLauncher.Infrastructure.Runtime.CommunitySource.AutomaticName;
    private string _fileNameFormat = ProjectAndFileName;
    private string _modManagementStyle = "标题显示资源名称，详情显示文件名";
    private bool _wikiNames = true;
    private LauncherCloseBehavior _closeBehavior;
    private bool _minimizeToTray;
    private long _cacheBytes = FocusMinecraftLauncher.Infrastructure.Runtime.CommunityCache.Measure();

    public string[] DownloadSources { get; } = [AutomaticSource, PreferOfficial, "BMCLAPI 镜像源"];
    public IReadOnlyList<CloseBehaviorOption> CloseBehaviors { get; } =
    [
        new(LauncherCloseBehavior.Ask, "询问用户"),
        new(LauncherCloseBehavior.Close, "直接关闭启动器"),
        new(LauncherCloseBehavior.Minimize, "最小化到系统托盘")
    ];
    public LauncherCloseBehavior CloseBehavior
    {
        get => _closeBehavior;
        set { if (Set(ref _closeBehavior, value)) _configuration.CloseBehavior = value; }
    }
    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set { if (Set(ref _minimizeToTray, value)) _configuration.MinimizeToTray = value; }
    }
    public string[] ResourceSources { get; } = [FocusMinecraftLauncher.Infrastructure.Runtime.CommunitySource.AutomaticName, FocusMinecraftLauncher.Infrastructure.Runtime.CommunitySource.MirrorName, FocusMinecraftLauncher.Infrastructure.Runtime.CommunitySource.OfficialName];
    public string[] FileNameFormats { get; } = [ProjectAndFileName, "原文件名", "资源名称 + 版本号"];
    public string[] ModManagementStyles { get; } =
        ["标题显示资源名称，详情显示文件名", "标题显示原名，详情显示文件名", "仅显示文件名"];

    public string FileDownloadSource { get => _fileDownloadSource; set => SaveChange(() => Set(ref _fileDownloadSource, value)); }
    public string VersionListSource { get => _versionListSource; set => SaveChange(() => Set(ref _versionListSource, value)); }
    public int MaximumThreads { get => _maximumThreads; set => SaveChange(() => Set(ref _maximumThreads, Math.Clamp(value, 1, 64))); }
    public string ResourceSource { get => _resourceSource; set => SaveChange(() => Set(ref _resourceSource, value)); }
    public string FileNameFormat
    {
        get => _fileNameFormat;
        set { if (Set(ref _fileNameFormat, value)) { Changed(nameof(FileNamePreview)); Persist(); } }
    }
    public string ModManagementStyle { get => _modManagementStyle; set => SaveChange(() => Set(ref _modManagementStyle, value)); }
    /// <summary>从 MC 百科补全中文名；关掉后列表只显示项目原名。</summary>
    public bool WikiNames
    {
        get => _wikiNames;
        set
        {
            if (!Set(ref _wikiNames, value)) return;
            FocusMinecraftLauncher.Infrastructure.Runtime.WikiNameService.Enabled = value;
            Persist();
        }
    }
    public string CacheUsage => "已用 " + FocusMinecraftLauncher.Infrastructure.Runtime.CommunityCache.DescribeSize(_cacheBytes);
    public UiCommand ClearCache { get; }
    public Features.Multiplayer.Services.MultiplayerService Network => ((App)System.Windows.Application.Current).Multiplayer;
    public UiCommand UninstallNetwork { get; }
    public UiCommand InstallNetwork { get; }
    public UiCommand CancelNetworkInstall { get; }
    private string _sharedNode = "", _sharedNodeNotice = "", _sharedNodeSaveError = "";
    private string _sharedNodeNetworkName = "", _sharedNodePassword = "";
    private string _sharedNodeConnectivity = "";
    private bool _sharedNodeConnectivityError;
    private bool _savingSharedNode;
    public string SharedNode
    {
        get => _sharedNode;
        set
        {
            if (!Set(ref _sharedNode, value)) return;
            _sharedNodeSaveError = ""; SharedNodeNotice = "";
            SharedNodeConnectivity = "";
            Changed(nameof(SharedNodeError));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }
    public string SharedNodeNetworkName
    {
        get => _sharedNodeNetworkName;
        set
        {
            if (!Set(ref _sharedNodeNetworkName, value)) return;
            _sharedNodeSaveError = ""; SharedNodeNotice = "";
            SharedNodeConnectivity = "";
            Changed(nameof(SharedNodeError));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }
    public string SharedNodePassword
    {
        get => _sharedNodePassword;
        set
        {
            if (!Set(ref _sharedNodePassword, value)) return;
            _sharedNodeSaveError = ""; SharedNodeNotice = "";
            SharedNodeConnectivity = "";
            Changed(nameof(SharedNodeError));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }
    public string SharedNodeError => _sharedNodeSaveError.Length > 0 ? _sharedNodeSaveError : SharedNodeValidationError;
    public string SharedNodeConnectivity
    {
        get => _sharedNodeConnectivity;
        private set
        {
            if (!Set(ref _sharedNodeConnectivity, value)) return;
            Changed(nameof(HasSharedNodeConnectivity));
        }
    }
    public bool SharedNodeConnectivityError
    {
        get => _sharedNodeConnectivityError;
        private set => Set(ref _sharedNodeConnectivityError, value);
    }
    public bool HasSharedNodeConnectivity => SharedNodeConnectivity.Length > 0;
    private string SharedNodeValidationError
    {
        get
        {
            try
            {
                Features.Multiplayer.Models.RoomInvitation.NormalizeNode(SharedNode);
                string networkName = SharedNodeNetworkName.Trim();
                if ((networkName.Length == 0) != (SharedNodePassword.Length == 0))
                    return "私有节点需要同时填写网络名称和密码；公益节点请将两项都留空。";
                if (networkName.Length > 64 || networkName.Any(char.IsControl))
                    return "节点网络名称最多 64 个字符，且不能包含控制字符。";
                if (SharedNodePassword.Length > 128 || SharedNodePassword.Any(char.IsControl))
                    return "节点密码最多 128 个字符，且不能包含控制字符。";
                return "";
            }
            catch (FormatException error) { return error.Message; }
        }
    }
    public string SharedNodeNotice { get => _sharedNodeNotice; private set => Set(ref _sharedNodeNotice, value); }
    public bool IsSavingSharedNode => _savingSharedNode;
    public UiCommand SaveSharedNode { get; }

    private readonly FocusMinecraftLauncher.Infrastructure.Configuration.LauncherConfiguration _configuration = ((App)System.Windows.Application.Current).Configuration;
    public OtherSettingsViewModel()
    {
        _closeBehavior = _configuration.CloseBehavior;
        _minimizeToTray = _configuration.MinimizeToTray;
        _sharedNode = Network.SharedNode;
        _sharedNodeNetworkName = Network.SharedNodeNetworkName;
        _sharedNodePassword = Network.SharedNodePassword;
        SaveSharedNode = new(async _ => await SaveSharedNodeAsync(), _ => !_savingSharedNode && SharedNodeValidationError.Length == 0);
        InstallNetwork = new(async _ => await Network.InstallComponentsAsync(), _ => Network.CanInstallComponents);
        CancelNetworkInstall = new(_ => Network.CancelComponentInstallation(), _ => Network.CanCancelComponentInstallation);
        UninstallNetwork = new(async _ => await Network.UninstallAsync(), _ => Network.CanUninstall);
        var data = _configuration.ReadLauncher()["downloads"];
        _fileDownloadSource = NormalizeSource(FocusMinecraftLauncher.Infrastructure.Configuration.LauncherConfiguration.Text(data?["fileDownloadSource"], AutomaticSource));
        _versionListSource = NormalizeSource(FocusMinecraftLauncher.Infrastructure.Configuration.LauncherConfiguration.Text(data?["versionListSource"], PreferOfficial));
        _maximumThreads = Math.Clamp((int)FocusMinecraftLauncher.Infrastructure.Configuration.LauncherConfiguration.Number(data?["maximumThreads"], 16), 1, 64);
        _resourceSource = NormalizeCommunity(FocusMinecraftLauncher.Infrastructure.Configuration.LauncherConfiguration.Text(data?["communitySource"], FocusMinecraftLauncher.Infrastructure.Runtime.CommunitySource.AutomaticName));
        _fileNameFormat = FocusMinecraftLauncher.Infrastructure.Configuration.LauncherConfiguration.Text(data?["fileNameFormat"], ProjectAndFileName);
        if(_fileNameFormat=="译名 + 原文件名")_fileNameFormat=ProjectAndFileName;
        _modManagementStyle = FocusMinecraftLauncher.Infrastructure.Configuration.LauncherConfiguration.Text(data?["modManagementStyle"], "标题显示资源名称，详情显示文件名");
        if(_modManagementStyle=="标题显示译名，详情显示文件名")_modManagementStyle="标题显示资源名称，详情显示文件名";
        _wikiNames = FocusMinecraftLauncher.Infrastructure.Configuration.LauncherConfiguration.Boolean(data?["wikiNames"], true);
        FocusMinecraftLauncher.Infrastructure.Runtime.WikiNameService.Enabled = _wikiNames;
        ClearCache = new(_ =>
        {
            FocusMinecraftLauncher.Infrastructure.Runtime.CommunityCache.Clear();
            _cacheBytes = FocusMinecraftLauncher.Infrastructure.Runtime.CommunityCache.Measure();
            Changed(nameof(CacheUsage));
        });
        if (Network.SharedNode.Length > 0) _ = TestSharedNodeStatusAsync(Network.SharedNode, Network.SharedNodeNetworkName, Network.SharedNodePassword);
    }

    private async Task<bool> TestSharedNodeStatusAsync(string node, string networkName, string password)
    {
        SharedNodeConnectivity = "正在测试节点连通性…";
        SharedNodeConnectivityError = false;
        try
        {
            SharedNodeConnectivity = await Network.TestSharedNodeAsync(node, networkName, password);
            return true;
        }
        catch (Exception error)
        {
            SharedNodeConnectivity = "节点连接失败：" + error.Message;
            SharedNodeConnectivityError = true;
            return false;
        }
    }

    /// <summary>确认保存后再修改节点；校验与写入失败就地反馈，不误报保存成功。</summary>
    private async Task SaveSharedNodeAsync()
    {
        if (_savingSharedNode) return;
        _savingSharedNode = true; SharedNodeNotice = ""; _sharedNodeSaveError = "";
        Changed(nameof(IsSavingSharedNode)); Changed(nameof(SharedNodeError));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        try
        {
            var node = Features.Multiplayer.Models.RoomInvitation.NormalizeNode(SharedNode);
            var saved = await Network.SaveSharedNodeAsync(node, SharedNodeNetworkName, SharedNodePassword);
            SharedNode = node;
            SharedNodeNetworkName = SharedNodeNetworkName.Trim();
            if (saved)
            {
                if (node.Length == 0) SharedNodeNotice = "节点已清除。";
                else
                {
                    SharedNodeNotice = "已保存。";
                    if (await TestSharedNodeStatusAsync(node, SharedNodeNetworkName, SharedNodePassword)
                        && System.Windows.Application.Current.MainWindow is MainWindow window)
                        window.Session.SetGlobalNotification("节点端口连接成功", false);
                }
            }
            else _sharedNodeSaveError = "保存失败，更改仅在本次运行中生效。请检查程序目录的读写权限。";
        }
        catch (Exception error) { _sharedNodeSaveError = "节点保存失败：" + error.Message; }
        finally
        {
            _savingSharedNode = false;
            Changed(nameof(IsSavingSharedNode)); Changed(nameof(SharedNodeError));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }
    private static string NormalizeSource(string value)=>value==AutomaticSource?AutomaticSource:value.StartsWith("优先使用镜像")||value.Contains("BMCLAPI")?"BMCLAPI 镜像源":PreferOfficial;
    /// <summary>只有明确选过官方源才回到官方，其余旧值或空值都按默认的镜像源处理。</summary>
    private static string NormalizeCommunity(string value)=>value is FocusMinecraftLauncher.Infrastructure.Runtime.CommunitySource.OfficialName or FocusMinecraftLauncher.Infrastructure.Runtime.CommunitySource.MirrorName ? value : FocusMinecraftLauncher.Infrastructure.Runtime.CommunitySource.AutomaticName;
    private void SaveChange(Func<bool> change) { if (change()) Persist(); }
    private void Persist()
    {
        _configuration.ChangeLauncher(root =>
        {
            var data = FocusMinecraftLauncher.Infrastructure.Configuration.LauncherConfiguration.Object(root, "downloads");
            data["fileDownloadSource"] = FileDownloadSource; data["versionListSource"] = VersionListSource;
            data["maximumThreads"] = MaximumThreads; data["communitySource"] = ResourceSource;
            data["fileNameFormat"] = FileNameFormat; data["modManagementStyle"] = ModManagementStyle;
            data["wikiNames"] = WikiNames;
            data.Remove("speedLimit"); data.Remove("verifySslCertificate"); data.Remove("resourceSource");
        });
        // 立即生效，不必重启启动器；下一次请求就会用新的源。
        FocusMinecraftLauncher.Infrastructure.Runtime.CommunitySource.Apply(_configuration);
    }
    public string FileNamePreview => FileNameFormat switch
    { "原文件名" => "resource-1.0.jar", "资源名称 + 版本号" => "资源名称-1.0.jar", _ => "[资源名称] resource-1.0.jar" };
}
