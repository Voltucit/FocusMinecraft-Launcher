using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FMCore.Downloads;
using FMCore.ModsResources;
using FocusMinecraftLauncher.Preview;

namespace FocusMinecraftLauncher.Infrastructure.Runtime;

public sealed record ResourceInstallFile(string RelativePath,string ProjectId,string VersionId,string Name,ModrinthFile File);
public sealed record ResourceReplacement(string RelativePath,ResourceOrigin Origin);
public sealed record ResourceInstallPlan(IReadOnlyList<ResourceInstallFile> Files,IReadOnlyList<ResourceReplacement> Replacements);

/// <summary>先验证和下载全部资源，再备份旧版本并提交；不覆盖来源不明或已被用户修改的文件。</summary>
public static class ResourceFileInstaller
{
    public static async Task<ResourceInstallPlan> PlanAsync(PreviewInstance instance,IReadOnlyList<ResourceInstallFile> files,CancellationToken token)
    {
        ModpackArchive.EnsureGameDirectoryWritePath(instance);
        var options=InstanceStore.Read(instance);
        var targets=new List<ResourceInstallFile>();var replacements=new Dictionary<string,ResourceReplacement>(StringComparer.OrdinalIgnoreCase);
        foreach(var requested in files)
        {
            token.ThrowIfCancellationRequested();
            var item=requested;
            // 同一文件已存在时沿用其文件名，切换命名选项不会安装第二份相同的 Mod。
            foreach(var old in options.ResourcesFor(instance).Where(pair=>pair.Value.ProjectId==item.ProjectId&&SameContainer(pair.Key,item.RelativePath)&&!Disabled(pair.Key)&&pair.Value.Sha1.Equals(item.File.Hashes.Sha1,StringComparison.OrdinalIgnoreCase)))
                if(await MatchesAsync(ModpackArchive.SafePath(instance.GameDirectory,old.Key),item.File.Size,item.File.Hashes.Sha1,token))
                {item=item with{RelativePath=old.Key};break;}
            var target=ModpackArchive.SafePath(instance.GameDirectory,item.RelativePath);
            foreach(var old in options.ResourcesFor(instance).Where(pair=>pair.Value.ProjectId==item.ProjectId&&SameContainer(pair.Key,item.RelativePath)&&!Disabled(pair.Key)))
            {
                var oldPath=ModpackArchive.SafePath(instance.GameDirectory,old.Key);
                if(!File.Exists(oldPath))continue;
                if(string.Equals(old.Key,item.RelativePath,StringComparison.OrdinalIgnoreCase)&&await MatchesAsync(oldPath,item.File.Size,item.File.Hashes.Sha1,token))continue;
                if(!await MatchesAsync(oldPath,old.Value.Size,old.Value.Sha1,token))throw new IOException("已有资源已被修改，未自动替换："+Path.GetFileName(old.Key));
                replacements[old.Key]=new(old.Key,old.Value);
            }
            if(File.Exists(target)&&!replacements.ContainsKey(item.RelativePath)&&!await MatchesAsync(target,item.File.Size,item.File.Hashes.Sha1,token))
                throw new IOException("目标位置有其他文件，未覆盖："+Path.GetFileName(target));
            targets.Add(item);
        }
        if(targets.Select(item=>item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=targets.Count)
            throw new InvalidDataException("所选资源生成了重复文件名，请调整文件名格式。");
        return new(targets,replacements.Values.ToArray());
    }

    /// <summary>使用核心的下载和 SHA 校验；提交失败时恢复原文件，并保留可供手动恢复的旧版本备份。</summary>
    public static async Task ApplyAsync(PreviewInstance instance,ResourceInstallPlan plan,ModrinthClient client,string configDirectory,Action<string,DownloadProgress?> report,CancellationToken token,int concurrency=4)
    {
        var staging=ModpackArchive.SafePath(configDirectory,"resource-staging/"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var staged=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var moved=new List<(string Original,string Backup)>();var created=new List<(string Target,string Staged,long Size,string? Sha1)>();
        try
        {
            var downloads = new List<DownloadRequest>();
            foreach(var item in plan.Files)
            {
                var target=ModpackArchive.SafePath(instance.GameDirectory,item.RelativePath);
                if(await MatchesAsync(target,item.File.Size,item.File.Hashes.Sha1,token))continue;
                var temporary=Path.Combine(staging,staged.Count+".bin");staged[item.RelativePath]=temporary;
                downloads.Add(client.CreateDownloadRequest(item.File, temporary));
            }
            await FileDownloader.DownloadManyAsync(downloads, concurrency, progress => report("下载资源与必需前置",
                new DownloadProgress("", progress.ProcessedBytes, progress.TotalBytes)
                { TransferredBytes=progress.TransferredBytes, BytesPerSecond=progress.BytesPerSecond, RemainingTime=progress.RemainingTime }), token);
            token.ThrowIfCancellationRequested();report("准备应用资源",null);
            foreach(var old in plan.Replacements)
                if(!await MatchesAsync(ModpackArchive.SafePath(instance.GameDirectory,old.RelativePath),old.Origin.Size,old.Origin.Sha1,token))
                    throw new IOException("下载期间原文件发生变化，未继续替换："+Path.GetFileName(old.RelativePath));
            foreach(var item in plan.Files)
            {
                var target=ModpackArchive.SafePath(instance.GameDirectory,item.RelativePath);
                if(!staged.ContainsKey(item.RelativePath)&&!await MatchesAsync(target,item.File.Size,item.File.Hashes.Sha1,token))
                    throw new IOException("已安装资源在下载期间发生变化："+Path.GetFileName(target));
                if(!plan.Replacements.Any(old=>string.Equals(old.RelativePath,item.RelativePath,StringComparison.OrdinalIgnoreCase))
                    &&File.Exists(target)&&!await MatchesAsync(target,item.File.Size,item.File.Hashes.Sha1,token))
                    throw new IOException("下载期间目标文件发生变化，未覆盖："+Path.GetFileName(target));
            }
            if(plan.Replacements.Count>0)
            {
                var backupRoot=ModpackArchive.SafePath(instance.Folder,".fmlauncher/resource-backups/"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(backupRoot);
                await File.WriteAllTextAsync(Path.Combine(backupRoot,"sources.json"),JsonSerializer.Serialize(plan.Replacements,new JsonSerializerOptions{WriteIndented=true}),token);
                foreach(var old in plan.Replacements)
                {
                    token.ThrowIfCancellationRequested();
                    var original=ModpackArchive.SafePath(instance.GameDirectory,old.RelativePath);var backup=ModpackArchive.SafePath(backupRoot,old.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);File.Move(original,backup,false);moved.Add((original,backup));
                }
            }
            foreach(var item in plan.Files)
            {
                token.ThrowIfCancellationRequested();
                if(!staged.TryGetValue(item.RelativePath,out var temporary))continue;
                var target=ModpackArchive.SafePath(instance.GameDirectory,item.RelativePath);
                if(File.Exists(target))
                {
                    if(!await MatchesAsync(target,item.File.Size,item.File.Hashes.Sha1,token))throw new IOException("目标文件发生变化，未覆盖："+Path.GetFileName(target));
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Move(temporary,target,false);created.Add((target,temporary,item.File.Size,item.File.Hashes.Sha1));
            }
            token.ThrowIfCancellationRequested();
            instance.Options=await InstanceStore.UpdateAsync(instance,options=>
            {
                foreach(var old in plan.Replacements)options.ResourcesFor(instance).Remove(old.RelativePath);
                foreach(var item in plan.Files)options.ResourcesFor(instance)[item.RelativePath]=new(item.ProjectId,item.VersionId,item.File.Url,item.File.Hashes.Sha1??"",item.File.Hashes.Sha512??"",item.File.Size,item.Name);
            });
        }
        catch(Exception failure)
        {
            var recoveryErrors=new List<Exception>();
            foreach(var file in created.AsEnumerable().Reverse())
                try
                {
                    if(!File.Exists(file.Target))continue;
                    if(await MatchesAsync(file.Target,file.Size,file.Sha1,CancellationToken.None))File.Move(file.Target,file.Staged,false);
                    else recoveryErrors.Add(new IOException("新文件已被修改，未在还原时移除。"));
                }
                catch(Exception error){recoveryErrors.Add(error);}
            foreach(var file in moved.AsEnumerable().Reverse())
                try{if(!File.Exists(file.Original))File.Move(file.Backup,file.Original,false);else recoveryErrors.Add(new IOException("原位置已被其他文件占用。"));}catch(Exception error){recoveryErrors.Add(error);}
            if(recoveryErrors.Count>0)throw new IOException("资源应用未完成，部分旧文件仍保留在游戏备份目录中，请先检查文件再重试。",new AggregateException(new[]{failure}.Concat(recoveryErrors)));
            throw;
        }
        finally
        {
            foreach(var temporary in staged.Values)
                try{if(File.Exists(temporary))File.Delete(temporary);}catch(Exception error) when(error is IOException or UnauthorizedAccessException){System.Diagnostics.Trace.TraceWarning("资源缓存清理失败：{0}",error.GetType().Name);}
            try{Directory.Delete(staging,false);}catch(Exception error) when(error is IOException or UnauthorizedAccessException){ }
        }
    }
    private static bool Disabled(string path)=>path.EndsWith(".disabled",StringComparison.OrdinalIgnoreCase);
    private static bool SameContainer(string left,string right)=>string.Equals(Path.GetDirectoryName(left),Path.GetDirectoryName(right),StringComparison.OrdinalIgnoreCase);
    private static async Task<bool> MatchesAsync(string path,long size,string? sha1,CancellationToken token)
    {
        if(!File.Exists(path)||new FileInfo(path).Length!=size||string.IsNullOrWhiteSpace(sha1))return false;
        await using var input=File.OpenRead(path);
        return Convert.ToHexString(await SHA1.HashDataAsync(input,token)).Equals(sha1,StringComparison.OrdinalIgnoreCase);
    }
}
