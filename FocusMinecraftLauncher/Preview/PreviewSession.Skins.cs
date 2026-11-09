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
        foreach (var account in Accounts.Where(account => account.IsMicrosoft || account.IsYggdrasil).ToArray())
        {
            try
            {
                var cached = await Skins.ReadCacheAsync(account.Uuid, _lifetime.Token);
                if (cached != null && Accounts.Contains(account)) account.ApplySkin(cached.Head, cached.Png, cached.IsSlim, cached.CapePng);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) when (IsSkinError(error))
            { Trace.TraceWarning("皮肤缓存无法读取：{0}", error.GetType().Name); }
        }
    }

    /// <summary>登录后或手动刷新时读取皮肤；正版走官方档案，外置账号走自己的验证服务。失败保留登录结果与可用的旧头像。</summary>
    public async Task RefreshSkinAsync(PreviewAccount account, bool replacePending = false)
    {
        if (!(account.IsMicrosoft || account.IsYggdrasil) || !account.IsValid || account.RuntimeAccount == null || !Accounts.Contains(account)) return;
        var runtime = account.RuntimeAccount;
        if (_skinRequests.TryGetValue(account.Id, out var previous))
        { if (!replacePending) return; previous.Cancel(); }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _skinRequests[account.Id] = cancellation;
        account.SetSkinState(true, "正在读取皮肤…");
        try
        {
            var skin = runtime switch
            {
                MicrosoftAccount microsoft => await Skins.FetchAsync(microsoft, cancellation.Token),
                YggdrasilAccount external => await Skins.FetchYggdrasilAsync(external.ApiRoot, external.Uuid, cancellation.Token),
                _ => null
            };
            if (cancellation.IsCancellationRequested || !Accounts.Contains(account) || !ReferenceEquals(account.RuntimeAccount, runtime)) return;
            account.ApplySkin(skin?.Head, skin?.Png, skin?.IsSlim ?? false, skin?.CapePng);
            var message = skin?.Png == null ? "未获取到自定义皮肤，显示默认外观。" : skin.CacheSaved ? "" : "外观已更新，但皮肤缓存保存失败。";
            if (!string.IsNullOrEmpty(skin?.CapeMessage)) message = (message + " " + skin.CapeMessage).Trim();
            account.SetSkinState(false, message);
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
                : error is HttpRequestException { StatusCode: HttpStatusCode.NotFound } ? "验证服务没有提供皮肤档案接口，请联系服主。"
                : error is HttpRequestException { StatusCode: { } status } ? $"皮肤服务返回 {status}，请稍后重试。"
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

    /// <summary>预览已保存的离线本地皮肤，只读取现有游戏目录，不修改皮肤或游戏文件。</summary>
    public async Task RefreshLocalSkinAsync(PreviewAccount account)
    {
        if (account.IsOnline || !Accounts.Contains(account)) return;
        var name = account.Name;
        var paths = new[] { CurrentInstance?.GameDirectory, SavedSettings.GameDirectory }
            .Concat(Instances.Select(instance => instance.GameDirectory))
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => LocalSkinStore.FileFor(directory!, name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        try
        {
            var skin = await Task.Run(async () =>
            {
                var path = paths.FirstOrDefault(File.Exists);
                if (path == null) return null;
                if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("本地皮肤图片过大。");
                return OfficialSkinService.DecodeSkin(await File.ReadAllBytesAsync(path, _lifetime.Token));
            }, _lifetime.Token);
            if (Accounts.Contains(account) && account.Name == name)
            {
                account.ApplySkin(skin?.Head, skin?.Png, skin?.IsSlim ?? false);
                account.SetSkinState(false, "");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (IsSkinError(error))
        { if (Accounts.Contains(account) && account.Name == name) account.SetSkinState(false, "本地皮肤无法读取，请重新选择有效的 PNG 皮肤。"); }
    }

    private readonly HashSet<Guid> _profileRefreshes = [];
    /// <summary>资料的版本号，主页据此重新读取披风，避免两处各存一份过期数据。</summary>
    public int ProfileRevision { get => _profileRevision; private set { if (_profileRevision == value) return; _profileRevision = value; Changed(nameof(ProfileRevision)); } }
    private int _profileRevision;

    /// <summary>手动刷新资料：重新校验授权状态，再读取头像、皮肤与披风。</summary>
    public async Task<bool> RefreshProfileAsync(PreviewAccount account)
    {
        if (!account.IsOnline) return false;
        if (account.IsYggdrasil)
        {
            if (!Accounts.Contains(account) || !_profileRefreshes.Add(account.Id)) return false;
            try
            {
                account.SetRestoring(true);
                var result = await YggdrasilSessionService.RestoreAsync(account.Stored.YggdrasilServer, account.Stored.ProtectedYggdrasil, _lifetime.Token);
                if (result.Account is { } external && Accounts.Contains(account)) account.ApplyYggdrasil(external, result.Protected);
                else if (result.RequiresLogin) account.MarkLoginFailure("需要重新登录");
                if (!account.IsValid) return false;
                await RefreshSkinAsync(account, true);
                ProfileRevision++;
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception error) when (IsSkinError(error))
            { Trace.TraceWarning("外置账号资料刷新失败：{0}", error.GetType().Name); account.MarkUnverified("暂时无法校验登录"); return false; }
            finally
            { _profileRefreshes.Remove(account.Id); account.SetRestoring(false); Signal(); }
        }
        if (!account.IsMicrosoft || !Accounts.Contains(account) || !_profileRefreshes.Add(account.Id)) return false;
        try
        {
            account.SetRestoring(true);
            await AuthenticateMicrosoftAsync(account, false);
            if (!account.IsValid) return false;
            await RefreshSkinAsync(account, true);
            if (account.RuntimeAccount is MicrosoftAccount runtime)
            {
                try { await Skins.GetCapesAsync(runtime, _lifetime.Token); }
                catch (OperationCanceledException) { return false; }
                catch (Exception error) when (IsSkinError(error))
                { Trace.TraceWarning("披风列表无法读取：{0}", error.GetType().Name); }
            }
            ProfileRevision++;
            return true;
        }
        finally
        {
            _profileRefreshes.Remove(account.Id);
            account.SetRestoring(false);
            Signal();
        }
    }
    private static bool IsSkinError(Exception error) => error is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException;
}
