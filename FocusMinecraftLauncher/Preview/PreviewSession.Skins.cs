using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using FMCore.Accounts;
using FocusMinecraftLauncher.Infrastructure.Authentication;

namespace FocusMinecraftLauncher.Preview;

public sealed partial class PreviewSession
{
    private OfficialSkinService? _skinService;
    private OfficialSkinService Skins => _skinService ??= new(_configuration.ConfigDirectory);
    private readonly Dictionary<Guid, CancellationTokenSource> _skinRequests = [];

    /// <summary>恢复已有正版账号的头像缓存，不把缓存恢复当作会话认证。</summary>
    private async Task RestoreSkinCacheAsync()
    {
        foreach (var account in Accounts.Where(account => account.IsMicrosoft).ToArray())
        {
            try
            {
                var cached = await Skins.ReadCacheAsync(account.Uuid, _lifetime.Token);
                if (cached != null && Accounts.Contains(account)) account.ApplySkin(cached.Head, cached.Png);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) when (IsSkinError(error))
            { Trace.TraceWarning("皮肤缓存无法读取：{0}", error.GetType().Name); }
        }
    }

    /// <summary>登录后或手动刷新时读取官方皮肤；失败保留登录结果与可用的旧头像。</summary>
    public async Task RefreshSkinAsync(PreviewAccount account, bool replacePending = false)
    {
        if (!account.IsMicrosoft || !account.IsValid || account.RuntimeAccount is not MicrosoftAccount runtime || !Accounts.Contains(account)) return;
        if (_skinRequests.TryGetValue(account.Id, out var previous))
        { if (!replacePending) return; previous.Cancel(); }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _skinRequests[account.Id] = cancellation;
        account.SetSkinState(true, "正在读取皮肤…");
        try
        {
            var skin = await Skins.FetchAsync(runtime, cancellation.Token);
            if (cancellation.IsCancellationRequested || !Accounts.Contains(account) || !ReferenceEquals(account.RuntimeAccount, runtime)) return;
            account.ApplySkin(skin?.Head, skin?.Png);
            account.SetSkinState(false, skin == null ? "未获取到自定义皮肤，显示默认头像。" : skin.CacheSaved ? "" : "头像已更新，但皮肤缓存保存失败。");
        }
        catch (OperationCanceledException)
        {
            if (!cancellation.IsCancellationRequested) account.SetSkinState(false, "皮肤服务响应超时，请稍后重试。");
        }
        catch (Exception error) when (IsSkinError(error))
        {
            if (cancellation.IsCancellationRequested || !Accounts.Contains(account)) return;
            Trace.TraceWarning("官方皮肤读取失败：{0}", error.GetType().Name);
            var message = error is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }
                ? "皮肤授权不可用，请重新登录后重试。"
                : error is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } ? "皮肤服务请求过于频繁，请稍后重试。"
                : error is HttpRequestException ? "无法连接皮肤服务，请检查网络后重试。" : "皮肤数据无法读取，请稍后重试。";
            account.SetSkinState(false, message + (account.HeadImage != null ? " 已保留缓存头像。" : " 显示默认头像。"));
        }
        finally
        {
            if (_skinRequests.TryGetValue(account.Id, out var current) && ReferenceEquals(current, cancellation))
            { _skinRequests.Remove(account.Id); account.SetSkinState(false, account.SkinMessage); }
            Signal();
        }
    }
    private void CancelSkinRequest(Guid id)
    { if (_skinRequests.Remove(id, out var cancellation)) cancellation.Cancel(); }
    private static bool IsSkinError(Exception error) => error is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException;
}
