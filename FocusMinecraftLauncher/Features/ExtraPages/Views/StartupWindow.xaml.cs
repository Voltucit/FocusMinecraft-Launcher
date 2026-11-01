using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FocusMinecraftLauncher.Features.ExtraPages.Views;

/// <summary>仅展示启动前的淡入下滑和淡出，不模拟业务进度或额外等待。</summary>
public partial class StartupWindow : Window
{
    internal event EventHandler? Completed;

    public StartupWindow()
    {
        InitializeComponent();

        if (SystemParameters.HighContrast)
        {
            StartupSurface.Background = SystemColors.WindowBrush;
            ProductName.Foreground = SystemColors.WindowTextBrush;
        }

        ContentRendered += FirstFrameRendered;
    }

    /// <summary>在 250ms 内同步淡入并下滑 12 DIP，以减速曲线自然落位。</summary>
    private void FirstFrameRendered(object? sender, EventArgs e)
    {
        ContentRendered -= FirstFrameRendered;
        var entrance = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var slide = new DoubleAnimation(-12, 0, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        EntranceOffset.BeginAnimation(TranslateTransform.YProperty, slide);
        entrance.Completed += (_, _) => FadeOut();
        BeginAnimation(OpacityProperty, entrance);
    }

    /// <summary>淡入下滑结束后直接淡出，完成时通知应用显示主窗口。</summary>
    private void FadeOut()
    {
        if (!IsVisible) return;

        var exit = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        exit.Completed += (_, _) =>
        {
            if (!IsVisible) return;
            Completed?.Invoke(this, EventArgs.Empty);
            Close();
        };
        BeginAnimation(OpacityProperty, exit);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        base.OnKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        ContentRendered -= FirstFrameRendered;
        BeginAnimation(OpacityProperty, null);
        EntranceOffset.BeginAnimation(TranslateTransform.YProperty, null);
        base.OnClosed(e);
    }
}
