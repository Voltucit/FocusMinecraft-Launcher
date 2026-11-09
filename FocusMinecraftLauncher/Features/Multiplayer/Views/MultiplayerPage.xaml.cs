using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FocusMinecraftLauncher.Features.Multiplayer.ViewModels;

namespace FocusMinecraftLauncher.Features.Multiplayer.Views;

/// <summary>在现有内容区呈现联机入口与房间页，仅协调焦点和与主导航一致的短过渡。</summary>
public partial class MultiplayerPage : UserControl
{
    private MultiplayerViewModel? _viewModel;
    public MultiplayerPage() => InitializeComponent();
    private void PageLoaded(object sender, RoutedEventArgs e) => ObserveViewModel();
    private void PageDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) { if (IsLoaded) ObserveViewModel(); }
    private void PageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null) _viewModel.PropertyChanged -= ViewModelChanged;
        _viewModel = null;
        PageBody.BeginAnimation(OpacityProperty, null);
        PageShift.BeginAnimation(TranslateTransform.YProperty, null);
    }
    private void ObserveViewModel()
    {
        if (ReferenceEquals(_viewModel, DataContext)) return;
        if (_viewModel != null) _viewModel.PropertyChanged -= ViewModelChanged;
        _viewModel = DataContext as MultiplayerViewModel;
        if (_viewModel != null) _viewModel.PropertyChanged += ViewModelChanged;
    }
    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MultiplayerViewModel.IsRoomPage)) return;
        var viewModel = _viewModel;
        Dispatcher.InvokeAsync(() =>
        {
            if (!IsLoaded || viewModel == null || !ReferenceEquals(DataContext, viewModel)) return;
            PageBody.BeginAnimation(OpacityProperty, null);
            PageShift.BeginAnimation(TranslateTransform.YProperty, null);
            if (viewModel.Session.MotionAllowed && SystemParameters.ClientAreaAnimation)
            {
                var duration = TimeSpan.FromMilliseconds(190);
                var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
                PageBody.BeginAnimation(OpacityProperty, new DoubleAnimation(.5, 1, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
                PageShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
            }
            if (viewModel.IsRoomPage) BackButton.Focus();
            else PageBody.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }, DispatcherPriority.Loaded);
    }
}
