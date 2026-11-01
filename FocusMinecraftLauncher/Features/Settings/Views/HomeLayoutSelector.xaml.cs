using System.Windows;
using System.Windows.Controls;

namespace FocusMinecraftLauncher.Features.Settings.Views;

public partial class HomeLayoutSelector : UserControl
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(nameof(Columns), typeof(int), typeof(HomeLayoutSelector), new PropertyMetadata(3));
    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public HomeLayoutSelector() => InitializeComponent();
    private void ResizeOptions(object sender, SizeChangedEventArgs e) => Columns = ActualWidth >= 760 ? 3 : ActualWidth >= 500 ? 2 : 1;
}
