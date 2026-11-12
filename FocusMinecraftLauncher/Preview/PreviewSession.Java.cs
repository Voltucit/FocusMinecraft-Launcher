using System.ComponentModel;
using System.IO;
using FMCore.Java;
using FMCore.Versions;
using FocusMinecraftLauncher.Infrastructure.Runtime;
using Microsoft.Win32;

namespace FocusMinecraftLauncher.Preview;

public sealed partial class PreviewSession
{
    /// <summary>UI 捕获 JavaNotFoundException 后展示下载或手动选择引导，不把底层异常直接当作玩家提示。</summary>
    private async Task<bool> GuideJavaAsync(JavaNotFoundException error, PreviewTask task, CancellationToken token)
    {
        try
        {
            int? required = error.RequiredMajor;
            bool canDownload = !required.HasValue || JavaBootstrap.CanInstall(required.Value);
            var choice = Presentation.Dialogs.JavaRuntimeDialog.Ask(required, null, canDownload,
                task.Kind is not PreviewTaskKind.Launch, allowManual: true);
            if (choice == Presentation.Dialogs.JavaRuntimeChoice.Cancel)
            {
                task.Error = error.Message;
                if (task.Failure is { } report) NotifyError(report);
                return false;
            }
            if (choice == Presentation.Dialogs.JavaRuntimeChoice.Manual)
            {
                var dialog = new OpenFileDialog { Filter = "Java|java.exe;javaw.exe", CheckFileExists = true };
                if (dialog.ShowDialog() != true) return false;
                var java = await JavaReader.GetJavaInfoAsync(dialog.FileName, token);
                await SaveJavaPathAsync(error.Instance, java.Executable);
                string action = task.Kind == PreviewTaskKind.Launch ? "正在重新启动。" : "请再次开始安装。";
                task.Error = $"已选择 Java {java.MajorVersion}，{action}";
                NotifyTask($"已选择 Java，{action}", false);
                return true;
            }

            int? major = required ?? Presentation.Dialogs.JavaDownloadDialog.Ask(null);
            if (major == null) return false;
            task.State = "进行中"; task.Stage = $"下载 Java {major}"; _taskStages[task] = task.Stage; task.Error = ""; task.Refresh(); Signal();
            var installed = await JavaBootstrap.InstallAsync(major.Value, FileSource == FMCore.Downloads.DownloadSource.BmclApi,
                progress => ReportJavaInstall(task, progress), token);
            JavaScanner.Remember(installed);
            await SaveJavaScanCacheAsync(JavaScanner.Cached);
            await SaveJavaPathAsync(error.Instance, installed.Executable);
            task.State = "失败"; task.Stage = task.Kind is PreviewTaskKind.Launch ? "启动失败" : "安装失败";
            string downloadAction = task.Kind == PreviewTaskKind.Launch ? "正在重新启动。" : "请再次开始安装。";
            task.Error = $"Java {installed.MajorVersion} 已安装，{downloadAction}";
            NotifyTask($"Java {installed.MajorVersion} 已安装，{downloadAction}", false);
            return true;
        }
        catch (OperationCanceledException)
        {
            task.Error = "已取消 Java 操作。";
            NotifyTask("已取消 Java 操作。", true);
            return false;
        }
        catch (Exception guideError)
        {
            await SetTaskFailureAsync(task, guideError, _taskStages.TryGetValue(task, out var stage) ? stage : "配置 Java");
            return false;
        }
    }

    /// <summary>把手动选择或自动下载的 Java 写回当前实例或全局设置，供下一次启动使用。</summary>
    private async Task SaveJavaPathAsync(PreviewInstance instance, string path)
    {
        var options = InstanceStore.Read(instance);
        if (options.GlobalJava)
        {
            var global = SavedSettings.Copy();
            global.JavaPath = path;
            if (!await SaveGlobalSettingsAsync(global)) throw new IOException("无法保存 Java 设置。");
            return;
        }
        options = await InstanceStore.UpdateAsync(instance, value => value.JavaPath = path);
        instance.Options = options;
    }

    /// <summary>仅 Forge、NeoForge 和 OptiFine 安装器需要先找到 Java；普通安装与启动解析分开。</summary>
    private async Task<string> ResolveInstallerJavaAsync(PreviewInstance instance, InstanceOptions settings,
        IReadOnlyList<string>? bundledJava, PreviewTask task, CancellationToken token)
    {
        Report(task, "检测安装所需的 Java");
        var versions = await new MinecraftVersionReader().GetVersionsAsync(instance.Root, token);
        var metadata = await InstanceStore.ReadVersionAsync(versions.Single(version => version.Id == instance.Id), versions, instance.Root, token);
        instance.RequiredJava = metadata.RequiredJava;

        var bundled = new List<JavaInfo>();
        foreach (var path in bundledJava ?? [])
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                var java = await JavaReader.GetJavaInfoAsync(path, timeout.Token);
                if (instance.RequiredJava is not { } required || java.MajorVersion >= required)
                    bundled.Add(java);
                else Log($"整合包自带 Java {java.MajorVersion} 不满足 Java {required} 的要求。");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception
                or ArgumentException or InvalidOperationException or NotSupportedException or TimeoutException or OperationCanceledException)
            {
                Log("整合包自带 Java 检测失败：" + RuntimeSupport.SafeText(path + "；" + error.Message));
            }
        }
        if (bundled.OrderByDescending(java => java.MajorVersion == instance.RequiredJava)
            .ThenByDescending(java => java.MajorVersion).FirstOrDefault() is { } preferred)
        {
            settings.GlobalJava = false;
            settings.JavaPath = preferred.Executable;
            Report(task, $"使用整合包自带的 Java {preferred.MajorVersion}");
            Log($"整合包默认 Java：{preferred.MajorVersion}；{RuntimeSupport.SafeText(preferred.Executable)}");
            return preferred.Executable;
        }
        if (bundledJava is { Count: > 0 })
            NotifyTask("整合包自带的 Java 不可用或不满足版本要求，将继续检测本机 Java。", true);

        var global = SavedSettings.Copy();
        IReadOnlyList<JavaInfo> candidates;
        try { candidates = await RuntimeSupport.ReadLaunchJavaCandidatesAsync(instance, settings, global, token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception
            or ArgumentException or InvalidOperationException or NotSupportedException or TimeoutException)
        {
            throw new JavaNotFoundException(instance, instance.RequiredJava,
                "配置的 Java 无法使用，请在设置中重新选择 Java 或切换为自动选择。", error);
        }
        candidates = await ResolveJavaAsync(instance, settings, global, candidates, task, token, installing: true);
        token.ThrowIfCancellationRequested();
        if (candidates.Count == 0)
            throw new JavaNotFoundException(instance, instance.RequiredJava, "未检测到可用的 Java，请下载或手动选择 Java 后重试安装。");
        return candidates[0].Executable;
    }
}
