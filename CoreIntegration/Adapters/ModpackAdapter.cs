using FMCore.Modpacks;
namespace FMLauncher.CoreIntegration.Adapters;

/// <summary>只读取正式 SDK 支持的整合包信息，不调用固定 instances 目录的整包安装 API。</summary>
public sealed class ModpackAdapter
{
    public async Task<string> ReadAsync(string archive, CancellationToken ct)
    {
        var pack = await ModpackArchive.ReadAsync(archive, ct);
        return $"{pack.Name} {pack.Version}\nMinecraft {pack.MinecraftVersion}\n加载器：{pack.Loader} {pack.LoaderVersion}\n清单文件：{pack.Files.Count}\n" + string.Join(Environment.NewLine, pack.CompatibilityWarnings);
    }
}
