using System.Windows;
using System.Windows.Interop;
using Wpf.Ui.Controls;

namespace FocusMinecraftLauncher.Presentation.Controls;

/// <summary>补充原生非客户区按钮的按下外观；命中检测、窗口操作和贴靠仍交给 WPF UI。</summary>
public sealed class CaptionButtonFeedback : IDisposable
{
    public static readonly DependencyProperty IsNativePressedProperty = DependencyProperty.RegisterAttached(
        "IsNativePressed", typeof(bool), typeof(CaptionButtonFeedback), new PropertyMetadata(false));

    public static bool GetIsNativePressed(DependencyObject target) => (bool)target.GetValue(IsNativePressedProperty);
    public static void SetIsNativePressed(DependencyObject target, bool value) => target.SetValue(IsNativePressedProperty, value);

    private readonly HwndSource _source;
    private readonly TitleBarButton[] _buttons;

    public CaptionButtonFeedback(HwndSource source, TitleBar titleBar)
    {
        _source = source;
        _buttons = new[] { "PART_MinimizeButton", "PART_MaximizeButton", "PART_CloseButton" }
            .Select(name => titleBar.Template.FindName(name, titleBar)).OfType<TitleBarButton>().ToArray();
        var close = _buttons.First(button => button.ButtonType == TitleBarButtonType.Close);
        close.SetResourceReference(TitleBarButton.MouseOverBackgroundProperty, "CaptionDangerHover");
        close.SetResourceReference(TitleBarButton.MouseOverButtonsForegroundProperty, "CaptionDangerForeground");
        _source.AddHook(ObserveCaptionInput);
    }

    /// <summary>只观察按下与释放，不吞掉任何消息，保持系统菜单、拖动和窗口按钮的原生行为。</summary>
    private nint ObserveCaptionInput(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int ncLeftDown = 0x00A1, ncLeftUp = 0x00A2, ncMouseLeave = 0x02A2;
        const int leftUp = 0x0202, captureChanged = 0x0215, cancelMode = 0x001F;
        if (message == ncLeftDown)
        {
            foreach (var button in _buttons) SetIsNativePressed(button, button.IsHovered);
        }
        else if (message is ncLeftUp or leftUp or ncMouseLeave or captureChanged or cancelMode)
        {
            foreach (var button in _buttons) SetIsNativePressed(button, false);
        }
        return nint.Zero;
    }

    public void Dispose() => _source.RemoveHook(ObserveCaptionInput);
}
