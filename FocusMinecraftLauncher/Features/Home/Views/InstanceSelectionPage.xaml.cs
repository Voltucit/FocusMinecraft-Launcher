using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FocusMinecraftLauncher.Features.Home.ViewModels;
using FocusMinecraftLauncher.Presentation.Controls;
namespace FocusMinecraftLauncher.Features.Home.Views;

/// <summary>同一主页上下文中的实例选择；鼠标与键盘都调用相同选择命令。</summary>
public partial class InstanceSelectionPage : UserControl
{
    private readonly TranslateTransform _versionOffset = new();
    private InstanceSelectionViewModel? _viewModel;
    private bool _switching;

    public InstanceSelectionPage()
    {
        InitializeComponent();
        VersionList.RenderTransform = _versionOffset;
        DataContextChanged += (_, _) => Track(DataContext as InstanceSelectionViewModel);
    }
    private void ChooseWithKeyboard(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is ListBox list && DataContext is InstanceSelectionViewModel vm
            && vm.ChooseInstance.CanExecute(list.SelectedItem))
        { vm.ChooseInstance.Execute(list.SelectedItem); e.Handled = true; }
    }

    private void Track(InstanceSelectionViewModel? viewModel)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= ViewModelChanged;
        _viewModel = viewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += ViewModelChanged;
    }
    /// <summary>切换文件夹时先让列表轻微下沉淡出，读取结束后再浮回，遮盖结果替换的一帧。</summary>
    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstanceSelectionViewModel.SelectedFolder))
        {
            _switching = true;
            Animate(8, .72, 120, EasingMode.EaseIn);
            return;
        }
        if (e.PropertyName != nameof(InstanceSelectionViewModel.IsLoading) || _viewModel?.IsLoading != false || !_switching) return;
        _switching = false;
        Animate(0, 1, 220, EasingMode.EaseOut);
    }
    private void Animate(double offset, double opacity, int milliseconds, EasingMode mode)
    {
        if (!ThemeVisuals.GetMotionAllowed(this) || !SystemParameters.ClientAreaAnimation) return;
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var easing = new CubicEase { EasingMode = mode };
        var fromOffset = _versionOffset.Y;
        var fromOpacity = VersionList.Opacity;
        _versionOffset.BeginAnimation(TranslateTransform.YProperty, null);
        VersionList.BeginAnimation(OpacityProperty, null);
        _versionOffset.Y = offset;
        VersionList.Opacity = opacity;
        _versionOffset.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(fromOffset, offset, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        VersionList.BeginAnimation(OpacityProperty,
            new DoubleAnimation(fromOpacity, opacity, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
    }
}
