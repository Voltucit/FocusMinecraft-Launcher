using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FocusMinecraftLauncher.Infrastructure.Runtime;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace FocusMinecraftLauncher.Presentation.Controls;

public sealed class WallpaperRenderEventArgs(int version, string error) : EventArgs
{
    public int Version { get; } = version;
    public string Error { get; } = error;
}

/// <summary>原图原生解码，视频使用 MediaElement，场景与网页使用现有 WebWallGL；各次切换独立取消与释放。</summary>
public partial class WallpaperBackground : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(WallpaperBackground), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(nameof(SourcePath), typeof(string), typeof(WallpaperBackground), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty PreviewPathProperty = DependencyProperty.Register(nameof(PreviewPath), typeof(string), typeof(WallpaperBackground), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty ProjectPathProperty = DependencyProperty.Register(nameof(ProjectPath), typeof(string), typeof(WallpaperBackground), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty RefreshVersionProperty = DependencyProperty.Register(nameof(RefreshVersion), typeof(int), typeof(WallpaperBackground), new PropertyMetadata(0, Changed));
    public static readonly DependencyProperty PropertiesJsonProperty = DependencyProperty.Register(nameof(PropertiesJson), typeof(string), typeof(WallpaperBackground), new PropertyMetadata("{}", SettingsChanged));
    public static readonly DependencyProperty MotionAllowedProperty = DependencyProperty.Register(nameof(MotionAllowed), typeof(bool), typeof(WallpaperBackground), new PropertyMetadata(true, SettingsChanged));
    public static readonly DependencyProperty BlurPercentProperty = DependencyProperty.Register(nameof(BlurPercent), typeof(double), typeof(WallpaperBackground), new PropertyMetadata(0d, SettingsChanged));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public string SourcePath { get => (string)GetValue(SourcePathProperty); set => SetValue(SourcePathProperty, value); }
    public string PreviewPath { get => (string)GetValue(PreviewPathProperty); set => SetValue(PreviewPathProperty, value); }
    public string ProjectPath { get => (string)GetValue(ProjectPathProperty); set => SetValue(ProjectPathProperty, value); }
    public int RefreshVersion { get => (int)GetValue(RefreshVersionProperty); set => SetValue(RefreshVersionProperty, value); }
    public string PropertiesJson { get => (string)GetValue(PropertiesJsonProperty); set => SetValue(PropertiesJsonProperty, value); }
    public bool MotionAllowed { get => (bool)GetValue(MotionAllowedProperty); set => SetValue(MotionAllowedProperty, value); }
    public double BlurPercent { get => (double)GetValue(BlurPercentProperty); set => SetValue(BlurPercentProperty, value); }
    public event EventHandler<WallpaperRenderEventArgs>? RenderStateChanged;

    private Window? _owner;
    private int _revision, _videoVersion;
    private string _appliedKey = "";
    private bool _queued, _paused, _webReady;
    private CancellationTokenSource? _load;
    private WebView2CompositionControl? _web;
    private WallpaperWebHost? _host;
    private TaskCompletionSource<bool>? _ready;
    private string? _sentProperties;
    private double _sentBlur = double.NaN;
    private bool? _sentPaused;
    private static Task<CoreWebView2Environment>? _environment;

    public WallpaperBackground()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdatePlayback();
        VideoLayer.MediaEnded += (_, _) => { if (!_paused) { VideoLayer.Position = TimeSpan.Zero; VideoLayer.Play(); } };
        VideoLayer.MediaOpened += (_, _) =>
        {
            if (Kind.Equals("video", StringComparison.OrdinalIgnoreCase) && _videoVersion == RefreshVersion)
            {
                ImageLayer.Visibility = Visibility.Collapsed;
                RenderStateChanged?.Invoke(this, new(_videoVersion, ""));
                UpdatePlayback();
            }
        };
        VideoLayer.MediaFailed += (_, e) =>
        {
            if (!Kind.Equals("video", StringComparison.OrdinalIgnoreCase) || _videoVersion != RefreshVersion) return;
            VideoLayer.Visibility = Visibility.Collapsed;
            _ = RestorePreviewAsync(_revision, PreviewPath);
            ReportFailure(_videoVersion, e.ErrorException ?? new InvalidOperationException("视频格式无法播放。"));
        };
    }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((WallpaperBackground)sender).QueueApply();
    private static void SettingsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var control = (WallpaperBackground)sender;
        if (e.Property == BlurPercentProperty) control.ApplyBlur();
        control.UpdatePlayback();
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _owner = Window.GetWindow(this);
        if (_owner != null) _owner.StateChanged += OwnerStateChanged;
        QueueApply();
    }
    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_owner != null) _owner.StateChanged -= OwnerStateChanged;
        _owner = null;
        _revision++;
        _load?.Cancel();
        _appliedKey = "";
        VideoLayer.Stop();
        VideoLayer.Source = null;
        await ReleaseWebAsync();
    }
    private void OwnerStateChanged(object? sender, EventArgs e) => UpdatePlayback();

    /// <summary>一次账号状态通知可能更新多个绑定，统一等本轮绑定完成后只导航一次。</summary>
    private void QueueApply()
    {
        if (!IsLoaded || _queued) return;
        _queued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _queued = false;
            if (!IsLoaded) return;
            string kind = Kind.Trim().ToLowerInvariant();
            string key = string.Join('\0', kind, SourcePath, ProjectPath, PreviewPath, RefreshVersion);
            if (key == _appliedKey) return;
            _appliedKey = key;
            _load?.Cancel();
            var cancellation = new CancellationTokenSource();
            _load = cancellation;
            _ = ApplyAsync(++_revision, RefreshVersion, kind, SourcePath, ProjectPath, PreviewPath, cancellation);
        }));
    }
    private bool IsCurrent(int revision, CancellationToken token) => revision == _revision && !token.IsCancellationRequested && IsLoaded;

    private async Task ApplyAsync(int revision, int version, string kind, string source, string project, string preview, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            await ReleaseWebAsync();
            if (!IsCurrent(revision, token)) return;
            VideoLayer.Stop(); VideoLayer.Source = null; VideoLayer.Visibility = Visibility.Collapsed;
            ImageLayer.Source = null; ImageLayer.Visibility = Visibility.Collapsed;
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(kind)) return;
            if (kind != "image" || !string.Equals(source, preview, StringComparison.OrdinalIgnoreCase))
                await RestorePreviewAsync(revision, preview);
            if (!IsCurrent(revision, token)) return;
            if (!File.Exists(source)) throw new FileNotFoundException("壁纸原始文件已不存在。");
            if (kind == "image")
            {
                string extension = Path.GetExtension(source).ToLowerInvariant();
                // GIF/APNG 需要动画；WebP/AVIF/SVG 在本机没有 WIC 解码器时改用浏览器原生支持。
                var image = extension is ".gif" or ".apng" or ".pnga" ? null : await DecodeImageAsync(source, token);
                if (!IsCurrent(revision, token)) return;
                if (image != null)
                {
                    ImageLayer.Source = image; ImageLayer.Visibility = Visibility.Visible;
                    ApplyBlur(); RenderStateChanged?.Invoke(this, new(version, "")); return;
                }
            }
            if (kind == "video")
            {
                _videoVersion = version;
                VideoLayer.Visibility = Visibility.Visible;
                VideoLayer.Source = new Uri(source);
                ApplyBlur(); UpdatePlayback(); return;
            }
            if (kind is not ("scene" or "web" or "image")) throw new NotSupportedException("这个壁纸类型暂不支持播放。");
            await OpenWebAsync(revision, version, source, project, kind, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!IsCurrent(revision, token)) return;
            await ReleaseWebAsync();
            await RestorePreviewAsync(revision, preview);
            if (IsCurrent(revision, token)) ReportFailure(version, error);
        }
        finally
        {
            if (ReferenceEquals(_load, cancellation)) _load = null;
            cancellation.Dispose();
        }
    }

    private static Task<CoreWebView2Environment> EnvironmentAsync()
    {
        if (_environment == null || _environment.IsFaulted || _environment.IsCanceled)
        {
            string data = Path.Combine(AppContext.BaseDirectory, "FMLauncher", "WebView2");
            Directory.CreateDirectory(data);
            _environment = CoreWebView2Environment.CreateAsync(userDataFolder: data);
        }
        return _environment;
    }
    private async Task OpenWebAsync(int revision, int version, string source, string project, string kind, CancellationToken token)
    {
        var host = new WallpaperWebHost(source, project, kind, PropertiesJson);
        var web = new WebView2CompositionControl
        {
            DefaultBackgroundColor = System.Drawing.Color.Transparent,
            Focusable = false, IsHitTestVisible = false, AllowExternalDrop = false
        };
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host = host; _web = web; _ready = ready; _webReady = false;
        _sentProperties = null; _sentBlur = double.NaN; _sentPaused = null;
        WebHost.Children.Add(web); WebHost.Visibility = Visibility.Visible;
        await web.EnsureCoreWebView2Async(await EnvironmentAsync().WaitAsync(token)).WaitAsync(token);
        if (!IsCurrent(revision, token)) return;
        var core = web.CoreWebView2;
        core.IsMuted = true;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(host.Url, StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (!IsCurrent(revision, token) || !ReferenceEquals(_web, web) || e.IsSuccess
                || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
            ready.TrySetException(new IOException("壁纸页面加载失败：" + e.WebErrorStatus));
        };
        core.WebMessageReceived += (_, e) =>
        {
            if (!IsCurrent(revision, token) || !ReferenceEquals(_web, web) || !e.Source.Equals(host.Url, StringComparison.OrdinalIgnoreCase)) return;
            string message;
            try { message = e.TryGetWebMessageAsString(); }
            catch (ArgumentException) { return; }
            if (message == "wallpaper-ready") ready.TrySetResult(true);
            else if (message.StartsWith("wallpaper-error:", StringComparison.Ordinal))
            {
                var error = new InvalidOperationException(message[16..]);
                if (!ready.TrySetException(error) && _webReady) _ = FailLiveAsync(web, revision, version, error);
            }
        };
        core.ProcessFailed += (_, e) =>
        {
            if (!IsCurrent(revision, token) || !ReferenceEquals(_web, web)) return;
            var error = new InvalidOperationException("壁纸渲染进程异常退出：" + e.ProcessFailedKind);
            if (!ready.TrySetException(error) && _webReady) _ = FailLiveAsync(web, revision, version, error);
        };
        // 每个播放器只发起一次导航，取消旧播放器时忽略它自己的导航中止事件。
        core.Navigate(host.Url);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(75), token);
        if (!IsCurrent(revision, token) || !ReferenceEquals(_web, web)) return;
        _webReady = true;
        await SyncWebAsync(web);
        if (!IsCurrent(revision, token) || !_webReady || !ReferenceEquals(_web, web)) return;
        ImageLayer.Source = null; ImageLayer.Visibility = Visibility.Collapsed;
        RenderStateChanged?.Invoke(this, new(version, ""));
        UpdatePlayback();
    }
    private async Task FailLiveAsync(WebView2CompositionControl web, int revision, int version, Exception error)
    {
        if (!ReferenceEquals(_web, web)) return;
        _webReady = false;
        await ReleaseWebAsync();
        await RestorePreviewAsync(revision, PreviewPath);
        if (revision == _revision && IsLoaded) ReportFailure(version, error);
    }
    private void ReportFailure(int version, Exception error)
    {
        WallpaperDiagnostics.Write(error.ToString());
        string message = error is WebView2RuntimeNotFoundException ? "缺少 Microsoft Edge WebView2 运行时，请安装后重试。"
            : error is TimeoutException ? "壁纸加载超时，请重试或选择其他壁纸。" : "壁纸未能显示：" + error.Message.Split('\n')[0].Trim();
        if (message.Length > 220) message = message[..220];
        RenderStateChanged?.Invoke(this, new(version, message));
    }
    private async Task ReleaseWebAsync()
    {
        var web = _web; var host = _host;
        _web = null; _host = null; _webReady = false;
        _ready?.TrySetCanceled(); _ready = null;
        if (web != null)
        {
            WebHost.Children.Remove(web);
            try { web.Dispose(); } catch (Exception error) { Trace.TraceWarning("释放壁纸播放器：{0}", error.GetType().Name); }
        }
        WebHost.Visibility = Visibility.Collapsed;
        if (host != null) await host.DisposeAsync();
    }
    private async Task RestorePreviewAsync(int revision, string path)
    {
        var image = await DecodeImageAsync(path, CancellationToken.None);
        if (revision != _revision) return;
        ImageLayer.Source = image;
        ImageLayer.Visibility = image == null ? Visibility.Collapsed : Visibility.Visible;
    }
    private static async Task<BitmapSource?> DecodeImageAsync(string path, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 1920; image.StreamSource = stream; image.EndInit(); image.Freeze();
                token.ThrowIfCancellationRequested();
                return (BitmapSource)image;
            }, token);
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or FormatException or System.Runtime.InteropServices.COMException)
        { WallpaperDiagnostics.Write("图片解码：" + error.Message); return null; }
    }
    private void ApplyBlur()
    {
        double radius = Math.Clamp(BlurPercent, 0, 100) / 2;
        ImageLayer.Effect = radius > 0 ? new BlurEffect { Radius = radius } : null;
        VideoLayer.Effect = radius > 0 ? new BlurEffect { Radius = radius } : null;
    }
    private void UpdatePlayback()
    {
        _paused = !IsVisible || _owner?.WindowState == WindowState.Minimized || !MotionAllowed;
        if (VideoLayer.Visibility == Visibility.Visible) { if (_paused) VideoLayer.Pause(); else VideoLayer.Play(); }
        if (_webReady && _web is {} web) _ = SyncWebAsync(web);
    }
    private async Task SyncWebAsync(WebView2CompositionControl web)
    {
        if (!_webReady || !ReferenceEquals(_web, web) || web.CoreWebView2 == null) return;
        try
        {
            bool hidden = !IsVisible || _owner?.WindowState == WindowState.Minimized;
            web.CoreWebView2.Resume();
            if (!hidden) web.Visibility = Visibility.Visible;
            var script = new System.Text.StringBuilder();
            if (_sentProperties != PropertiesJson)
            {
                script.Append("window.fmlSetProperties?.(").Append(JsonSerializer.Serialize(PropertiesJson)).Append(");");
                _sentProperties = PropertiesJson;
            }
            if (_sentBlur != BlurPercent)
            {
                script.Append("window.fmlSetBlur?.(").Append(BlurPercent.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(");");
                _sentBlur = BlurPercent;
            }
            if (_sentPaused != _paused)
            {
                script.Append("window.fmlSetPaused?.(").Append(_paused ? "true" : "false").Append(");");
                _sentPaused = _paused;
            }
            if (script.Length > 0) await web.ExecuteScriptAsync(script.ToString());
            if (hidden && ReferenceEquals(_web, web))
            {
                web.Visibility = Visibility.Collapsed;
                await web.CoreWebView2.TrySuspendAsync();
                if (!_paused && ReferenceEquals(_web, web)) { web.CoreWebView2.Resume(); web.Visibility = Visibility.Visible; }
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
        { Trace.TraceWarning("壁纸播放状态更新：{0}", error.GetType().Name); }
    }
}
