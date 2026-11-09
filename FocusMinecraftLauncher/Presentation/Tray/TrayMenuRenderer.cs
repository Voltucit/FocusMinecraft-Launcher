using System.Windows;
using FocusMinecraftLauncher.Presentation.Controls;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;
using Media = System.Windows.Media;

namespace FocusMinecraftLauncher.Presentation.Tray;

internal enum TrayMenuGlyph { FullWindow, Panel, Exit }

/// <summary>仅绘制当前托盘菜单；保留系统菜单的焦点、键盘、定位与关闭行为。</summary>
internal sealed class TrayMenuRenderer : Forms.ToolStripRenderer
{
    private Drawing.Color _background, _foreground, _muted, _hover, _border, _danger, _highlightText;
    private bool _flat, _highContrast;
    private static float Scale(Forms.ToolStrip? menu) => (menu?.DeviceDpi ?? 96) / 96f;

    public void ApplyTheme(Window owner, Forms.ContextMenuStrip menu)
    {
        _flat = ThemeVisuals.GetIsFlat(owner);
        _highContrast = SystemParameters.HighContrast;
        var ground = Read(owner, "AppBackground", Drawing.Color.FromArgb(25, 25, 25), Drawing.Color.Black);
        _background = Read(owner, "PopupSurface", ground, ground);
        _foreground = Read(owner, "AppText", Drawing.Color.White, _background);
        _muted = Read(owner, "MutedText", Drawing.Color.Gray, _background);
        _hover = Read(owner, _flat ? "FlatBlack" : "ControlHover", Drawing.Color.FromArgb(45, 45, 45), _background);
        _border = Read(owner, _flat ? "BorderBrush" : "SubtleBorder", Drawing.Color.DimGray, _background);
        _danger = Read(owner, "ErrorText", Drawing.Color.IndianRed, _background);
        _highlightText = _flat ? Drawing.Color.White : _foreground;
        if (_highContrast)
        {
            _background = Drawing.SystemColors.Menu; _foreground = Drawing.SystemColors.MenuText;
            _hover = Drawing.SystemColors.Highlight; _highlightText = Drawing.SystemColors.HighlightText;
            _muted = Drawing.SystemColors.GrayText; _border = Drawing.SystemColors.WindowText; _danger = _foreground;
        }
        var scale = Scale(menu);
        menu.SuspendLayout();
        menu.BackColor = _background; menu.ForeColor = _foreground;
        menu.Padding = new Forms.Padding((int)Math.Round(6 * scale));
        menu.MinimumSize = new Drawing.Size((int)Math.Round(232 * scale), 0);
        menu.DropShadowEnabled = !_flat && !_highContrast;
        foreach (Forms.ToolStripItem item in menu.Items)
            if (item is Forms.ToolStripMenuItem)
                item.Padding = new Forms.Padding((int)Math.Round(30 * scale), (int)Math.Round(8 * scale), (int)Math.Round(14 * scale), (int)Math.Round(8 * scale));
        menu.ResumeLayout(true);
        UpdateRegion(menu);
        menu.Invalidate();
    }

    public void UpdateRegion(Forms.ContextMenuStrip menu)
    {
        if (menu.Width <= 1 || menu.Height <= 1) return;
        var previous = menu.Region;
        if (_flat || _highContrast) menu.Region = null;
        else
        {
            using var outline = Rounded(new Drawing.RectangleF(0, 0, menu.Width, menu.Height), 10 * Scale(menu));
            menu.Region = new Drawing.Region(outline);
        }
        previous?.Dispose();
    }

    protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
    {
        using var brush = new Drawing.SolidBrush(_background);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
    {
        var scale = Scale(e.ToolStrip);
        var thickness = (_flat ? 2 : 1) * scale;
        var bounds = new Drawing.RectangleF(thickness / 2, thickness / 2, e.ToolStrip.Width - thickness, e.ToolStrip.Height - thickness);
        using var outline = Rounded(bounds, _flat || _highContrast ? 0 : 10 * scale);
        using var pen = new Drawing.Pen(_border, thickness);
        var state = e.Graphics.Save();
        e.Graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, outline); e.Graphics.Restore(state);
    }

    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var scale = Scale(e.ToolStrip);
        using var shape = Rounded(new Drawing.RectangleF(0, 1 * scale, e.Item.Width, e.Item.Height - 2 * scale), _flat || _highContrast ? 0 : 4 * scale);
        using var brush = new Drawing.SolidBrush(_hover);
        var state = e.Graphics.Save();
        e.Graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.FillPath(brush, shape); e.Graphics.Restore(state);
    }

    protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
    {
        var scale = Scale(e.ToolStrip);
        using var pen = new Drawing.Pen(_border, Math.Max(1, scale));
        e.Graphics.DrawLine(pen, 8 * scale, e.Item.Height / 2f, e.Item.Width - 8 * scale, e.Item.Height / 2f);
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        var scale = Scale(e.ToolStrip);
        var glyph = e.Item.Tag is TrayMenuGlyph value ? value : TrayMenuGlyph.Panel;
        var color = !e.Item.Enabled ? _muted : e.Item.Selected && (_flat || _highContrast) ? _highlightText
            : glyph == TrayMenuGlyph.Exit ? _danger : _foreground;
        var left = Math.Max(e.TextRectangle.Left, (int)Math.Round(34 * scale));
        var rectangle = new Drawing.Rectangle(left, 0, Math.Max(1, e.Item.Width - left - (int)Math.Round(12 * scale)), e.Item.Height);
        Forms.TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, rectangle, color,
            Forms.TextFormatFlags.Left | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.SingleLine | Forms.TextFormatFlags.NoPadding);
        DrawGlyph(e.Graphics, glyph, color, 8 * scale, (e.Item.Height - 18 * scale) / 2f, scale);
    }

    private static void DrawGlyph(Drawing.Graphics graphics, TrayMenuGlyph glyph, Drawing.Color color, float x, float y, float scale)
    {
        var state = graphics.Save();
        graphics.TranslateTransform(x, y); graphics.ScaleTransform(scale, scale);
        graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Drawing.Pen(color, 1.4f) { LineJoin = Drawing2D.LineJoin.Round, StartCap = Drawing2D.LineCap.Round, EndCap = Drawing2D.LineCap.Round };
        if (glyph == TrayMenuGlyph.Exit)
        { graphics.DrawArc(pen, 3, 3, 12, 12, -50, 280); graphics.DrawLine(pen, 9, 1, 9, 8); }
        else
        {
            graphics.DrawRectangle(pen, 2, 3, 14, 12); graphics.DrawLine(pen, 2, 6, 16, 6);
            if (glyph == TrayMenuGlyph.Panel) { graphics.DrawLine(pen, 5, 9, 13, 9); graphics.DrawLine(pen, 5, 12, 10, 12); }
        }
        graphics.Restore(state);
    }

    private static Drawing2D.GraphicsPath Rounded(Drawing.RectangleF bounds, float radius)
    {
        var path = new Drawing2D.GraphicsPath();
        if (radius <= 0) { path.AddRectangle(bounds); return path; }
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure(); return path;
    }

    private static Drawing.Color Read(Window owner, string key, Drawing.Color fallback, Drawing.Color background)
    {
        if (owner.TryFindResource(key) is not Media.SolidColorBrush brush) return fallback;
        var color = brush.Color;
        var alpha = color.A / 255d * brush.Opacity;
        return Drawing.Color.FromArgb((int)Math.Round(color.R * alpha + background.R * (1 - alpha)),
            (int)Math.Round(color.G * alpha + background.G * (1 - alpha)), (int)Math.Round(color.B * alpha + background.B * (1 - alpha)));
    }
}
