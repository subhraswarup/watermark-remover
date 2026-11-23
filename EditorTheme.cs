using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WatermarkRemover;

internal enum Glyph { Image, Open, Upload, Brush, Erase, Trash, Undo, Sparkles, Save, Layers, Eye, Check, Minus, Plus, Fit, Stop }

internal static class EditorTheme
{
    internal static readonly Color Frame = Color.FromArgb(19, 24, 34);
    internal static readonly Color Panel = Color.FromArgb(25, 32, 45);
    internal static readonly Color Workspace = Color.FromArgb(13, 18, 27);
    internal static readonly Color Border = Color.FromArgb(48, 59, 77);
    internal static readonly Color Text = Color.FromArgb(241, 245, 251);
    internal static readonly Color Muted = Color.FromArgb(168, 181, 201);
    internal static readonly Color Accent = Color.FromArgb(132, 233, 197);
    internal static readonly Color DeepAccent = Color.FromArgb(28, 59, 57);

    internal static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static void DrawGlyph(Graphics graphics, Glyph glyph, RectangleF bounds, Color color)
    {
        var state = graphics.Save();
        graphics.TranslateTransform(bounds.X, bounds.Y);
        graphics.ScaleTransform(bounds.Width / 24, bounds.Height / 24);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (glyph)
        {
            case Glyph.Image:
                using (var path = Rounded(new RectangleF(3, 3, 18, 18), 3)) graphics.DrawPath(pen, path);
                graphics.DrawEllipse(pen, 7, 7, 3, 3);
                Lines((4, 17), (9, 12), (12, 15), (16, 11), (20, 15));
                break;
            case Glyph.Open:
                Lines((3, 19), (3, 6), (9, 6), (11, 8), (21, 8), (21, 10));
                Lines((3, 19), (7, 11), (22, 11), (19, 19), (3, 19));
                break;
            case Glyph.Upload:
                Lines((12, 16), (12, 4)); Lines((7, 9), (12, 4), (17, 9));
                Lines((4, 15), (4, 20), (20, 20), (20, 15));
                break;
            case Glyph.Brush:
                Lines((10, 15), (8, 13), (18, 3), (21, 6), (11, 16));
                graphics.DrawBezier(pen, 11, 16, 13, 22, 7, 21, 3, 21);
                graphics.DrawBezier(pen, 3, 21, 6, 19, 4, 14, 8, 14);
                break;
            case Glyph.Erase:
                Lines((3, 14), (13, 4), (20, 11), (11, 20), (8, 20), (3, 15), (3, 14));
                Lines((8, 10), (15, 16)); Lines((11, 20), (22, 20));
                break;
            case Glyph.Trash:
                Lines((4, 7), (20, 7)); Lines((9, 7), (9, 4), (15, 4), (15, 7));
                Lines((6, 7), (7, 20), (17, 20), (18, 7)); Lines((10, 11), (10, 16)); Lines((14, 11), (14, 16));
                break;
            case Glyph.Undo:
                Lines((8, 5), (3, 10), (8, 15));
                graphics.DrawBezier(pen, 3, 10, 10, 10, 21, 7, 21, 15);
                graphics.DrawBezier(pen, 21, 15, 21, 19, 17, 20, 14, 20);
                break;
            case Glyph.Sparkles:
                Lines((10, 3), (12, 9), (18, 11), (12, 13), (10, 19), (8, 13), (2, 11), (8, 9), (10, 3));
                Lines((19, 2), (19, 8)); Lines((16, 5), (22, 5)); Lines((19, 15), (19, 21)); Lines((16, 18), (22, 18));
                break;
            case Glyph.Save:
                Lines((12, 3), (12, 15)); Lines((7, 10), (12, 15), (17, 10));
                Lines((4, 15), (4, 20), (20, 20), (20, 15));
                break;
            case Glyph.Layers:
                Lines((12, 4), (21, 9), (12, 14), (3, 9), (12, 4));
                Lines((3, 13), (12, 18), (21, 13)); Lines((3, 17), (12, 22), (21, 17));
                break;
            case Glyph.Eye:
                graphics.DrawBezier(pen, 2, 12, 8, 3, 16, 3, 22, 12);
                graphics.DrawBezier(pen, 2, 12, 8, 21, 16, 21, 22, 12);
                graphics.DrawEllipse(pen, 9, 9, 6, 6);
                break;
            case Glyph.Check: Lines((5, 12), (10, 17), (19, 7)); break;
            case Glyph.Minus: Lines((5, 12), (19, 12)); break;
            case Glyph.Plus: Lines((5, 12), (19, 12)); Lines((12, 5), (12, 19)); break;
            case Glyph.Fit:
                Lines((4, 9), (4, 4), (9, 4)); Lines((15, 4), (20, 4), (20, 9));
                Lines((20, 15), (20, 20), (15, 20)); Lines((9, 20), (4, 20), (4, 15));
                break;
            case Glyph.Stop:
                using (var path = Rounded(new RectangleF(5, 5, 14, 14), 2)) graphics.DrawPath(pen, path);
                break;
        }
        graphics.Restore(state);

        void Lines(params (float X, float Y)[] points) => graphics.DrawLines(pen, Array.ConvertAll(points, p => new PointF(p.X, p.Y)));
    }

    internal static Icon CreateWindowIcon()
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var background = new SolidBrush(Accent);
            using var shape = Rounded(new RectangleF(1, 1, 30, 30), 7);
            graphics.FillPath(background, shape);
            DrawGlyph(graphics, Glyph.Sparkles, new RectangleF(5, 5, 22, 22), Frame);
        }
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}

internal sealed class IconButton : Button
{
    private readonly Glyph glyph;
    private bool hovered;
    private bool selected;
    private bool primary;
    internal float ScaleFactor = 1;

    internal IconButton(string text, Glyph glyph, string? accessibleName = null)
    {
        Text = text;
        AccessibleName = accessibleName ?? text;
        this.glyph = glyph;
        FlatStyle = FlatStyle.Flat;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    internal void Style(bool active, bool emphasized = false)
    {
        selected = active;
        primary = emphasized;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.Clear(Parent?.BackColor ?? EditorTheme.Panel);
        if (Width < 2 || Height < 2) return;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        bool highlight = Enabled && primary;
        Color fill = highlight ? (hovered ? Color.FromArgb(173, 250, 224) : EditorTheme.Accent)
            : Enabled && selected ? EditorTheme.DeepAccent
            : Enabled && hovered ? EditorTheme.Border : EditorTheme.Panel;
        Color ink = highlight ? EditorTheme.Workspace : Enabled && selected ? EditorTheme.Accent
            : Enabled ? EditorTheme.Text : EditorTheme.Muted;
        using var shape = EditorTheme.Rounded(new RectangleF(0.75f, 0.75f, Width - 1.5f, Height - 1.5f), 8 * ScaleFactor);
        using var brush = new SolidBrush(fill);
        using var border = new Pen(Focused || highlight || (Enabled && selected) ? EditorTheme.Accent : EditorTheme.Border, Math.Max(1, ScaleFactor));
        graphics.FillPath(brush, shape);
        graphics.DrawPath(border, shape);

        const TextFormatFlags textFlags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        int icon = (int)Math.Round(18 * ScaleFactor);
        int gap = (int)Math.Round(8 * ScaleFactor);
        int textWidth = Text.Length == 0 ? 0 : TextRenderer.MeasureText(graphics, Text, Font, Size.Empty, textFlags).Width;
        int left = (Width - icon - (textWidth > 0 ? gap + textWidth : 0)) / 2;
        EditorTheme.DrawGlyph(graphics, glyph, new RectangleF(left, (Height - icon) / 2f, icon, icon), ink);
        if (textWidth > 0)
            TextRenderer.DrawText(graphics, Text, Font, new Rectangle(left + icon + gap, 0, textWidth + 2, Height), ink, textFlags);
    }
}
