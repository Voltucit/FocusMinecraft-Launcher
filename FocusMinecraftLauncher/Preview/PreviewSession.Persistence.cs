using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using FMCore.Accounts;
using FocusMinecraftLauncher.Features.Home.Models;
using FocusMinecraftLauncher.Infrastructure.Authentication;
using FocusMinecraftLauncher.Infrastructure.Configuration;
using FocusMinecraftLauncher.Presentation;

namespace FocusMinecraftLauncher.Preview;

public sealed partial class PreviewSession
{
    private readonly LauncherConfiguration _configuration = ((App)Application.Current).Configuration;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _accountAttempts = [];
    private readonly List<JsonNode?> _unreadableAccounts = [];
    private bool _restoringConfiguration = true, _authenticating;
    private string _loginCode = "", _loginProgress = "", _loginError = "", _loginHint = "";
    private Uri? _loginUri;
    private HomeLayout _homeLayout;
    private bool _showLauncherUpdates = true, _showGameUpdates = true;
    private static readonly JsonSerializerOptions AccountJson = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public string ConfigurationMessage => _configuration.Status;
    public bool HasConfigurationMessage => ConfigurationMessage.Length > 0;
    public bool IsAuthenticating { get => _authenticating; private set => Set(ref _authenticating, value); }
    public string LoginCode { get => _loginCode; private set => Set(ref _loginCode, value); }
    public string LoginProgress { get => _loginProgress; private set => Set(ref _loginProgress, value); }
    public string LoginError { get => _loginError; private set => Set(ref _loginError, value); }
    /// <summary>授权码展示行的说明文字，随复制与打开浏览器的结果变化。</summary>
    public string LoginHint { get => _loginHint; private set => Set(ref _loginHint, value); }
    public Uri? LoginUri { get => _loginUri; private set => Set(ref _loginUri, value); }
    public HomeLayout HomeLayout
    {
        get => _homeLayout;
        set
        {
            var layout = Enum.IsDefined(value) ? value : HomeLayout.Classic;
            if (Set(ref _homeLayout, layout)) { Changed(nameof(HomeLayoutName)); SaveAppearance(); }
        }
    }

    public string HomeLayoutName => HomeLayout switch { HomeLayout.Minimal => "留白布局", HomeLayout.Centered => "居中启动", _ => "经典布局" };

    /// <summary>主页两类更新记录独立选择并即时保存，不影响账号、实例或启动任务。</summary>
    public bool ShowLauncherUpdates
    {
        get => _showLauncherUpdates;
        set { if (Set(ref _showLauncherUpdates, value)) SaveAppearance(); }
    }
    public bool ShowGameUpdates
    {
        get => _showGameUpdates;
        set { if (Set(ref _showGameUpdates, value)) SaveAppearance(); }
    }

    private static string ThemeKey(string theme) => theme == UiTheme.SoftThemeName ? "SoftUI" : theme == UiTheme.FlatThemeName ? "MinimalistFlat" : "Glass";
    private static string ThemeLabel(string key) => key switch { "SoftUI" => UiTheme.SoftThemeName, "MinimalistFlat" => UiTheme.FlatThemeName, _ => UiTheme.GlassThemeName };
    private double ReadThemeTransparency(string theme)
    {
        var root = _configuration.ReadLauncher();
        return Math.Clamp(LauncherConfiguration.Number(root["appearance"]?["themes"]?[ThemeKey(theme)]?["glassTransparency"], 50), 0, 100);
    }

    /// <summary>主窗口构建前恢复配置；Microsoft 条目只恢复资料，认证成功后才标记为可用。</summary>
    private void RestoreConfiguration()
    {
        var root = _configuration.ReadLauncher();
        _theme = ThemeLabel(LauncherConfiguration.Text(root["appearance"]?["theme"], "Glass"));
        _homeLayout = LauncherConfiguration.Text(root["appearance"]?["homeLayout"]) switch { "Minimal" => HomeLayout.Minimal, "Centered" => HomeLayout.Centered, _ => HomeLayout.Classic };
        _showLauncherUpdates = LauncherConfiguration.Boolean(root["appearance"]?["homeContent"]?["showLauncherUpdates"], true);
        _showGameUpdates = LauncherConfiguration.Boolean(root["appearance"]?["homeContent"]?["showGameUpdates"], true);
        _animations = LauncherConfiguration.Boolean(root["appearance"]?["animationsEnabled"], true);
        _glassTransparency = ReadThemeTransparency(_theme);
        RestorePersonalization(root);
        RestoreFlatAppearance(root);
        RestoreSoftAppearance(root);
        SavedSettings = new PreviewSettings
        {
            VersionIsolation = Enum.TryParse<VersionIsolationMode>(LauncherConfiguration.Text(root["global"]?["versionIsolation"]), out var isolation) && Enum.IsDefined(isolation) ? isolation : VersionIsolationMode.All,
            JavaPath = LauncherConfiguration.Text(root["global"]?["javaPath"]),
            GameDirectory = LauncherConfiguration.Text(root["global"]?["gameDirectory"], _configuration.MinecraftDirectory),
            MemoryMb = Math.Max(512, (int)LauncherConfiguration.Number(root["global"]?["memoryMb"], 4096)),
            JvmArguments = LauncherConfiguration.Text(root["global"]?["jvmArguments"]),
            GcMode = LauncherConfiguration.Text(root["global"]?["gcMode"], "Auto"),
            AutomaticMemory = LauncherConfiguration.Boolean(root["global"]?["automaticMemory"], true),
            OptimizeMemory = LauncherConfiguration.Boolean(root["global"]?["optimizeMemory"]),
            AuthlibInjectorPath = LauncherConfiguration.Text(root["global"]?["authlibInjectorPath"])
        };
        RestoreRuntimeConfiguration(root);
        var accountData = _configuration.ReadAccounts();
        if (accountData["accounts"] is JsonArray list)
            foreach (var node in list)
                try
                {
                    if (node?.Deserialize<StoredAccount>(AccountJson) is not { } stored || stored.Type is not ("Offline" or "Microsoft" or "Yggdrasil") || string.IsNullOrWhiteSpace(stored.Name))
                        throw new JsonException("账户资料不完整。");
                    stored.ProtectedSession ??= "";
                    if (Accounts.Any(account => account.Id == stored.Id)) throw new JsonException("重复的账户标识。");
                    Accounts.Add(new PreviewAccount(stored));
                }
                catch (Exception error) when (error is JsonException or ArgumentException)
                { _unreadableAccounts.Add(node?.DeepClone()); _configuration.ReportNotice("部分账户资料无法读取，已保留原始条目，请检查账户配置文件。"); }
        PreviewAccount? Find(string key) => Guid.TryParse(LauncherConfiguration.Text(accountData[key]), out var id) ? Accounts.FirstOrDefault(account => account.Id == id) : null;
        _microsoftMode = LauncherConfiguration.Boolean(accountData["microsoftMode"]);
        _lastMicrosoftAccount = Find("lastMicrosoftAccountId") is { IsOnline: true } microsoft ? microsoft : null;
        _lastOfflineAccount = Find("lastOfflineAccountId") is { IsMicrosoft: false, IsYggdrasil: false } offline ? offline : null;
        _defaultAccount = Find("defaultAccountId");
        foreach (var account in Accounts) account.IsDefault = ReferenceEquals(account, _defaultAccount);
        CurrentAccount = _defaultAccount is { } preferred && preferred.IsOnline == _microsoftMode ? preferred
            : Find("currentAccountId") is { } current && current.IsOnline == _microsoftMode ? current
            : (_microsoftMode ? _lastMicrosoftAccount : _lastOfflineAccount) ?? Accounts.FirstOrDefault(account => account.IsOnline == _microsoftMode);
        foreach (var account in Accounts) account.PropertyChanged += AccountChanged;
        Accounts.CollectionChanged += PersistentAccountsChanged;
        _configuration.StatusChanged += ConfigurationChanged;
        _restoringConfiguration = false;
    }

    private void SaveAppearance()
    {
        if (_restoringConfiguration) return;
        _configuration.ChangeLauncher(root =>
        {
            var appearance = LauncherConfiguration.Object(root, "appearance");
            appearance["theme"] = ThemeKey(Theme);
            appearance["homeLayout"] = HomeLayout.ToString();
            appearance["animationsEnabled"] = AnimationsEnabled;
            var homeContent = LauncherConfiguration.Object(appearance, "homeContent");
            homeContent["showLauncherUpdates"] = ShowLauncherUpdates;
            homeContent["showGameUpdates"] = ShowGameUpdates;
            LauncherConfiguration.Object(LauncherConfiguration.Object(appearance, "themes"), ThemeKey(Theme))["glassTransparency"] = GlassTransparency;
        });
    }
    private void SaveSelectedInstance()
    {
        if (!_restoringConfiguration) _configuration.ChangeLauncher(root => SaveInstanceSelection(root));
    }
    private void SaveAccountSelection()
    {
        if (_restoringConfiguration) return;
        var accounts = JsonSerializer.SerializeToNode(Accounts.Select(account => account.Stored).ToArray(), AccountJson)!.AsArray();
        foreach (var unreadable in _unreadableAccounts) accounts.Add(unreadable?.DeepClone());
        _configuration.ChangeAccounts(root =>
        {
            root["accounts"] = accounts;
            root["currentAccountId"] = CurrentAccount?.Id.ToString("D");
            root["defaultAccountId"] = _defaultAccount?.Id.ToString("D");
            root["microsoftMode"] = MicrosoftMode;
            root["lastMicrosoftAccountId"] = _lastMicrosoftAccount?.Id.ToString("D");
            root["lastOfflineAccountId"] = _lastOfflineAccount?.Id.ToString("D");
        });
    }
    private void AccountChanged(object? sender, PropertyChangedEventArgs e) => Signal();
    private void PersistentAccountsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (PreviewAccount account in e.OldItems)
        {
            account.PropertyChanged -= AccountChanged;
            CancelSkinRequest(account.Id);
            if (_accountAttempts.Remove(account.Id, out var cancellation)) { cancellation.Cancel(); }
        }
        if (e.NewItems != null) foreach (PreviewAccount account in e.NewItems) account.PropertyChanged += AccountChanged;
        if (_lastMicrosoftAccount != null && !Accounts.Contains(_lastMicrosoftAccount)) _lastMicrosoftAccount = null;
        if (_lastOfflineAccount != null && !Accounts.Contains(_lastOfflineAccount)) _lastOfflineAccount = null;
        if (_defaultAccount != null && !Accounts.Contains(_defaultAccount)) _defaultAccount = null;
        if (_account != null && !Accounts.Contains(_account)) CurrentAccount = Accounts.FirstOrDefault(account => account.IsOnline == MicrosoftMode);
        SaveAccountSelection(); Signal();
    }
    private void ConfigurationChanged() => PostUi(() => { Changed(nameof(ConfigurationMessage)); Changed(nameof(HasConfigurationMessage)); });
    private void PostUi(Action action)
    {
        if (_lifetime.IsCancellationRequested) return;
        Application.Current.Dispatcher.InvokeAsync(() => { if (!_lifetime.IsCancellationRequested) action(); });
    }
    /// <summary>保存全局草稿；隔离方式仅切换读取目录，不迁移文件，写入或游戏任务进行时禁止切换。</summary>
    public async Task<bool> SaveGlobalSettingsAsync(PreviewSettings settings)
    {
        settings.GameDirectory = Infrastructure.Runtime.InstanceStore.NormalizeRoot(settings.GameDirectory);
        var changingIsolation = settings.VersionIsolation != SavedSettings.VersionIsolation;
        var changingRoot = !string.Equals(settings.GameDirectory, SavedSettings.GameDirectory, StringComparison.OrdinalIgnoreCase);
        if (changingIsolation && (IsBusy || Instances.Any(instance => instance.IsEditingContent)))
            throw new InvalidOperationException("请等待游戏和文件操作结束后再更改版本隔离。");
        if (changingIsolation) { _applyingIsolation = true; Signal(); }
        try
        {
            _configuration.ChangeLauncher(root =>
            {
                var global = LauncherConfiguration.Object(root, "global");
                global["javaPath"] = settings.JavaPath; global["memoryMb"] = settings.MemoryMb;
                global["gameDirectory"] = settings.GameDirectory; global["jvmArguments"] = settings.JvmArguments;
                global["gcMode"] = settings.GcMode;
                global["automaticMemory"] = settings.AutomaticMemory; global["optimizeMemory"] = settings.OptimizeMemory;
                global["authlibInjectorPath"] = settings.AuthlibInjectorPath;
                global["versionIsolation"] = settings.VersionIsolation.ToString();
            });
            if (!await _configuration.FlushAsync()) return false;
            SavedSettings = settings.Copy();
            foreach (var instance in Instances) instance.IsolationMode = SavedSettings.VersionIsolation;
            if (changingRoot) await SelectRootAsync(settings.GameDirectory);
            if (changingIsolation)
                foreach (var instance in Instances) NotifyInstanceContentChanged(instance.Root, instance.Id);
            Signal(); return true;
        }
        finally { if (changingIsolation) { _applyingIsolation = false; Signal(); } }
    }

    public PreviewAccount AddOfflineAccount(string name)
    {
        var offline = OfflineAccount.Create(name);
        var account = new PreviewAccount(new StoredAccount { Type = "Offline", Name = offline.Username, Uuid = offline.Uuid });
        Accounts.Add(account); CurrentAccount = account;
        return account;
    }
    /// <summary>验证离线昵称并及时保存；共享对象、选中账号、UUID 和各模式的选择均保持原值。</summary>
    public async Task<bool> RenameOfflineAccountAsync(PreviewAccount account, string name)
    {
        if (!Accounts.Contains(account)) throw new InvalidOperationException("这个账号已被移除。");
        if (account.IsMicrosoft) throw new InvalidOperationException("正版账号请前往官网修改玩家名称。");
        if (IsEditingBlocked || IsLoading || IsAuthenticating) throw new InvalidOperationException("请等待游戏或文件操作结束后再修改昵称。");
        name = name.Trim();
        var error = ValidateOfflineName(name);
        if (error.Length > 0) throw new ArgumentException(error);
        if (Accounts.Any(other => other != account && !other.IsMicrosoft && other.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("这个离线昵称已被其他账号使用。");
        account.RenameOffline(name);
        SaveAccountSelection();
        Signal();
        return await _configuration.FlushAsync();
    }
    public static string ValidateOfflineName(string name) => name.Length is < 3 or > 16 || name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_'))
        ? "使用 3–16 位字母、数字或下划线。" : "";
    public async Task RestoreMicrosoftAccountsAsync()
    {
        await RestoreSkinCacheAsync();
        foreach (var account in Accounts.Where(account => account.IsMicrosoft).ToArray())
        {
            if (_lifetime.IsCancellationRequested) break;
            if (account.Stored.ProtectedSession.Length == 0) { account.MarkLoginFailure("需要重新登录"); continue; }
            await AuthenticateMicrosoftAsync(account, false);
        }
    }
    private bool _validatingAccounts;
    /// <summary>进入账号页时静默校验一次：只处理超过半小时未验证的正版账号，不进入交互式设备码流程。</summary>
    public async Task ValidateAccountsAsync()
    {
        if (_validatingAccounts || IsAuthenticating || IsBusy) return;
        _validatingAccounts = true;
        try
        {
            foreach (var account in Accounts.Where(account => account.IsMicrosoft && account.Stored.ProtectedSession.Length > 0).ToArray())
            {
                if (_lifetime.IsCancellationRequested) break;
                if (!Accounts.Contains(account)) continue;
                if (account.Stored.LastAuthenticatedAt is { } verified && DateTimeOffset.UtcNow - verified < TimeSpan.FromMinutes(30)) continue;
                await AuthenticateMicrosoftAsync(account, false);
            }
        }
        finally { _validatingAccounts = false; }
    }
    /// <summary>启动时恢复外置账号：只重发刷新请求，失败保留资料并提示重新登录。</summary>
    public async Task RestoreYggdrasilAccountsAsync()
    {
        foreach (var account in Accounts.Where(account => account.IsYggdrasil).ToArray())
        {
            if (_lifetime.IsCancellationRequested) break;
            if (account.Stored.ProtectedYggdrasil.Length == 0) { account.MarkLoginFailure("需要重新登录"); continue; }
            account.SetRestoring(true);
            try
            {
                var result = await YggdrasilSessionService.RestoreAsync(account.Stored.YggdrasilServer, account.Stored.ProtectedYggdrasil, _lifetime.Token);
                if (!Accounts.Contains(account)) continue;
                if (result.Account is { } runtime)
                {
                    account.ApplyYggdrasil(runtime, result.Protected);
                    SaveAccountSelection();
                    _ = RefreshSkinAsync(account, true);
                }
                else if (result.RequiresLogin) account.MarkLoginFailure("需要重新登录");
                else account.MarkUnverified("暂时无法校验登录");
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is HttpRequestException or IOException or System.Text.Json.JsonException)
            {
                Trace.TraceWarning("外置账号未恢复：{0}", error.GetType().Name);
                account.MarkUnverified("暂时无法校验登录");
            }
            catch (Exception error) when (error is InvalidOperationException or UriFormatException)
            {
                account.MarkLoginFailure("需要重新登录");
            }
            finally { account.SetRestoring(false); Signal(); }
        }
    }

    /// <summary>交互式登录外置账号：账号名与密码只交给验证服务，不落到本地配置。</summary>
    public async Task<PreviewAccount> LoginYggdrasilAsync(string server, string username, string password)
    {
        if (IsAuthenticating) throw new InvalidOperationException("正在登录，请稍候。");
        IsAuthenticating = true; LoginError = ""; LoginProgress = "正在登录外置账号";
        try
        {
            var result = await YggdrasilSessionService.LoginAsync(server, username, password, SelectYggdrasilProfileAsync, _lifetime.Token);
            if (result.Account is not { } runtime) throw new InvalidOperationException("验证服务没有返回角色。");
            var target = Accounts.FirstOrDefault(account => account.IsYggdrasil && account.Uuid == runtime.Uuid && account.Stored.YggdrasilServer == runtime.ApiRoot.ToString().TrimEnd('/'));
            if (target == null) { target = new PreviewAccount(new StoredAccount { Type = "Yggdrasil", Name = runtime.Username, Uuid = runtime.Uuid }); Accounts.Add(target); }
            target.ApplyYggdrasil(runtime, result.Protected);
            CurrentAccount = target; SaveAccountSelection(); Signal();
            _ = RefreshSkinAsync(target, true);
            return target;
        }
        finally
        {
            IsAuthenticating = false; LoginProgress = ""; Signal();
        }
    }

    /// <summary>角色选择在界面线程弹框，取消时让核心的登录流程一起中止。</summary>
    private Task<Guid> SelectYggdrasilProfileAsync(IReadOnlyList<FMCore.Accounts.YggdrasilProfile> profiles, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<Guid>();
        PostUi(() =>
        {
            try
            {
                var selected = Presentation.Dialogs.YggdrasilProfileDialog.Ask(System.Windows.Application.Current?.MainWindow, profiles);
                if (selected is { } id) completion.TrySetResult(id);
                else completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception error) { completion.TrySetException(error); }
        });
        return completion.Task;
    }

    public Task LoginMicrosoftAsync(PreviewAccount? account = null) => AuthenticateMicrosoftAsync(account, true);    public void CancelMicrosoftLogin()
    { foreach (var cancellation in _accountAttempts.Values.ToArray()) cancellation.Cancel(); }

    private async Task AuthenticateMicrosoftAsync(PreviewAccount? existing, bool interactive)
    {
        if (interactive && IsAuthenticating) return;
        var attemptId = existing?.Id ?? Guid.NewGuid();
        if (_accountAttempts.ContainsKey(attemptId)) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _accountAttempts[attemptId] = cancellation;
        if (interactive) { IsAuthenticating = true; LoginCode = ""; LoginError = ""; LoginHint = ""; LoginUri = null; LoginProgress = "正在恢复 Microsoft 授权"; }
        existing?.SetRestoring(true);
        try
        {
            var result = await MicrosoftSessionService.AuthenticateAsync(existing?.Stored.ProtectedSession ?? "", interactive,
                code => PostUi(() => PresentLoginCode(code)),
                progress => { if (interactive) PostUi(() => LoginProgress = progress); },
                encrypted => { if (existing != null && !interactive) PostUi(() => { if (Accounts.Contains(existing)) { existing.Stored.ProtectedSession = encrypted; SaveAccountSelection(); } }); },
                cancellation.Token);
            if (existing != null && !Accounts.Contains(existing)) return;
            if (result.Account == null) { existing?.MarkLoginFailure("需要重新登录"); return; }
            if (existing != null && existing.Uuid != Guid.Empty && existing.Uuid != result.Account.Uuid)
                throw new InvalidOperationException("登录的账户与所选账户不一致。");
            var target = existing ?? Accounts.FirstOrDefault(account => account.IsMicrosoft && account.Uuid == result.Account.Uuid);
            if (target == null) { target = new PreviewAccount(new StoredAccount { Type = "Microsoft", Name = result.Account.Username, Uuid = result.Account.Uuid }); Accounts.Add(target); }
            target.ApplyMicrosoft(result.Account, result.ProtectedSession);
            if (interactive) { CurrentAccount = target; LoginProgress = "Microsoft 登录成功"; }
            SaveAccountSelection();
            _ = RefreshSkinAsync(target, true);
        }
        catch (OperationCanceledException)
        { existing?.MarkLoginFailure("需要重新登录"); if (interactive) LoginError = "登录已取消。"; }
        catch (Exception error)
        {
            Trace.TraceWarning("Microsoft 会话未恢复：{0}", error.GetType().Name);
            // 只有明确拒绝（缓存不可解密、账号不一致）才清登录态；网络与服务端临时故障保留令牌，下次再校验。
            var rejected = error is CryptographicException or FormatException or InvalidOperationException;
            var message = rejected
                ? error is InvalidOperationException ? "登录的账户与所选账户不一致，请重新登录。" : "授权缓存无法解密，请重新登录。"
                : error is HttpRequestException ? "无法连接认证服务，请检查网络后重试。"
                : "认证服务暂时不可用，请稍后重试。";
            if (rejected) existing?.MarkLoginFailure(message);
            else existing?.MarkUnverified("暂时无法校验登录");
            if (interactive)
            {
                var report = await ReportErrorAsync(error, "Microsoft 登录", LoginProgress);
                LoginError = report.Summary;
            }
            else Log("验证 Microsoft 账号失败：" + SanitizeError(error.ToString()));
        }
        finally
        {
            _accountAttempts.Remove(attemptId);
            existing?.SetRestoring(false);
            if (interactive) { IsAuthenticating = false; LoginCode = ""; LoginUri = null; LoginHint = ""; }
            Signal();
        }
    }

    /// <summary>设备码一到手就复制并拉起 Edge，用户只需要在浏览器里粘贴一次。</summary>
    private void PresentLoginCode(MicrosoftLoginCode code)
    {
        LoginCode = code.UserCode;
        LoginUri = code.VerificationUri;
        var copied = false;
        try { Clipboard.SetText(code.UserCode); copied = true; }
        catch (ExternalException) { }
        catch (InvalidOperationException) { }
        var opened = OpenVerification(LoginUri);
        LoginHint = (copied, opened) switch
        {
            (true, true) => "授权代码已复制，并已在 Edge 中打开授权页面，直接粘贴即可。",
            (true, false) => "授权代码已复制，请手动打开授权页面后粘贴。",
            (false, true) => "已在 Edge 中打开授权页面，请复制下方代码后粘贴。",
            _ => "请手动复制下方代码，并在浏览器中打开授权页面。"
        };
    }
    /// <summary>优先用 Edge 打开授权页；系统没有 Edge 或协议未注册时退回默认浏览器。</summary>
    private static bool OpenVerification(Uri? uri)
    {
        if (uri is not { Scheme: "https" } target) return false;
        foreach (var start in new[]
        {
            new ProcessStartInfo("microsoft-edge:" + target.AbsoluteUri) { UseShellExecute = true },
            new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true }
        })
        {
            try { Process.Start(start); return true; }
            catch (Win32Exception) { }
            catch (InvalidOperationException) { }
        }
        return false;
    }
    private void DisposePersistence()
    {
        _lifetime.Cancel();
        _configuration.StatusChanged -= ConfigurationChanged;
        Accounts.CollectionChanged -= PersistentAccountsChanged;
        foreach (var account in Accounts) account.PropertyChanged -= AccountChanged;
        foreach (var cancellation in _accountAttempts.Values.ToArray()) cancellation.Cancel();
    }
}
