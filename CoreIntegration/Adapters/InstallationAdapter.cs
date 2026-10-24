using FMCore.Installer;
using FMCore.Java;
using FMCore.Loaders;
using System.IO;
using FMLauncher.CoreIntegration.Models;
using FMLauncher.Infrastructure.Configuration;
namespace FMLauncher.CoreIntegration.Adapters;

/// <summary>原版、加载器和完整性操作只调用 FMCore，移除旧下载器、安装 Java 桥接与 profile 修补逻辑。</summary>
public sealed class InstallationAdapter
{
    public async Task<IReadOnlyList<string>> LoadersAsync(string root, string game, string kind, CancellationToken ct) => kind switch
    {
        "Fabric" => (await new FabricInstaller(root).GetLoaderVersionsAsync(game, cancellationToken: ct)).Select(v => v.Version + (v.Stable ? "" : " (预览)")).ToArray(),
        "Forge" => (await new ForgeInstaller(root).GetVersionsAsync(game, cancellationToken: ct)).Select(v => v.Version).ToArray(),
        "NeoForge" => (await new NeoForgeInstaller(root).GetVersionsAsync(game, cancellationToken: ct)).Select(v => v.Version).ToArray(),
        "OptiFine" => (await new OptiFineInstaller(root).GetVersionsAsync(game, cancellationToken: ct)).Select(v => v.Version).ToArray(),
        _ => []
    };
    private static InlineProgress<InstallationProgress> Relay(IProgress<WorkProgress> target) => new(p => target.Report(FMLauncher.CoreIntegration.Mapping.ProgressMapping.Map(p.Stage.ToString(), p.CompletedFiles, p.TotalFiles, p.Download)));
    /// <summary>安装原版或单一受支持加载器；版本 ID 使用 SDK 结果，不自行改写元数据。</summary>
    public async Task<string> InstallAsync(LauncherSettings s, string game, string kind, string? loader, string? name, IProgress<WorkProgress> progress, CancellationToken ct)
    {
        loader = loader?.Replace(" (预览)", "");
        if (kind == "Vanilla" && !string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("原版请保留官方版本 ID；自定义目录名称仅用于加载器版本。");
        var profile = kind == "Vanilla" ? game : string.IsNullOrWhiteSpace(name) ? $"{game}-{kind}-{loader}" : name.Trim();
        var installation = await OptionsAsync(s, profile, ct);
        var options = new LoaderInstallOptions { JavaPath = string.IsNullOrWhiteSpace(s.JavaPath) ? null : s.JavaPath, VersionId = profile, Installation = installation };
        var relay = new InlineProgress<LoaderInstallationProgress>(p => progress.Report(FMLauncher.CoreIntegration.Mapping.ProgressMapping.Map(p.Stage.ToString(), p.CompletedFiles, p.TotalFiles, p.Download ?? p.Installation?.Download)));
        return kind switch
        {
            "Fabric" => (await new FabricInstaller(s.GameRoot).InstallAsync(game, loader, new FabricInstallOptions { VersionId = profile, Installation = installation }, progress: new InlineProgress<FabricInstallationProgress>(p => progress.Report(FMLauncher.CoreIntegration.Mapping.ProgressMapping.Map(p.Stage.ToString(), p.CompletedFiles, p.TotalFiles, p.Download ?? p.Installation?.Download))), cancellationToken: ct)).VersionId,
            "Forge" => (await new ForgeInstaller(s.GameRoot).InstallAsync(game, loader, options, relay, ct)).VersionId,
            "NeoForge" => (await new NeoForgeInstaller(s.GameRoot).InstallAsync(game, loader, options, relay, ct)).VersionId,
            "OptiFine" => (await new OptiFineInstaller(s.GameRoot).InstallAsync(game, loader, options, relay, ct)).VersionId,
            "Vanilla" => await InstallVanillaAsync(s, game, progress, ct),
            _ => throw new InvalidOperationException("正式 SDK 不支持所选加载器。")
        };
    }
    private async Task<InstallationOptions> OptionsAsync(LauncherSettings s, string version, CancellationToken ct) => new()
    {
        Context = string.IsNullOrWhiteSpace(s.JavaPath) ? FMCore.Versions.RuleContext.Current : (await JavaUtil.GetJavaInfoAsync(s.JavaPath, cancellationToken: ct)).GetRuleContext(),
        GameDirectory = Path.Combine("versions", version)
    };
    private async Task<string> InstallVanillaAsync(LauncherSettings s, string version, IProgress<WorkProgress> p, CancellationToken ct)
    {
        await new GameInstaller(s.GameRoot).InstallAsync(version, await OptionsAsync(s, version, ct), Relay(p), ct); return version;
    }
    /// <summary>只检查或修复核心安装清单范围；运行时功能仍由所有者验证。</summary>
    public async Task<string> VerifyAsync(LauncherSettings s, string version, bool repair, IProgress<WorkProgress> p, CancellationToken ct)
    {
        var installer = new GameInstaller(s.GameRoot);
        if (repair) { await installer.RepairAsync(version, await OptionsAsync(s, version, ct), Relay(p), ct); return "修复操作已完成，请自行验证游戏。"; }
        var check = await installer.VerifyAsync(version, await OptionsAsync(s, version, ct), Relay(p), ct);
        return $"文件校验：{(check.IsValid ? "未发现清单范围内的问题" : "发现 " + check.Problems.Count + " 项问题")}；已检查 {check.Files.Count} 个文件，原生文件：{(check.NativeFilesChecked ? "已纳入检查" : "未纳入本次检查")}。";
    }
}

