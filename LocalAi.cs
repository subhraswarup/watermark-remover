using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace WatermarkRemover;

internal static class LocalAi
{
    private const int DetectorSize = 640;
    private const int RepairSize = 512;
    private const float Confidence = 0.50f;
    private static string ModelPath(string name) => Path.Combine(AppContext.BaseDirectory, "models", name);
    private static SessionOptions CreateOptions() => new() { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount, 1, 8) };

    internal static (Bitmap? Image, Rectangle[] Regions, PointF[][]? Outlines) Remove(Bitmap original, IProgress<string> progress)
    {
        var gemini = GeminiWatermark.Detect(original);
        using var options = CreateOptions();
        var text = File.Exists(ModelPath("lama_fp32.onnx"))
            ? TextWatermark.Find(original, ModelPath("text-detector.onnx"), ModelPath("text-recognizer.onnx"), options)
            : [];
        if (text.Length > 0) return RemoveText(original, text, gemini, options, progress);

        if (gemini is { } mark)
        {
            progress.Report("Detected a Gemini sparkle. Restoring the pixels underneath…");
            return (GeminiWatermark.Remove(original, mark), [mark.Region], null);
        }

        if (GrokWatermark.FindMask(original) is { } grok)
        {
            using var mask = grok.Mask;
            progress.Report("Detected the Grok wordmark. Repairing its outline locally…");
            return (RepairSelection(original, mask), [grok.Region], null);
        }

        string detectorPath = ModelPath("watermark-detector.onnx");
        string repairPath = ModelPath("lama_fp32.onnx");
        if (!File.Exists(detectorPath) || !File.Exists(repairPath))
            throw new InvalidOperationException("Local AI models are missing. Run Setup-AiModels.ps1 from the project folder and rebuild the app.");

        var bounds = new Rectangle(Point.Empty, original.Size);
        Rectangle[] regions;
        using (var detector = new InferenceSession(detectorPath, options))
        {
            regions = Detect(original, bounds, detector);
            if (regions.Length == 0)
                regions = Detect(original, bounds, detector, mirrored: true);
        }
        if (regions.Length == 0) return (null, [], null);

        progress.Report($"AI found {regions.Length} {(regions.Length == 1 ? "region" : "regions")}. Reconstructing locally…");
        using var repair = new InferenceSession(repairPath, options);
        var output = original.Clone(bounds, PixelFormat.Format32bppArgb);
        try
        {
            foreach (var region in regions) RepairRegion(original, output, region, repair);
            return (output, regions, null);
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static (Bitmap Image, Rectangle[] Regions, PointF[][] Outlines) RemoveText(
        Bitmap original, PointF[][] text, GeminiWatermark.Match? gemini, SessionOptions options, IProgress<string> progress)
    {
        var bounds = new Rectangle(Point.Empty, original.Size);
        bool denseText = text.Length >= TextWatermark.DenseMarkCount;
        if (denseText) text = Array.ConvertAll(text, PadTextOutline);
        var regions = text.Select(polygon => Rectangle.Intersect(bounds, TextWatermark.Bounds(polygon))).ToList();
        var outlines = text.ToList();
        using var mask = new Bitmap(original.Width, original.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(mask))
        {
            foreach (var polygon in text) graphics.FillPolygon(Brushes.White, polygon);
            if (GrokWatermark.FindMask(original) is { } grok)
            {
                using var grokMask = grok.Mask;
                graphics.DrawImageUnscaled(grokMask, Point.Empty);
                AddLogo(grok.Region);
            }
        }

        using var baseImage = gemini is null ? null : GeminiWatermark.Remove(original, gemini);
        if (gemini is not null) AddLogo(gemini.Region);
        Bitmap source = baseImage ?? original;
        Bitmap result = source.Clone(bounds, PixelFormat.Format32bppArgb);
        try
        {
            using var repair = new InferenceSession(ModelPath("lama_fp32.onnx"), options);
            int tileSize = denseText ? 384 : 448;
            int border = (RepairSize - tileSize) / 2;
            int step = 0;
            var tiles = new List<Rectangle>();
            for (int y = 0; y < bounds.Height; y += tileSize)
            for (int x = 0; x < bounds.Width; x += tileSize)
            {
                var tile = Rectangle.Intersect(bounds, new Rectangle(x, y, tileSize, tileSize));
                if (regions.Any(region => region.IntersectsWith(tile))) tiles.Add(tile);
            }

            foreach (var tile in tiles)
            {
                progress.Report($"Removing text watermarks… {++step} of {tiles.Count} areas");
                var crop = tile;
                crop.Inflate(border, border);
                crop.Intersect(bounds);
                RepairRegion(source, result, tile, repair, mask, crop);
            }
            return (result, regions.ToArray(), outlines.ToArray());
        }
        catch
        {
            result.Dispose();
            throw;
        }

        void AddLogo(Rectangle area)
        {
            regions.Add(area);
            outlines.Add([new(area.Left, area.Top), new(area.Right, area.Top), new(area.Right, area.Bottom), new(area.Left, area.Bottom)]);
        }
    }

    private static PointF[] PadTextOutline(PointF[] outline)
    {
        var along = PaddingAlong(outline[0], outline[1], 10);
        var across = PaddingAlong(outline[0], outline[3], 8);
        return [
            new(outline[0].X - along.X - across.X, outline[0].Y - along.Y - across.Y),
            new(outline[1].X + along.X - across.X, outline[1].Y + along.Y - across.Y),
            new(outline[2].X + along.X + across.X, outline[2].Y + along.Y + across.Y),
            new(outline[3].X - along.X + across.X, outline[3].Y - along.Y + across.Y)
        ];

        static PointF PaddingAlong(PointF start, PointF end, float amount)
        {
            float x = end.X - start.X, y = end.Y - start.Y;
            float scale = amount / MathF.Max(1, MathF.Sqrt(x * x + y * y));
            return new(x * scale, y * scale);
        }
    }

    internal static Bitmap RepairSelection(Bitmap original, Bitmap selection)
    {
        string repairPath = ModelPath("lama_fp32.onnx");
        if (!File.Exists(repairPath)) return Inpainting.Repair(original, selection);

        var bounds = new Rectangle(Point.Empty, original.Size);
        var region = Inpainting.GetRepairArea(Inpainting.ReadPixels(selection, bounds), original.Size);
        using var options = CreateOptions();
        using var repair = new InferenceSession(repairPath, options);
        var output = original.Clone(bounds, PixelFormat.Format32bppArgb);
        try
        {
            RepairRegion(original, output, region, repair, selection);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static Rectangle[] Detect(Bitmap original, Rectangle bounds, InferenceSession session, bool mirrored = false)
    {
        var (input, size) = PrepareImage(original, bounds, DetectorSize, mirrored);
        using var results = session.Run([NamedOnnxValue.CreateFromTensor("images", input)]);
        var output = results.Single().AsTensor<float>();
        var shape = output.Dimensions;
        if (shape.Length != 3 || shape[0] != 1 || shape[2] != 6)
            throw new InvalidOperationException("The watermark detector has an unexpected output format.");

        float xScale = (float)bounds.Width / size.Width;
        float yScale = (float)bounds.Height / size.Height;
        var candidates = new List<(Rectangle Bounds, float Score)>();
        for (int i = 0; i < shape[1]; i++)
        {
            float score = output[0, i, 4];
            if (!float.IsFinite(score) || score < Confidence) continue;
            int left = (int)MathF.Floor(Math.Clamp(output[0, i, 0], 0, size.Width) * xScale);
            int top = (int)MathF.Floor(Math.Clamp(output[0, i, 1], 0, size.Height) * yScale);
            int right = (int)MathF.Ceiling(Math.Clamp(output[0, i, 2], 0, size.Width) * xScale);
            int bottom = (int)MathF.Ceiling(Math.Clamp(output[0, i, 3], 0, size.Height) * yScale);
            if (mirrored) (left, right) = (bounds.Width - right, bounds.Width - left);
            var box = Rectangle.Intersect(Rectangle.FromLTRB(left, top, right, bottom), bounds);
            if (box.Width > 0 && box.Height > 0) candidates.Add((box, score));
        }

        var kept = new List<Rectangle>();
        foreach (var (box, _) in candidates.OrderByDescending(candidate => candidate.Score))
        {
            if (kept.Any(existing => Overlap(existing, box) > 0.45f)) continue;
            kept.Add(box);
        }

        var regions = new List<Rectangle>();
        foreach (var detection in kept)
        {
            var region = detection;
            int padding = Math.Max(3, (int)Math.Ceiling(Math.Min(region.Width, region.Height) * 0.08));
            region.Inflate(padding, padding);
            region.Intersect(bounds);
            for (int i = regions.Count - 1; i >= 0; i--)
            {
                if (!regions[i].IntersectsWith(region)) continue;
                region = Rectangle.Union(regions[i], region);
                regions.RemoveAt(i);
                i = regions.Count;
            }
            regions.Add(region);
        }
        return regions.ToArray();
    }

    private static float Overlap(Rectangle first, Rectangle second)
    {
        var shared = Rectangle.Intersect(first, second);
        int area = shared.Width * shared.Height;
        return (float)area / (first.Width * first.Height + second.Width * second.Height - area);
    }

    private static void RepairRegion(Bitmap original, Bitmap repaired, Rectangle region, InferenceSession session, Bitmap? selection = null, Rectangle? context = null)
    {
        int width = Math.Min(original.Width, Math.Max(128, (int)Math.Ceiling(Math.Max(region.Width, region.Height) * 1.6)));
        int height = Math.Min(original.Height, Math.Max(128, (int)Math.Ceiling(Math.Max(region.Width, region.Height) * 1.6)));
        int left = Math.Clamp(region.Left - (width - region.Width) / 2, 0, original.Width - width);
        int top = Math.Clamp(region.Top - (height - region.Height) / 2, 0, original.Height - height);
        var crop = context ?? new Rectangle(left, top, width, height);
        var (image, fit) = PrepareImage(original, crop, RepairSize);
        var mask = new DenseTensor<float>([1, 1, RepairSize, RepairSize]);
        float scaleX = (float)fit.Width / crop.Width, scaleY = (float)fit.Height / crop.Height;
        var maskArea = context ?? region;
        byte[]? selectionPixels = selection is null ? null : Inpainting.ReadPixels(selection, maskArea);
        if (selectionPixels is null)
        {
            Mark(region);
        }
        else
        {
            // Scale contiguous painted pixels together so thin strokes still reach the model.
            for (int y = 0; y < maskArea.Height; y++)
            {
                int x = 0;
                while (x < maskArea.Width)
                {
                    if (selectionPixels[(y * maskArea.Width + x) * 4 + 3] == 0) { x++; continue; }
                    int start = x++;
                    while (x < maskArea.Width && selectionPixels[(y * maskArea.Width + x) * 4 + 3] != 0) x++;
                    Mark(new Rectangle(maskArea.X + start, maskArea.Y + y, x - start, 1));
                }
            }
        }

        using var results = session.Run([
            NamedOnnxValue.CreateFromTensor("image", image), NamedOnnxValue.CreateFromTensor("mask", mask)
        ]);
        var output = results.Single().AsTensor<float>();
        if (!output.Dimensions.SequenceEqual([1, 3, RepairSize, RepairSize]))
            throw new InvalidOperationException("The LaMa model has an unexpected output format.");
        var channels = output.ToArray();
        var pixels = Inpainting.ReadPixels(original, region);
        for (int y = 0; y < region.Height; y++)
        for (int x = 0; x < region.Width; x++)
        {
            int index = (y * region.Width + x) * 4;
            int selected = ((y + region.Y - maskArea.Y) * maskArea.Width + x + region.X - maskArea.X) * 4 + 3;
            if (selectionPixels is not null && selectionPixels[selected] == 0) continue;
            float row = Math.Clamp((y + region.Top - crop.Top + 0.5f) * scaleY - 0.5f, 0, fit.Height - 1);
            float col = Math.Clamp((x + region.Left - crop.Left + 0.5f) * scaleX - 0.5f, 0, fit.Width - 1);
            for (int channel = 0; channel < 3; channel++)
                pixels[index + 2 - channel] = SampleChannel(channels, channel, col, row);
        }
        Inpainting.WritePixels(repaired, region, pixels);

        void Mark(Rectangle box)
        {
            int x0 = (int)MathF.Floor((box.Left - crop.Left) * scaleX);
            int x1 = Math.Min(fit.Width, (int)MathF.Ceiling((box.Right - crop.Left) * scaleX));
            int y0 = (int)MathF.Floor((box.Top - crop.Top) * scaleY);
            int y1 = Math.Min(fit.Height, (int)MathF.Ceiling((box.Bottom - crop.Top) * scaleY));
            for (int y = y0; y < y1; y++) mask.Buffer.Span.Slice(y * RepairSize + x0, x1 - x0).Fill(1);
        }
    }

    private static byte SampleChannel(float[] channels, int channel, float x, float y)
    {
        int left = (int)x, top = (int)y;
        int right = Math.Min(left + 1, RepairSize - 1), bottom = Math.Min(top + 1, RepairSize - 1);
        int plane = channel * RepairSize * RepairSize;
        float above = float.Lerp(channels[plane + top * RepairSize + left], channels[plane + top * RepairSize + right], x - left);
        float below = float.Lerp(channels[plane + bottom * RepairSize + left], channels[plane + bottom * RepairSize + right], x - left);
        return (byte)Math.Clamp(MathF.Round(float.Lerp(above, below, y - top)), 0, 255);
    }

    private static (DenseTensor<float> Tensor, Size Fit) PrepareImage(Bitmap source, Rectangle area, int target, bool mirrored = false)
    {
        float ratio = (float)target / Math.Max(area.Width, area.Height);
        var fit = new Size(Math.Clamp((int)MathF.Round(area.Width * ratio), 1, target), Math.Clamp((int)MathF.Round(area.Height * ratio), 1, target));
        using var resized = new Bitmap(fit.Width, fit.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.Clear(Color.Black);
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(source, new Rectangle(Point.Empty, fit), area.X, area.Y, area.Width, area.Height, GraphicsUnit.Pixel, attributes);
        }

        var pixels = Inpainting.ReadPixels(resized, new Rectangle(Point.Empty, fit));
        var tensor = new DenseTensor<float>([1, 3, target, target]);
        var values = tensor.Buffer.Span;
        int plane = target * target;
        for (int y = 0; y < fit.Height; y++)
        for (int x = 0; x < fit.Width; x++)
        {
            int sourceX = mirrored ? fit.Width - 1 - x : x;
            int pixel = (y * fit.Width + sourceX) * 4;
            int index = y * target + x;
            values[index] = pixels[pixel + 2] / 255f;
            values[plane + index] = pixels[pixel + 1] / 255f;
            values[2 * plane + index] = pixels[pixel] / 255f;
        }
        return (tensor, fit);
    }
}
