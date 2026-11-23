using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace WatermarkRemover;

internal sealed class ImageCanvas : Control
{
    internal static readonly Color MaskColor = Color.FromArgb(130, 133, 88, 255);
    private Bitmap? original;
    private Bitmap? mask;
    private Bitmap? preview;
    private PointF? strokePoint;
    private Point? panStart;
    private Point? pointer;
    private PointF offset;
    private float scale = 1;
    private bool fitted = true;

    public ImageCanvas()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = EditorTheme.Workspace;
        AllowDrop = true;
    }

    public event Action<Rectangle>? MaskChanging;
    public event Action? StrokeEnded;
    public event Action? ZoomChanged;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int BrushSize { get; set; } = 36;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Erasing { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool CanEdit { get; set; } = true;
    public int ZoomPercent => (int)MathF.Round(scale * 100);

    public void LoadImage(Bitmap image, Bitmap selection)
    {
        original = image;
        mask = selection;
        preview = null;
        Fit();
    }

    public void ShowPreview(Bitmap? image)
    {
        preview = image;
        Invalidate();
    }

    internal void ReplaceMask(Bitmap selection)
    {
        mask = selection;
        Invalidate();
    }

    public void Fit()
    {
        fitted = true;
        Refit();
    }

    public void Zoom(float factor) => ZoomAt(factor, new Point(ClientSize.Width / 2, ClientSize.Height / 2));

    internal void EndStroke()
    {
        if (strokePoint is not null) Capture = false;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (fitted) Refit();
        else ClampOffset();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (original is null) return;

        RectangleF imageBounds = GetImageBounds();
        var graphics = e.Graphics;
        using var white = new SolidBrush(Color.White);
        using var gray = new SolidBrush(Color.FromArgb(217, 222, 230));
        using var checker = new HatchBrush(HatchStyle.LargeCheckerBoard, gray.Color, white.Color);
        graphics.FillRectangle(checker, imageBounds);
        graphics.InterpolationMode = scale >= 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(preview ?? original, imageBounds);
        if (preview is null && mask is not null)
            graphics.DrawImage(mask, imageBounds);

        if (CanEdit && preview is null && panStart is null && pointer is Point p && imageBounds.Contains(p.X, p.Y))
        {
            float diameter = BrushSize * scale;
            var circle = new RectangleF(p.X - diameter / 2, p.Y - diameter / 2, diameter, diameter);
            using var outline = new Pen(Color.FromArgb(32, 39, 64), 2.5f);
            using var highlight = new Pen(Color.White, 1);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.DrawEllipse(outline, circle);
            graphics.DrawEllipse(highlight, circle);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (original is null) return;
        Focus();
        pointer = e.Location;
        if (e.Button == MouseButtons.Right)
        {
            panStart = e.Location;
            Capture = true;
            Cursor = Cursors.SizeAll;
            return;
        }
        if (e.Button != MouseButtons.Left || !CanEdit || preview is not null || !GetImageBounds().Contains(e.X, e.Y)) return;
        strokePoint = ToImage(e.Location);
        Capture = true;
        PaintStroke(strokePoint.Value, strokePoint.Value);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        pointer = e.Location;
        if (panStart is Point start)
        {
            fitted = false;
            offset = new PointF(offset.X + e.X - start.X, offset.Y + e.Y - start.Y);
            panStart = e.Location;
            ClampOffset();
        }
        else if (strokePoint is PointF previous && CanEdit)
        {
            PointF next = ToImage(e.Location);
            PaintStroke(previous, next);
            strokePoint = next;
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && strokePoint is PointF previous && CanEdit)
            PaintStroke(previous, ToImage(e.Location));
        if ((e.Button == MouseButtons.Left && strokePoint is not null) ||
            (e.Button == MouseButtons.Right && panStart is not null))
            Capture = false;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (Capture) return;
        bool wasDrawing = strokePoint is not null;
        strokePoint = null;
        panStart = null;
        Cursor = Cursors.Default;
        if (wasDrawing) StrokeEnded?.Invoke();
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        pointer = null;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (strokePoint is null && panStart is null)
            ZoomAt(MathF.Pow(1.2f, e.Delta / 120f), e.Location);
    }

    private void PaintStroke(PointF from, PointF to)
    {
        float radius = BrushSize / 2f;
        int padding = (int)MathF.Ceiling(radius) + 2;
        var bounds = Rectangle.FromLTRB(
            (int)MathF.Floor(Math.Min(from.X, to.X)) - padding,
            (int)MathF.Floor(Math.Min(from.Y, to.Y)) - padding,
            (int)MathF.Ceiling(Math.Max(from.X, to.X)) + padding,
            (int)MathF.Ceiling(Math.Max(from.Y, to.Y)) + padding);
        MaskChanging?.Invoke(bounds);
        using (var graphics = Graphics.FromImage(mask!))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            using var brush = new SolidBrush(Erasing ? Color.Transparent : MaskColor);
            using var pen = new Pen(brush, BrushSize);
            if (from != to) graphics.DrawLine(pen, from, to);
            graphics.FillEllipse(brush, from.X - radius, from.Y - radius, BrushSize, BrushSize);
            graphics.FillEllipse(brush, to.X - radius, to.Y - radius, BrushSize, BrushSize);
        }
        Invalidate();
    }

    private void Refit()
    {
        if (original is null) return;
        scale = Math.Min(Math.Max(1, Width - 48) / (float)original.Width, Math.Max(1, Height - 48) / (float)original.Height);
        scale = Math.Min(4, scale);
        offset = new PointF((Width - original.Width * scale) / 2, (Height - original.Height * scale) / 2);
        ZoomChanged?.Invoke();
        Invalidate();
    }

    private void ZoomAt(float factor, Point anchor)
    {
        if (original is null) return;
        PointF imagePoint = ToImage(anchor);
        scale = Math.Clamp(scale * factor, Math.Min(0.05f, scale), 16f);
        offset = new PointF(anchor.X - imagePoint.X * scale, anchor.Y - imagePoint.Y * scale);
        fitted = false;
        ClampOffset();
        ZoomChanged?.Invoke();
        Invalidate();
    }

    private void ClampOffset()
    {
        if (original is null) return;
        float visibleX = Math.Min(32, original.Width * scale);
        float visibleY = Math.Min(32, original.Height * scale);
        offset.X = Math.Clamp(offset.X, Math.Min(0, visibleX - original.Width * scale), Math.Max(0, Width - visibleX));
        offset.Y = Math.Clamp(offset.Y, Math.Min(0, visibleY - original.Height * scale), Math.Max(0, Height - visibleY));
    }

    private RectangleF GetImageBounds() => new(offset.X, offset.Y, original!.Width * scale, original.Height * scale);

    private PointF ToImage(Point point) => new((point.X - offset.X) / scale, (point.Y - offset.Y) / scale);
}
