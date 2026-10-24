using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
namespace FMLauncher.Appearance.Animations;
/// <summary>仅动画化透明度和位移，尊重主题与系统动画开关。</summary>
public static class PageAnimation
{
    public static async Task HideAsync(UIElement element, bool enabled, CancellationToken ct)
    {
        var current = element.Opacity;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (!enabled || !SystemParameters.ClientAreaAnimation) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(current, 0, TimeSpan.FromMilliseconds(80)));
        await Task.Delay(80, ct);
    }
    public static void Show(UIElement element, bool enabled, bool back = false)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = 1;
        var translation = new TranslateTransform(); element.RenderTransform = translation;
        if (!enabled || !SystemParameters.ClientAreaAnimation) return;
        var duration = TimeSpan.FromMilliseconds(210);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration));
        translation.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(back ? -28 : 36, 0, duration)
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}
