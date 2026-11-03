using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FMCore.Accounts;

namespace FocusMinecraftLauncher.Presentation.Dialogs;

/// <summary>外置账号在验证服务里有多个角色时，让用户选一个再继续登录。</summary>
public partial class YggdrasilProfileDialog : Window
{
    private readonly List<RadioButton> _options = [];

    private YggdrasilProfileDialog(IReadOnlyList<YggdrasilProfile> profiles)
    {
        InitializeComponent();
        foreach (var (profile, index) in profiles.Select((profile, index) => (profile, index)))
        {
            var button = new RadioButton
            {
                GroupName = "Profile",
                Style = (Style)FindResource("ProfileOption"),
                IsChecked = index == 0,
                Content = new TextBlock
                {
                    Text = profile.Name,
                    FontWeight = index == 0 ? FontWeights.SemiBold : FontWeights.Normal
                },
                Tag = profile.Uuid
            };
            _options.Add(button);
            Options.Children.Add(button);
        }
        Loaded += (_, _) => { if (_options.Count > 0) _options[0].Focus(); };
    }

    /// <summary>返回用户选中的角色；取消或直接关闭窗口时返回 null。</summary>
    public static System.Guid? Ask(Window? owner, IReadOnlyList<YggdrasilProfile> profiles)
    {
        if (profiles.Count == 1) return profiles[0].Uuid;
        var dialog = new YggdrasilProfileDialog(profiles);
        if (owner is { IsLoaded: true } target) dialog.Owner = target;
        return dialog.ShowDialog() == true ? dialog.Selected : null;
    }

    private System.Guid? Selected { get; set; }

    private void Accept(object sender, RoutedEventArgs e)
    {
        Selected = _options.FirstOrDefault(option => option.IsChecked == true)?.Tag as System.Guid?;
        if (Selected is null) return;
        DialogResult = true;
    }

    private void Decline(object sender, RoutedEventArgs e) => DialogResult = false;

    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
