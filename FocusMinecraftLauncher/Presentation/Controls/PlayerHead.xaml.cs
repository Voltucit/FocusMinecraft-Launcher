using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace FocusMinecraftLauncher.Presentation.Controls;

/// <summary>以最近邻插值显示像素头像；没有资源或资源失效时显示同一默认头像。</summary>
public partial class PlayerHead : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(PlayerHead), new PropertyMetadata(null, SourceChanged));
    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public PlayerHead() => InitializeComponent();
    private static void SourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    { if (d is PlayerHead head && head.HeadImage != null) head.HeadImage.Visibility = Visibility.Visible; }
    private void HeadFailed(object sender, ExceptionRoutedEventArgs e)
    { HeadImage.Visibility = Visibility.Collapsed; e.Handled = true; }
}
