using System.Security.Cryptography;
using FMCore.Accounts;


namespace FocusMinecraftLauncher.Infrastructure.Authentication;

/// <summary>通过 FMCore 的 MSAL 缓存接口恢复与刷新授权；落盘前使用 DPAPI 当前用户保护。</summary>
public static class MicrosoftSessionService
{
    // 启动器开发者提供的公共应用 ID；不包含 Client Secret。
    private const string ClientId = ClientIDS.MicrosoftClientId;
    public sealed record Result(MicrosoftAccount? Account, string ProtectedSession, bool RequiresLogin);

    public static async Task<Result> AuthenticateAsync(string protectedSession, bool interactive,
        Action<MicrosoftLoginCode> code, Action<string> progress, Action<string> cacheChanged, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var encrypted = protectedSession;
        if (!interactive && encrypted.Length == 0) return new(null, encrypted, true);
        var authenticator = new MicrosoftAuthenticator(ClientId);
        var needsLogin = false;
        authenticator.TokenCache.SetBeforeAccess(arguments =>
        {
            if (encrypted.Length == 0) return;
            byte[] bytes;
            try { bytes = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser); }
            catch (Exception error) when (interactive && (error is CryptographicException or FormatException))
            { encrypted = ""; return; }
            try { arguments.TokenCache.DeserializeMsalV3(bytes, true); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        });
        authenticator.TokenCache.SetAfterAccess(arguments =>
        {
            if (!arguments.HasStateChanged) return;
            var bytes = arguments.TokenCache.SerializeMsalV3();
            try { encrypted = Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            cacheChanged(encrypted);
        });
        try
        {
            var account = await authenticator.LoginAsync(loginCode =>
            {
                if (!interactive) { needsLogin = true; linked.Cancel(); return; }
                code(loginCode);
            }, progress, linked.Token).ConfigureAwait(false);
            return new(account, encrypted, false);
        }
        catch (OperationCanceledException) when (needsLogin && !cancellationToken.IsCancellationRequested)
        { return new(null, encrypted, true); }
    }
}
