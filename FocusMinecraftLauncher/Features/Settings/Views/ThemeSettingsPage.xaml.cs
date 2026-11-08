using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FocusMinecraftLauncher.Features.Settings.Views;

/// <summary>设置内的主题子页，使用父页面的共享设置状态。</summary>
public partial class ThemeSettingsPage : UserControl
{
    public ThemeSettingsPage() => InitializeComponent();

    private void WallpaperList_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ListBox list) return;
        ScrollViewer? inner = FindVisualChild<ScrollViewer>(list);
        bool canScroll = inner is not null && inner.ScrollableHeight > 0
            && ((e.Delta > 0 && inner.VerticalOffset > 0)
                || (e.Delta < 0 && inner.VerticalOffset < inner.ScrollableHeight));
        if (canScroll) return;

        ScrollViewer? outer = FindAncestor<ScrollViewer>(list);
        if (outer is null) return;
        e.Handled = true;
        outer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = outer
        });
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? current = VisualTreeHelper.GetParent(child);
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}

