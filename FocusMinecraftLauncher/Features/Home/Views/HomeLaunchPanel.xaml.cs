using System.Windows;
using System.Windows.Controls;

namespace FocusMinecraftLauncher.Features.Home.Views;

/// <summary>三种主页共用的启动按钮与状态展示，不创建任务或持有独立业务状态。</summary>
public partial class HomeLaunchPanel : UserControl
{
    public static readonly DependencyProperty FeedbackMaxHeightProperty = DependencyProperty.Register(
        nameof(FeedbackMaxHeight), typeof(double), typeof(HomeLaunchPanel), new PropertyMetadata(120d));
    public double FeedbackMaxHeight { get => (double)GetValue(FeedbackMaxHeightProperty); set => SetValue(FeedbackMaxHeightProperty, value); }
    public HomeLaunchPanel() => InitializeComponent();
}
