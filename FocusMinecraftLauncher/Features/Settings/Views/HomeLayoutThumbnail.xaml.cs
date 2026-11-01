using System.Windows;
using System.Windows.Controls;
using FocusMinecraftLauncher.Features.Home.Models;

namespace FocusMinecraftLauncher.Features.Settings.Views;

/// <summary>完全离线的布局结构缩略图，不创建额外的主页会话。</summary>
public partial class HomeLayoutThumbnail : UserControl
{
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(nameof(Layout), typeof(HomeLayout), typeof(HomeLayoutThumbnail), new PropertyMetadata(HomeLayout.Classic));
    public HomeLayout Layout { get => (HomeLayout)GetValue(LayoutProperty); set => SetValue(LayoutProperty, value); }
    public HomeLayoutThumbnail() => InitializeComponent();
}
