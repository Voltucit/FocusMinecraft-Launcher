using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FocusMinecraftLauncher.Common;
using FocusMinecraftLauncher.Features.Multiplayer.Models;
using FocusMinecraftLauncher.Features.Multiplayer.Services;
using FocusMinecraftLauncher.Navigation;
using FocusMinecraftLauncher.Preview;
using FocusMinecraftLauncher.Presentation.Dialogs;

namespace FocusMinecraftLauncher.Features.Multiplayer.ViewModels;

/// <summary>把应用级真实联机会话绑定到已有页面，不读取游戏列表或参与客户端、游戏端口校验。</summary>
public sealed class MultiplayerViewModel : ObservableObject, IDisposable
{
    private readonly MultiplayerService _network = ((App)Application.Current).Multiplayer;
    private string _roomName, _invitation = "";
    private string _playerLimit;
    private bool _isRoomPage, _roomNameEdited, _requestHosting;
    private int _selectedTab;
    private PreviewAccount? _observedAccount;
    private PreviewAccount? _roomAccount;
    private readonly Dictionary<string, (string Pixels, ImageSource? Image)> _memberAvatars = new(StringComparer.Ordinal);
    private string _clipboardNotice = "";
    public PreviewSession Session { get; }
    public ObservableCollection<MultiplayerMember> Members { get; } = [];
    public string PlayerName => Session.CurrentAccount?.Name ?? "本机玩家";
    public string RoomName { get => _roomName; set { if (Set(ref _roomName, value)) { _roomNameEdited = true; Publish(); } } }
    public string PlayerLimit { get => _playerLimit; set { if (Set(ref _playerLimit, value)) Publish(); } }
    private int ParsedPlayerLimit => int.TryParse(PlayerLimit.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : 0;
    public string PlayerLimitError => ParsedPlayerLimit is >= Models.RoomInvitation.MinimumPlayers and <= Models.RoomInvitation.MaximumPlayers
        ? "" : $"请输入 {Models.RoomInvitation.MinimumPlayers}–{Models.RoomInvitation.MaximumPlayers} 之间的整数。";
    public string Invitation { get => _invitation; set { if (Set(ref _invitation, value)) Publish(); } }
    public int SelectedTab { get => _selectedTab; set { if (Set(ref _selectedTab, value)) { _clipboardNotice = ""; Publish(); } } }
    public string RoomNameError => string.IsNullOrWhiteSpace(RoomName) ? "请输入房间名称。" : RoomName.Trim().Length > 48 || RoomName.Any(char.IsControl) ? "房间名称最多 48 个字符，不能包含换行。" : "";
    public string InvitationError
    {
        get { if (string.IsNullOrWhiteSpace(Invitation)) return ""; try { _network.ReadInvitation(Invitation); return ""; } catch (FormatException e) { return e.Message; } }
    }
    public bool CanEditRoom => _network.CanStart;
    public bool CanCreate => CanEditRoom && RoomNameError.Length == 0 && PlayerLimitError.Length == 0;
    public bool CanJoin => CanEditRoom && !string.IsNullOrWhiteSpace(Invitation) && InvitationError.Length == 0;
    public bool HasRoom => _network.HasSession;
    public bool IsHosting => _network.IsHosting;
    public bool IsRoomPage { get => _isRoomPage; private set => Set(ref _isRoomPage, value); }
    public string PageTitle => IsRoomPage && RoomTitle.Length > 0 ? RoomTitle : "联机";
    public string EndRoomLabel => IsHosting ? "解散房间" : "退出房间";
    public string RoomTitle => _network.RoomTitle;
    public string RoomInvitation => _network.Snapshot.Invitation;
    public string MemberHeading => _network.Snapshot.MaxPlayers > 0 ? $"成员（{Members.Count} / {_network.Snapshot.MaxPlayers}）" : $"成员（{Members.Count}）";
    public string Notice => _clipboardNotice.Length > 0 ? _clipboardNotice : _network.Notice;
    public bool IsConnecting => !_network.IsStopping && (_network.IsBusy || _network.HasSession && !_network.Snapshot.Ready);
    public bool IsConnected => _network.HasSession && _network.Snapshot.Ready;
    public bool IsFailed => !_network.HasActivity && _network.Snapshot.Error.Length > 0;
    public string ConnectionStatus => _network.IsStopping ? "正在退出联机" : _network.IsBusy && !_network.HasSession ? _network.ComponentStatus : _network.Snapshot.Status;
    public string ConnectionError => _network.Snapshot.Error;
    public string ConnectionKind
    {
        get
        {
            var remote = _network.Snapshot.Members.Where(x => !x.IsLocal).ToArray();
            if (remote.Length == 0) return "等待成员连接";
            return remote.Select(x => x.Connection).Distinct().Count() == 1 ? remote[0].Connection : "直连与中继";
        }
    }
    public string ConnectionLatency
    {
        get
        {
            var remote = _network.Snapshot.Members.Where(member => !member.IsLocal).ToArray();
            // 成员端继续显示到房主的延迟，双人房间保留原始测量值。
            if (remote.FirstOrDefault(member => member.IsHost) is { } host) return host.Latency;
            if (remote.Length == 1) return remote[0].Latency;

            // 房主可能有多名远端成员；用有效测量值的范围汇总，不因人数增加而丢弃数据。
            var measured = remote.Select(member => member.Latency)
                .Where(text => text.EndsWith(" ms", StringComparison.Ordinal))
                .Select(text => (Text: text, Milliseconds: double.TryParse(text.AsSpan(0, text.Length - 3),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN))
                .Where(value => double.IsFinite(value.Milliseconds) && value.Milliseconds > 0)
                .OrderBy(value => value.Milliseconds).ToArray();
            if (measured.Length == 0) return "未测量";
            var minimum = measured[0];
            var maximum = measured[^1];
            return minimum.Milliseconds == maximum.Milliseconds ? minimum.Text
                : minimum.Text[..^3] + "–" + maximum.Text;
        }
    }
    public UiCommand CreateRoom { get; }
    public UiCommand JoinRoom { get; }
    public UiCommand PasteInvitation { get; }
    public UiCommand CopyInvitation { get; }
    public UiCommand CopyMemberIp { get; }
    public UiCommand Retry { get; }
    public UiCommand CancelConnection { get; }
    public UiCommand ReturnToLobby { get; }
    public UiCommand ReturnToRoom { get; }
    public UiCommand EndRoom { get; }
    public UiCommand OpenAccounts { get; }

    public MultiplayerViewModel(PreviewSession session)
    {
        Session = session; _roomName = PlayerName + "的房间";
        _playerLimit = _network.PreferredMaxPlayers.ToString(CultureInfo.InvariantCulture);
        CreateRoom = new(async _ => await StartAsync(true), _ => CanCreate);
        JoinRoom = new(async _ => await StartAsync(false), _ => CanJoin);
        PasteInvitation = new(_ => Paste(), _ => CanEditRoom);
        CopyInvitation = new(_ => Copy(RoomInvitation, "已复制邀请信息，请仅发送给受邀成员。"), _ => HasRoom && IsHosting && RoomInvitation.Length > 0);
        CopyMemberIp = new(parameter => { if (parameter is MultiplayerMember member) Copy(member.IpAddress, "已复制“" + member.Name + "”的 IP。"); }, parameter => HasRoom && parameter is MultiplayerMember { HasIpAddress: true });
        Retry = new(async _ => await StartAsync(_requestHosting), _ => IsFailed && CanEditRoom);
        CancelConnection = new(async _ => await StopAsync(false), _ => IsConnecting);
        ReturnToLobby = new(_ => { IsRoomPage = false; Publish(); }, _ => IsRoomPage);
        ReturnToRoom = new(_ => { IsRoomPage = true; Publish(); }, _ => HasRoom && !IsRoomPage);
        EndRoom = new(async _ => await StopAsync(true), _ => _network.HasActivity && !_network.IsStopping);
        OpenAccounts = new(_ => Session.Navigate(MainPage.Accounts));
        _network.PropertyChanged += NetworkChanged;
        Session.PropertyChanged += SessionChanged;
        ObserveAccount(); RebuildMembers();
    }

    private async Task StartAsync(bool hosting)
    {
        _requestHosting = hosting; _clipboardNotice = "";
        ObserveRoomAccount(Session.CurrentAccount);
        var task = _network.StartAsync(PlayerName, RoomName, Invitation, hosting, MemberAvatarCodec.Encode(_roomAccount?.HeadImage), ParsedPlayerLimit);
        IsRoomPage = true; Publish();
        if (!await task) { IsRoomPage = false; ObserveRoomAccount(null); _memberAvatars.Clear(); }
        Publish();
    }

    private async Task StopAsync(bool confirm)
    {
        if (confirm)
        {
            var effect = IsHosting ? "房间将解散，成员会退出这次联机。" : "退出不会解散房间，其他成员不受影响。";
            if (!MessageDialog.Confirm(EndRoomLabel + "？", "“" + RoomTitle + "”\n\n" + effect, EndRoomLabel, "取消")) return;
        }
        try { await _network.StopAsync(); IsRoomPage = false; }
        catch (Exception error) { _clipboardNotice = EasyTierWorker.Describe(error); }
        Publish();
    }

    private void NetworkChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_network.Snapshot.Ended && !_network.HasActivity) IsRoomPage = false;
        if (!_network.HasActivity) { ObserveRoomAccount(null); _memberAvatars.Clear(); }
        RebuildMembers(); Publish();
    }

    private void RebuildMembers()
    {
        foreach (var id in _memberAvatars.Keys.Where(id => !_network.Snapshot.Members.Any(member => member.Id == id)).ToArray())
            _memberAvatars.Remove(id);
        var rows = _network.Snapshot.Members.Select(member => new MultiplayerMember(member.Id, member.Name,
            member.IsHost ? member.IsLocal ? "房主（你）" : "房主" : member.IsLocal ? "你" : "成员",
            AvatarFor(member), member.IpAddress,
            member.IsLocal ? "本机" : member.Connection + " / " + member.Latency)).ToArray();
        if (Members.SequenceEqual(rows)) return;
        Members.Clear(); foreach (var member in rows) Members.Add(member);
        Changed(nameof(MemberHeading));
    }

    /// <summary>头像像素不变时复用冻结位图，避免每次房间心跳都重新解码和重建成员列表。</summary>
    private ImageSource? AvatarFor(NetworkMember member)
    {
        if (member.IsLocal && _roomAccount?.HeadImage != null) return _roomAccount.HeadImage;
        if (!_memberAvatars.TryGetValue(member.Id, out var cached) || cached.Pixels != member.AvatarPixels)
        {
            cached = (member.AvatarPixels, MemberAvatarCodec.Decode(member.AvatarPixels));
            _memberAvatars[member.Id] = cached;
        }
        return cached.Image;
    }

    private void ObserveRoomAccount(PreviewAccount? account)
    {
        if (ReferenceEquals(_roomAccount, account)) return;
        if (_roomAccount != null) _roomAccount.PropertyChanged -= RoomAccountChanged;
        _roomAccount = account;
        if (_roomAccount != null) _roomAccount.PropertyChanged += RoomAccountChanged;
    }

    private void RoomAccountChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or nameof(PreviewAccount.HeadImage))) return;
        // 继续同步加入时所选账号；在账号页切换身份不会把另一账号的头像套到当前成员上。
        _network.UpdateLocalAvatar(MemberAvatarCodec.Encode(_roomAccount?.HeadImage));
        RebuildMembers();
    }
    private void Copy(string value, string message)
    {
        try { Clipboard.SetText(value); _clipboardNotice = message; }
        catch (ExternalException) { _clipboardNotice = "剪贴板正被占用，请稍后重试。"; }
        Publish();
    }
    private void Paste()
    {
        try
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
            if (text.Length > Models.RoomInvitation.MaximumLength) { _clipboardNotice = "邀请信息过长，请检查粘贴内容。"; Publish(); return; }
            Invitation = text; _clipboardNotice = ""; Publish();
        }
        catch (ExternalException) { _clipboardNotice = "暂时无法读取剪贴板，请手动粘贴。"; Publish(); }
    }
    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is null or nameof(PreviewSession.CurrentAccount)) { ObserveAccount(); UpdateDefaultRoomName(); Changed(nameof(PlayerName)); } }
    private void ObserveAccount()
    {
        if (ReferenceEquals(_observedAccount, Session.CurrentAccount)) return;
        if (_observedAccount != null) _observedAccount.PropertyChanged -= AccountChanged;
        _observedAccount = Session.CurrentAccount;
        if (_observedAccount != null) _observedAccount.PropertyChanged += AccountChanged;
        RebuildMembers();
    }
    private void AccountChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is null or nameof(PreviewAccount.Name) or nameof(PreviewAccount.HeadImage)) { RebuildMembers(); UpdateDefaultRoomName(); Changed(nameof(PlayerName)); } }
    private void UpdateDefaultRoomName()
    { if (!_roomNameEdited && Set(ref _roomName, PlayerName + "的房间", nameof(RoomName))) Publish(); }
    private void Publish() { Refresh(); CommandManager.InvalidateRequerySuggested(); }
    public void Dispose()
    {
        _network.PropertyChanged -= NetworkChanged; Session.PropertyChanged -= SessionChanged;
        if (_observedAccount != null) _observedAccount.PropertyChanged -= AccountChanged;
        ObserveRoomAccount(null);
        _memberAvatars.Clear();
    }
}
