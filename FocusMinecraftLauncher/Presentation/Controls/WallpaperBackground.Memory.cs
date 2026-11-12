using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FocusMinecraftLauncher.Infrastructure.Runtime;

namespace FocusMinecraftLauncher.Presentation.Controls;

public partial class WallpaperBackground
{
    private readonly DispatcherTimer _backgroundReleaseTimer = new(DispatcherPriority.Background)
        { Interval = TimeSpan.FromSeconds(15) };
    private bool _releasedForBackground;
    private bool IsBackgroundHidden => !IsVisible || _owner?.WindowState == WindowState.Minimized;

    private void UpdateMemoryManagement()
    {
        if (!IsLoaded || !IsBackgroundHidden || StaticMode)
        {
            _backgroundReleaseTimer.Stop();
            if (IsLoaded && !IsBackgroundHidden && !StaticMode && _releasedForBackground)
            {
                _releasedForBackground = false;
                _appliedKey = "";
                QueueApply();
            }
            return;
        }
        if (!_releasedForBackground && !_backgroundReleaseTimer.IsEnabled
            && (_web != null || VideoLayer.Source != null || _load != null))
            _backgroundReleaseTimer.Start();
    }

    /// <summary>短暂最小化只挂起；持续隐藏后关闭本控件拥有的播放器，恢复时按当前选择重新加载。</summary>
    private async void ReleaseBackgroundPlayback(object? sender, EventArgs e)
    {
        _backgroundReleaseTimer.Stop();
        if (!IsLoaded || !IsBackgroundHidden || StaticMode || _releasedForBackground
            || (_web == null && VideoLayer.Source == null && _load == null)) return;
        _releasedForBackground = true;
        int revision = ++_revision;
        _load?.Cancel();
        _appliedKey = "";
        string kind = Kind.Trim().ToLowerInvariant(), source = SourcePath, preview = PreviewPath, project = ProjectPath;
        try
        {
            VideoLayer.Close(); VideoLayer.Source = null; VideoLayer.Visibility = Visibility.Collapsed;
            await ReleaseWebAsync();
            if (!IsCurrent(revision, CancellationToken.None) || !_releasedForBackground) return;
            var frame = await ReadStaticFrameAsync(kind, source, preview, project, CancellationToken.None);
            if (!IsCurrent(revision, CancellationToken.None) || !_releasedForBackground) return;
            ImageLayer.Source = frame;
            ImageLayer.Visibility = frame == null ? Visibility.Collapsed : Visibility.Visible;
            ApplyBlur();
        }
        catch (Exception error)
        { WallpaperDiagnostics.Write("后台释放壁纸：" + error); }
    }

    /// <summary>静态模式只读现成的图片和缓存帧，缺少预览时也不启动浏览器或视频解码器。</summary>
    private static async Task<BitmapSource?> ReadStaticFrameAsync(string kind, string source, string preview,
        string project, CancellationToken token)
    {
        foreach (var path in (kind == "image" ? new[] { source, preview } : new[] { preview }).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var frame = await DecodeImageAsync(path, token);
            if (frame != null) return frame;
        }
        if (kind == "scene" && !string.IsNullOrWhiteSpace(project))
        {
            var cached = await Task.Run(() => WallpaperFrameCache.FindUsable(project), token);
            if (cached != null) return await DecodeImageAsync(cached, token);
        }
        return null;
    }
}
