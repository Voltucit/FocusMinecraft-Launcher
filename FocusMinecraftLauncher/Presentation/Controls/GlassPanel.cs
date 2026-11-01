using System.Windows;
using System.Windows.Controls;

namespace FocusMinecraftLauncher.Presentation.Controls;

/// <summary>共用内容宿主；仅提供主题模板需要的圆角和表面层级标记。</summary>
public sealed class GlassPanel : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(GlassPanel), new PropertyMetadata(new CornerRadius(16)));
    public static readonly DependencyProperty HoverFeedbackProperty = DependencyProperty.Register(
        nameof(HoverFeedback), typeof(bool), typeof(GlassPanel), new PropertyMetadata(false));
    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public bool HoverFeedback { get => (bool)GetValue(HoverFeedbackProperty); set => SetValue(HoverFeedbackProperty, value); }

    public GlassPanel()
    {
        SetResourceReference(StyleProperty, typeof(GlassPanel));
        Focusable = false;
    }
}
