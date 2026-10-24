using FMCore.Resources;
using FMLauncher.CoreIntegration.Models;
namespace FMLauncher.CoreIntegration.Adapters;
/// <summary>当前仅启用不依赖开发者私钥的 Modrinth 查询；CurseForge 未开通，不读取旧的本地密钥。</summary>
public sealed class ResourceAdapter
{
    private readonly ModrinthClient _modrinth = new("FMLauncher/2.0 (https://github.com/Voltucit/FM_MinecraftLaucher)");
    public async Task<IReadOnlyList<ResourceItem>> SearchAsync(string query, string source, int offset, string version, string loader, CancellationToken ct)
    {
        if (source != "Modrinth") throw new InvalidOperationException("这个资源来源暂未开放，请使用 Modrinth 搜索。");
        return (await _modrinth.SearchAsync(query, new() { Offset = offset, Limit = 20, ProjectType = "mod", MinecraftVersion = Empty(version), Loader = Empty(loader) }, ct))
            .Hits.Select(m => new ResourceItem(m.Id, m.Title, m.Description, source)).ToArray();
    }
    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
