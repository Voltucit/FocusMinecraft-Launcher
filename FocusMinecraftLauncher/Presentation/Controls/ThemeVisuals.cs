using System.Windows;

namespace FocusMinecraftLauncher.Presentation.Controls;

/// <summary>向界面子树传递主题、页面表面与动效偏好，不依赖业务 ViewModel。</summary>
public static class ThemeVisuals
{
    public static readonly DependencyProperty PageSurfaceVisibleProperty = DependencyProperty.RegisterAttached(
        "PageSurfaceVisible", typeof(bool), typeof(ThemeVisuals), new FrameworkPropertyMetadata(true, PreferenceChanged));
    public static bool GetPageSurfaceVisible(DependencyObject target) => (bool)target.GetValue(PageSurfaceVisibleProperty);
    public static void SetPageSurfaceVisible(DependencyObject target, bool value) => target.SetValue(PageSurfaceVisibleProperty, value);

    public static readonly DependencyProperty IsSoftProperty = DependencyProperty.RegisterAttached(
        "IsSoft", typeof(bool), typeof(ThemeVisuals), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, PreferenceChanged));
    public static bool GetIsSoft(DependencyObject target) => (bool)target.GetValue(IsSoftProperty);
    public static void SetIsSoft(DependencyObject target, bool value) => target.SetValue(IsSoftProperty, value);

    public static readonly DependencyProperty IsFlatProperty = DependencyProperty.RegisterAttached(
        "IsFlat", typeof(bool), typeof(ThemeVisuals), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetIsFlat(DependencyObject target) => (bool)target.GetValue(IsFlatProperty);
    public static void SetIsFlat(DependencyObject target, bool value) => target.SetValue(IsFlatProperty, value);

    public static readonly DependencyProperty MotionAllowedProperty = DependencyProperty.RegisterAttached(
        "MotionAllowed", typeof(bool), typeof(ThemeVisuals), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits, PreferenceChanged));
    public static readonly DependencyProperty WindowActiveProperty = DependencyProperty.RegisterAttached(
        "WindowActive", typeof(bool), typeof(ThemeVisuals), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits, PreferenceChanged));
    public static bool GetMotionAllowed(DependencyObject target) => (bool)target.GetValue(MotionAllowedProperty);
    public static void SetMotionAllowed(DependencyObject target, bool value) => target.SetValue(MotionAllowedProperty, value);
    public static bool GetWindowActive(DependencyObject target) => (bool)target.GetValue(WindowActiveProperty);
    public static void SetWindowActive(DependencyObject target, bool value) => target.SetValue(WindowActiveProperty, value);

    private static void PreferenceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        => SoftMotion.RefreshPreferences(sender);
}
