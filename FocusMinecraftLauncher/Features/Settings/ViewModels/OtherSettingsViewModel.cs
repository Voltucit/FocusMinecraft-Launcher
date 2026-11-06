using FocusMinecraftLauncher.Common;

namespace FocusMinecraftLauncher.Features.Settings.ViewModels;

/// <summary>统一保存下载和资源显示选项；认证证书校验始终使用库的默认安全行为。</summary>
public sealed class OtherSettingsViewModel : ObservableObject
{
    public FocusMinecraftLauncher.Features.More.ViewModels.LauncherUpdateViewModel Updates => ((App)System.Windows.Application.Current).Updates;
    private const string PreferOfficial = "官方源";
    private const string ProjectAndFileName = "资源名称 + 原文件名";
    private string _fileDownloadSource = PreferOfficial;
    private string _versionListSource = PreferOfficial;
    private int _maximumThreads = 16;
    private string _resourceSource = FocusMinecraftLauncher.Infrastructure.Runtime.CommunitySource.MirrorName;
    private string _fileNameFormat = ProjectAndFileName;
    private string _modManagementStyle = "标题显示资源名称，详情显示文件名";
    private bool _wikiNames = true;
    private long _cacheBytes = FocusMinecraftLauncher.Infrastructure.Runtime.CommunityCache.Measure();

    public string[] DownloadSources { get; } = [PreferOfficial, "BMCLAPI 镜像源"];
    public string[] ResourceSources { get; } = [FocusMinecraftLauncher.Infrastructure.Runtime.CommunitySource.MirrorName, FocusMinecraftLauncher.Infrastructure.Runtime.CommunitySource.OfficialName];
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

    private readonly FocusMinecraftLauncher.Infrastructure.Configuration.LauncherConfiguration _configuration = ((App)System.Windows.Application.Current).Configuration;
    public OtherSettingsViewModel()
    {
        var data = _configuration.ReadLauncher()["downloads"];
        _fileDownloadSource = NormalizeSource(FocusMinecraftLauncher.Infrastructure.Configuration.LauncherConfiguration.Text(data?["fileDownloadSource"], PreferOfficial));
        _versionListSource = NormalizeSource(FocusMinecraftLauncher.Infrastructure.Configuration.LauncherConfiguration.Text(data?["versionListSource"], PreferOfficial));
        _maximumThreads = Math.Clamp((int)FocusMinecraftLauncher.Infrastructure.Configuration.LauncherConfiguration.Number(data?["maximumThreads"], 16), 1, 64);
        _resourceSource = NormalizeCommunity(FocusMinecraftLauncher.Infrastructure.Configuration.LauncherConfiguration.Text(data?["communitySource"], FocusMinecraftLauncher.Infrastructure.Runtime.CommunitySource.MirrorName));
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
    }
    private static string NormalizeSource(string value)=>value.StartsWith("优先使用镜像")||value.Contains("BMCLAPI")?"BMCLAPI 镜像源":PreferOfficial;
    /// <summary>只有明确选过官方源才回到官方，其余旧值或空值都按默认的镜像源处理。</summary>
    private static string NormalizeCommunity(string value)=>value==FocusMinecraftLauncher.Infrastructure.Runtime.CommunitySource.OfficialName?value:FocusMinecraftLauncher.Infrastructure.Runtime.CommunitySource.MirrorName;
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
