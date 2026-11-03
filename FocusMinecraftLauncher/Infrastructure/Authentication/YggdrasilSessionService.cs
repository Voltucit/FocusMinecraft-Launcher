using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FMCore.Accounts;

namespace FocusMinecraftLauncher.Infrastructure.Authentication;

/// <summary>外置（Yggdrasil）身份：认证交给核心完成，令牌只以当前 Windows 用户为范围加密后保存。</summary>
public static class YggdrasilSessionService
{
    /// <summary>LittleSkin 的验证服务地址，界面上作为默认选项。</summary>
    public const string LittleSkin = "https://littleskin.cn/api/yggdrasil";

    /// <summary>登录或恢复的结果；Account 为空时说明需要重新登录。</summary>
    public sealed record LoginResult(YggdrasilAccount? Account, string Protected, bool RequiresLogin);

    /// <summary>校验并规范化用户填写的验证服务地址；只接受 HTTPS，且不允许内嵌账号或查询参数。</summary>
    public static bool TryNormalize(string text, out string address, out string error)
    {
        address = ""; error = "";
        var trimmed = text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "验证服务地址需要是 HTTPS 地址。";
            return false;
        }
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            error = "验证服务地址不能包含账号、查询参数或片段。";
            return false;
        }
        address = uri.ToString().TrimEnd('/');
        return true;
    }

    public static async Task<LoginResult> LoginAsync(
        string address,
        string username,
        string password,
        Func<IReadOnlyList<YggdrasilProfile>, CancellationToken, Task<Guid>>? selectProfile,
        CancellationToken cancellationToken)
    {
        var account = await new YggdrasilAuthenticator(address).LoginAsync(username, password, selectProfile, cancellationToken);
        return new(account, Protect(account), false);
    }

    public static async Task<LoginResult> RestoreAsync(string address, string protectedSession, CancellationToken cancellationToken)
    {
        if (protectedSession.Length == 0) return new(null, "", true);
        if (!TryUnprotect(protectedSession, out var cached) || cached is null) return new(null, "", true);
        var source = string.IsNullOrWhiteSpace(cached.Address) ? address : cached.Address;
        try
        {
            var account = await new YggdrasilAuthenticator(source)
                .RestoreAsync(cached.Login, cached.Profile, cached.AccessToken, cached.ClientToken, cancellationToken);
            return new(account, Protect(account), false);
        }
        catch (InvalidOperationException)
        {
            // 核心在令牌失效时抛这个异常；其余网络或格式问题交给调用方按“暂时无法校验”处理。
            return new(null, "", true);
        }
    }

    private sealed record Cached(string Address, string Login, Guid Profile, string AccessToken, string ClientToken);

    private static string Protect(YggdrasilAccount account) => Convert.ToBase64String(ProtectedData.Protect(
        JsonSerializer.SerializeToUtf8Bytes(new Cached(
            account.ApiRoot.ToString().TrimEnd('/'), account.LoginName, account.Uuid, account.AccessToken, account.ClientToken)),
        null, DataProtectionScope.CurrentUser));

    private static bool TryUnprotect(string value, out Cached? cached)
    {
        cached = null;
        try
        {
            cached = JsonSerializer.Deserialize<Cached>(
                ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
            return cached is not null;
        }
        catch (Exception error) when (error is FormatException or CryptographicException or JsonException or ArgumentException)
        {
            return false;
        }
    }
}
