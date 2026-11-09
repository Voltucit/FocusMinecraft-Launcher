using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Preview;

namespace FocusMinecraftLauncher.Features.Accounts.ViewModels;

/// <summary>管理共享账户列表及认证入口；资料选择与正版认证分别处理。</summary>
public sealed class AccountsViewModel : ObservableObject, IDisposable
{
    private PreviewAccount? _selected;
    private string _notice = "", _renameName = "", _renameError = "";
    private bool _renaming, _savingRename;
    private PreviewAccount? _renameTarget;
    private bool _remove, _compact;
    public PreviewSession Session { get; }
    public PreviewAccount? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                CancelRenameEditor();
                if (value is { IsOnline: false }) _ = Session.RefreshLocalSkinAsync(value);
            }
            ConfirmRemove = false; Changed(nameof(HasSelection));
        }
    }
    public bool HasSelection => Selected != null;
    public bool IsCompact { get => _compact; set => Set(ref _compact, value); }
    public bool ConfirmRemove { get => _remove; set => Set(ref _remove, value); }
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public bool IsRenaming { get => _renaming; private set => Set(ref _renaming,value); }
    public bool CanEditRename => !_savingRename;
    public string RenameName { get => _renameName; set {if(Set(ref _renameName,value)){RenameError="";System.Windows.Input.CommandManager.InvalidateRequerySuggested();}} }
    public string RenameError { get => _renameError; private set => Set(ref _renameError,value); }
    private bool CanManage => !_savingRename && !Session.IsEditingBlocked && !Session.IsLoading && !Session.IsAuthenticating;
    public UiCommand ShowAdd { get; }
    public UiCommand AddMicrosoft { get; }
    public UiCommand OpenLogin { get; }
    public UiCommand CancelLogin { get; }
    /// <summary>重新复制授权码（自动复制失败或需要再粘贴一次时用）。</summary>
    public UiCommand CopyLoginCode { get; }
    public UiCommand SetDefault { get; }
    /// <summary>离线账号的 UUID 常被服务器白名单或插件使用，提供一键复制。</summary>
    public UiCommand CopyUuid { get; }
    public UiCommand Relogin { get; }
    public UiCommand RefreshProfile { get; }
    public UiCommand RequestRemove { get; }
    public UiCommand Remove { get; }
    public UiCommand CancelRemove { get; }
    public UiCommand ShowRename { get; }
    public UiCommand SaveRename { get; }
    public UiCommand CancelRename { get; }

    /// <summary>主页和账号页打开同一份离线昵称草稿。</summary>
    public void BeginRename(PreviewAccount? account = null)
    {
        if(!CanManage)return;
        var target=account??Selected;
        if(target==null||target.IsMicrosoft||target.IsYggdrasil||!Session.Accounts.Contains(target))return;
        Selected=target;_renameTarget=target;RenameName=target.Name;RenameError="";
        ConfirmRemove=false;Notice="";IsRenaming=true;
    }
    private void CancelRenameEditor()
    { IsRenaming=false;_renameTarget=null;RenameError=""; }
    private async Task SaveRenameAsync()
    {
        if(_renameTarget is not { } target)return;
        var name=RenameName.Trim();RenameError=PreviewSession.ValidateOfflineName(name);
        if(RenameError.Length>0)return;
        _savingRename=true;Changed(nameof(CanEditRename));System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        try
        {
            var saved=await Session.RenameOfflineAccountAsync(target,name);
            await Session.RefreshLocalSkinAsync(target);
            if(ReferenceEquals(Selected,target))
            {
                CancelRenameEditor();
                Notice=saved?"已修改昵称。":"昵称已更改，但配置未完整保存，请检查程序目录的写入权限后重试。";
            }
        }
        catch(Exception error){if(ReferenceEquals(Selected,target))RenameError=Infrastructure.Runtime.RuntimeSupport.Error(error);}
        finally{_savingRename=false;Changed(nameof(CanEditRename));System.Windows.Input.CommandManager.InvalidateRequerySuggested();}
    }

    /// <summary>主页与账户页使用相同的添加和认证流程。</summary>
    public void BeginAdd(bool microsoft)
    {
        CancelRenameEditor();
        Selected = Session.CurrentAccount;
        Notice = "";
        if (microsoft) AddMicrosoft.Execute(null); else ChooseAccountType();
    }
    public AccountsViewModel(PreviewSession session)
    {
        Session = session; Selected = session.CurrentAccount;
        ShowAdd = new(_ => ChooseAccountType(), _ => CanManage);
        AddMicrosoft = new(async _ => { CancelRenameEditor(); Notice = ""; await session.LoginMicrosoftAsync(); Selected = session.CurrentAccount; }, _ => CanManage);
        ShowRename = new(_=>BeginRename(),_=>Selected is {IsMicrosoft:false,IsYggdrasil:false}&&CanManage);
        SaveRename = new(async _=>await SaveRenameAsync(),_=>IsRenaming&&CanManage&&_renameTarget!=null&&RenameName.Trim()!=_renameTarget.Name);
        CancelRename = new(_=>CancelRenameEditor(),_=>!_savingRename);
        SetDefault = new(_ => { if (Selected is { } account) { session.SetDefaultAccount(account); Notice = "已设为默认账号。"; } },
            _ => Selected is { IsDefault: false } && CanManage);
        CopyUuid = new(_ =>
        {
            if (Selected is not { } account || account.UuidText.Length == 0) return;
            try { Clipboard.SetText(account.UuidText); Notice = "UUID 已复制。"; }
            catch (ExternalException) { Notice = "无法写入剪贴板，请手动复制。"; }
            catch (InvalidOperationException) { Notice = "无法写入剪贴板，请手动复制。"; }
        }, _ => Selected is { IsMicrosoft: false } && Selected.Uuid != Guid.Empty);
        Relogin = new(async _ => { Notice = ""; await session.LoginMicrosoftAsync(Selected); }, _ => Selected is { IsMicrosoft: true, IsRestoring: false } && CanManage);
        RefreshProfile = new(async _ =>
        {
            if (Selected is not { } account) return;
            Notice = "";
            var refreshed = await session.RefreshProfileAsync(account);
            Notice = refreshed ? "资料已刷新。" : "资料刷新未完成，请重新登录后重试。";
        }, _ => Selected is { IsOnline: true, IsSkinLoading: false, IsRestoring: false } && CanManage);
        OpenLogin = new(_ =>
        {
            try { Process.Start(new ProcessStartInfo(session.LoginUri!.AbsoluteUri) { UseShellExecute = true }); }
            catch (Win32Exception) { Notice = "无法打开浏览器，请手动打开授权网址。"; }
        }, _ => session.LoginUri is { Scheme: "https" });
        CancelLogin = new(_ => session.CancelMicrosoftLogin(), _ => session.IsAuthenticating);
        CopyLoginCode = new(_ =>
        {
            if (session.LoginCode.Length == 0) return;
            try { Clipboard.SetText(session.LoginCode); Notice = "授权代码已复制。"; }
            catch (ExternalException) { Notice = "无法写入剪贴板，请手动选择代码复制。"; }
            catch (InvalidOperationException) { Notice = "无法写入剪贴板，请手动选择代码复制。"; }
        }, _ => session.LoginCode.Length > 0);
        RequestRemove = new(_ => {CancelRenameEditor();ConfirmRemove = true;}, _ => Selected != null && CanManage);
        CancelRemove = new(_ => ConfirmRemove = false);
        Remove = new(_ =>
        {
            if (Selected is not { } account) return;
            session.Accounts.Remove(account);
            Selected = session.CurrentAccount ?? session.Accounts.FirstOrDefault(); ConfirmRemove = false;
            Notice = "已移除本机账户记录及授权缓存，游戏文件不受影响。";
        }, _ => Selected != null && CanManage);
        session.PropertyChanged += SessionChanged;
        _ = session.ValidateAccountsAsync();
    }
    /// <summary>先问账号类型与输入，再在后台执行；所有输入都在对话框内完成。</summary>
    private async void ChooseAccountType()
    {
        CancelRenameEditor();
        Notice = "";
        if (Presentation.Dialogs.AccountTypeDialog.Ask() is not { } request) return;
        switch (request.Kind)
        {
            case Presentation.Dialogs.AccountTypeDialog.Choice.Microsoft:
                if (AddMicrosoft.CanExecute(null)) AddMicrosoft.Execute(null);
                break;
            case Presentation.Dialogs.AccountTypeDialog.Choice.Offline:
                AddOfflineAccount(request.Name);
                break;
            default:
                await LoginYggdrasilAsync(request.Server, request.Login, request.Password);
                break;
        }
    }

    private void AddOfflineAccount(string rawName)
    {
        var name = rawName.Trim();
        var error = PreviewSession.ValidateOfflineName(name);
        if (error.Length > 0) { Notice = error; return; }
        if (Session.Accounts.Any(account => account is { IsMicrosoft: false, IsYggdrasil: false }
            && account.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Notice = "这个离线账号已存在。";
            return;
        }

        Selected = Session.AddOfflineAccount(name);
        Notice = "已添加账号。";
    }

    private async Task LoginYggdrasilAsync(string server, string login, string password)
    {
        Notice = "正在登录外置账号…";
        try
        {
            CancelRenameEditor();
            var account = await Session.LoginYggdrasilAsync(server, login, password);
            Selected = account;
            Notice = "外置账号已登录。";
        }
        catch (OperationCanceledException) { Notice = "已取消登录。"; }
        catch (Exception error) { Notice = Infrastructure.Runtime.RuntimeSupport.Error(error); }
        finally { System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
    }
    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Selected == null || !Session.Accounts.Contains(Selected)) Selected = Session.CurrentAccount ?? Session.Accounts.FirstOrDefault();
        Refresh();
    }
    public void Dispose() => Session.PropertyChanged -= SessionChanged;
}
