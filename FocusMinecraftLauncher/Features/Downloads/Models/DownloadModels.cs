using System.Globalization;
using System.Windows;
using System.Windows.Media;
using FocusMinecraftLauncher.Presentation;

namespace FocusMinecraftLauncher.Features.Downloads.Models;

public enum DownloadSection { Vanilla = 0, Tasks = 1, Mods = 2, Modpacks = 3, Datapacks = 4, Resourcepacks = 5, Shaders = 6 }
/// <summary>资源的提供方；两个来源的模型在界面上合并，安装路径按来源分派。</summary>
public enum ResourceSource { Modrinth = 0, CurseForge = 1 }
public sealed record DownloadNavigation(DownloadSection? Section, string Title)
{
    public bool IsHeading => Section == null;
}

public sealed record GameVersionItem(string Id, string Number, string Kind, string VersionType, DateOnly Date)
{
    public ImageSource Image => GameVersionIcons.Get("原版", VersionType);
    public string DateText => Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>独立的本地资源元数据；空值表达未提供，不推断下载量或兼容性。</summary>
public sealed record ResourceProject(string Id, DownloadSection Category, string Name, string[] Aliases,
    string Description, string[] Tags, string[]? GameVersions, string[]? Environments,
    string? Author, string? Source, long? Downloads, DateOnly? Updated, string? ImageKey)
{
    public IReadOnlyList<ResourceFileItem> Files { get; set; } = [];
    public string[] Gallery { get; init; } = [];
    public string Body { get; init; } = "";
    /// <summary>CurseForge 项目没有独立的图标字段，这里保留原始图标地址供异步加载。</summary>
    public string? IconUrl { get; init; }
    public ImageSource? LoadedImage { get; set; }
    public ImageSource Image => LoadedImage ?? ResourceArtwork.Get(ImageKey);
    /// <summary>简介的中文译文，取不到时留空并继续显示原文。</summary>
    public string? TranslatedDescription { get; set; }
    /// <summary>MC 百科提供的中文名，取不到时留空并继续显示项目原名。</summary>
    public string? ChineseName { get; set; }
    public string TitleText => ChineseName is { Length: > 0 } chinese ? chinese : Name;
    /// <summary>标题已经换成中文名时，用小字把原名带在下面，避免认错项目。</summary>
    public string? OriginalNameText => ChineseName is { Length: > 0 } && !Name.Equals(ChineseName, StringComparison.OrdinalIgnoreCase) ? Name : null;
    public string DescriptionText => TranslatedDescription is { Length: > 0 } translated ? translated : Description;
    public string AliasText => string.Join(" / ", Aliases.Where(a => !a.Equals(Name, StringComparison.OrdinalIgnoreCase)));
    public string TagsText => Tags.Length == 0 ? "类型未提供" : string.Join(" / ", Tags);
    public string CompatibilityText => ResourceText.VersionSummary(GameVersions);
    public string FullCompatibilityText => ResourceText.FullVersions(GameVersions);
    public bool UsesEnvironment => Category is DownloadSection.Mods or DownloadSection.Modpacks or DownloadSection.Shaders;
    public string EnvironmentText => !UsesEnvironment ? "" : Environments is { Length: > 0 } ? string.Join(" / ", Environments) : "运行环境未提供";
    public string AuthorText => "作者：" + (Author ?? "未提供");
    public string SourceText => Source ?? "来源未提供";
    public string DownloadsText => Downloads is { } count ? ResourceText.Count(count) + " 次下载" : "下载量未知";
    public string FullDownloads => Downloads?.ToString("N0", CultureInfo.InvariantCulture) ?? "未知";
    public string UpdatedText => Updated is { } date ? "更新 " + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "更新时间未提供";
    public string Introduction => PlainText(Body.Length > 0 ? Body : DescriptionText);
    private static string PlainText(string text)
    {
        text=System.Text.RegularExpressions.Regex.Replace(text,@"!\[[^\]]*\]\([^)]*\)","");
        text=System.Text.RegularExpressions.Regex.Replace(text,@"\[([^\]]+)\]\([^)]*\)","$1");
        text=System.Text.RegularExpressions.Regex.Replace(text,@"<[^>]+>"," ");
        text=System.Text.RegularExpressions.Regex.Replace(text,@"(?m)^\s{0,3}#{1,6}\s+","");
        return System.Net.WebUtility.HtmlDecode(text.Replace("**","").Replace("__","").Replace("`","")).Trim();
    }
}

public sealed record ResourceDependency(string ProjectId, string Requirement, bool Required, string[] GameVersions, string? Environment, string Note);

/// <summary>与来源无关的下载描述：CurseForge 文件用它携带地址与校验值，安装流程不需要认识具体来源的 DTO。</summary>
public sealed record ResourceDownload(string FileName, string Url, long Size, string? Sha1);

/// <summary>具体文件的版本、下载量和前置信息，与项目总下载量分别存储。</summary>
public sealed record ResourceFileItem(string Id, string ResourceVersion, string FileName, string[] GameVersions,
    string? Environment, DateOnly Date, long? Bytes, long? Downloads, IReadOnlyList<ResourceDependency>? Dependencies)
{
    public FMCore.ModsResources.ModrinthVersion? CoreVersion { get; init; }
    public FMCore.ModsResources.ModrinthFile? DownloadFile { get; init; }
    public string ProjectId { get; init; } = "";
    public string[] Loaders { get; init; } = [];
    /// <summary>来源项目所属分类，安装时决定落到 mods / shaderpacks / resourcepacks / datapacks。</summary>
    public DownloadSection Section { get; init; } = DownloadSection.Mods;
    /// <summary>CurseForge 等非 Modrinth 来源用它对携带下载信息；Modrinth 文件仍走核心客户端。</summary>
    public ResourceDownload? Download { get; init; }
    public IReadOnlyList<Infrastructure.Runtime.CurseForgeDependency>? CurseForgeDependencies { get; init; }
    public bool IsCurseForge => Download != null;
    public IReadOnlyList<ResourceDependency>? ResolvedDependencies { get; set; }
    public IReadOnlyList<ResourceDependency>? EffectiveDependencies => ResolvedDependencies ?? Dependencies;
    public string Label => ResourceVersion + " / " + CompatibilityText + (Environment == null ? "" : " / " + Environment);
    public string CompatibilityText => ResourceText.VersionSummary(GameVersions);
    public string EnvironmentText => Environment ?? "";
    public string DateText => Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string SizeText => Bytes is { } bytes ? ResourceText.Size(bytes) : "文件大小未提供";
    public string DownloadsText => Downloads is { } count ? ResourceText.Count(count) + " 次文件下载" : "文件下载量未知";
    public string FullDownloads => Downloads?.ToString("N0", CultureInfo.InvariantCulture) ?? "未知";
    public string DependencyText => EffectiveDependencies == null ? "前置信息未提供"
        : CoreVersion?.Dependencies.Any(item=>item.DependencyType is "required" or "optional" && item.ProjectId==null&&item.VersionId==null)==true
            ? EffectiveDependencies.Count+" 项已知前置资源，另有文件依赖待确认"
            : EffectiveDependencies.Count == 0 ? "没有前置资源" : EffectiveDependencies.Count + " 项前置资源";
}

public static class ResourceText
{
    /// <summary>列表仅展示至多三个确切版本和总数，不将不连续版本伪装成连续兼容范围。</summary>
    public static string VersionSummary(IEnumerable<string>? versions)
    {
        var ordered = OrderedVersions(versions);
        return ordered.Length == 0 ? "兼容版本未提供" : "Minecraft " + string.Join(" / ", ordered.Take(3))
            + (ordered.Length > 3 ? $" 等 {ordered.Length} 个版本" : "");
    }

    /// <summary>完整兼容信息留在详情的按需展开区域；不裁剪用于筛选和安装的原始数组。</summary>
    public static string FullVersions(IEnumerable<string>? versions)
    {
        var ordered = OrderedVersions(versions);
        return ordered.Length == 0 ? "兼容版本未提供" : string.Join(" / ", ordered);
    }
    private static string[] OrderedVersions(IEnumerable<string>? versions) => (versions ?? [])
        .Where(version => !string.IsNullOrWhiteSpace(version)).Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(version => version, MinecraftVersionOrder.Instance).ToArray();

    public static string Count(long number) => number >= 10000 ? (number / 10000d).ToString("0.#", CultureInfo.InvariantCulture) + " 万" : number.ToString("N0", CultureInfo.InvariantCulture);
    public static string Size(long bytes) => bytes >= 1048576 ? (bytes / 1048576d).ToString("0.##", CultureInfo.InvariantCulture) + " MiB" : (bytes / 1024d).ToString("0.##", CultureInfo.InvariantCulture) + " KiB";
}

/// <summary>缺失资源图片时提供统一内嵌图标，远程图片通过受控异步加载器读取。</summary>
public static class ResourceArtwork
{
    private static readonly ResourceDictionary Images = new()
    { Source = new Uri("pack://application:,,,/FocusMinecraftLauncher;component/Features/Downloads/Assets/ResourceArtwork.xaml") };
    public static ImageSource Get(string? key)
    {
        var image = (ImageSource)Images[key != null && Images.Contains(key) ? key : "Fallback"];
        return image;
    }
}

/// <summary>页面生命周期外保留滚动位置；重置筛选和分页时才主动归零。</summary>
public sealed class DownloadScrollPosition
{
    public double Offset { get; set; }
    public event Action? ResetRequested;
    public void Reset() { Offset = 0; ResetRequested?.Invoke(); }
}
