using System.Windows.Controls;
using System.Windows.Input;
using FocusMinecraftLauncher.Features.Home.ViewModels;
namespace FocusMinecraftLauncher.Features.Home.Views;

/// <summary>同一主页上下文中的实例选择；鼠标与键盘都调用相同选择命令。</summary>
public partial class InstanceSelectionPage : UserControl
{
    public InstanceSelectionPage() => InitializeComponent();
    private void ChooseWithKeyboard(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is ListBox list && DataContext is InstanceSelectionViewModel vm
            && vm.ChooseInstance.CanExecute(list.SelectedItem))
        { vm.ChooseInstance.Execute(list.SelectedItem); e.Handled = true; }
    }
}
