using System.Globalization;
using System.Text.Json;

namespace FocusMinecraftLauncher.Features.Downloads.Models;

/// <summary>列表页的磁盘缓存格式：只保存摘要字段，文件列表与正文仍按需联网读取。</summary>
public sealed record CachedProject(string Id, DownloadSection Category, string Name, string[] Aliases, string Description,
    string[] Tags, string[]? GameVersions, string[]? Environments, string? Author, string? Source, long? Downloads,
    string? Updated, string? ImageKey, string? IconUrl, string? ChineseName)
{
    public static CachedProject From(ResourceProject project) => new(project.Id, project.Category, project.Name, project.Aliases,
        project.Description, project.Tags, project.GameVersions, project.Environments, project.Author, project.Source,
        project.Downloads, project.Updated?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), project.ImageKey, project.IconUrl,
        project.ChineseName);

    public ResourceProject ToProject()
    {
        var updated = DateOnly.TryParse(Updated, CultureInfo.InvariantCulture, out var parsed) ? parsed : (DateOnly?)null;
        return new ResourceProject(Id, Category, Name, Aliases, Description, Tags, GameVersions, Environments, Author, Source,
            Downloads, updated, ImageKey)
        {
            IconUrl = IconUrl,
            ChineseName = ChineseName
        };
    }
}

/// <summary>一页列表的缓存：条目加上服务端给出的总数，重建时不必再猜分页。</summary>
public sealed record CachedPage(long Total, CachedProject[] Items);

/// <summary>列表缓存的序列化，任何解析异常都按“没有缓存”处理，不让坏文件阻断列表。</summary>
public static class CachedPageList
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(IEnumerable<(ResourceProject Project, string? Icon)> items, long total) =>
        JsonSerializer.Serialize(new CachedPage(total, items.Select(item => CachedProject.From(item.Project)).ToArray()), Options);

    public static CachedPage? Deserialize(string json)
    {
        try
        {
            var page = JsonSerializer.Deserialize<CachedPage>(json, Options);
            return page is { Items: not null } ? page : null;
        }
        catch (JsonException) { return null; }
    }
}
