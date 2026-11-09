using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace FocusMinecraftLauncher.Presentation.Tray;

/// <summary>Windows 托盘入口；复用现有标志，生命周期跟随主窗口，不另建后台进程。</summary>
internal sealed class LauncherTrayIcon : IDisposable
{
    private readonly Window _owner;
    private readonly Action _restore, _close;
    private readonly Action<TrayAnchor> _togglePanel, _showPanel;
    private readonly DispatcherTimer _singleClickTimer;
    private TrayAnchor _anchor;
    private bool _leftButtonDown;
    public bool IsContextMenuOpen { get; private set; }
    public bool HasPendingInteraction => IsContextMenuOpen || _singleClickTimer.IsEnabled
        || _leftButtonDown && (Forms.Control.MouseButtons & Forms.MouseButtons.Left) != 0;
    private readonly Drawing.Icon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly TrayMenuRenderer _menuRenderer = new();
    private readonly Drawing.Font _menuFont = new("Microsoft YaHei UI", 10f, Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Point);
    private readonly Forms.NotifyIcon _notifyIcon;
    private bool _disposed;

    public LauncherTrayIcon(Window owner, Action<TrayAnchor> togglePanel, Action restore, Action<TrayAnchor> showPanel, Action close)
    {
        _owner = owner; _restore = restore; _close = close;
        _togglePanel = togglePanel; _showPanel = showPanel;
        _anchor = TrayPositioner.CursorAnchor();
        _singleClickTimer = new DispatcherTimer(DispatcherPriority.Input, owner.Dispatcher);
        _singleClickTimer.Tick += SingleClick;
        _icon = CreateIcon(owner);
        _menu = new Forms.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = false, Renderer = _menuRenderer, Font = _menuFont };
        _menu.Items.Add("打开完整启动器", null, (_, _) => Dispatch(_restore)).Tag = TrayMenuGlyph.FullWindow;
        _menu.Items.Add("显示精简面板", null, (_, _) => Dispatch(() => _showPanel(_anchor))).Tag = TrayMenuGlyph.Panel;
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("退出 FMLauncher", null, (_, _) => Dispatch(_close)).Tag = TrayMenuGlyph.Exit;
        _menu.Opening += MenuOpening;
        _menu.Closed += MenuClosed;
        _menu.SizeChanged += MenuSizeChanged;
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon, Text = "FMLauncher", ContextMenuStrip = _menu
        };
        _notifyIcon.MouseDown += MouseDown;
        _notifyIcon.MouseUp += MouseUp;
        _notifyIcon.MouseClick += MouseClick;
        _notifyIcon.MouseDoubleClick += DoubleClick;
    }

    public void Show() { if (!_disposed) _notifyIcon.Visible = true; }
    public void Hide()
    {
        if (_disposed) return;
        _singleClickTimer.Stop();
        _leftButtonDown = false;
        _menu.Close();
        IsContextMenuOpen = false;
        _notifyIcon.Visible = false;
    }

    private void MouseDown(object? sender, Forms.MouseEventArgs e)
    {
        _anchor = TrayPositioner.CursorAnchor();
        if (e.Button == Forms.MouseButtons.Left) _leftButtonDown = true;
    }
    private void MouseUp(object? sender, Forms.MouseEventArgs e)
    { if (e.Button == Forms.MouseButtons.Left) _leftButtonDown = false; }
    private void MouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left) return;
        _anchor = TrayPositioner.CursorAnchor();
        _singleClickTimer.Stop();
        _singleClickTimer.Interval = TimeSpan.FromMilliseconds(Forms.SystemInformation.DoubleClickTime + 25);
        _singleClickTimer.Start();
    }
    private void SingleClick(object? sender, EventArgs e)
    { _singleClickTimer.Stop(); Dispatch(() => _togglePanel(_anchor)); }
    private void DoubleClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left) return;
        _singleClickTimer.Stop();
        Dispatch(_restore);
    }
    private void MenuOpening(object? sender, CancelEventArgs e)
    {
        _singleClickTimer.Stop(); _anchor = TrayPositioner.CursorAnchor();
        _menuRenderer.ApplyTheme(_owner, _menu);
        IsContextMenuOpen = true;
    }
    private void MenuSizeChanged(object? sender, EventArgs e) => _menuRenderer.UpdateRegion(_menu);
    private void MenuClosed(object? sender, Forms.ToolStripDropDownClosedEventArgs e) => IsContextMenuOpen = false;

    private void Dispatch(Action action)
    {
        if (_disposed || _owner.Dispatcher.HasShutdownStarted) return;
        _owner.Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) action(); }));
    }

    private static Drawing.Icon CreateIcon(Window owner)
    {
        var image = (ImageSource)owner.FindResource("LauncherIcon");
        var background = (owner.TryFindResource("AppBackground") as SolidColorBrush)?.Color ?? Colors.White;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(background.R, background.G, background.B)), null, new Rect(0, 0, 32, 32), 6, 6);
            context.DrawImage(image, new Rect(4, 4, 24, 24));
        }
        var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); stream.Position = 0;
        using var drawingBitmap = new Drawing.Bitmap(stream);
        var handle = drawingBitmap.GetHicon();
        try
        {
            using var borrowed = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _singleClickTimer.Stop(); _singleClickTimer.Tick -= SingleClick;
        _notifyIcon.MouseDown -= MouseDown;
        _notifyIcon.MouseUp -= MouseUp;
        _notifyIcon.MouseClick -= MouseClick;
        _notifyIcon.MouseDoubleClick -= DoubleClick;
        _menu.Opening -= MenuOpening; _menu.Closed -= MenuClosed;
        _menu.SizeChanged -= MenuSizeChanged;
        var region = _menu.Region; _menu.Region = null; region?.Dispose();
        _notifyIcon.Dispose(); _menu.Dispose(); _menuFont.Dispose(); _icon.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
