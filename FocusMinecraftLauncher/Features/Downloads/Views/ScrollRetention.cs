using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FocusMinecraftLauncher.Features.Downloads.Models;
namespace FocusMinecraftLauncher.Features.Downloads.Views;

/// <summary>为下载分类和详情保留一个主滚动位置；导航恢复期间不让初始零偏移覆盖缓存。</summary>
public static class ScrollRetention
{
    public static readonly DependencyProperty PositionProperty = DependencyProperty.RegisterAttached("Position", typeof(DownloadScrollPosition), typeof(ScrollRetention), new PropertyMetadata(null, Changed));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State), typeof(ScrollRetention));
    public static DownloadScrollPosition? GetPosition(DependencyObject target) => (DownloadScrollPosition?)target.GetValue(PositionProperty);
    public static void SetPosition(DependencyObject target, DownloadScrollPosition? value) => target.SetValue(PositionProperty, value);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if(target is not ScrollViewer viewer) return;
        if(viewer.GetValue(StateProperty) is not State state) { state = new(viewer); viewer.SetValue(StateProperty,state); }
        state.Switch((DownloadScrollPosition?)e.NewValue);
    }
    private sealed class State
    {
        private readonly ScrollViewer _viewer;
        private DownloadScrollPosition? _position;
        private bool _ready, _subscribed;
        private int _revision;
        public State(ScrollViewer viewer)
        {
            _viewer = viewer;
            viewer.Loaded += (_,_) => { Subscribe(); Restore(); };
            viewer.Unloaded += (_,_) => { Save(); Unsubscribe(); _ready = false; _revision++; };
            viewer.IsVisibleChanged += (_,e) => { if(e.NewValue is true) Restore(); else { Save(); _ready = false; _revision++; } };
            viewer.ScrollChanged += (_,_) => Save();
        }
        public void Switch(DownloadScrollPosition? position)
        { Save(); Unsubscribe(); _position = position; _ready = false; _revision++; Subscribe(); Restore(); }
        private void Save() { if(_ready && _viewer.IsLoaded && _viewer.IsVisible && _position != null) _position.Offset = _viewer.VerticalOffset; }
        private void Subscribe() { if(!_subscribed && _viewer.IsLoaded && _position != null) { _position.ResetRequested += Restore; _subscribed = true; } }
        private void Unsubscribe() { if(_subscribed && _position != null) _position.ResetRequested -= Restore; _subscribed = false; }
        private void Restore()
        {
            _ready = false; var expected = ++_revision; var position = _position;
            if(position == null || !_viewer.IsLoaded || !_viewer.IsVisible) return;
            _viewer.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(() =>
            {
                if(expected != _revision || !_viewer.IsLoaded || !_viewer.IsVisible) return;
                _viewer.ScrollToVerticalOffset(position.Offset); _ready = true;
            }));
        }
    }
}
