using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FocusMinecraftLauncher.Infrastructure.Configuration;

/// <summary>程序目录内的统一配置入口；串行原子写入，并合并高频修改，不依赖工作目录。</summary>
public sealed class LauncherConfiguration
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly HashSet<string> _blocked = [];
    private readonly List<string> _warnings = [];
    private CancellationTokenSource? _delay;
    private string _saveError = "";
    private JsonObject _launcher = new(), _accounts = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
    };

    public string BaseDirectory { get; }
    public string ConfigDirectory => Path.Combine(BaseDirectory, "FMLauncher");
    public string MinecraftDirectory => Path.Combine(BaseDirectory, ".minecraft");
    public string LauncherFile => Path.Combine(ConfigDirectory, "Launcher_config.json");
    public string AccountFile => Path.Combine(ConfigDirectory, "account_config.json");
    public event Action? StatusChanged;
    public void ReportNotice(string message) { lock (_gate) _warnings.Add(message); StatusChanged?.Invoke(); }
    public string Status { get { lock (_gate) return string.Join("\n", _warnings.Concat(_saveError.Length == 0 ? [] : new[] { _saveError })); } }

    private LauncherConfiguration(string baseDirectory) => BaseDirectory = Path.GetFullPath(baseDirectory);

    /// <summary>先读取并补齐配置，再交给主窗口使用；可指定目录用于隔离验收，产品默认始终为程序目录。</summary>
    public static async Task<LauncherConfiguration> OpenAsync(string? baseDirectory = null)
    {
        var configuration = new LauncherConfiguration(baseDirectory ?? AppContext.BaseDirectory);
        try { Directory.CreateDirectory(configuration.ConfigDirectory); Directory.CreateDirectory(configuration.MinecraftDirectory); }
        catch (Exception error) when (IsFileError(error))
        { configuration.Warn("无法创建启动器配置或游戏目录，请检查程序目录的写入权限。", error); }
        var launcherDefaults = configuration.DefaultLauncher();
        var accountDefaults = DefaultAccounts();
        configuration._launcher = await configuration.ReadAsync(configuration.LauncherFile, launcherDefaults).ConfigureAwait(false);
        configuration._accounts = await configuration.ReadAsync(configuration.AccountFile, accountDefaults).ConfigureAwait(false);
        var changed = MergeMissing(configuration._launcher, launcherDefaults) | MergeMissing(configuration._accounts, accountDefaults);
        if (changed || !File.Exists(configuration.LauncherFile) || !File.Exists(configuration.AccountFile))
            await configuration.FlushAsync().ConfigureAwait(false);
        return configuration;
    }

    public JsonObject ReadLauncher() { lock (_gate) return (JsonObject)_launcher.DeepClone(); }
    public JsonObject ReadAccounts() { lock (_gate) return (JsonObject)_accounts.DeepClone(); }
    public void ChangeLauncher(Action<JsonObject> change) { lock (_gate) change(_launcher); ScheduleSave(); }
    public void ChangeAccounts(Action<JsonObject> change) { lock (_gate) change(_accounts); ScheduleSave(); }

    /// <summary>滑块等连续操作合并为一次写入；普通操作不依赖退出事件才保存。</summary>
    private void ScheduleSave()
    {
        lock (_gate)
        {
            _delay?.Cancel(); _delay?.Dispose(); _delay = new CancellationTokenSource();
            _ = SaveAfterDelayAsync(_delay.Token);
        }
    }
    private async Task SaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(180, cancellationToken).ConfigureAwait(false); await FlushAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    public async Task<bool> FlushAsync()
    {
        lock (_gate) { _delay?.Cancel(); _delay?.Dispose(); _delay = null; }
        await _writer.WaitAsync().ConfigureAwait(false);
        try
        {
            string launcher, accounts;
            lock (_gate) { launcher = _launcher.ToJsonString(JsonOptions); accounts = _accounts.ToJsonString(JsonOptions); }
            var success = true;
            foreach (var (path, text) in new[] { (LauncherFile, launcher), (AccountFile, accounts) })
            {
                if (_blocked.Contains(path)) { success = false; continue; }
                try { await WriteAtomicAsync(path, text).ConfigureAwait(false); }
                catch (Exception error) when (IsFileError(error))
                { success = false; Trace.TraceWarning("配置保存失败：{0}", error.Message); }
            }
            lock (_gate) _saveError = success ? "" : "配置保存失败。更改仅在本次运行中生效，请检查程序目录的读写权限。";
            StatusChanged?.Invoke();
            return success;
        }
        finally { _writer.Release(); }
    }

    private async Task<JsonObject> ReadAsync(string path, JsonObject defaults)
    {
        if (!File.Exists(path)) return (JsonObject)defaults.DeepClone();
        try
        {
            var text = await File.ReadAllTextAsync(path, Encoding.UTF8).ConfigureAwait(false);
            var root = JsonNode.Parse(text, new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject
                ?? throw new JsonException("配置根节点应为对象。");
            if (path == AccountFile && root.ContainsKey("accounts") && root["accounts"] is not JsonArray)
                throw new JsonException("账户列表应为数组。");
            if (RepairObjectFields(root, defaults))
            {
                // 已知配置分组类型错误时，先备份，再只恢复错误分组，保留其余有效值。
                File.Copy(path, path + ".broken-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..6]);
                Warn(Path.GetFileName(path) + " 的部分字段格式错误，原文件已备份，其余设置已保留。", new JsonException());
                await WriteAtomicAsync(path, root.ToJsonString(JsonOptions)).ConfigureAwait(false);
            }
            return root;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            var backup = path + ".broken-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..6];
            try
            {
                File.Copy(path, backup);
                Warn(Path.GetFileName(path) + " 已损坏，原文件已备份，现使用默认配置。", error);
                await WriteAtomicAsync(path, defaults.ToJsonString(JsonOptions)).ConfigureAwait(false);
            }
            catch (Exception backupError) when (IsFileError(backupError))
            { _blocked.Add(path); Warn("损坏配置无法备份或恢复，未继续覆盖原文件，请检查程序目录权限。", backupError); }
            return (JsonObject)defaults.DeepClone();
        }
        catch (Exception error) when (IsFileError(error))
        { _blocked.Add(path); Warn("无法读取 " + Path.GetFileName(path) + "，未覆盖已有配置。", error); return (JsonObject)defaults.DeepClone(); }
    }

    private static async Task WriteAtomicAsync(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false)).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (IsFileError(error)) { Trace.TraceWarning("配置临时文件清理失败：{0}", error.Message); }
        }
    }
    private void Warn(string message, Exception error)
    { lock (_gate) _warnings.Add(message); Trace.TraceWarning("{0} {1}", message, error.GetType().Name); }
    private static bool IsFileError(Exception error) => error is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    private static bool MergeMissing(JsonObject target, JsonObject defaults)
    {
        var changed = false;
        foreach (var item in defaults)
        {
            if (!target.ContainsKey(item.Key)) { target[item.Key] = item.Value?.DeepClone(); changed = true; }
            else if (target[item.Key] is JsonObject existing && item.Value is JsonObject nested) changed |= MergeMissing(existing, nested);
        }
        return changed;
    }
    private static bool RepairObjectFields(JsonObject target, JsonObject defaults)
    {
        var repaired = false;
        foreach (var item in defaults)
        {
            if (item.Value is not JsonObject expected || !target.ContainsKey(item.Key)) continue;
            if (target[item.Key] is JsonObject existing) repaired |= RepairObjectFields(existing, expected);
            else { target[item.Key] = expected.DeepClone(); repaired = true; }
        }
        return repaired;
    }
    private JsonObject DefaultLauncher() => new()
    {
        ["schemaVersion"] = 1,
        ["appearance"] = new JsonObject
        {
            ["theme"] = "Glass", ["homeLayout"] = "Classic", ["animationsEnabled"] = true,
            ["homeContent"] = new JsonObject { ["showLauncherUpdates"] = true, ["showGameUpdates"] = true },
            ["themes"] = new JsonObject
            {
                ["Glass"] = new JsonObject { ["glassTransparency"] = 50, ["backgroundBlur"] = 50, ["dynamicBlurEnabled"] = false, ["backgroundImage"] = "", ["backgroundImageName"] = "" },
                ["MinimalistFlat"] = new JsonObject { ["colorPreset"] = "", ["accentColor"] = Presentation.UiTheme.DefaultFlatAccent, ["nightMode"] = false, ["backgroundBlur"] = 0, ["backgroundImage"] = "", ["backgroundImageName"] = "" },
                ["SoftUI"] = new JsonObject { ["colorPreset"] = "", ["accentColor"] = Presentation.UiTheme.DefaultSoftAccent, ["nightMode"] = false, ["backgroundBlur"] = 0, ["backgroundImage"] = "", ["backgroundImageName"] = "" },
            },
            // 三种主页的定制项分别保留；当前没有额外编辑项时保持空对象。
            ["homeLayouts"] = new JsonObject { ["Classic"] = new JsonObject(), ["Minimal"] = new JsonObject(), ["Centered"] = new JsonObject() }
        },
        ["selection"] = new JsonObject { ["instanceId"] = null },
        ["global"] = new JsonObject { ["versionIsolation"] = "All", ["javaPath"] = "", ["memoryMb"] = 4096, ["gameDirectory"] = MinecraftDirectory, ["jvmArguments"] = "" },
        ["downloads"] = new JsonObject()
    };
    private static JsonObject DefaultAccounts() => new()
    {
        ["schemaVersion"] = 1, ["accounts"] = new JsonArray(), ["currentAccountId"] = null,
        ["microsoftMode"] = false, ["lastMicrosoftAccountId"] = null, ["lastOfflineAccountId"] = null
    };

    public static JsonObject Object(JsonObject parent, string key)
    { if (parent[key] is JsonObject value) return value; var created = new JsonObject(); parent[key] = created; return created; }
    public static string Text(JsonNode? value, string fallback = "") => value is JsonValue json && json.TryGetValue<string>(out var text) ? text ?? fallback : fallback;
    public static bool Boolean(JsonNode? value, bool fallback = false) => value is JsonValue json && json.TryGetValue<bool>(out var result) ? result : fallback;
    public static double Number(JsonNode? value, double fallback) => value is JsonValue json && json.TryGetValue<double>(out var result) && double.IsFinite(result) ? result : fallback;
}
