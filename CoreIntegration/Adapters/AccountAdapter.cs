using FMCore.Authentication;
using FMLauncher.CoreIntegration.Models;
using FMLauncher.Infrastructure.Configuration;
namespace FMLauncher.CoreIntegration.Adapters;

/// <summary>所有认证都交给正式 SDK；不读取令牌，不允许正版失败后回退离线。</summary>
public sealed class AccountAdapter(AppPaths paths) : IDisposable
{
    private readonly AccountManager _accounts = new(paths.AccountsDirectory);
    /// <summary>读取账号资料快照，不读取或暴露安全存储中的令牌。</summary>
    public async Task<IReadOnlyList<AccountItem>> ListAsync(CancellationToken ct)
    {
        var snapshot = await _accounts.GetAccountsAsync(ct);
        return snapshot.Accounts.Select(a => new AccountItem(a.Id, a.Name, a.Kind.ToString(), a.Id == snapshot.DefaultAccountId, a.Uuid, a.HasGameEntitlement)).ToArray();
    }
    public async Task AddOfflineAsync(string name, CancellationToken ct) => await _accounts.LoginOfflineAsync(name, ct);
    /// <summary>使用开发者配置的合法公共应用 ID 发起设备码授权；回调只负责展示，取消传递至 SDK。</summary>
    public async Task<Guid> LoginAsync(string clientId, Action<DevicePrompt> show, IProgress<WorkProgress> progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new InvalidOperationException("此版本暂未配置 Microsoft 登录服务，请联系启动器开发者。");
        var identity = await _accounts.LoginMicrosoftAsync(new(clientId), (code, token) =>
        {
            show(new(code.UserCode, code.VerificationUri, code.ExpiresAt)); return Task.CompletedTask;
        }, new InlineProgress<AuthenticationProgress>(p => progress.Report(new(p.Stage.ToString()))), ct);
        return identity.Account.Id;
    }
    public async Task SelectAsync(Guid id, CancellationToken ct) => await _accounts.SwitchAsync(id, ct);
    public Task DefaultAsync(Guid id, CancellationToken ct) => _accounts.SetDefaultAsync(id, ct);
    public async Task DeleteAsync(Guid id, CancellationToken ct) => await _accounts.DeleteAsync(id, ct);
    /// <summary>显式刷新指定账号；任何认证失败均保留正版身份并反馈错误。</summary>
    public async Task RefreshAsync(Guid id, CancellationToken ct) => await _accounts.RefreshAsync(id, cancellationToken: ct);
    public void Dispose() => _accounts.Dispose();
}

