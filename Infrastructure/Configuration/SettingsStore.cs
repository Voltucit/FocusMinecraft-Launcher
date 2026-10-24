using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using FMLauncher.Infrastructure.Persistence;
namespace FMLauncher.Infrastructure.Configuration;

/// <summary>读取设置并执行一次性迁移。旧配置只备份、读取，不改写或删除。</summary>
public sealed class SettingsStore(AppPaths paths)
{
    public LauncherSettings Current { get; private set; } = new();
    public string MigrationNotice { get; private set; } = "";
    /// <summary>为跨进程或排队操作创建独立快照，防止用户后续编辑改变已提交任务的目标。</summary>
    public LauncherSettings Snapshot() => JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(Current, AtomicJson.Options), AtomicJson.Options)!;
    public void UpdatePreferences(LauncherSettings draft)
    {
        var previous = Current;
        Current = draft with
        {
            Appearance = previous.Appearance,
            Wallpaper = previous.Wallpaper,
            VersionId = previous.VersionId,
            AccountId = previous.AccountId,
            MicrosoftClientId = previous.MicrosoftClientId,
            LegacyOfflineName = previous.LegacyOfflineName
        };
        try { Save(); } catch { Current = previous; throw; }
    }
    /// <summary>从用户明确选择的旧游戏根目录导入非机密设置，并先加密备份原配置。</summary>
    public void ImportFromGameRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new InvalidDataException("旧游戏目录必须为绝对路径。");
        var legacyFile = Path.Combine(root, "LauncherSetting.json");
        if (!File.Exists(legacyFile)) throw new InvalidDataException("所选目录中没有 LauncherSetting.json。");
        var previous = Current; Current = Snapshot() with { GameRoot = root };
        try { ImportLegacy(legacyFile); Save(); } catch { Current = previous; throw; }
    }
    public void Load()
    {
        if (File.Exists(paths.SettingsFile))
        {
            if (new FileInfo(paths.SettingsFile).Length > 1024 * 1024) throw new InvalidDataException("配置文件超过 1 MiB。");
            Current = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(paths.SettingsFile), AtomicJson.Options)
                ?? throw new InvalidDataException("配置内容为空。");
            if (Current.SchemaVersion != 2) throw new InvalidDataException("配置格式不受支持，请保留文件并使用相应版本启动器。");
        }
        else
        {
            Current.GameRoot = paths.DefaultGameRoot;
            if (File.Exists(paths.LegacySettings)) ImportLegacy(paths.LegacySettings);
            Save();
        }
        Current.Appearance ??= new(); Current.Wallpaper ??= new();
        if (string.IsNullOrWhiteSpace(Current.GameRoot)) Current.GameRoot = paths.DefaultGameRoot;
    }
    private void ImportLegacy(string legacyFile)
    {
        if (new FileInfo(legacyFile).Length > 1024 * 1024) throw new InvalidDataException("旧配置超过 1 MiB，请手动迁移。");
        // 旧配置可能含明文令牌，新增备份必须用 DPAPI，原文件保留以便用户回退。
        var backupDirectory = Path.Combine(paths.UserDirectory, "migration-backups");
        Directory.CreateDirectory(backupDirectory);
        var original = File.ReadAllBytes(legacyFile);
        try { File.WriteAllBytes(Path.Combine(backupDirectory, "legacy-" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + ".dpapi"), ProtectedData.Protect(original, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(original); }
        using var json = JsonDocument.Parse(File.ReadAllText(legacyFile));
        var root = json.RootElement;
        string Text(JsonElement item, string key, string fallback = "") => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
        double Number(JsonElement item, string key, double fallback) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var result) && double.IsFinite(result) ? result : fallback;
        Current.VersionId = Text(root, "GameVersion"); Current.JavaPath = Text(root, "JavaPath");
        if (Number(root, "LoginMode", 1) == 1) Current.LegacyOfflineName = Text(root, "Playername");
        Current.MaxMemoryMb = (int)Math.Clamp(Number(root, "Memory", 4) * 1024, 512, 1048576);
        if (root.TryGetProperty("Appearance", out var appearance) && appearance.ValueKind == JsonValueKind.Object)
        {
            Current.Appearance.Mode = appearance.TryGetProperty("DarkMode", out var dark) && dark.ValueKind == JsonValueKind.True ? "Dark" : "Light";
            var presetAccent = Text(appearance, "Preset") switch { "mono" => "#25282D", "red" => "#B83E47", "blue" => "#2869B2", "amber" => "#A85C20", "violet" => "#7952AC", _ => "#25765B" };
            var custom = appearance.TryGetProperty("UseCustomColors", out var useCustom) && useCustom.ValueKind == JsonValueKind.True;
            Current.Appearance.Accent = custom ? Text(appearance, "CustomAccent", presetAccent) : presetAccent;
            Current.Appearance.Background = custom ? Text(appearance, "CustomBackground") : "";
            Current.Appearance.WallpaperOpacity = Math.Clamp(Number(appearance, "BackgroundOpacity", 0.55), 0, 1);
            Current.Appearance.Blur = Math.Clamp(Number(appearance, "BackgroundBlur", 0), 0, 40);
            Current.Appearance.Animations = !appearance.TryGetProperty("AnimationsEnabled", out var animation) || animation.ValueKind != JsonValueKind.False;
            Current.Wallpaper.LocalPath = Text(appearance, "BackgroundPath");
            if (!string.IsNullOrEmpty(Current.Wallpaper.LocalPath)) Current.Wallpaper.Source = "Local";
        }
        MigrationNotice = "已导入旧版的版本、Java、内存与外观设置。旧账号缓存仍保留，请重新授权或添加离线账号；旧凭据不转换为新凭据。";
    }
    /// <summary>保存前验证范围；游戏目录更改只影响后续操作，不移动任何游戏文件。</summary>
    public void Save()
    {
        FMLauncher.Appearance.Themes.ThemeService.Validate(Current.Appearance);
        if (!Path.IsPathFullyQualified(Current.GameRoot)) throw new InvalidDataException("游戏根目录必须为绝对路径。");
        if (Current.MinMemoryMb < 0 || Current.MaxMemoryMb < 512 || Current.MinMemoryMb > Current.MaxMemoryMb) throw new InvalidDataException("最小堆应介于 0 与最大堆之间，最大堆至少 512 MiB。");
        if (Current.Width < 320 || Current.Height < 200 || Current.ServerPort is < 1 or > 65535) throw new InvalidDataException("窗口大小或服务器端口无效。");
        if (File.Exists(paths.SettingsFile)) File.Copy(paths.SettingsFile, paths.SettingsFile + ".bak", true);
        AtomicJson.Write(paths.SettingsFile, Current);
    }
}

