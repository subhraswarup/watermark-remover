using System.Drawing.Drawing2D;

namespace WatermarkRemover;

internal sealed partial class Editor
{
    private readonly Panel header = Surface(EditorTheme.Frame);
    private readonly Panel footer = Surface(EditorTheme.Frame);
    private readonly Panel tools = Surface(EditorTheme.Panel);
    private readonly Panel toolbar = Surface(EditorTheme.Panel);
    private readonly Panel welcome = Surface(EditorTheme.Workspace);
    private readonly Panel card = Surface(EditorTheme.Panel);
    private readonly Panel brand = Surface(EditorTheme.Frame);
    private readonly Panel uploadArt = Surface(EditorTheme.Panel);
    private readonly Panel toolDivider = Surface(EditorTheme.Border);
    private readonly Panel autoDivider = Surface(EditorTheme.Border);
    private readonly Label title = Caption("Watermark Remover");
    private readonly Label welcomeTitle = Caption("Open an image to start", centered: true);
    private readonly Label welcomeHelp = Caption("Drag an image here or choose a file", muted: true, centered: true);
    private readonly Label formats = Caption("PNG, JPG or BMP  ·  Up to 16 megapixels", muted: true, centered: true);
    private readonly Label workflow = Caption("Open image   →   Use AI or brush over the mark   →   Save copy", muted: true, centered: true);
    private readonly Label toolsTitle = Caption("Mark the watermark");
    private readonly Label autoHelp = Caption("Detect and remove watermarks on this device.", muted: true);
    private readonly Label toolsHelp = Caption("Paint over the watermark. Use Erase to refine the selection.", muted: true);
    private readonly Label stepHelp = Caption("Compare Original and Result before saving.", muted: true);
    private readonly Label shortcuts = Caption("Scroll to zoom · Right-drag to pan", muted: true);
    private readonly IconButton choose = new("Choose image", Glyph.Open);
    private readonly ToolTip tips = new() { ShowAlways = true };
    private Font[] fonts = [];
    private Icon? windowIcon;
    private int layoutDpi = 96;
    private bool layoutReady;

    private void BuildLayout()
    {
        AutoScaleMode = AutoScaleMode.None;
        BackColor = EditorTheme.Workspace;
        ForeColor = EditorTheme.Text;
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        windowIcon = EditorTheme.CreateWindowIcon();
        Icon = windowIcon;

        Controls.AddRange([header, footer, tools, toolbar, canvas, welcome]);
        header.Controls.AddRange([brand, title, batch, undo, open, save]);
        footer.Controls.AddRange([status, fileInfo]);
        tools.Controls.AddRange([autoRemove, autoHelp, autoDivider, toolsTitle, toolsHelp, brush, eraser, sizeLabel, brushSize, clear, toolDivider, repair, stepHelp, shortcuts]);
        toolbar.Controls.AddRange([selection, original, result, zoomOut, zoomLabel, zoomIn, fit]);
        welcome.Controls.AddRange([card, workflow]);
        card.Controls.AddRange([uploadArt, welcomeTitle, welcomeHelp, choose, formats]);
        tools.AutoScroll = true;
        brushSize.BackColor = EditorTheme.Panel;
        fileInfo.TextAlign = ContentAlignment.MiddleRight;
        zoomLabel.TextAlign = ContentAlignment.MiddleCenter;
        autoHelp.TextAlign = toolsHelp.TextAlign = stepHelp.TextAlign = ContentAlignment.TopLeft;
        choose.Click += (_, _) => OpenImage();
        choose.Style(true, emphasized: true);

        foreach (Control surface in new Control[] { welcome, card, uploadArt, welcomeTitle, welcomeHelp, formats, workflow, choose })
        {
            surface.AllowDrop = true;
            surface.DragEnter += OnFileDrag;
            surface.DragDrop += OnFileDrop;
        }

        welcome.Paint += PaintWelcome;
        card.Paint += (_, e) => DrawCardBorder(e.Graphics, card.ClientRectangle);
        brand.Paint += (_, e) => DrawTile(e.Graphics, brand.ClientRectangle, Glyph.Sparkles, EditorTheme.Accent, EditorTheme.Workspace, Px(9));
        uploadArt.Paint += (_, e) => DrawTile(e.Graphics, uploadArt.ClientRectangle, Glyph.Upload, EditorTheme.DeepAccent, EditorTheme.Accent, Px(17));
        tips.SetToolTip(open, "Open image (Ctrl+O)");
        tips.SetToolTip(batch, "Queue up to 30 images for automatic removal");
        tips.SetToolTip(save, "Save cleaned copy (Ctrl+S)");
        tips.SetToolTip(undo, "Undo the last edit (Ctrl+Z)");
        tips.SetToolTip(zoomOut, "Zoom out");
        tips.SetToolTip(zoomIn, "Zoom in");
        tips.SetToolTip(fit, "Fit image to the canvas");
        layoutReady = true;
        ConfigureUiScale(96);
        ClientSize = new Size(1150, 750);
    }

    protected override void OnLoad(EventArgs e)
    {
        ConfigureUiScale(DeviceDpi);
        var area = Screen.FromControl(this).WorkingArea;
        ClientSize = new Size(Math.Min(Px(1150), area.Width - Px(36)), Math.Min(Px(750), area.Height - Px(66)));
        CenterToScreen();
        base.OnLoad(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        if (layoutReady) ConfigureUiScale(e.DeviceDpiNew);
        base.OnDpiChanged(e);
        ArrangeEditor();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (layoutReady) ArrangeEditor();
    }

    private void ConfigureUiScale(int dpi)
    {
        if (dpi == layoutDpi && fonts.Length > 0) return;
        layoutDpi = dpi;
        var previous = fonts;
        fonts = [MakeFont(14), MakeFont(14, true), MakeFont(21, true), MakeFont(33, true), MakeFont(12)];
        foreach (var button in new[] { open, batch, save, undo, choose, autoRemove, brush, eraser, clear, repair, selection, original, result, zoomOut, zoomIn, fit })
        {
            button.ScaleFactor = dpi / 96f;
            button.Font = fonts[1];
        }
        foreach (var label in new[] { welcomeHelp, autoHelp, toolsHelp, stepHelp, sizeLabel, zoomLabel }) label.Font = fonts[0];
        foreach (var label in new[] { formats, workflow, shortcuts, status, fileInfo }) label.Font = fonts[4];
        title.Font = toolsTitle.Font = fonts[2];
        welcomeTitle.Font = fonts[3];
        var area = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(Px(850), area.Width), Math.Min(Px(565), area.Height));
        ArrangeEditor();
        foreach (var font in previous) font.Dispose();
    }

    private void ArrangeEditor()
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        if (w < 1 || h < 1) return;
        int padding = Px(24), head = Px(74), foot = Px(35);
        int middle = Math.Max(0, h - head - foot);
        header.SetBounds(0, 0, w, head);
        footer.SetBounds(0, h - foot, w, foot);
        welcome.SetBounds(0, head, w, middle);
        Place(brand, 24, 18, 38, 38);
        int saveWidth = layoutHasImage ? Px(130) : 0;
        int openRight = w - padding - (saveWidth > 0 ? saveWidth + Px(10) : 0);
        save.SetBounds(w - padding - Px(130), Px(17), Px(130), Px(40));
        open.SetBounds(openRight - Px(132), Px(17), Px(132), Px(40));
        undo.SetBounds(open.Left - Px(106), Px(17), Px(96), Px(40));
        int batchRight = (layoutHasImage ? undo.Left : open.Left) - Px(10);
        batch.SetBounds(batchRight - Px(94), Px(17), Px(94), Px(40));
        title.SetBounds(Px(74), Px(15), Math.Max(0, batch.Left - Px(88)), Px(44));

        int fileWidth = layoutHasImage ? Math.Min(Px(350), w / 3) : 0;
        status.SetBounds(padding, 0, Math.Max(0, w - 2 * padding - fileWidth), foot);
        fileInfo.SetBounds(w - padding - fileWidth, 0, fileWidth, foot);
        int cardWidth = Math.Max(0, Math.Min(Px(524), w - Px(48)));
        int cardHeight = Px(302);
        card.SetBounds((w - cardWidth) / 2, Math.Max(Px(12), (middle - cardHeight - Px(46)) / 2), cardWidth, cardHeight);
        uploadArt.SetBounds((cardWidth - Px(70)) / 2, Px(30), Px(70), Px(70));
        CenterLine(welcomeTitle, cardWidth, 113, 48);
        CenterLine(welcomeHelp, cardWidth, 162, 27);
        choose.SetBounds((cardWidth - Px(172)) / 2, Px(204), Px(172), Px(46));
        CenterLine(formats, cardWidth, 260, 24);
        workflow.SetBounds((w - cardWidth) / 2, card.Bottom + Px(16), cardWidth, Px(30));

        int sidebar = Px(w >= Px(830) ? 278 : 246);
        int workWidth = Math.Max(0, w - sidebar);
        bool twoRows = workWidth < Px(545);
        int barHeight = Px(twoRows ? 96 : 56);
        toolbar.SetBounds(sidebar, head, workWidth, barHeight);
        canvas.SetBounds(sidebar, head + barHeight, workWidth, Math.Max(0, middle - barHeight));
        tools.SetBounds(0, head, sidebar, middle);
        // Align all children from the logical origin even when the tool panel is scrolled.
        const int toolHeight = 493;
        tools.AutoScrollMinSize = new Size(0, Px(toolHeight));
        int scroll = tools.AutoScrollPosition.Y;
        int gutter = middle < Px(toolHeight) ? Math.Max(Px(18), sidebar - tools.ClientSize.Width) : 0;
        int toolWidth = sidebar - Px(43) - gutter;
        void Tool(Control item, int y, int height) => item.SetBounds(Px(21), Px(y) + scroll, toolWidth, Px(height));
        Tool(autoRemove, 19, 42);
        Tool(autoHelp, 72, 41);
        Tool(autoDivider, 119, 1);
        Tool(toolsTitle, 132, 32);
        Tool(toolsHelp, 166, 42);
        int half = (toolWidth - Px(8)) / 2;
        brush.SetBounds(Px(21), Px(217) + scroll, half, Px(42));
        eraser.SetBounds(Px(29) + half, Px(217) + scroll, toolWidth - half - Px(8), Px(42));
        Tool(sizeLabel, 270, 25);
        Tool(brushSize, 296, 32);
        Tool(clear, 343, 36);
        Tool(toolDivider, 392, 1);
        Tool(repair, 405, 42);
        Tool(stepHelp, 456, 36);
        shortcuts.Visible = middle >= Px(530);
        shortcuts.SetBounds(Px(21), Math.Max(0, middle - Px(37)) + scroll, toolWidth, Px(22));

        Place(selection, 12, 9, 108, 38);
        Place(original, 126, 9, 100, 38);
        Place(result, 232, 9, 89, 38);
        int zoomX = Math.Max(Px(12), workWidth - Px(202));
        int zoomY = twoRows ? 49 : 9;
        zoomOut.SetBounds(zoomX, Px(zoomY), Px(36), Px(38));
        zoomLabel.SetBounds(zoomX + Px(37), Px(zoomY), Px(49), Px(38));
        zoomIn.SetBounds(zoomX + Px(87), Px(zoomY), Px(36), Px(38));
        fit.SetBounds(zoomX + Px(129), Px(zoomY), Px(61), Px(38));
    }

    private void ShowEditorSurface(bool loaded)
    {
        // Control.Visible includes ancestor visibility. Use the document state before the form is shown.
        bool changed = layoutHasImage != loaded;
        layoutHasImage = loaded;
        canvas.Visible = tools.Visible = toolbar.Visible = fileInfo.Visible = save.Visible = undo.Visible = loaded;
        welcome.Visible = !loaded;
        open.Style(!loaded, emphasized: !loaded);
        if (changed) ArrangeEditor();
    }

    private bool layoutHasImage;

    private void PaintWelcome(object? sender, PaintEventArgs e)
    {
        if (welcome.Width < 1 || welcome.Height < 1) return;
        using var gradient = new LinearGradientBrush(welcome.ClientRectangle, Color.FromArgb(24, 35, 48), EditorTheme.Workspace, 80f);
        e.Graphics.FillRectangle(gradient, welcome.ClientRectangle);
        using var dot = new SolidBrush(Color.FromArgb(53, 67, 84));
        int grid = Px(24);
        for (int y = grid / 2; y < welcome.Height; y += grid)
            for (int x = grid / 2; x < welcome.Width; x += grid)
                e.Graphics.FillRectangle(dot, x, y, Px(1), Px(1));
    }

    private void DrawCardBorder(Graphics graphics, Rectangle bounds)
    {
        if (bounds.Width < 2 || bounds.Height < 2) return;
        using var border = new Pen(EditorTheme.Border, Math.Max(1, Px(1)));
        graphics.DrawRectangle(border, 0, 0, bounds.Width - 1, bounds.Height - 1);
    }

    private static void DrawTile(Graphics graphics, Rectangle bounds, Glyph glyph, Color fill, Color ink, int inset)
    {
        if (bounds.Width < 2 * inset || bounds.Height < 2 * inset) return;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = EditorTheme.Rounded(bounds, inset * 0.85f);
        using var brush = new SolidBrush(fill);
        graphics.FillPath(brush, shape);
        bounds.Inflate(-inset, -inset);
        EditorTheme.DrawGlyph(graphics, glyph, bounds, ink);
    }

    private void Place(Control control, int x, int y, int width, int height) => control.SetBounds(Px(x), Px(y), Px(width), Px(height));
    private void CenterLine(Control control, int width, int y, int height) => control.SetBounds(Px(14), Px(y), Math.Max(0, width - Px(28)), Px(height));
    private Font MakeFont(int size, bool bold = false) => new("Segoe UI", Px(size), bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
    private int Px(int units) => (int)Math.Round(units * layoutDpi / 96.0);

    private static Panel Surface(Color color) => new() { BackColor = color, Margin = Padding.Empty };

    private static Label Caption(string text, bool muted = false, bool centered = false) => new()
    {
        Text = text, ForeColor = muted ? EditorTheme.Muted : EditorTheme.Text, AutoEllipsis = true,
        TextAlign = centered ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft,
        BackColor = Color.Transparent, UseMnemonic = false, Margin = Padding.Empty
    };
}
