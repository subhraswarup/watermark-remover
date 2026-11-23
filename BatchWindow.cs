namespace WatermarkRemover;

internal sealed class BatchWindow : Form
{
    private const int Capacity = 30;

    private readonly IconButton add = new("Add images", Glyph.Open);
    private readonly IconButton remove = new("Remove selected", Glyph.Trash);
    private readonly IconButton browse = new("Choose folder", Glyph.Open);
    private readonly IconButton start = new("Run batch", Glyph.Sparkles);
    private readonly IconButton stop = new("Stop after current", Glyph.Stop);
    private readonly Label heading = LabelFor("Batch watermark removal");
    private readonly Label intro = LabelFor("Add or drop images. Cleaned PNG copies save automatically.", muted: true);
    private readonly Label count = LabelFor("", muted: true, alignRight: true);
    private readonly Label folderLabel = LabelFor("Save to");
    private readonly Label status = LabelFor("Add images and choose where to save them.", muted: true);
    private readonly TextBox folder = new() { ReadOnly = true, AccessibleName = "Output folder", PlaceholderText = "Choose an output folder", TabStop = false };
    private readonly ProgressBar progress = new() { AccessibleName = "Batch progress", Style = ProgressBarStyle.Continuous };
    private readonly DataGridView queue = new()
    {
        AccessibleName = "Batch image queue", AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, ReadOnly = true, RowHeadersVisible = false, MultiSelect = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BorderStyle = BorderStyle.FixedSingle,
        BackgroundColor = EditorTheme.Panel, GridColor = EditorTheme.Border, EnableHeadersVisualStyles = false,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
    };
    private Font[] fonts = [];
    private int dpi = 96;
    private bool layoutReady;
    private bool running;
    private bool stopRequested;

    private enum State { Queued, Processing, Saved, Skipped, Failed }

    private sealed class Entry(string path, DataGridViewRow row)
    {
        internal readonly string Path = path;
        internal readonly DataGridViewRow Row = row;
        internal State State;
    }

    internal BatchWindow(string[]? incoming = null)
    {
        Text = "Batch watermark removal";
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = EditorTheme.Frame;
        ForeColor = EditorTheme.Text;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Controls.AddRange([heading, intro, add, remove, count, folderLabel, folder, browse, queue, status, progress, stop, start]);

        folder.BackColor = EditorTheme.Panel;
        folder.ForeColor = EditorTheme.Text;
        folder.BorderStyle = BorderStyle.FixedSingle;
        queue.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = EditorTheme.Panel, ForeColor = EditorTheme.Text,
            SelectionBackColor = EditorTheme.DeepAccent, SelectionForeColor = EditorTheme.Text
        };
        queue.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = EditorTheme.Frame, ForeColor = EditorTheme.Muted, SelectionBackColor = EditorTheme.Frame
        };
        queue.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Image", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 45, SortMode = DataGridViewColumnSortMode.NotSortable });
        queue.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", AutoSizeMode = DataGridViewAutoSizeColumnMode.None, SortMode = DataGridViewColumnSortMode.NotSortable });
        queue.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Details", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 55, SortMode = DataGridViewColumnSortMode.NotSortable });
        start.Style(true, emphasized: true);

        add.Click += (_, _) => ChooseImages();
        remove.Click += (_, _) => RemoveSelected();
        browse.Click += (_, _) => ChooseFolder();
        start.Click += async (_, _) => await RunQueue();
        stop.Click += (_, _) => RequestStop();
        queue.SelectionChanged += (_, _) => UpdateControls();
        foreach (var target in new Control[] { this, queue })
        {
            target.AllowDrop = true;
            target.DragEnter += (_, e) => e.Effect = !running && e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 }
                ? DragDropEffects.Copy : DragDropEffects.None;
            target.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AddImages(paths); };
        }

        layoutReady = true;
        ApplyScale(96);
        ClientSize = new Size(780, 568);
        if (incoming is { Length: > 0 }) AddImages(incoming);
        UpdateControls();
    }

    private void ChooseImages()
    {
        using var dialog = new OpenFileDialog { Title = "Add images to the batch", Filter = ImageFiles.OpenFilter, Multiselect = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddImages(dialog.FileNames);
    }

    private void AddImages(IEnumerable<string> paths)
    {
        if (running) return;
        var known = Entries().Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int duplicate = 0, excess = 0, unsupported = 0;
        foreach (string item in paths)
        {
            if (!ImageFiles.HasSupportedExtension(item) || Directory.Exists(item)) { unsupported++; continue; }
            string path = Path.GetFullPath(item);
            if (!known.Add(path)) { duplicate++; continue; }
            if (queue.Rows.Count == Capacity) { excess++; continue; }

            var row = queue.Rows[queue.Rows.Add(Path.GetFileName(path), "Queued", "")];
            row.Height = Px(32);
            row.Cells[0].ToolTipText = path;
            row.Tag = new Entry(path, row);
        }

        var notices = new List<string>();
        if (excess > 0) notices.Add($"{excess} over the {Capacity}-image limit");
        if (duplicate > 0) notices.Add($"{duplicate} duplicate{(duplicate == 1 ? "" : "s")}");
        if (unsupported > 0) notices.Add($"{unsupported} unsupported item{(unsupported == 1 ? "" : "s")}");
        status.Text = notices.Count > 0 ? $"Ignored {string.Join(", ", notices)}." : "Images added to the queue.";
        UpdateControls();
    }

    private void RemoveSelected()
    {
        foreach (var row in queue.SelectedRows.Cast<DataGridViewRow>().ToArray()) queue.Rows.Remove(row);
        status.Text = "Selected entries removed from the queue.";
        UpdateControls();
    }

    private void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose where cleaned PNG copies will be saved", UseDescriptionForTitle = true, SelectedPath = folder.Text };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        folder.Text = dialog.SelectedPath;
        status.Text = "Output folder selected.";
        UpdateControls();
    }

    private async Task RunQueue()
    {
        if (running) return;
        string destination = folder.Text;
        if (!Directory.Exists(destination))
        {
            status.Text = "Choose an output folder that exists.";
            return;
        }

        var pending = Entries().Where(entry => entry.State == State.Queued).ToArray();
        if (pending.Length == 0) return;
        running = true;
        stopRequested = false;
        progress.Value = 0;
        progress.Maximum = pending.Length;
        UpdateControls();
        try
        {
            foreach (var entry in pending)
            {
                if (stopRequested) break;
                status.Text = $"Processing {progress.Value + 1} of {pending.Length}…";
                SetState(entry, State.Processing, "Looking for a watermark…");
                queue.FirstDisplayedScrollingRowIndex = entry.Row.Index;
                var updates = new Progress<string>(message =>
                {
                    if (entry.State == State.Processing) SetState(entry, State.Processing, message);
                });
                var (result, details) = await Task.Run(() => ProcessImage(entry.Path, destination, updates));
                SetState(entry, result, details);
                progress.Value++;
            }
        }
        finally
        {
            running = false;
            UpdateControls();
        }

        var entries = Entries().ToArray();
        string totals = $"{entries.Count(entry => entry.State == State.Saved)} saved, {entries.Count(entry => entry.State == State.Skipped)} skipped, {entries.Count(entry => entry.State == State.Failed)} failed";
        int remaining = entries.Count(entry => entry.State == State.Queued);
        status.Text = remaining > 0 ? $"Stopped. {totals}, {remaining} still queued." : $"Finished. {totals}.";
    }

    private static (State State, string Details) ProcessImage(string path, string destination, IProgress<string> progress)
    {
        try
        {
            using var input = ImageFiles.Load(path);
            var (image, _, _) = LocalAi.Remove(input, progress);
            using var result = image;
            if (result is null) return (State.Skipped, "No watermark confidently detected");
            string stem = Path.GetFileNameWithoutExtension(path);
            if (stem.Length > 200) stem = stem[..(char.IsHighSurrogate(stem[199]) ? 199 : 200)];
            string output = ImageFiles.SaveCopy(result, Path.Combine(destination, $"{stem}-cleaned.png"), keepExisting: true);
            return (State.Saved, output);
        }
        catch (Exception error)
        {
            return (State.Failed, error.Message);
        }
    }

    private void RequestStop()
    {
        if (!running) return;
        stopRequested = true;
        status.Text = "Stopping after the current image finishes…";
        UpdateControls();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (running && e.CloseReason == CloseReason.UserClosing)
        {
            RequestStop();
            e.Cancel = true;
        }
        base.OnFormClosing(e);
    }

    private IEnumerable<Entry> Entries() => queue.Rows.Cast<DataGridViewRow>().Select(row => row.Tag).OfType<Entry>();

    private static void SetState(Entry entry, State state, string details)
    {
        entry.State = state;
        entry.Row.Cells[1].Value = state.ToString();
        entry.Row.Cells[1].Style.ForeColor = state switch
        {
            State.Saved => EditorTheme.Accent,
            State.Skipped => Color.FromArgb(242, 209, 147),
            State.Failed => Color.FromArgb(255, 167, 172),
            _ => EditorTheme.Text
        };
        entry.Row.Cells[2].Value = state == State.Saved ? Path.GetFileName(details) : details;
        entry.Row.Cells[2].ToolTipText = details;
    }

    private void UpdateControls()
    {
        count.Text = $"{queue.Rows.Count} of {Capacity} images";
        add.Enabled = !running && queue.Rows.Count < Capacity;
        remove.Enabled = !running && queue.SelectedRows.Count > 0;
        browse.Enabled = !running;
        start.Enabled = !running && folder.Text.Length > 0 && Entries().Any(entry => entry.State == State.Queued);
        stop.Enabled = running && !stopRequested;
    }

    protected override void OnLoad(EventArgs e)
    {
        if (Owner?.Icon is Icon ownerIcon) Icon = ownerIcon;
        ApplyScale(DeviceDpi);
        var area = Screen.FromControl(this).WorkingArea;
        ClientSize = new Size(Math.Min(Px(780), area.Width - Px(32)), Math.Min(Px(568), area.Height - Px(60)));
        CenterToParent();
        base.OnLoad(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        if (layoutReady) ApplyScale(e.DeviceDpiNew);
        base.OnDpiChanged(e);
        Arrange();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (layoutReady) Arrange();
    }

    private void ApplyScale(int scale)
    {
        dpi = scale;
        var previous = fonts;
        fonts = [new Font("Segoe UI", Px(14), FontStyle.Regular, GraphicsUnit.Pixel),
            new Font("Segoe UI", Px(14), FontStyle.Bold, GraphicsUnit.Pixel),
            new Font("Segoe UI", Px(23), FontStyle.Bold, GraphicsUnit.Pixel)];
        heading.Font = fonts[2];
        foreach (var control in new Control[] { intro, count, folderLabel, folder, status, queue }) control.Font = fonts[0];
        foreach (var button in new[] { add, remove, browse, start, stop })
        {
            button.Font = fonts[1];
            button.ScaleFactor = dpi / 96f;
        }
        queue.Columns[1].Width = Px(105);
        queue.ColumnHeadersHeight = Px(32);
        foreach (DataGridViewRow row in queue.Rows) row.Height = Px(32);
        var area = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(Px(640), area.Width), Math.Min(Px(440), area.Height));
        Arrange();
        foreach (var font in previous) font.Dispose();
    }

    private void Arrange()
    {
        int width = ClientSize.Width, height = ClientSize.Height;
        int left = Px(22), right = width - left;
        int content = Math.Max(0, right - left);
        heading.SetBounds(left, Px(14), content, Px(35));
        intro.SetBounds(left, Px(51), content, Px(25));
        add.SetBounds(left, Px(89), Px(130), Px(38));
        remove.SetBounds(left + Px(140), Px(89), Px(152), Px(38));
        count.SetBounds(right - Px(130), Px(89), Px(130), Px(38));
        folderLabel.SetBounds(left, Px(139), Px(59), Px(38));
        browse.SetBounds(right - Px(145), Px(139), Px(145), Px(38));
        folder.SetBounds(left + Px(66), Px(146), Math.Max(0, content - Px(222)), Px(26));
        queue.SetBounds(left, Px(189), content, Math.Max(0, height - Px(300)));
        status.SetBounds(left, height - Px(102), content, Px(23));
        progress.SetBounds(left, height - Px(71), content, Px(8));
        start.SetBounds(right - Px(126), height - Px(51), Px(126), Px(38));
        stop.SetBounds(start.Left - Px(179), height - Px(51), Px(169), Px(38));
    }

    private static Label LabelFor(string text, bool muted = false, bool alignRight = false) => new()
    {
        Text = text, ForeColor = muted ? EditorTheme.Muted : EditorTheme.Text, UseMnemonic = false, AutoEllipsis = true,
        TextAlign = alignRight ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft
    };

    private int Px(int value) => (int)Math.Round(value * dpi / 96.0);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            foreach (var font in fonts) font.Dispose();
    }
}
