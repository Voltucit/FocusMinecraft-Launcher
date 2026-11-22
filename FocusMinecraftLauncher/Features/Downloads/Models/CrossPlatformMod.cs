namespace FocusMinecraftLauncher.Features.Downloads.Models;

/// <summary>同一个 Mod 在两个发布平台上的项目。文件仍保留各自的项目 ID 和来源。</summary>
internal sealed record CrossPlatformMod(ResourceProject Modrinth, ResourceProject CurseForge)
{
    public string Id => Modrinth.Id + "+" + CurseForge.Id;
    public ResourceProject[] Sources => [Modrinth, CurseForge];

    public static Dictionary<string, CrossPlatformMod> Match(IEnumerable<ResourceProject> items)
    {
        var projects = items.Where(project => project.Category == DownloadSection.Mods).ToArray();
        var curseForge = projects.Where(project => project.Source == "CurseForge").ToArray();
        var matches = new Dictionary<string, CrossPlatformMod>(StringComparer.Ordinal);
        foreach (var modrinth in projects.Where(project => project.Source == "Modrinth"))
        {
            if (string.IsNullOrWhiteSpace(modrinth.Slug) || string.IsNullOrWhiteSpace(modrinth.Author)) continue;
            var counterpart = curseForge.FirstOrDefault(project =>
                !matches.ContainsKey(project.Id)
                && SameText(modrinth.Slug, project.Slug)
                && SameText(modrinth.Name, project.Name)
                && SharesAuthor(modrinth.Author, project.Author));
            if (counterpart is null) continue;
            var group = new CrossPlatformMod(modrinth, counterpart);
            matches[modrinth.Id] = matches[counterpart.Id] = group;
        }
        return matches;
    }

    public ResourceProject Combine(ResourceProject modrinth, ResourceProject curseForge)
    {
        var files = modrinth.Files.Concat(curseForge.Files).DistinctBy(file => file.Id).ToArray();
        return modrinth with
        {
            Id = Id,
            Source = "Modrinth / CurseForge",
            ChineseName = modrinth.ChineseName ?? curseForge.ChineseName,
            IconUrl = modrinth.IconUrl ?? curseForge.IconUrl,
            LoadedImage = modrinth.LoadedImage ?? curseForge.LoadedImage,
            GameVersions = CombineValues(modrinth.GameVersions, curseForge.GameVersions, files.SelectMany(file => file.GameVersions)),
            Environments = CombineValues(modrinth.Environments, curseForge.Environments, files.SelectMany(file => file.Loaders)),
            Files = files
        };
    }

    private static bool SameText(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SharesAuthor(string left, string? right)
    {
        if (string.IsNullOrWhiteSpace(right)) return false;
        return left.Split(" / ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Intersect(right.Split(" / ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase).Any();
    }

    private static string[] CombineValues(string[]? left, string[]? right, IEnumerable<string> fromFiles) =>
        (left ?? []).Concat(right ?? []).Concat(fromFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
