using System.ComponentModel;
using System.Diagnostics;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Preview;

namespace FocusMinecraftLauncher.Features.Accounts.ViewModels;

/// <summary>管理共享账户列表及认证入口；资料选择与正版认证分别处理。</summary>
public sealed class AccountsViewModel : ObservableObject, IDisposable
{
    private PreviewAccount? _selected;
    private string _name = "", _error = "", _notice = "", _renameName = "", _renameError = "";
    private bool _renaming, _savingRename;
    private PreviewAccount? _renameTarget;
    private bool _add, _remove, _compact;
    public PreviewSession Session { get; }
    public PreviewAccount? Selected { get => _selected; set { if(Set(ref _selected, value)) CancelRenameEditor(); ConfirmRemove = false; Changed(nameof(HasSelection)); } }
    public bool HasSelection => Selected != null;
    public bool IsCompact { get => _compact; set => Set(ref _compact, value); }
    public bool IsAdding { get => _add; set => Set(ref _add, value); }
    public bool ConfirmRemove { get => _remove; set => Set(ref _remove, value); }
    public string Name { get => _name; set { Set(ref _name, value); NameError = ""; } }
    public string NameError { get => _error; private set => Set(ref _error, value); }
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public bool IsRenaming { get => _renaming; private set => Set(ref _renaming,value); }
    public bool CanEditRename => !_savingRename;
    public string RenameName { get => _renameName; set {if(Set(ref _renameName,value)){RenameError="";System.Windows.Input.CommandManager.InvalidateRequerySuggested();}} }
    public string RenameError { get => _renameError; private set => Set(ref _renameError,value); }
    private bool CanManage => !_savingRename && !Session.IsBusy && !Session.IsLoading && !Session.IsAuthenticating;
    public UiCommand ShowAdd { get; }
    public UiCommand CancelAdd { get; }
    public UiCommand AddOffline { get; }
    public UiCommand AddMicrosoft { get; }
    public UiCommand OpenLogin { get; }
    public UiCommand CancelLogin { get; }
    public UiCommand Use { get; }
    public UiCommand Relogin { get; }
    public UiCommand RefreshSkin { get; }
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
        if(target==null||target.IsMicrosoft||!Session.Accounts.Contains(target))return;
        Selected=target;_renameTarget=target;RenameName=target.Name;RenameError="";
        IsAdding=false;ConfirmRemove=false;Notice="";IsRenaming=true;
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
        IsAdding = !microsoft;
        Notice = ""; NameError = "";
        if (microsoft) AddMicrosoft.Execute(null);
    }
    public AccountsViewModel(PreviewSession session)
    {
        Session = session; Selected = session.CurrentAccount;
        ShowAdd = new(_ => {CancelRenameEditor();IsAdding = true;}, _ => CanManage);
        CancelAdd = new(_ => { IsAdding = false; NameError = ""; });
        AddOffline = new(_ =>
        {
            var name = Name.Trim();
            NameError=PreviewSession.ValidateOfflineName(name);
            if(NameError.Length>0)return;
            if (session.Accounts.Any(a => !a.IsMicrosoft && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { NameError = "这个离线账号已存在。"; return; }
            Selected = session.AddOfflineAccount(name);
            Name = ""; IsAdding = false; Notice = "已添加账号。";
        }, _ => CanManage);
        AddMicrosoft = new(async _ => { CancelRenameEditor();IsAdding = false; Notice = ""; await session.LoginMicrosoftAsync(); Selected = session.CurrentAccount; }, _ => CanManage);
        ShowRename = new(_=>BeginRename(),_=>Selected is {IsMicrosoft:false}&&CanManage);
        SaveRename = new(async _=>await SaveRenameAsync(),_=>IsRenaming&&CanManage&&_renameTarget!=null&&RenameName.Trim()!=_renameTarget.Name);
        CancelRename = new(_=>CancelRenameEditor(),_=>!_savingRename);
        Use = new(_ => { session.CurrentAccount = Selected; Notice = "已切换账号。"; }, _ => Selected is { IsCurrent: false, IsRestoring: false } && CanManage);
        Relogin = new(async _ => { Notice = ""; await session.LoginMicrosoftAsync(Selected); }, _ => Selected is { IsMicrosoft: true, IsRestoring: false } && CanManage);
        RefreshSkin = new(async _ => { if (Selected is { } account) await session.RefreshSkinAsync(account); },
            _ => Selected is { IsMicrosoft: true, IsValid: true, IsSkinLoading: false } && CanManage);
        OpenLogin = new(_ =>
        {
            try { Process.Start(new ProcessStartInfo(session.LoginUri!.AbsoluteUri) { UseShellExecute = true }); }
            catch (Win32Exception) { Notice = "无法打开浏览器，请手动打开授权网址。"; }
        }, _ => session.LoginUri is { Scheme: "https" });
        CancelLogin = new(_ => session.CancelMicrosoftLogin(), _ => session.IsAuthenticating);
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
    }
    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Selected == null || !Session.Accounts.Contains(Selected)) Selected = Session.CurrentAccount ?? Session.Accounts.FirstOrDefault();
        Refresh();
    }
    public void Dispose() => Session.PropertyChanged -= SessionChanged;
}
