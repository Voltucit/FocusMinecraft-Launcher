using System.IO;
using System.Windows;
using FMCore.Downloads;
using FMCore.ModsResources;
using FocusMinecraftLauncher.Features.Downloads.Models;
using FocusMinecraftLauncher.Infrastructure.Configuration;
using FocusMinecraftLauncher.Infrastructure.Runtime;
using FocusMinecraftLauncher.Presentation.Dialogs;

namespace FocusMinecraftLauncher.Preview;

public sealed partial class PreviewSession
{
    /// <summary>确认目标、版本与必需依赖后下载；不会覆盖来源不明的现有文件。</summary>
    public void DownloadResource(ResourceFileItem file, PreviewInstance? instance)
    {
        if (file.DownloadFile == null || file.CoreVersion == null) throw new InvalidOperationException("这个文件没有可用下载信息。");
        var pack = file.DownloadFile.Filename.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase);
        if (!pack && instance == null) throw new InvalidOperationException("请先选择目标游戏。");
        if (!pack && !file.GameVersions.Contains(instance!.Version)) throw new InvalidOperationException("所选文件不支持目标 Minecraft 版本，请选择其他文件。");
        if (instance != null && Tasks.Any(t => t.IsActive && string.Equals(InstanceStore.Key(t.Root,t.InstanceId),InstanceStore.Key(instance.Root,instance.Id),StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("目标游戏正在运行或处理其他任务。");
        var root = instance?.Root ?? SelectedRoot;
        var targetId = instance?.Id ?? file.ProjectId;
        if (Tasks.Any(t => t.IsActive && t.Kind == PreviewTaskKind.Resource && t.Name == file.FileName && t.Root == root && t.InstanceId == targetId)) throw new InvalidOperationException("这个文件已加入下载列表。");
        PackImportRequest? packImport = null;
        Enqueue(PreviewTaskKind.Resource, file.FileName, string.Join(" / ", file.GameVersions), root, targetId, async (task, token) =>
        {
            var client = Infrastructure.Runtime.CommunitySource.NewClient();
            if (pack)
            {
                var destination = Path.Combine(_configuration.ConfigDirectory, "Downloads", file.DownloadFile.Hashes.Sha1 ?? file.CoreVersion.Id, Path.GetFileName(file.FileName));
                await client.DownloadAsync(file.DownloadFile, destination, p => Report(task, "下载整合包", file: p), token);
                token.ThrowIfCancellationRequested();
                var project=await client.GetProjectAsync(file.ProjectId,token);
                packImport ??= await PrepareModpackImportAsync(destination, root, token);
                if(packImport == null) { Cancel(task); token.ThrowIfCancellationRequested(); }
                await InstallModpackAsync(destination, packImport!, task, token, file.ProjectId, file.CoreVersion.Id, project.IconUrl);
                return;
            }
            var selected = instance!; ModpackArchive.EnsureInstanceWritePath(selected);
            var options = InstanceStore.Read(selected);
            var visited = new HashSet<string>();
            var projectVersions = new Dictionary<string, string>(StringComparer.Ordinal);
            var installs = new List<(ModrinthProject Project, ModrinthVersion Version, ModrinthFile File)>();
            async Task ResolveAsync(ModrinthVersion version, ModrinthFile? preferred = null)
            {
                token.ThrowIfCancellationRequested();
                if (!visited.Add(version.Id)) return;
                if(projectVersions.TryGetValue(version.ProjectId,out var chosen)&&chosen!=version.Id)
                    throw new InvalidDataException("前置资源要求同一项目的不同版本，请调整资源版本后重试。");
                projectVersions[version.ProjectId]=version.Id;
                if (!version.GameVersions.Contains(selected.Version)) throw new InvalidDataException("前置资源 " + version.Name + " 与目标游戏版本不兼容。");
                var project = await client.GetProjectAsync(version.ProjectId, token);
                if(project.ClientSide=="unsupported")throw new InvalidDataException(project.Title+"不支持客户端环境。");
                if (project.ProjectType == "mod" && !version.Loaders.Contains("datapack"))
                {
                    if (!selected.SupportsMods || !version.Loaders.Contains(selected.Loader.ToLowerInvariant())) throw new InvalidDataException(project.Title + " 与当前加载器不兼容。");
                }
                foreach(var incompatible in version.Dependencies.Where(d=>d.DependencyType=="incompatible"))
                {
                    var conflict=options.ResourcesFor(selected).FirstOrDefault(pair=>!pair.Key.EndsWith(".disabled",StringComparison.OrdinalIgnoreCase)
                        && File.Exists(ModpackArchive.SafePath(selected.GameDirectory,pair.Key))
                        && (incompatible.VersionId!=null?pair.Value.VersionId==incompatible.VersionId:pair.Value.ProjectId==incompatible.ProjectId));
                    if(conflict.Value!=null)throw new InvalidDataException(project.Title+" 与已安装资源 "+conflict.Value.Name+" 不兼容，请先在 Mod 管理中处理。");
                }
                foreach (var dependency in version.Dependencies.Where(d => d.DependencyType == "required"))
                {
                    ModrinthVersion dependencyVersion;
                    if (dependency.VersionId != null) dependencyVersion = await client.GetVersionAsync(dependency.VersionId, token);
                    else if (dependency.ProjectId != null)
                    {
                        var dependencyProject = await client.GetProjectAsync(dependency.ProjectId, token);
                        var dependencyLoader = dependencyProject.ProjectType=="datapack"||dependencyProject.Loaders.Contains("datapack")&&version.Loaders.Contains("datapack")
                            ? "datapack" : dependencyProject.ProjectType=="mod"&&selected.SupportsMods ? selected.Loader.ToLowerInvariant() : null;
                        ModrinthVersion? reusable=null;
                        if(projectVersions.TryGetValue(dependency.ProjectId,out var planned))reusable=await client.GetVersionAsync(planned,token);
                        else
                        {
                            var installed=options.ResourcesFor(selected).Where(pair=>pair.Value.ProjectId==dependency.ProjectId&&pair.Value.VersionId.Length>0&&!pair.Key.EndsWith(".disabled",StringComparison.OrdinalIgnoreCase)
                                &&File.Exists(ModpackArchive.SafePath(selected.GameDirectory,pair.Key))).Select(pair=>pair.Value.VersionId).Distinct().ToArray();
                            if(installed.Length==1)
                            {
                                try{reusable=await client.GetVersionAsync(installed[0],token);}
                                catch(System.Net.Http.HttpRequestException error) when(error.StatusCode==System.Net.HttpStatusCode.NotFound){Log("原前置版本已不在资源服务中，重新选择兼容版本。");}
                            }
                        }
                        if(reusable!=null&&reusable.GameVersions.Contains(selected.Version)&&(dependencyLoader==null||reusable.Loaders.Contains(dependencyLoader)))dependencyVersion=reusable;
                        else
                        {
                            var candidates = await client.GetVersionsAsync(dependency.ProjectId, selected.Version, dependencyLoader, token);
                            dependencyVersion = candidates.FirstOrDefault() ?? throw new InvalidDataException("没有适用于当前版本的必需前置资源：" + dependency.ProjectId);
                        }
                    }
                    else throw new InvalidDataException("前置资源只提供文件名，无法确定应下载的文件：" + dependency.FileName);
                    await ResolveAsync(dependencyVersion);
                }
                installs.Add((project, version, preferred ?? version.PrimaryFile ?? throw new InvalidDataException("资源没有可下载文件。")));
            }
            await ResolveAsync(file.CoreVersion!, file.DownloadFile);
            foreach(var item in installs)
                foreach(var conflict in item.Version.Dependencies.Where(dependency=>dependency.DependencyType=="incompatible"))
                    if(conflict.VersionId!=null?installs.Any(other=>other.Version.Id==conflict.VersionId):conflict.ProjectId!=null&&projectVersions.ContainsKey(conflict.ProjectId))
                        throw new InvalidDataException(item.Project.Title+" 与所选的其他前置资源不兼容。");
            string? datapackFolder = null;
            if (installs.Any(i => i.Project.ProjectType == "datapack" || i.Version.Loaders.Contains("datapack")))
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择目标存档文件夹", InitialDirectory = Path.Combine(selected.GameDirectory, "saves") };
                if (dialog.ShowDialog(Application.Current.MainWindow) != true) {Cancel(task); throw new OperationCanceledException(token); }
                var saves = Path.GetFullPath(Path.Combine(selected.GameDirectory, "saves")) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(dialog.FolderName).StartsWith(saves, StringComparison.OrdinalIgnoreCase)||!File.Exists(Path.Combine(dialog.FolderName,"level.dat"))) throw new InvalidOperationException("请选择当前游戏 saves 内的存档文件夹。");
                datapackFolder = Path.Combine(dialog.FolderName, "datapacks");
            }
            var files=new List<ResourceInstallFile>();
            foreach (var item in installs)
            {
                var directory = item.Version.Loaders.Contains("datapack") || item.Project.ProjectType == "datapack" ? datapackFolder!
                    : Path.Combine(selected.GameDirectory, item.Project.ProjectType switch { "resourcepack" => "resourcepacks", "shader" => "shaderpacks", _ => "mods" });
                var format = LauncherConfiguration.Text(_configuration.ReadLauncher()["downloads"]?["fileNameFormat"], "原文件名");
                var name = format switch { "资源名称 + 版本号" => item.Project.Title + "-" + item.Version.VersionNumber + Path.GetExtension(item.File.Filename), "译名 + 原文件名" or "资源名称 + 原文件名" => "[" + item.Project.Title + "] " + item.File.Filename, _ => item.File.Filename };
                name = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
                _ = ModpackArchive.SafePath(selected.GameDirectory,Path.GetRelativePath(selected.GameDirectory,Path.Combine(directory,name)));
                var destination = ModpackArchive.SafePath(directory, name);
                var relative = Path.GetRelativePath(selected.GameDirectory, destination).Replace('\\', '/');
                files.Add(new(relative,item.Project.Id,item.Version.Id,item.Project.Title,item.File));
            }
            var plan=await ResourceFileInstaller.PlanAsync(selected,files,token);
            if(plan.Replacements.Count>0)
            {
                var names=string.Join("\n",plan.Replacements.Take(10).Select(item=>Path.GetFileName(item.RelativePath)));
                if(MessageBox.Show($"将替换 {plan.Replacements.Count} 个旧资源文件，旧文件会保留备份。\n\n{names}\n\n是否继续？","更换资源版本",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK)
                {Cancel(task);token.ThrowIfCancellationRequested();}
            }
            await ResourceFileInstaller.ApplyAsync(selected,plan,client,_configuration.ConfigDirectory,(stage,progress)=>Report(task,stage,file:progress),token,DownloadConcurrency);
            NotifyTask(file.FileName + "下载完成", false);
        });
    }

    /// <summary>CurseForge 文件走镜像直链：必需前置按工程编号递归补齐，已存在的同名文件不覆盖。</summary>
    public void DownloadCurseForgeResource(ResourceFileItem file, PreviewInstance? instance)
    {
        if(file.Download==null)throw new InvalidOperationException("这个文件没有可用下载信息。");
        var pack=file.Section==DownloadSection.Modpacks;
        if(!pack&&instance==null)throw new InvalidOperationException("请先选择目标游戏。");
        if(!pack&&!file.GameVersions.Contains(instance!.Version))throw new InvalidOperationException("所选文件不支持目标 Minecraft 版本，请选择其他文件。");
        if(!pack&&file.Section==DownloadSection.Mods&&file.Loaders.Length>0&&!file.Loaders.Contains(instance!.Loader,StringComparer.OrdinalIgnoreCase))throw new InvalidOperationException("所选文件不支持目标加载器，请选择其他文件。");
        if(instance!=null&&Tasks.Any(t=>t.IsActive&&string.Equals(InstanceStore.Key(t.Root,t.InstanceId),InstanceStore.Key(instance.Root,instance.Id),StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("目标游戏正在运行或处理其他任务。");
        var root=instance?.Root??SelectedRoot;
        var targetId=instance?.Id??file.ProjectId;
        if(Tasks.Any(t=>t.IsActive&&t.Kind==PreviewTaskKind.Resource&&t.Name==file.FileName&&t.Root==root&&t.InstanceId==targetId))throw new InvalidOperationException("这个文件已加入下载列表。");
        Enqueue(PreviewTaskKind.Resource, file.FileName, string.Join(" / ", file.GameVersions), root, targetId, async (task, token) =>
        {
            if(pack)
            {
                var archive=Path.Combine(_configuration.ConfigDirectory,"Downloads","curseforge",file.FileName);
                Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
                await FileDownloader.DownloadManyAsync([CurseForgeRequest(file, archive)],1,p=>Report(task,"下载整合包",p),token);
                await ImportModpackAsync(archive,file.ProjectId,file.Id);
                return;
            }
            var selected=instance!;ModpackArchive.EnsureInstanceWritePath(selected);
            var client=new CurseForgeClient();
            var pending=new Queue<ResourceFileItem>();pending.Enqueue(file);
            var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var downloads = new List<DownloadRequest>();
            var dependencyChoices = new Dictionary<long, CurseForgeFile>();
            Report(task, "解析资源与必需前置");
            while(pending.Count>0)
            {
                token.ThrowIfCancellationRequested();
                var item=pending.Dequeue();
                if(!visited.Add(item.FileName))continue;
                var relative=CurseForgeFolder(item.Section)+"/"+item.FileName;
                var destination=ModpackArchive.SafePath(selected.GameDirectory,relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                downloads.Add(CurseForgeRequest(item, destination));
                foreach(var dependency in item.CurseForgeDependencies?.Where(item=>item.Required)??[])
                {
                    if (!dependencyChoices.TryGetValue(dependency.ModId, out var best))
                    {
                        var candidates=await client.GetFilesAsync(dependency.ModId,selected.Version,selected.Loader,token);
                        best=candidates.Where(candidate=>candidate.Sha1 is {Length:40}&&!candidate.FileName.EndsWith(".pom",StringComparison.OrdinalIgnoreCase)&&!candidate.FileName.EndsWith(".module",StringComparison.OrdinalIgnoreCase)).OrderByDescending(candidate=>candidate.Date).FirstOrDefault();
                        if(best==null)throw new InvalidDataException($"前置资源 {dependency.ModId} 在 {selected.Version} / {selected.Loader} 没有可用文件。");
                        dependencyChoices[dependency.ModId] = best;
                    }
                    pending.Enqueue(CurseForgeItem(dependency.ModId,best,item.Section));
                }
            }
            await FileDownloader.DownloadManyAsync(downloads, DownloadConcurrency, p => Report(task, "下载资源与必需前置", p), token);
            NotifyTask(file.FileName+"下载完成",false);
        });
    }
    private static DownloadRequest CurseForgeRequest(ResourceFileItem file, string destination)
    {
        _ = long.TryParse(file.Id.StartsWith("cf:", StringComparison.Ordinal) ? file.Id[3..] : file.Id, out var fileId);
        var download = file.Download ?? throw new InvalidDataException("资源没有下载信息。");
        var urls = CurseForgeClient.DownloadUrls(fileId, file.FileName, download.Url);
        if (urls.Length == 0) throw new InvalidDataException("资源没有可用的下载地址。");
        return new DownloadRequest { Url = urls[0], FallbackUrls = urls.Skip(1).ToArray(), FilePath = destination,
            Size = download.Size, Sha1 = download.Sha1 ?? "", UseSourcePerformance = false };
    }
    private static string CurseForgeFolder(DownloadSection section)=>section switch
    {
        DownloadSection.Shaders=>"shaderpacks",
        DownloadSection.Resourcepacks=>"resourcepacks",
        DownloadSection.Datapacks=>"datapacks",
        _=>"mods"
    };
    private static ResourceFileItem CurseForgeItem(long modId,CurseForgeFile file,DownloadSection section)=>new("cf:"+file.Id,file.DisplayName.Length>0?file.DisplayName:file.FileName,file.FileName,file.GameVersions,
        file.Loaders.Length>0?string.Join(" / ",file.Loaders):null,file.Date,file.FileLength,file.Downloads,null)
        {ProjectId="cf-"+modId,Section=section,Loaders=file.Loaders,Download=new ResourceDownload(file.FileName,CurseForgeClient.Rewrite(file.DownloadUrl),file.FileLength,file.Sha1),CurseForgeDependencies=file.Dependencies};
    private sealed record PackImportRequest(PackManifest Manifest, string Root, string InstanceName)
    {
        public Dictionary<string, string> ManualSources { get; } = new(StringComparer.OrdinalIgnoreCase);
        public PackManifest? ResolvedManifest { get; set; }
    }

    /// <summary>文件选择和主窗口拖放共用校验及确认；安装失败不登记为可用实例。</summary>
    public async Task ImportModpackAsync(string path,string sourceProjectId="",string sourceVersionId="",string? sourceIcon=null)
    {
        var root=SelectedRoot;
        try
        {
            var request=await PrepareModpackImportAsync(path,root,_lifetime.Token);
            if(request==null)return;
            Enqueue(PreviewTaskKind.Import,request.Manifest.Name,request.Manifest.Minecraft,root,request.InstanceName,
                (task,token)=>InstallModpackAsync(path,request,task,token,sourceProjectId,sourceVersionId,sourceIcon));
            NotifyTask(request.Manifest.Name+"已加入下载列表",false);
        }
        catch(OperationCanceledException) when(_lifetime.IsCancellationRequested) { }
        catch(Exception error){await ReportErrorAsync(error,"导入整合包","读取整合包与安装确认","整合包文件："+path);}
    }
    private async Task<PackImportRequest?> PrepareModpackImportAsync(string path,string root,CancellationToken token)
    {
        var manifest=await Task.Run(() => ModpackArchive.ReadPreview(path), token);
        token.ThrowIfCancellationRequested();
        var name=string.Join("_",manifest.Name.Split(Path.GetInvalidFileNameChars())).Trim().TrimEnd('.');
        if(name.Length==0)name="Modpack";
        if(name.Length>80)name=name[..80].TrimEnd('.',' ');
        _ = ModpackArchive.SafePath(root,"versions/"+name);
        var original=name;var suffix=1;
        while(Directory.Exists(Path.Combine(root,"versions",name))||Tasks.Any(t=>t.IsActive&&string.Equals(InstanceStore.Key(t.Root,t.InstanceId),InstanceStore.Key(root,name),StringComparison.OrdinalIgnoreCase)))name=original+"-"+suffix++;
        var optional=manifest.OptionalResourceCount;
        if(new PackImportDialog(manifest,name,optional){Owner=Application.Current.MainWindow}.ShowDialog()!=true)return null;
        return new(manifest,root,name);
    }
    /// <summary>在线下载与本地拖拽共用完整安装步骤；同一在线任务包含安装、取消和重试，不提前报告完成。</summary>
    private async Task InstallModpackAsync(string path,PackImportRequest request,PreviewTask task,CancellationToken token,string sourceProjectId,string sourceVersionId,string? sourceIcon)
    {
        var manifest=request.ResolvedManifest??request.Manifest;var root=request.Root;var instanceName=request.InstanceName;
        if (manifest.PendingCurseForgeFiles.Count > 0)
        {
            Report(task, "解析整合包资源信息");
            manifest = await Task.Run(() => ModpackArchive.ReadAsync(path, token, (completed, total) =>
                Report(task, $"解析整合包资源信息 {completed}/{total}", new DownloadBatchProgress(total, completed, 0, 0, 0, 0))), token);
            request.ResolvedManifest = manifest;
        }
        // 等待用户补齐资源期间不占用游戏根目录写锁；任务重试仍复用本次导入的选择。
        manifest = await ResolveManualPackFilesAsync(request, manifest, task, token);
        if(!_rootWriters.TryGetValue(root,out var writer))_rootWriters[root]=writer=new(1,1);
        await writer.WaitAsync(token);
        var writerHeld = true;
        var baseExisted = File.Exists(Path.Combine(root, "versions", manifest.Minecraft, manifest.Minecraft + ".json"));
        try
        {
            await SetIncompleteAsync(root,instanceName,true);
            var instance=new PreviewInstance(instanceName,manifest.Name,manifest.Minecraft,manifest.Loader){Root=root,IsolationMode=SavedSettings.VersionIsolation};
            var javaOptions = InstanceStore.Read(instance);
            Report(task, "检查整合包自带的 Java");
            var bundledJava = await Task.Run(() => ModpackArchive.PrepareBundledJavaAsync(path, manifest, instance, javaOptions,
                DownloadConcurrency, p => Report(task, "准备整合包自带的 Java", p), token), token);
            await InstallComponentsAsync(root,instanceName,manifest.Minecraft,manifest.Loader,manifest.LoaderVersion,"",task,token,javaOptions,bundledJava);
            await RemoveAutoInstalledBaseAsync(root,manifest.Minecraft,instanceName,baseExisted);
            if(!baseExisted&&!string.Equals(instanceName,manifest.Minecraft,StringComparison.OrdinalIgnoreCase))MarkDependencyVersion(root,manifest.Minecraft);
            var options=InstanceStore.Read(instance);options.Name=manifest.Name;options.Description=manifest.Summary;options.Source="Modrinth 整合包";options.SourceProjectId=sourceProjectId;options.SourceVersionId=sourceVersionId;
            options.GlobalJava = javaOptions.GlobalJava;
            options.JavaPath = javaOptions.JavaPath;
            while (true)
            {
                try
                {
                    await Task.Run(()=>ModpackArchive.InstallContentAsync(path,manifest,instance,options,DownloadConcurrency,p=>Report(task,"收集整合包资源",p),token),token);
                    break;
                }
                catch (PackDownloadUnavailableException error)
                {
                    Log("整合包自动下载来源已耗尽：" + error);
                    // 自动来源均失败后才询问用户；不占用其他游戏任务的目录写锁。
                    writer.Release(); writerHeld = false;
                    manifest = await ResolveManualPackFilesAsync(request, manifest, task, token, error.Files);
                    await writer.WaitAsync(token); writerHeld = true;
                }
            }
            if(sourceIcon!=null&&!File.Exists(Path.Combine(instance.Folder,"icon.png")))
            {
                try
                {
                    if(await Features.Downloads.Models.RemoteArtwork.LoadAsync(sourceIcon,token) is System.Windows.Media.Imaging.BitmapSource bitmap)
                    {
                        var iconPath=Path.Combine(instance.Folder,"fmlauncher.pack-icon.png");
                        await Task.Run(()=>{var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using var output=File.Create(iconPath);encoder.Save(output);},token);
                        options.PackIconPath=iconPath;
                    }
                }
                catch(Exception error) when(error is not OperationCanceledException){Log("整合包图标："+RuntimeSupport.Error(error));}
            }
            await InstanceStore.SaveAsync(instance,options);
            token.ThrowIfCancellationRequested();await SetIncompleteAsync(root,instanceName,false);
            if(string.Equals(root,SelectedRoot,StringComparison.OrdinalIgnoreCase))await RefreshInstancesAsync();
            NotifyTask(manifest.Name+"导入完成",false);
        }
        finally{if(writerHeld)writer.Release();NotifyInstanceContentChanged(root,instanceName);if(!baseExisted&&!string.Equals(instanceName,manifest.Minecraft,StringComparison.OrdinalIgnoreCase))MarkDependencyVersion(root,manifest.Minecraft);}
    }
}
