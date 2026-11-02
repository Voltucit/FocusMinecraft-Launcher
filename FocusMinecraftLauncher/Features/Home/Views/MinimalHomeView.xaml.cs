using System.Windows;
using System.Windows.Controls;
namespace FocusMinecraftLauncher.Features.Home.Views;
public partial class MinimalHomeView : UserControl
{
    public MinimalHomeView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) MinimalActions.IsOpen = false; };
    }
    /// <summary>头像即入口：动作行从头像上方撑开，再点一次收起。</summary>
    private void OpenAccountActions(object sender, RoutedEventArgs e) => MinimalActions.Toggle();
}
