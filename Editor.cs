using System.Drawing.Imaging;

namespace WatermarkRemover;

internal sealed partial class Editor : Form
{
    private readonly ImageCanvas canvas = new();
    private readonly IconButton open = new("Open image", Glyph.Open);
    private readonly IconButton batch = new("Batch", Glyph.Layers);
    private readonly IconButton save = new("Save copy", Glyph.Save);
    private readonly IconButton undo = new("Undo", Glyph.Undo);
    private readonly IconButton autoRemove = new("AI auto-remove", Glyph.Sparkles);
    private readonly IconButton brush = new("Brush", Glyph.Brush);
    private readonly IconButton eraser = new("Erase", Glyph.Erase);
    private readonly IconButton clear = new("Clear selection", Glyph.Trash);
    private readonly IconButton repair = new("Remove watermark", Glyph.Sparkles);
    private readonly IconButton selection = new("Selection", Glyph.Layers);
    private readonly IconButton original = new("Original", Glyph.Eye);
    private readonly IconButton result = new("Result", Glyph.Check);
    private readonly IconButton zoomOut = new("", Glyph.Minus, "Zoom out");
    private readonly IconButton zoomIn = new("", Glyph.Plus, "Zoom in");
    private readonly IconButton fit = new("Fit", Glyph.Fit, "Fit image");
    private readonly TrackBar brushSize = new() { Minimum = 2, Maximum = 200, Value = 36, TickStyle = TickStyle.None, AutoSize = false, AccessibleName = "Brush size" };
    private readonly Label sizeLabel = Caption("Brush size   36 px");
    private readonly Label status = Caption("Images are processed on your device.", muted: true);
    private readonly Label fileInfo = Caption("", muted: true);
    private readonly Label zoomLabel = Caption("", muted: true, centered: true);
    private Bitmap? sourceImage;
    private Bitmap? selectionMask;
    private Bitmap? resultImage;
    private string? sourcePath;
    private ViewMode currentView = ViewMode.Selection;
    private bool busy;
    private bool maskEdited;
    private bool unsaved;
    private bool automaticRepair;
    private readonly LinkedList<UndoEntry> history = new();
    private long historyBytes;
    private UndoEntry? currentStroke;

    private enum ViewMode { Selection, Original, Result }

    public Editor()
    {
        Text = "Watermark Remover";
        BuildLayout();

        open.Click += (_, _) => OpenImage();
        batch.Click += (_, _) => OpenBatch();
        save.Click += (_, _) => SaveImage();
        undo.Click += (_, _) => Undo();
        autoRemove.Click += async (_, _) => await RepairImage(automatic: true);
        brush.Click += (_, _) => ChooseBrush(false);
        eraser.Click += (_, _) => ChooseBrush(true);
        clear.Click += (_, _) => ClearSelection();
        repair.Click += async (_, _) => await RepairImage();
        selection.Click += (_, _) => ShowView(ViewMode.Selection);
        original.Click += (_, _) => ShowView(ViewMode.Original);
        result.Click += (_, _) => ShowView(ViewMode.Result);
        zoomOut.Click += (_, _) => canvas.Zoom(1 / 1.2f);
        zoomIn.Click += (_, _) => canvas.Zoom(1.2f);
        fit.Click += (_, _) => canvas.Fit();
        brushSize.ValueChanged += (_, _) =>
        {
            canvas.BrushSize = brushSize.Value;
            sizeLabel.Text = $"Brush size   {brushSize.Value} px";
            canvas.Invalidate();
        };
        canvas.MaskChanging += CaptureStroke;
        canvas.StrokeEnded += CommitStroke;
        canvas.ZoomChanged += () => zoomLabel.Text = $"{canvas.ZoomPercent}%";
        DragEnter += OnFileDrag;
        DragDrop += OnFileDrop;
        canvas.DragEnter += OnFileDrag;
        canvas.DragDrop += OnFileDrop;
        UpdateControls();
    }

    private void OpenImage()
    {
        using var dialog = new OpenFileDialog { Filter = ImageFiles.OpenFilter, Title = "Open image" };
        if (dialog.ShowDialog(this) == DialogResult.OK) LoadImage(dialog.FileName);
    }

    private void OpenBatch(string[]? files = null)
    {
        if (busy) return;
        canvas.EndStroke();
        using var window = new BatchWindow(files);
        window.ShowDialog(this);
    }

    private void LoadImage(string path)
    {
        if (busy || !ConfirmDiscard()) return;
        Bitmap? newSource = null, newMask = null;
        try
        {
            newSource = ImageFiles.Load(path);
            newMask = new Bitmap(newSource.Width, newSource.Height, PixelFormat.Format32bppArgb);
        }
        catch (Exception error)
        {
            newSource?.Dispose();
            newMask?.Dispose();
            ShowError("Could not open image", error);
            return;
        }

        DisposeResult();
        ResetUndo();
        sourceImage?.Dispose();
        selectionMask?.Dispose();
        sourceImage = newSource;
        selectionMask = newMask;
        sourcePath = Path.GetFullPath(path);
        canvas.LoadImage(newSource, newMask);
        maskEdited = unsaved = false;
        canvas.Erasing = false;
        fileInfo.Text = $"{Path.GetFileName(path)}   ·   {newSource.Width} × {newSource.Height}";
        status.Text = "Use AI auto-remove or brush over the watermark yourself.";
        ShowView(ViewMode.Selection);
    }


    private void ChooseBrush(bool erase)
    {
        canvas.Erasing = erase;
        ShowView(ViewMode.Selection);
    }

    private void ClearSelection()
    {
        if (selectionMask is null) return;
        canvas.EndStroke();
        var entry = CaptureEdit("clear selection", null);
        ReplaceSelection(new Bitmap(selectionMask.Width, selectionMask.Height, PixelFormat.Format32bppArgb), entry);
        resultImage = null;
        AddUndo(entry);
        maskEdited = unsaved = false;
        status.Text = "Selection cleared.";
        ShowView(ViewMode.Selection);
    }

    private async Task RepairImage(bool automatic = false)
    {
        if (busy || sourceImage is null || selectionMask is null || (!automatic && !maskEdited)) return;
        canvas.EndStroke();
        busy = true;
        automaticRepair = automatic;
        status.Text = automatic ? "Looking for watermarks on this device…" : "Removing watermark…";
        UpdateControls();
        try
        {
            var bounds = new Rectangle(0, 0, sourceImage.Width, sourceImage.Height);
            using var sourceSnapshot = sourceImage.Clone(bounds, PixelFormat.Format32bppArgb);
            using var maskSnapshot = automatic ? null : selectionMask.Clone(bounds, PixelFormat.Format32bppArgb);
            var progress = new Progress<string>(message => { if (busy) status.Text = message; });
            var (repaired, regions, outlines) = await Task.Run(() => automatic
                ? LocalAi.Remove(sourceSnapshot, progress)
                : (LocalAi.RepairSelection(sourceSnapshot, maskSnapshot!), Array.Empty<Rectangle>(), null));
            if (repaired is null)
            {
                status.Text = "AI found no confident match. Brush over the watermark to remove it manually.";
                return;
            }

            try
            {
                var entry = CaptureEdit(automatic ? "AI removal" : "removal", null);
                if (automatic)
                {
                    ReplaceSelection(CreateAiMask(bounds.Size, regions, outlines), entry);
                    maskEdited = true;
                }
                AddUndo(entry);
            }
            catch
            {
                repaired.Dispose();
                throw;
            }
            resultImage = repaired;
            unsaved = true;
            status.Text = "Compare with Original, then select Save copy.";
            ShowView(ViewMode.Result);
        }
        catch (InvalidOperationException error)
        {
            status.Text = error.Message;
        }
        catch (Exception error)
        {
            status.Text = "Could not repair the selection.";
            ShowError("Could not remove watermark", error);
        }
        finally
        {
            busy = false;
            UpdateControls();
        }
    }

    private static Bitmap CreateAiMask(Size size, Rectangle[] regions, PointF[][]? outlines)
    {
        var mask = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(mask);
            using var mark = new SolidBrush(ImageCanvas.MaskColor);
            if (outlines is null) graphics.FillRectangles(mark, regions);
            else foreach (var outline in outlines) graphics.FillPolygon(mark, outline);
            return mask;
        }
        catch
        {
            mask.Dispose();
            throw;
        }
    }

    private void ReplaceSelection(Bitmap replacement, UndoEntry entry)
    {
        entry.FullMask = selectionMask;
        selectionMask = replacement;
        canvas.ReplaceMask(replacement);
    }

    private void CaptureStroke(Rectangle dirtyArea)
    {
        if (selectionMask is null) return;
        if (currentStroke is null)
        {
            currentStroke = CaptureEdit(canvas.Erasing ? "erase stroke" : "brush stroke", dirtyArea);
            resultImage = null;
            maskEdited = unsaved = true;
            status.Text = "Select Remove watermark to preview the repair.";
            UpdateControls();
        }
        else
        {
            currentStroke.Mask.Capture(selectionMask, dirtyArea);
        }
    }

    private void CommitStroke()
    {
        if (currentStroke is null) return;
        AddUndo(currentStroke);
        currentStroke = null;
        UpdateControls();
    }

    private UndoEntry CaptureEdit(string action, Rectangle? area)
    {
        var entry = new UndoEntry(action, resultImage, currentView, maskEdited, unsaved);
        if (area is Rectangle bounds) entry.Mask.Capture(selectionMask!, bounds);
        return entry;
    }

    private void AddUndo(UndoEntry entry)
    {
        const long budget = 128L * 1024 * 1024;
        history.AddLast(entry);
        historyBytes += entry.Bytes;
        while (historyBytes > budget && history.Count > 1)
        {
            var oldest = history.First!.Value;
            history.RemoveFirst();
            historyBytes -= oldest.Bytes;
            oldest.Dispose();
        }
    }

    private void Undo()
    {
        if (busy || selectionMask is null) return;
        canvas.EndStroke();
        if (history.Last?.Value is not UndoEntry entry) return;
        if (entry.FullMask is null) entry.Mask.Restore(selectionMask);
        history.RemoveLast();
        historyBytes -= entry.Bytes;
        if (entry.FullMask is Bitmap previousMask)
        {
            var oldMask = selectionMask;
            selectionMask = previousMask;
            entry.FullMask = null;
            canvas.ReplaceMask(previousMask);
            oldMask.Dispose();
        }
        DisposeResult();
        resultImage = entry.Result;
        entry.Result = null;
        maskEdited = entry.HadMask;
        unsaved = entry.Unsaved;
        status.Text = $"Undid {entry.Action}.";
        ShowView(entry.View);
        entry.Dispose();
    }

    private void ResetUndo()
    {
        currentStroke?.Dispose();
        currentStroke = null;
        foreach (var entry in history) entry.Dispose();
        history.Clear();
        historyBytes = 0;
    }

    private sealed class UndoEntry(string action, Bitmap? result, ViewMode view, bool hadMask, bool unsaved) : IDisposable
    {
        internal readonly MaskEdit Mask = new();
        internal readonly string Action = action;
        internal readonly ViewMode View = view;
        internal readonly bool HadMask = hadMask;
        internal readonly bool Unsaved = unsaved;
        internal Bitmap? Result = result;
        internal Bitmap? FullMask;
        internal long Bytes => Mask.Bytes + BitmapBytes(Result) + BitmapBytes(FullMask);

        private static long BitmapBytes(Bitmap? bitmap) => bitmap is null ? 0L : (long)bitmap.Width * bitmap.Height * 4;

        public void Dispose()
        {
            Result?.Dispose();
            FullMask?.Dispose();
        }
    }

    private void SaveImage()
    {
        if (busy || resultImage is null || sourcePath is null) return;
        using var dialog = new SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg",
            FileName = $"{Path.GetFileNameWithoutExtension(sourcePath)}-cleaned",
            InitialDirectory = Path.GetDirectoryName(sourcePath),
            Title = "Save cleaned copy"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            string path = Path.GetFullPath(dialog.FileName);
            if (string.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Choose a different filename to keep the original image.");
            ImageFiles.SaveCopy(resultImage, path);
            unsaved = false;
            status.Text = $"Saved {Path.GetFileName(path)}";
        }
        catch (Exception error)
        {
            ShowError("Could not save copy", error);
        }
    }


    private void DisposeResult()
    {
        resultImage?.Dispose();
        resultImage = null;
    }

    private void ShowView(ViewMode view)
    {
        currentView = view;
        canvas.ShowPreview(view switch { ViewMode.Original => sourceImage, ViewMode.Result => resultImage, _ => null });
        UpdateControls();
    }

    private void UpdateControls()
    {
        bool loaded = sourceImage is not null;
        open.Enabled = batch.Enabled = !busy;
        undo.Enabled = !busy && history.Count > 0;
        autoRemove.Enabled = loaded && !busy;
        brush.Enabled = eraser.Enabled = brushSize.Enabled = loaded && !busy;
        clear.Enabled = repair.Enabled = loaded && maskEdited && !busy;
        selection.Enabled = original.Enabled = loaded && !busy;
        save.Enabled = result.Enabled = resultImage is not null && !busy;
        zoomIn.Enabled = zoomOut.Enabled = fit.Enabled = loaded;
        canvas.CanEdit = loaded && !busy && currentView == ViewMode.Selection;
        repair.Text = busy && !automaticRepair ? "Removing…" : "Remove watermark";
        autoRemove.Text = busy && automaticRepair ? "AI processing…" : "AI auto-remove";
        autoRemove.Style(autoRemove.Enabled, emphasized: !maskEdited && resultImage is null);
        repair.Style(repair.Enabled, emphasized: repair.Enabled && resultImage is null);
        save.Style(save.Enabled, emphasized: save.Enabled);
        brush.Style(brush.Enabled && !canvas.Erasing);
        eraser.Style(eraser.Enabled && canvas.Erasing);
        selection.Style(selection.Enabled && currentView == ViewMode.Selection);
        original.Style(original.Enabled && currentView == ViewMode.Original);
        result.Style(result.Enabled && currentView == ViewMode.Result);
        ShowEditorSurface(loaded);
    }

    private void OnFileDrag(object? sender, DragEventArgs e) => e.Effect = !busy && e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        if (files.Length == 1) LoadImage(files[0]);
        else OpenBatch(files);
    }

    private bool ConfirmDiscard() => !unsaved || MessageBox.Show(this, "Discard the current selection and unsaved result?", "Discard edits", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private void ShowError(string title, Exception error) => MessageBox.Show(this, error.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Z) && (undo.Enabled || currentStroke is not null)) { Undo(); return true; }
        if (keyData == (Keys.Control | Keys.O) && open.Enabled) { OpenImage(); return true; }
        if (keyData == (Keys.Control | Keys.S) && save.Enabled) { SaveImage(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && (busy || !ConfirmDiscard())) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            sourceImage?.Dispose();
            selectionMask?.Dispose();
            DisposeResult();
            ResetUndo();
        }
        base.Dispose(disposing);
        if (disposing)
        {
            foreach (var font in fonts) font.Dispose();
            windowIcon?.Dispose();
            tips.Dispose();
        }
    }

}
