using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using FMLauncher.Common;
using FMLauncher.Infrastructure.Logging;
using FMLauncher.CoreIntegration.Adapters;
using FMLauncher.CoreIntegration.Models;
using FMLauncher.Infrastructure.Configuration;
namespace FMLauncher.Features.Accounts.ViewModels;

public sealed class AccountsViewModel : PageViewModel
{
    private readonly AccountAdapter _adapter;
    private readonly SettingsStore _settings;
    private readonly DeveloperServices _developer;
    private AccountItem? _selected, _current;
    public AccountItem? Current { get => _current; private set => Set(ref _current, value); }
    private string _name = "Player";
    private string _deviceText = "";
    private Uri? _deviceAddress;
    private bool _loaded;
    public ObservableCollection<AccountItem> Accounts { get; } = [];
    public AccountItem? Selected { get => _selected; set => Set(ref _selected, value); }
    public string PlayerName { get => _name; set => Set(ref _name, value); }
    public string DeviceText { get => _deviceText; private set => Set(ref _deviceText, value); }
    public AsyncCommand Reload { get; }
    public AsyncCommand AddOffline { get; }
    public AsyncCommand Login { get; }
    public AsyncCommand Switch { get; }
    public AsyncCommand SetDefault { get; }
    public AsyncCommand Delete { get; }
    public AsyncCommand Refresh { get; }
    public AsyncCommand OpenLogin { get; }
    public AsyncCommand Cancel { get; }
    public AccountsViewModel(AccountAdapter adapter, SettingsStore settings, DeveloperServices developer)
    {
        _adapter = adapter; _settings = settings; _developer = developer;
        Reload = Command(ReloadAsync);
        OpenLogin = Command(ct => { if (_deviceAddress != null) Process.Start(new ProcessStartInfo(_deviceAddress.AbsoluteUri) { UseShellExecute = true }); return Task.CompletedTask; });
        AddOffline = Command(async ct =>
        {
            var name = PlayerName.Trim(); await adapter.AddOfflineAsync(name, ct); await ReloadAsync(ct);
            Selected = Accounts.FirstOrDefault(a => a.Kind == "Offline" && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            await UseSelectedAsync(ct); Status = "离线账号已添加，可以开始游戏。";
        });
        Login = Command(async ct =>
        {
            if (!developer.MicrosoftLoginConfigured) throw new InvalidOperationException("此版本暂未配置 Microsoft 登录服务，请联系启动器开发者。离线账号仍可正常使用。");
            using var expiry = CancellationTokenSource.CreateLinkedTokenSource(ct); expiry.CancelAfter(TimeSpan.FromMinutes(16));
            try
            {
                var loggedIn = await adapter.LoginAsync(developer.MicrosoftClientId!, code =>
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        _deviceAddress = code.Address; DeviceText = $"授权码：{code.Code} · 有效至 {code.ExpiresAt.LocalDateTime:HH:mm}\n请在已打开的 Microsoft 官方网页输入该代码；未打开时可点击下方按钮。";
                        OpenLogin.Execute(null);
                    }), new Progress<WorkProgress>(p => Status = PlayerText.Stage(p.Stage)), expiry.Token);
                await ReloadAsync(ct); Selected = Accounts.FirstOrDefault(a => a.Id == loggedIn);
                await UseSelectedAsync(ct); Status = "登录成功，可以开始游戏。";
            }
            finally { DeviceText = ""; _deviceAddress = null; }
        });
        Switch = Command(UseSelectedAsync);
        SetDefault = Command(async ct => { var account = Require(); await adapter.DefaultAsync(account.Id, ct); await ReloadAsync(ct); Status = "默认账号已保存。"; });
        Delete = Command(async ct =>
        {
            var account = Require();
            if (MessageBox.Show($"删除本地账号 {account.Name}？游戏文件不会受影响。", "删除账号", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await adapter.DeleteAsync(account.Id, ct);
            if (settings.Current.AccountId == account.Id) { settings.Current.AccountId = null; settings.Save(); }
            await ReloadAsync(ct);
        });
        Refresh = Command(async ct => { await adapter.RefreshAsync(Require().Id, ct); Status = "账号已刷新。"; });
        Cancel = Command(ct => { Login.Cancel(); Refresh.Cancel(); return Task.CompletedTask; });
    }
    public bool MicrosoftLoginConfigured => _developer.MicrosoftLoginConfigured;
    /// <summary>账号切换由 SDK 完成；用户配置保存成功后才更新主页当前身份。</summary>
    private async Task UseSelectedAsync(CancellationToken ct)
    {
        var account = Require(); await _adapter.SelectAsync(account.Id, ct);
        var previous = _settings.Current.AccountId; _settings.Current.AccountId = account.Id;
        try { _settings.Save(); } catch { _settings.Current.AccountId = previous; throw; }
        Current = account; Status = "已切换到 " + account.Name + "。";
    }
    private AccountItem Require() => Selected ?? throw new InvalidOperationException("请先选择账号。");
    public async Task ReloadAsync(CancellationToken ct)
    {
        var id = Selected?.Id ?? _settings.Current.AccountId;
        var accounts = await _adapter.ListAsync(ct);
        Accounts.Clear(); foreach (var item in accounts) Accounts.Add(item);
        Selected = !_loaded ? Accounts.FirstOrDefault(a => a.IsDefault) ?? Accounts.FirstOrDefault(a => a.Id == id) ?? Accounts.FirstOrDefault()
            : Accounts.FirstOrDefault(a => a.Id == id) ?? Accounts.FirstOrDefault(a => a.IsDefault) ?? Accounts.FirstOrDefault();
        if ((!_loaded || _settings.Current.AccountId == null) && Selected != null) { _settings.Current.AccountId = Selected.Id; _settings.Save(); }
        Current = Accounts.FirstOrDefault(a => a.Id == _settings.Current.AccountId) ?? Selected;
        _loaded = true;
    }
}

