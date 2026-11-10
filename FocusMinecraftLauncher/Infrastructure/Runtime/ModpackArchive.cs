using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FMCore.Downloads;
using FocusMinecraftLauncher.Preview;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

/// <summary>Modrinth mrpack 格式读写；游戏和加载器仍通过核心安装，归档路径不得逃离实例。</summary>
public static partial class ModpackArchive
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string SafePath(string root, string relative)
    {
        var parts = ValidateRelative(relative);
        var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(basePath, Path.Combine(parts)));
        if (!path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("文件路径超出游戏范围。");
        var current = basePath.TrimEnd(Path.DirectorySeparatorChar);
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("不能通过目录链接写入游戏外的文件。");
        }
        return path;
    }
    private static string[] ValidateRelative(string relative)
    {
        var parts=relative.Replace('\\','/').Split('/');
        if(parts.Length==0||parts.Any(s=>string.IsNullOrWhiteSpace(s)||s is "." or ".."||s.IndexOfAny(Path.GetInvalidFileNameChars())>=0||s.EndsWith('.')||s.EndsWith(' ')||ReservedName(s)))throw new InvalidDataException("整合包包含无效的文件路径。");
        return parts;
    }
    public static void EnsureInstanceWritePath(PreviewInstance instance)
    {
        var versions=Path.GetFullPath(Path.Combine(instance.Root,"versions"));var folder=Path.GetFullPath(instance.Folder);
        if(!string.Equals(Path.GetDirectoryName(folder),versions,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("游戏必须位于所选目录的 versions 文件夹内。");
        _ = SafePath(instance.Root,"versions/"+Path.GetFileName(folder));
    }
    /// <summary>版本元数据仍在 versions 内，游戏内容仅允许写入该版本目录或明确选择的共享根目录。</summary>
    public static void EnsureGameDirectoryWritePath(PreviewInstance instance)
    {
        EnsureInstanceWritePath(instance);
        var game = Path.GetFullPath(instance.GameDirectory);
        if (!game.Equals(Path.GetFullPath(instance.Root), StringComparison.OrdinalIgnoreCase)
            && !game.Equals(Path.GetFullPath(instance.Folder), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("游戏目录不属于当前游戏或所选共享目录。");
        if ((Directory.Exists(game) || File.Exists(game)) && File.GetAttributes(game).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("不能通过目录链接写入游戏目录。");
    }
    private static bool SharedCorePath(string relative) => new[] { "versions", "libraries", "assets", "natives", ".fmcore" }
        .Contains(relative.Replace('\\', '/').Split('/')[0], StringComparer.OrdinalIgnoreCase);
    /// <summary>整合包允许的下载地址：Modrinth CDN、代码托管与 CurseForge CDN；下载后仍按索引里的 SHA-1 和 SHA-512 校验。</summary>
    private static bool AllowedDownload(string address)
        => Uri.TryCreate(address, UriKind.Absolute, out var url) && url.Scheme == "https" && url.UserInfo.Length == 0
            && (url.Host is "cdn.modrinth.com" or "github.com" or "raw.githubusercontent.com" or "gitlab.com"
                || url.Host.Equals("forgecdn.net", StringComparison.OrdinalIgnoreCase)
                || url.Host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase));
    private static bool ReservedName(string name)=>System.Text.RegularExpressions.Regex.IsMatch(name.Split('.')[0],@"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    /// <summary>确认导入前只读取包内清单；CurseForge 资源的联网解析留在可取消的导入任务中。</summary>
    public static PackManifest ReadPreview(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.GetEntry("manifest.json") != null && archive.GetEntry("modrinth.index.json") == null
            ? ReadCurseForgeIndex(archive) : Read(path);
    }

    /// <summary>读取完整资源信息；CurseForge 的清单编号需要联网补齐文件名、地址和校验值。</summary>
    public static async Task<PackManifest> ReadAsync(string path, CancellationToken token, Action<int, int>? metadataProgress = null)
    {
        using (var archive = ZipFile.OpenRead(path))
            if (archive.GetEntry("manifest.json") != null && archive.GetEntry("modrinth.index.json") == null)
                return await ReadCurseForgeAsync(path, token, metadataProgress);
        return Read(path);
    }

    private static PackManifest ReadCurseForgeIndex(ZipArchive archive)
    {
        var entry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("此文件不是可识别的整合包。");
        if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("整合包索引过大。");
        using var reader = new StreamReader(entry.Open(), System.Text.Encoding.UTF8);
        var json = JsonNode.Parse(reader.ReadToEnd()) ?? throw new InvalidDataException("整合包索引无效。");
        var minecraft = json["minecraft"] as JsonObject ?? throw new InvalidDataException("整合包缺少游戏版本声明。");
        var version = minecraft["version"]?.GetValue<string>() ?? throw new InvalidDataException("缺少 Minecraft 版本。");
        var loaderId = (minecraft["modLoaders"] as JsonArray)?.Select(item => item?["id"]?.GetValue<string>()).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)) ?? "";
        var separator = loaderId.IndexOf('-');
        var loader = separator > 0 ? loaderId[..separator] : "";
        var loaderVersion = separator > 0 ? loaderId[(separator + 1)..] : "";
        var overridesDirectory = json["overrides"]?.GetValue<string>() ?? "overrides";
        _ = ValidateRelative(overridesDirectory.Replace('\\', '/').TrimEnd('/'));
        var references = new List<CurseForgePackReference>();
        foreach (var item in json["files"] as JsonArray ?? throw new InvalidDataException("整合包文件列表无效。"))
        {
            var projectId = item?["projectID"]?.GetValue<long>() ?? 0;
            var fileId = item?["fileID"]?.GetValue<long>() ?? 0;
            if (projectId <= 0 || fileId <= 0) throw new InvalidDataException("整合包资源缺少有效的 CurseForge 工程或文件编号。");
            references.Add(new(projectId, fileId, item?["required"]?.GetValue<bool>() != false));
        }
        return new(json["name"]?.GetValue<string>() ?? "Modpack", "", version,
            loader switch { "fabric" => "Fabric", "forge" => "Forge", "neoforge" => "NeoForge", _ => "原版" }, loaderVersion, [])
            { OverridesDirectory = overridesDirectory, PendingCurseForgeFiles = references };
    }

    private static async Task<PackManifest> ReadCurseForgeAsync(string path, CancellationToken token, Action<int, int>? metadataProgress)
    {
        using var archive = ZipFile.OpenRead(path);
        var manifest = ReadCurseForgeIndex(archive);
        var references = manifest.PendingCurseForgeFiles;
        var overrides = ReadOverrideEntries(archive, manifest.OverridesDirectory);
        var client = new CurseForgeClient();
        var projects = new ConcurrentDictionary<long, Lazy<Task<CurseForgeMod?>>>();
        var files = new PackFile[references.Count];
        using var archiveGate = new SemaphoreSlim(1, 1);
        var progressGate = new object();
        var completed = 0;
        metadataProgress?.Invoke(0, references.Count);
        await Parallel.ForEachAsync(Enumerable.Range(0, references.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token }, async (index, readToken) =>
        {
            var reference = references[index];
            var projectId = reference.ProjectId;
            var fileId = reference.FileId;
            // 文件和工程信息互不依赖，同时发起，避免串行等待两次接口往返。
            var projectTask = projects.GetOrAdd(projectId,
                id => new Lazy<Task<CurseForgeMod?>>(() => ReadProjectAsync(id, readToken))).Value;
            CurseForgeFile file;
            string fallbackSource = "";
            try { file = await client.GetFileAsync(projectId, fileId, readToken); }
            catch (Exception error) when (error is HttpRequestException or InvalidDataException or IOException)
            {
                try
                {
                    var fallback = await client.GetFallbackFileAsync(projectId, fileId, readToken);
                    string directory = Path.Combine(Path.GetTempPath(), "FMLauncher", "CurseForgeFallback",
                        projectId.ToString(CultureInfo.InvariantCulture));
                    string local = Path.Combine(directory, fallback.FileName);
                    string sha1 = await client.DownloadFallbackAsync(fallback, local, readToken);
                    file = new CurseForgeFile(fallback.Id, fallback.FileName, fallback.FileName, fallback.DownloadUrl,
                        fallback.FileLength, sha1, DateOnly.FromDateTime(DateTime.Today), 0, 0, [], [], []);
                    fallbackSource = local;
                }
                catch (Exception fallbackError) when (fallbackError is not OperationCanceledException)
                {
                    throw new InvalidDataException($"整合包资源 {projectId}/{fileId} 的信息读取失败：镜像和官方兜底都不可用。",
                        new AggregateException(error, fallbackError));
                }
            }
            if (file.Id != fileId || file.Sha1 is not { Length: 40 } || !file.Sha1.All(Uri.IsHexDigit) || file.FileLength < 0)
                throw new InvalidDataException($"整合包资源 {file.FileName}（{projectId}/{fileId}）缺少有效的大小或 SHA-1，无法校验指定版本。");
            var project = await projectTask;
            var folder = project?.ClassId switch { 12 => "resourcepacks", 6552 => "shaderpacks", 6945 => "datapacks", _ => "mods" };
            var relative = folder + "/" + file.FileName;
            _ = ValidateRelative(relative);
            var resource = new PackFile(relative, CurseForgeDownloadUrls(file),
                file.Sha1, "", file.FileLength, reference.Required ? "required" : "optional")
            {
                CurseForgeProjectId = projectId, CurseForgeFileId = fileId,
                DownloadPage = CurseForgeFilePage(project?.Page, fileId),
                SourcePath = fallbackSource
            };
            if (overrides.TryGetValue(relative, out var bundled))
            {
                // ZipArchive 的条目共享底层流；联网可并发，归档校验保持串行。
                await archiveGate.WaitAsync(readToken);
                try
                {
                    if (!await MatchesEntryAsync(resource, bundled, readToken))
                        throw new InvalidDataException($"包内文件 {relative} 与 CurseForge 指定版本 {fileId} 的大小或 SHA-1 不一致。");
                }
                finally { archiveGate.Release(); }
                resource = resource with { IsBundled = true };
            }
            files[index] = resource;
            lock (progressGate) { completed++; metadataProgress?.Invoke(completed, references.Count); }
        });
        if (files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length)
            throw new InvalidDataException("整合包包含重复资源路径。");
        return manifest with { Files = files, PendingCurseForgeFiles = [] };

        async Task<CurseForgeMod?> ReadProjectAsync(long id, CancellationToken readToken)
        {
            try { return await client.GetModAsync(id, readToken); }
            catch (Exception error) when (error is HttpRequestException or InvalidDataException or IOException) { return null; }
        }
    }

    public static PackManifest Read(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var index = archive.GetEntry("modrinth.index.json") ?? throw new InvalidDataException("此文件不是 Modrinth 整合包。当前不支持 CurseForge 格式。");
        if (index.Length > 16 * 1024 * 1024) throw new InvalidDataException("整合包索引过大。");
        using var reader = new StreamReader(index.Open(), System.Text.Encoding.UTF8);
        var json = JsonNode.Parse(reader.ReadToEnd()) ?? throw new InvalidDataException("整合包索引无效。");
        if (json["formatVersion"]?.GetValue<int>() != 1 || json["game"]?.GetValue<string>() != "minecraft") throw new InvalidDataException("不支持的整合包格式版本。");
        var dependencies = json["dependencies"] as JsonObject ?? throw new InvalidDataException("整合包缺少游戏版本声明。");
        if (dependencies.Any(p => p.Key is not ("minecraft" or "fabric-loader" or "forge" or "neoforge"))) throw new NotSupportedException("整合包使用当前不支持的加载器：" + string.Join("、", dependencies.Where(p => p.Key is not ("minecraft" or "fabric-loader" or "forge" or "neoforge")).Select(p => p.Key)));
        var loaders = dependencies.Where(p => p.Key != "minecraft").ToArray();
        if (loaders.Length > 1) throw new InvalidDataException("整合包声明了冲突的加载器。");
        var files = new List<PackFile>();
        foreach (var item in json["files"] as JsonArray ?? throw new InvalidDataException("整合包文件列表无效。"))
        {
            var relative = item?["path"]?.GetValue<string>() ?? throw new InvalidDataException("资源缺少路径。");
            _ = ValidateRelative(relative);
            var sha1 = item?["hashes"]?["sha1"]?.GetValue<string>() ?? "";
            var sha512 = item?["hashes"]?["sha512"]?.GetValue<string>() ?? "";
            if (sha1.Length != 40 || sha512.Length != 128 || !sha1.All(Uri.IsHexDigit) || !sha512.All(Uri.IsHexDigit)) throw new InvalidDataException("整合包资源缺少有效校验值。");
            var urls = (item?["downloads"] as JsonArray)?.Select(u => u?.GetValue<string>() ?? "").ToArray() ?? [];
            foreach (var address in urls)
                if (!AllowedDownload(address)) throw new InvalidDataException("整合包包含未经允许的下载地址：" + (Uri.TryCreate(address, UriKind.Absolute, out var addressUri) ? addressUri.Host : address));
            if (urls.Length == 0) throw new InvalidDataException("整合包资源缺少下载地址。");
            var client = item?["env"]?["client"]?.GetValue<string>() ?? "required";
            if (client is not ("required" or "optional" or "unsupported")) throw new InvalidDataException("资源客户端环境声明无效。");
            var size = item?["fileSize"]?.GetValue<long>() ?? -1;
            if (size < 0) throw new InvalidDataException("资源文件大小无效。");
            files.Add(new(relative, urls, sha1, sha512, size, client));
        }
        if (files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count) throw new InvalidDataException("整合包包含重复资源路径。");
        foreach (var entry in archive.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith("overrides/") || name.StartsWith("client-overrides/"))
            {
                _ = ValidateRelative(name[(name.IndexOf('/') + 1)..]);
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("整合包不能包含文件链接。");
            }
        }
        var key = loaders.FirstOrDefault();
        return new(json["name"]?.GetValue<string>() ?? "Modpack", json["summary"]?.GetValue<string>() ?? "", dependencies["minecraft"]?.GetValue<string>() ?? throw new InvalidDataException("缺少 Minecraft 版本。"),
            key.Key switch { "fabric-loader" => "Fabric", "forge" => "Forge", "neoforge" => "NeoForge", _ => "原版" }, key.Value?.GetValue<string>() ?? "", files);
    }
    private static Dictionary<string, ZipArchiveEntry> ReadOverrideEntries(ZipArchive archive, string overridesDirectory)
    {
        overridesDirectory = overridesDirectory.Replace('\\', '/').TrimEnd('/');
        _ = ValidateRelative(overridesDirectory);
        var overrides = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in new[] { overridesDirectory + "/", "client-overrides/" }.Distinct(StringComparer.Ordinal))
            foreach (var entry in archive.Entries.Where(e => e.FullName.Replace('\\', '/').StartsWith(layer, StringComparison.Ordinal) && !e.FullName.Replace('\\', '/').EndsWith('/')))
            {
                var relative = entry.FullName[layer.Length..].Replace('\\', '/');
                _ = ValidateRelative(relative);
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("整合包不能包含文件链接。");
                overrides[relative] = entry;
            }
        return overrides;
    }

    private static Dictionary<string, ZipArchiveEntry> ReadOverrides(ZipArchive archive, PreviewInstance instance, PackManifest manifest)
    {
        var overrides = ReadOverrideEntries(archive, manifest.OverridesDirectory);
        foreach (var relative in overrides.Keys) ProtectProfile(relative, instance.Id, !instance.UsesGameIsolation);
        return overrides;
    }

    public static async Task InstallContentAsync(string archivePath, PackManifest manifest, PreviewInstance instance, InstanceOptions options, int concurrency, Action<DownloadBatchProgress> progress, CancellationToken token, Func<string, bool>? include = null)
    {
        EnsureGameDirectoryWritePath(instance);
        using var archive = ZipFile.OpenRead(archivePath);
        var overrides = ReadOverrides(archive, instance, manifest);
        var files = manifest.Files.Where(f => f.Client == "required" && !overrides.ContainsKey(f.Path.Replace('\\','/')) && (include == null || include(f.Path))).ToArray();
        foreach (var file in files) ProtectProfile(file.Path, instance.Id, !instance.UsesGameIsolation);
        var manualFiles = files.Where(file => file.Urls.Length == 0 || file.SourcePath.Length > 0).ToArray();
        long copied = 0, copyTotal = manualFiles.Sum(file => file.Size);
        for (var index = 0; index < manualFiles.Length; index++)
        {
            var file = manualFiles[index];
            await CopyManualFileAsync(file, SafePath(instance.GameDirectory, file.Path), token);
            copied += file.Size;
            progress(new DownloadBatchProgress(manualFiles.Length, index + 1, 0, 1, copied, copyTotal));
        }
        var downloads = files.Where(file => file.Urls.Length > 0 && file.SourcePath.Length == 0).ToArray();
        await DownloadPackFilesAsync(downloads, instance, concurrency, progress, token);
        foreach (var file in files)
        {
            await using var stream = File.OpenRead(SafePath(instance.GameDirectory, file.Path));
            if (file.Sha512.Length > 0 && !Convert.ToHexString(await SHA512.HashDataAsync(stream, token)).Equals(file.Sha512, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("整合包资源 SHA-512 校验失败：" + file.Path);
            RecordPackOrigin(file, instance, options);
        }
        foreach(var item in overrides.Where(item => include == null || include(item.Key)))
        {
            token.ThrowIfCancellationRequested();var destination=SafePath(instance.GameDirectory,item.Key);
            if(File.Exists(destination))
            {
                await using var current=File.OpenRead(destination);await using var expected=item.Value.Open();
                var currentHash=await SHA512.HashDataAsync(current,token);
                var expectedHash=await SHA512.HashDataAsync(expected,token);
                var same=currentHash.SequenceEqual(expectedHash);
                if(same)continue;
                throw new IOException("已有文件与整合包内容不同，未继续覆盖："+item.Key);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                await using(var input=item.Value.Open())await using(var output=File.Create(temporary))await input.CopyToAsync(output,token);
                token.ThrowIfCancellationRequested();File.Move(temporary,destination,false);
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
            options.ResourcesFor(instance).Remove(item.Key);
        }
        foreach (var file in manifest.Files.Where(file => file.Client == "required" && file.IsBundled && (include == null || include(file.Path))))
        {
            if (!await MatchesFileAsync(file, SafePath(instance.GameDirectory, file.Path), token))
                throw new InvalidDataException("包内文件与指定版本的校验值不一致：" + file.FileName);
            RecordPackOrigin(file, instance, options);
        }
    }
    private static void ProtectProfile(string relative, string id, bool shared = false)
    {
        if (shared && SharedCorePath(relative)) throw new InvalidDataException("整合包不能覆盖共享目录中的游戏核心文件。");
        var path = relative.Replace('\\', '/'); var first = path.Split('/')[0];
        if (first.Equals(id + ".json", StringComparison.OrdinalIgnoreCase) || first.Equals(id + ".jar", StringComparison.OrdinalIgnoreCase) || first.Equals(InstanceStore.FileName, StringComparison.OrdinalIgnoreCase)
            || first.Equals("FMLauncher", StringComparison.OrdinalIgnoreCase) || first.Equals("versions", StringComparison.OrdinalIgnoreCase) || first.Contains("account", StringComparison.OrdinalIgnoreCase) || first.Contains("token", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("整合包不能覆盖启动器、账户或核心版本配置。");
    }
    /// <summary>只枚举实例内的常规文件，不跟随链接；凭据、日志、历史和服务记录默认不进入导出。</summary>
    public static IEnumerable<string> Files(string root, bool skipSharedCore = false)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory))
                if (!File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) yield return file;
            foreach (var child in Directory.EnumerateDirectories(directory))
                if (!File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint)
                    && !(skipSharedCore && directory == root && SharedCorePath(Path.GetFileName(child)))) pending.Push(child);
        }
    }
    public static bool Private(string relative) => relative.Split('/', '\\').Any(p => p.Equals(".fmlauncher",StringComparison.OrdinalIgnoreCase)||p.Contains("account", StringComparison.OrdinalIgnoreCase) || p.Contains("token", StringComparison.OrdinalIgnoreCase) || p is "launcher_profiles.json" or "launcher_accounts.json" or "usercache.json" or "usernamecache.json" or "logs" or "crash-reports" or "command_history.txt");
    /// <summary>扫描并按真实读取字节报告进度；临时归档完成且原子替换成功后才报告完成。</summary>
    public static async Task ExportAsync(PreviewInstance instance,string destination,string name,string version,string summary,IEnumerable<string> selected,bool bundleResources,bool onlyModrinth,Action<PackExportProgress> progress,CancellationToken token)
    {
        var temporary=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
        int completedFiles=0,totalFiles=0;long processedBytes=0,totalBytes=0;
        var reportClock=System.Diagnostics.Stopwatch.StartNew();long lastReport=-100;
        void Report(string stage,bool force=false)
        {
            if(!force&&reportClock.ElapsedMilliseconds-lastReport<100)return;
            lastReport=reportClock.ElapsedMilliseconds;progress(new(stage,completedFiles,totalFiles,processedBytes,totalBytes));
        }
        try
        {
            Report("校验配置",true);var options=InstanceStore.Read(instance);
            var paths=selected.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var indexed=new JsonArray();var overrides=new List<(string Path,string File,long Size)>();
            var sources=new List<(string Path,string File,long Size,ResourceOrigin? Origin)>();
            Report("扫描所选文件",true);
            foreach(var relative in paths)
            {
                token.ThrowIfCancellationRequested();
                if (!instance.UsesGameIsolation && SharedCorePath(relative)) throw new InvalidDataException("不能导出共享目录中的游戏核心文件。");
                if(Private(relative))throw new InvalidDataException("不能导出账户凭据、历史记录或启动器备份："+relative);
                var file=SafePath(instance.GameDirectory,relative);
                if(!File.Exists(file))throw new FileNotFoundException("所选文件已不存在："+relative);
                var size=new FileInfo(file).Length;var origin=options.ResourcesFor(instance).GetValueOrDefault(relative);
                if(onlyModrinth&&relative.Split('/')[0] is "mods" or "resourcepacks" or "shaderpacks")
                    if(origin==null||!Uri.TryCreate(origin.Url,UriKind.Absolute,out var address)||address.Scheme!="https"||address.Host!="cdn.modrinth.com")
                        throw new InvalidDataException("资源不能确认为 Modrinth 来源："+relative);
                sources.Add((relative,file,size,origin));
                checked { if(origin!=null)totalBytes+=size;if(origin==null||bundleResources)totalBytes+=size; }
            }
            totalFiles=sources.Count+1; // 包括实际写入的 modrinth.index.json。
            Report("收集资源",true);
            foreach(var source in sources)
            {
                token.ThrowIfCancellationRequested();
                if(source.Origin is {} origin && AllowedDownload(origin.Url))
                {
                    using var sha1=IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                    using var sha512=IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
                    await using var input=File.OpenRead(source.File);
                    if(input.Length!=source.Size)throw new IOException("导出期间文件大小发生变化："+source.Path);
                    var buffer=new byte[65536];int count;
                    while((count=await input.ReadAsync(buffer,token))>0)
                    {
                        sha1.AppendData(buffer,0,count);sha512.AppendData(buffer,0,count);processedBytes+=count;Report("收集资源");
                    }
                    var actualSha1=Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
                    var actualSha512=Convert.ToHexString(sha512.GetHashAndReset()).ToLowerInvariant();
                    if(!actualSha1.Equals(origin.Sha1,StringComparison.OrdinalIgnoreCase)||source.Size!=origin.Size)
                        throw new InvalidDataException("资源已修改，无法使用原下载地址导出："+source.Path);
                    if(!bundleResources)
                    {
                        indexed.Add(new JsonObject{["path"]=source.Path,["hashes"]=new JsonObject{["sha1"]=actualSha1,["sha512"]=actualSha512},["downloads"]=new JsonArray(origin.Url),["fileSize"]=source.Size});
                        completedFiles++;
                    }
                    else overrides.Add((source.Path,source.File,source.Size));
                }
                else overrides.Add((source.Path,source.File,source.Size));
                Report("收集资源");
            }
            var dependencies=new JsonObject{["minecraft"]=instance.Version};
            if(instance.Loader is "Fabric" or "Forge" or "NeoForge")
            {
                if(instance.LoaderVersion.Length==0)throw new InvalidDataException("未能读取加载器版本。");
                var loaderVersion=instance.Loader=="Forge"&&instance.LoaderVersion.StartsWith(instance.Version+"-")?instance.LoaderVersion[(instance.Version.Length+1)..]:instance.LoaderVersion;
                dependencies[instance.Loader=="Fabric"?"fabric-loader":instance.Loader.ToLowerInvariant()]=loaderVersion;
            }
            if(instance.Loader=="OptiFine")throw new NotSupportedException("Modrinth 整合包声明不支持独立 OptiFine 加载器，请导出原版或支持的 Mod 加载器游戏。");
            var index=new JsonObject{["formatVersion"]=1,["game"]="minecraft",["name"]=name,["versionId"]=version,["summary"]=summary,["dependencies"]=dependencies,["files"]=indexed};
            Report("打包",true);
            using(var archive=ZipFile.Open(temporary,ZipArchiveMode.Create))
            {
                var entry=archive.CreateEntry("modrinth.index.json");
                await using(var output=entry.Open())await JsonSerializer.SerializeAsync(output,index,Json,token);
                completedFiles++;Report("打包");
                var buffer=new byte[65536];
                foreach(var item in overrides)
                {
                    token.ThrowIfCancellationRequested();
                    var target=archive.CreateEntry("overrides/"+item.Path,CompressionLevel.Optimal);
                    await using var input=File.OpenRead(item.File);await using var output=target.Open();
                    if(input.Length!=item.Size)throw new IOException("导出期间文件大小发生变化："+item.Path);
                    int count;
                    while((count=await input.ReadAsync(buffer,token))>0)
                    {await output.WriteAsync(buffer.AsMemory(0,count),token);processedBytes+=count;Report("打包");}
                    completedFiles++;Report("打包");
                }
            }
            token.ThrowIfCancellationRequested();File.Move(temporary,destination,true);Report("完成",true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }

}
public sealed record PackFile(string Path, string[] Urls, string Sha1, string Sha512, long Size, string Client)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public bool IsBundled { get; init; }
    public long CurseForgeProjectId { get; init; }
    public long CurseForgeFileId { get; init; }
    public string DownloadPage { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public bool RequiresManualFile => Client == "required" && !IsBundled && Urls.Length == 0;
}
public sealed record PackManifest(string Name, string Summary, string Minecraft, string Loader, string LoaderVersion, IReadOnlyList<PackFile> Files)
{
    public string OverridesDirectory { get; init; } = "overrides";
    public IReadOnlyList<CurseForgePackReference> PendingCurseForgeFiles { get; init; } = [];
    public int ResourceCount => Files.Count + PendingCurseForgeFiles.Count;
    public int OptionalResourceCount => Files.Count(file => file.Client == "optional") + PendingCurseForgeFiles.Count(file => !file.Required);
}
public sealed record CurseForgePackReference(long ProjectId, long FileId, bool Required);

public sealed record PackExportProgress(string Stage,int CompletedFiles,int TotalFiles,long ProcessedBytes,long TotalBytes);
