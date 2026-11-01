using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using FMCore.Versions;
using FocusMinecraftLauncher.Preview;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>保存启动器的实例显示信息和覆盖设置；版本解析、继承和启动仍交由 FMCore。</summary>
public static class InstanceStore
{
    public const string FileName = "fmlauncher.instance.json";
    private static readonly SemaphoreSlim Writer = new(1, 1);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public static string Key(string root, string id) => Path.GetFullPath(root).TrimEnd('\\', '/') + "|" + id;
    public static InstanceOptions Read(PreviewInstance instance)
    {
        var path = Path.Combine(instance.Folder, FileName);
        var options=File.Exists(path) ? JsonSerializer.Deserialize<InstanceOptions>(File.ReadAllText(path), Options) ?? new() : new();
        // Windows 路径不区分大小写；反序列化后仍按相同规则识别来源，防止把同一文件当成未知资源。
        var resources=new Dictionary<string,ResourceOrigin>(StringComparer.OrdinalIgnoreCase);
        foreach(var item in options.Resources)resources[item.Key.Replace('\\','/')]=item.Value;
        options.Resources=resources;
        var shared = new Dictionary<string, ResourceOrigin>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in options.SharedResources) shared[item.Key.Replace('\\', '/')] = item.Value;
        if (!instance.UsesGameIsolation)
        {
            var pathShared = ModpackArchive.SafePath(instance.Root, ".fmlauncher/shared-resource-sources.json");
            if (File.Exists(pathShared))
            {
                var saved = JsonSerializer.Deserialize<Dictionary<string, ResourceOrigin>>(File.ReadAllText(pathShared), Options) ?? new();
                foreach (var item in saved) shared[item.Key.Replace('\\', '/')] = item.Value;
            }
        }
        options.SharedResources = shared;
        options.SharedResourceSnapshot = new(shared, StringComparer.OrdinalIgnoreCase);
        return options;
    }
    /// <summary>同目录临时文件替换，不改写 Minecraft 的版本 JSON，不移动用户文件。</summary>
    public static async Task SaveAsync(PreviewInstance instance, InstanceOptions options)
    {
        await Writer.WaitAsync();
        try { await WriteAsync(instance, options); }
        finally { Writer.Release(); }
    }
    /// <summary>在同一个写入锁内读取并修改，避免个性化设置覆盖下载刚保存的资源来源。</summary>
    public static async Task<InstanceOptions> UpdateAsync(PreviewInstance instance, Action<InstanceOptions> update)
    {
        await Writer.WaitAsync();
        try
        {
            var options = Read(instance); update(options);
            await WriteAsync(instance, options); return options;
        }
        finally { Writer.Release(); }
    }
    private static async Task WriteAsync(PreviewInstance instance, InstanceOptions options)
    {
        string path = Path.Combine(instance.Folder, FileName), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if(!Directory.Exists(instance.Folder))throw new DirectoryNotFoundException("实例文件夹已不存在。");
            ModpackArchive.EnsureInstanceWritePath(instance);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(options, Options));
            File.Move(temporary, path, true);
            if (!instance.UsesGameIsolation) await SaveSharedSourcesAsync(instance, options);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    /// <summary>共享目录只有一份来源索引；仅合并本次变更，保留其他实例的记录与仍可用于识别的历史来源。</summary>
    private static async Task SaveSharedSourcesAsync(PreviewInstance instance, InstanceOptions options)
    {
        ModpackArchive.EnsureGameDirectoryWritePath(instance);
        var path = ModpackArchive.SafePath(instance.Root, ".fmlauncher/shared-resource-sources.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var saved = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, ResourceOrigin>>(await File.ReadAllTextAsync(path), Options) ?? new() : new();
            var sources = new Dictionary<string, ResourceOrigin>(saved, StringComparer.OrdinalIgnoreCase);
            foreach (var item in options.SharedResources)
                if (!options.SharedResourceSnapshot.TryGetValue(item.Key, out var previous) || previous != item.Value || !sources.ContainsKey(item.Key)) sources[item.Key] = item.Value;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(sources, Options));
            File.Move(temporary, path, true);
            options.SharedResourceSnapshot = new(options.SharedResources, StringComparer.OrdinalIgnoreCase);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    /// <summary>读取器负责基础元数据和继承；这里只补充 UI 所需的库标识和 Java 版本声明，不构造启动参数。</summary>
    public static async Task<IReadOnlyList<PreviewInstance>> ScanAsync(string root, CancellationToken token)
    {
        var reader = new MinecraftVersionReader();
        var versions = await reader.GetVersionsAsync(root, token);
        var result = new List<PreviewInstance>();
        foreach (var version in versions)
        {
            token.ThrowIfCancellationRequested();
            result.Add(await ReadVersionAsync(version, versions, root, token));
        }
        return result;
    }
    /// <summary>单个实例的父版本缺失不阻止其他实例展示；修复仍使用核心的公开安装及转换接口。</summary>
    public static async Task<PreviewInstance> ReadVersionAsync(MinecraftVersion version, IReadOnlyList<MinecraftVersion> versions, string root, CancellationToken token)
    {
            IReadOnlyList<MinecraftVersion> chain;
            var issue = "";
            try { chain = new MinecraftVersionReader().GetInheritanceChain(version, versions); }
            catch (KeyNotFoundException error) { chain = [version]; issue = RuntimeSupport.Error(error) + "。启动时将尝试修复。"; }
            var libraries = new List<string>();
            int? java = null;
            foreach (var part in chain)
            {
                var file = Path.Combine(part.Directory, Path.GetFileName(part.Directory) + ".json");
                var json = JsonNode.Parse(await File.ReadAllTextAsync(file, token));
                java ??= json?["javaVersion"]?["majorVersion"]?.GetValue<int>();
                if (json?["libraries"] is JsonArray list)
                    libraries.AddRange(list.Select(item => item?["name"]?.GetValue<string>()).OfType<string>());
            }
            var loader = libraries.Any(s => s.StartsWith("net.neoforged:neoforge:")||s.StartsWith("net.neoforged:forge:")) ? "NeoForge"
                : libraries.Any(s => s.StartsWith("net.minecraftforge:forge:")) ? "Forge"
                : libraries.Any(s => s.StartsWith("net.fabricmc:fabric-loader:")) ? "Fabric"
                : libraries.Any(s => s.StartsWith("optifine:OptiFine:")) ? "OptiFine" : "原版";
            var coordinates = libraries.FirstOrDefault(s => loader switch
            { "NeoForge" => s.StartsWith("net.neoforged:neoforge:")||s.StartsWith("net.neoforged:forge:"), "Forge" => s.StartsWith("net.minecraftforge:forge:"), "Fabric" => s.StartsWith("net.fabricmc:fabric-loader:"), "OptiFine" => s.StartsWith("optifine:OptiFine:"), _ => false });
            var gameVersion=chain.Last().Id;
            var loaderVersion=coordinates?.Split(':').ElementAtOrDefault(2)??"";
            if(loader is "Forge" or "NeoForge"&&loaderVersion.StartsWith(gameVersion+"-"))loaderVersion=loaderVersion[(gameVersion.Length+1)..];
            var instance = new PreviewInstance(version.Id, version.Id, gameVersion, loader, version.VersionType ?? "release")
            { Root = Path.GetFullPath(root), DirectoryPath = version.Directory, RequiredJava = java, LoaderVersion = loaderVersion, MetadataIssue = issue };
            var settings = Read(instance);
            if (!string.IsNullOrWhiteSpace(settings.GameVersion)) instance.Version = settings.GameVersion;
            if (loader is "Forge" or "NeoForge" && instance.LoaderVersion.StartsWith(instance.Version + "-")) instance.LoaderVersion = instance.LoaderVersion[(instance.Version.Length + 1)..];
            instance.Name = settings.Name.Length > 0 ? settings.Name : version.Id;
            instance.Description = settings.Description; instance.Category = settings.Category; instance.IsFavorite = settings.Favorite;
            instance.Options = settings;
            instance.RefreshOptiFineState();
            RefreshIcons(instance, settings);
            return instance;
    }
    /// <summary>自定义、整合包原图与默认图分别管理；兼容旧配置中的在线整合包图标文件。</summary>
    public static void RefreshIcons(PreviewInstance instance,InstanceOptions options)
    {
        instance.IconImage=null;instance.FallbackIconImage=null;
        foreach(var path in new[]{options.PackIconPath,Path.Combine(instance.Folder,"icon.png"),Path.Combine(instance.Folder,"fmlauncher.pack-icon.png")}.Where(path=>path.Length>0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if(!File.Exists(path))continue;
            try {instance.LoadIcon(path);instance.FallbackIconImage=instance.IconImage;instance.IconImage=null;break;}
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or NotSupportedException or FormatException)
            {System.Diagnostics.Trace.TraceWarning("整合包图标读取失败：{0}",error.GetType().Name);}
        }
        if(options.IconPath.Length>0&&File.Exists(options.IconPath))
        {
            try {instance.LoadIcon(options.IconPath);}
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or NotSupportedException or FormatException)
            {System.Diagnostics.Trace.TraceWarning("自定义图标读取失败：{0}",error.GetType().Name);}
        }
    }
}

public sealed class InstanceOptions
{
    /// <summary>安装或转换时记录真实游戏版本，避免独立配置的自定义 ID 被误当成 Minecraft 版本。</summary>
    public string GameVersion { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public bool Favorite { get; set; }
    public string IconPath { get; set; } = "";
    public string PackIconPath { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceProjectId { get; set; } = "";
    public string SourceVersionId { get; set; } = "";
    public OptiFineModComponent? OptiFineMod {get;set;}
    public bool GlobalJava { get; set; } = true;
    public bool GlobalMemory { get; set; } = true;
    public bool GlobalWindow { get; set; } = true;
    public bool GlobalArguments { get; set; } = true;
    public string JavaPath { get; set; } = "";
    public int MemoryMb { get; set; } = 4096;
    public bool AutomaticMemory { get; set; }
    public bool OptimizeMemory { get; set; }
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public bool Fullscreen { get; set; }
    public string JvmArguments { get; set; } = "";
    public string GameArguments { get; set; } = "";
    public Dictionary<string, ResourceOrigin> Resources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ResourceOrigin> SharedResources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, ResourceOrigin> SharedResourceSnapshot { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>分开保留独立目录和共享目录的来源记录，切换隔离方式不会覆盖另一目录的记录。</summary>
    public Dictionary<string, ResourceOrigin> ResourcesFor(PreviewInstance instance) => instance.UsesGameIsolation ? Resources : SharedResources;

    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalFields { get; set; }
}
public sealed record ResourceOrigin(string ProjectId, string VersionId, string Url, string Sha1, string Sha512, long Size, string Name);
