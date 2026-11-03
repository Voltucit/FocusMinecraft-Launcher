using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FocusMinecraftLauncher.Presentation.Dialogs;

/// <summary>添加账号时的类型与输入，由调用方决定如何真正登录。</summary>
public sealed record AccountRequest(AccountTypeDialog.Choice Kind, string Name, string Server, string Login, string Password);

/// <summary>添加账号的小向导：类型与表单都在同一个框内完成。</summary>
public partial class AccountTypeDialog : Window
{
    public enum Choice { Microsoft, Offline, Yggdrasil }

    private static readonly string[] Presets = ["LittleSkin", "自定义"];

    private Choice _choice = Choice.Microsoft;

    public AccountTypeDialog()
    {
        InitializeComponent();
        ServerPreset.ItemsSource = Presets;
        ServerPreset.SelectionChanged += (_, _) => ApplyPreset();
        ServerPreset.SelectedIndex = 0;
        ApplyPreset();
    }

    /// <summary>显示向导；取消或关闭时返回 null。</summary>
    public static AccountRequest? Ask()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
            return Application.Current.Dispatcher.Invoke(Ask);

        if (Application.Current.MainWindow is not { IsLoaded: true } owner) return null;

        var dialog = new AccountTypeDialog { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Request : null;
    }

    public AccountRequest? Request { get; private set; }

    private void PickType(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border card || card.Tag is not string tag) return;
        // 外框的拖拽会抢走鼠标抬起事件，这里在按下时直接选中并截断冒泡。
        e.Handled = true;
        Select(tag switch
        {
            "Offline" => Choice.Offline,
            "Yggdrasil" => Choice.Yggdrasil,
            _ => Choice.Microsoft
        });
    }

    /// <summary>键盘操作：卡片获得焦点后按 Enter 或空格选中。</summary>
    private void PickTypeKey(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        if (sender is not Border card || card.Tag is not string tag) return;
        e.Handled = true;
        Select(tag switch
        {
            "Offline" => Choice.Offline,
            "Yggdrasil" => Choice.Yggdrasil,
            _ => Choice.Microsoft
        });
    }

    /// <summary>切到某个类型的表单；返回后仍可再次选择。</summary>
    public void Select(Choice choice)
    {
        _choice = choice;
        MicrosoftCard.Style = (Style)FindResource(_choice == Choice.Microsoft ? "TypeCardSelected" : "TypeCard");
        OfflineCard.Style = (Style)FindResource(_choice == Choice.Offline ? "TypeCardSelected" : "TypeCard");
        YggdrasilCard.Style = (Style)FindResource(_choice == Choice.Yggdrasil ? "TypeCardSelected" : "TypeCard");

        TypeStep.Visibility = Visibility.Collapsed;
        FormStep.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;

        OfflineForm.Visibility = _choice == Choice.Offline ? Visibility.Visible : Visibility.Collapsed;
        YggdrasilForm.Visibility = _choice == Choice.Yggdrasil ? Visibility.Visible : Visibility.Collapsed;
        MicrosoftForm.Visibility = _choice == Choice.Microsoft ? Visibility.Visible : Visibility.Collapsed;

        Subtitle.Text = _choice switch
        {
            Choice.Offline => "填写玩家名称。",
            Choice.Yggdrasil => "填写验证服务与账号。",
            _ => "确认在浏览器中完成登录。"
        };
        ContinueButton.Content = _choice switch
        {
            Choice.Offline => "添加",
            Choice.Yggdrasil => "登录",
            _ => "继续"
        };

        switch (_choice)
        {
            case Choice.Offline: NameBox.Focus(); break;
            case Choice.Yggdrasil: AccountBox.Focus(); break;
            default: ContinueButton.Focus(); break;
        }
    }

    private void GoBack(object sender, RoutedEventArgs e)
    {
        FormStep.Visibility = Visibility.Collapsed;
        TypeStep.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
        Subtitle.Text = "选择账号类型。";
        ContinueButton.Content = "继续";
        MicrosoftCard.Style = (Style)FindResource("TypeCard");
        OfflineCard.Style = (Style)FindResource("TypeCard");
        YggdrasilCard.Style = (Style)FindResource("TypeCard");
    }

    private void Continue(object sender, RoutedEventArgs e)
    {
        switch (_choice)
        {
            case Choice.Offline:
                {
                    var name = NameBox.Text.Trim();
                    var nameError = Preview.PreviewSession.ValidateOfflineName(name);
                    if (nameError.Length > 0) { Fail(nameError); return; }
                    Request = new AccountRequest(Choice.Offline, name, "", "", "");
                    DialogResult = true;
                    return;
                }
            case Choice.Yggdrasil:
                {
                    if (!Infrastructure.Authentication.YggdrasilSessionService.TryNormalize(ServerBox.Text, out var address, out var addressError))
                    {
                        Fail(addressError);
                        return;
                    }
                    var login = AccountBox.Text.Trim();
                    if (login.Length == 0) { Fail("请填写账号。"); return; }
                    if (PasswordInput.Password.Length == 0) { Fail("请填写密码。"); return; }
                    Request = new AccountRequest(Choice.Yggdrasil, "", address, login, PasswordInput.Password);
                    DialogResult = true;
                    return;
                }
            default:
                Request = new AccountRequest(Choice.Microsoft, "", "", "", "");
                DialogResult = true;
                return;
        }
    }

    private void ApplyPreset()
    {
        var custom = ServerPreset.SelectedItem as string == "自定义";
        ServerBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        if (custom)
        {
            ServerBox.Focus();
            return;
        }

        ServerBox.Text = Infrastructure.Authentication.YggdrasilSessionService.LittleSkin;
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Decline(object sender, RoutedEventArgs e) => DialogResult = false;

    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (!SystemParameters.ClientAreaAnimation || Content is not FrameworkElement content) return;

        content.RenderTransformOrigin = new Point(0.5, 0.45);
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var scale = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var transform = new ScaleTransform(0.96, 0.96);
        content.RenderTransform = transform;
        BeginAnimation(OpacityProperty, fade);
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }
}
