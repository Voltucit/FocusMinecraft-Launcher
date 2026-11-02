using System.Windows;
using System.Windows.Controls;
namespace FocusMinecraftLauncher.Features.Home.Views;
public partial class CenteredHomeView : UserControl
{
    public CenteredHomeView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) CenteredActions.IsOpen = false; };
    }
    /// <summary>头像即入口：动作行在头像下方展开，整列重新居中，头像被顶上去。</summary>
    private void OpenAccountActions(object sender, RoutedEventArgs e) => CenteredActions.Toggle();
}
