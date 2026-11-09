using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FocusMinecraftLauncher.Presentation.Tray;

/// <summary>物理屏幕坐标；不把不同显示器的物理原点直接当成 WPF DIP。</summary>
internal readonly record struct TrayAnchor(int X, int Y);

internal static class TrayPositioner
{
    private enum Edge { Left, Top, Right, Bottom }
    public static TrayAnchor CursorAnchor()
    {
        if (!GetCursorPos(out var point)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(point.X, point.Y);
    }

    public static void Place(Window window, TrayAnchor anchor, bool useWindowDpi = false)
    {
        var monitor = MonitorFromPoint(new NativePoint(anchor.X, anchor.Y), 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        uint dpi = 96;
        if (useWindowDpi) dpi = GetDpiForWindow(hwnd);
        else if (GetDpiForMonitor(monitor, 0, out var targetDpi, out _) == 0) dpi = targetDpi;
        var scale = (dpi == 0 ? 96 : dpi) / 96d;
        var work = info.Work;
        var gap = Math.Max(1, Math.Min((int)Math.Round(8 * scale), Math.Min(work.Width, work.Height) / 4));
        var width = Math.Max(1, Math.Min((int)Math.Round(380 * scale), work.Width - gap * 2));
        var height = Math.Max(1, Math.Min((int)Math.Round(520 * scale), work.Height - gap * 2));
        window.Width = width / scale; window.Height = height / scale;
        var align = (int)Math.Round(24 * scale);
        var edge = TaskbarEdge(monitor, info, anchor);
        var x = anchor.X - width + align;
        var y = anchor.Y - height - gap;
        switch (edge)
        {
            case Edge.Top: y = Math.Max(anchor.Y + gap, work.Top + gap); break;
            case Edge.Left: x = Math.Max(anchor.X + gap, work.Left + gap); y = anchor.Y - height + align; break;
            case Edge.Right: x = Math.Min(anchor.X - width - gap, work.Right - width - gap); y = anchor.Y - height + align; break;
            case Edge.Bottom: y = Math.Min(y, work.Bottom - height - gap); break;
        }
        x = Math.Clamp(x, work.Left + gap, work.Right - width - gap);
        y = Math.Clamp(y, work.Top + gap, work.Bottom - height - gap);
        // 直接以目标显示器的物理像素定位，绕开多 DPI 显示器原点换算的歧义。
        if (!SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, 0x0004 | 0x0010 | 0x0200))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static Edge TaskbarEdge(IntPtr monitor, MonitorInfo info, TrayAnchor anchor)
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (TryEdge(taskbar, monitor, info, out var edge)) return edge;
        var secondary = IntPtr.Zero;
        while ((secondary = FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            if (TryEdge(secondary, monitor, info, out edge)) return edge;
        if (info.Work.Top > info.Monitor.Top) return Edge.Top;
        if (info.Work.Left > info.Monitor.Left) return Edge.Left;
        if (info.Work.Right < info.Monitor.Right) return Edge.Right;
        if (info.Work.Bottom < info.Monitor.Bottom) return Edge.Bottom;
        return new[]
        {
            (Edge.Left, Math.Abs(anchor.X - info.Monitor.Left)), (Edge.Top, Math.Abs(anchor.Y - info.Monitor.Top)),
            (Edge.Right, Math.Abs(anchor.X - info.Monitor.Right)), (Edge.Bottom, Math.Abs(anchor.Y - info.Monitor.Bottom))
        }.MinBy(item => item.Item2).Item1;
    }

    private static bool TryEdge(IntPtr hwnd, IntPtr monitor, MonitorInfo info, out Edge edge)
    {
        edge = Edge.Bottom;
        if (hwnd == IntPtr.Zero || MonitorFromWindow(hwnd, 2) != monitor || !GetWindowRect(hwnd, out var rect)) return false;
        edge = rect.Width >= rect.Height
            ? Math.Abs(rect.Top - info.Monitor.Top) < Math.Abs(info.Monitor.Bottom - rect.Bottom) ? Edge.Top : Edge.Bottom
            : Math.Abs(rect.Left - info.Monitor.Left) < Math.Abs(info.Monitor.Right - rect.Right) ? Edge.Left : Edge.Right;
        return true;
    }

    public static bool ForegroundIsLauncherPopup()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out var process);
        var main = Application.Current?.MainWindow;
        return process == (uint)Environment.ProcessId && (main == null || foreground != new WindowInteropHelper(main).Handle);
    }

    public static void ActivateOrFlash(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || SetForegroundWindow(hwnd) || !window.ShowInTaskbar) return;
        var flash = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = hwnd, Flags = 2, Count = 3 };
        FlashWindowEx(ref flash);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    { public int Left, Top, Right, Bottom; public readonly int Width => Right - Left; public readonly int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct FlashInfo
    { public uint Size; public IntPtr Window; public uint Flags, Count, Timeout; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FlashWindowEx(ref FlashInfo info);
}
