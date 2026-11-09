using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FocusMinecraftLauncher.Features.Settings.ViewModels;

namespace FocusMinecraftLauncher.Features.Settings.Views;

/// <summary>设置内的“其他”子页；交互状态由持久化设置 ViewModel 提供。</summary>
public partial class OtherSettingsPage : UserControl
{
    public OtherSettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is not OtherSettingsViewModel viewModel) return;
            PasswordBox? password = FindPasswordBox(this);
            if (password != null && password.Password != viewModel.SharedNodePassword)
                password.Password = viewModel.SharedNodePassword;
        };
    }

    private void SharedNodePassword_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && DataContext is OtherSettingsViewModel viewModel)
            viewModel.SharedNodePassword = box.Password;
    }

    private static PasswordBox? FindPasswordBox(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is PasswordBox box && Equals(box.Tag, "SharedNodePassword")) return box;
            if (FindPasswordBox(child) is { } nested) return nested;
        }
        return null;
    }
}
