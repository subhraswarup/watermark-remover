using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace WatermarkRemover;

internal static class TextWatermark
{
    internal const int DenseMarkCount = 8;

    // PP-OCRv6 small recognition's Latin prefix, following the CTC blank at index zero.
    private const string Alphabet = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz¢£¤¥¦§¨©";

    private sealed record Line(float Cos, float Sin, float Left, float Top, float Right, float Bottom)
    {
        internal float Width => Right - Left;
        internal float Height => Bottom - Top;
        internal PointF[] Outline => [Point(Left, Top), Point(Right, Top), Point(Right, Bottom), Point(Left, Bottom)];
        private PointF Point(float u, float v) => new(u * Cos - v * Sin, u * Sin + v * Cos);
    }

    internal static PointF[][] Find(Bitmap image, string detectorPath, string recognizerPath, SessionOptions options)
    {
        if (!File.Exists(detectorPath) || !File.Exists(recognizerPath)) return [];
        using var detector = new InferenceSession(detectorPath, options);
        using var recognizer = new InferenceSession(recognizerPath, options);
        var marks = new List<PointF[]>();
        var symbols = new List<PointF[]>();
        var tentative = new List<PointF[]>();
        var shortFragments = new List<PointF[]>();
        var extensions = new List<(int Index, PointF[] Outline)>();
        Scan(image, rotated: false, 1792);
        if (marks.Count >= 2)
        {
            using var rotated = (Bitmap)image.Clone();
            rotated.RotateFlip(RotateFlipType.Rotate90FlipNone);
            Scan(rotated, rotated: true, 1792);
        }
        if (marks.Count >= DenseMarkCount)
        {
            Scan(image, rotated: false, 2304);
            using var brightened = BrightenShadows(image);
            Scan(brightened, rotated: false, 1792);
            foreach (var candidate in tentative)
                if (!marks.Any(mark => SharesLine(mark, candidate))) marks.Add(candidate);
            foreach (var candidate in shortFragments)
                if (marks.Any(mark => IsAdjacent(candidate, mark)) && !marks.Any(mark => SharesLine(mark, candidate)))
                    marks.Add(candidate);
            foreach (var (index, outline) in extensions) marks[index] = outline;
        }
        var attached = symbols.Where(symbol => marks.Any(mark => IsAdjacent(symbol, mark)));
        return [.. marks, .. attached];

        void Scan(Bitmap view, bool rotated, int longestSide)
        {
            foreach (var line in Detect(view, detector, longestSide).OrderByDescending(line => line.Width * line.Height).Take(180))
            {
                float extra = Math.Clamp(line.Height / 8, 4, 10);
                PointF[] outline = Project(0, 0);
                if (marks.Any(other => SharesLine(other, outline))) continue;
                string label = Read(view, line, recognizer, false);
                bool forwardMatch = IsWatermark(label);
                string reverse = forwardMatch ? string.Empty : Read(view, line, recognizer, true);
                if (forwardMatch || IsWatermark(reverse))
                {
                    string text = forwardMatch ? label : reverse;
                    float copyrightMargin = !text.Contains('©') && text.Contains("watermark", StringComparison.OrdinalIgnoreCase) ? line.Height * 0.6f : 0;
                    marks.Add(Project(forwardMatch ? copyrightMargin : 0, forwardMatch ? 0 : copyrightMargin));
                    string normalized = Normalize(text);
                    if (copyrightMargin > 0)
                    {
                        float margin = line.Height * 1.25f;
                        if (normalized.StartsWith("mplewatermark", StringComparison.Ordinal)) margin += line.Height * 1.45f;
                        extensions.Add((marks.Count - 1, Project(forwardMatch ? margin : 0, forwardMatch ? 0 : margin)));
                    }
                }
                else if ((label is "©" or "C" or "c" || reverse is "©") && line.Width <= line.Height * 1.9f)
                {
                    var box = Bounds(outline);
                    box.Inflate(Math.Max(3, box.Width / 9), Math.Max(3, box.Height / 9));
                    symbols.Add([new(box.Left, box.Top), new(box.Right, box.Top), new(box.Right, box.Bottom), new(box.Left, box.Bottom)]);
                }
                else if (LooksLikeFragment(label) || LooksLikeFragment(reverse))
                {
                    bool forward = LooksLikeFragment(label);
                    string fragment = Normalize(forward ? label : reverse);
                    float length = fragment.StartsWith("exampl", StringComparison.Ordinal) && fragment.Length is >= 8 and <= 14
                        ? Math.Min(line.Width * 0.95f, (19 - fragment.Length) * line.Height * 0.65f) : 0;
                    tentative.Add(Project(forward ? 0 : length, forward ? length : 0));
                }
                else if (IsShortFragment(label) || IsShortFragment(reverse)) shortFragments.Add(outline);

                PointF[] Project(float left, float right)
                {
                    var polygon = (line with { Left = line.Left - extra - left, Right = line.Right + extra + right,
                        Top = line.Top - extra, Bottom = line.Bottom + extra }).Outline;
                    return rotated ? Array.ConvertAll(polygon, p => new PointF(p.Y, image.Height - p.X)) : polygon;
                }
            }
        }
    }

    internal static Rectangle Bounds(PointF[] outline) => Rectangle.FromLTRB(
        (int)MathF.Floor(outline.Min(p => p.X)), (int)MathF.Floor(outline.Min(p => p.Y)),
        (int)MathF.Ceiling(outline.Max(p => p.X)), (int)MathF.Ceiling(outline.Max(p => p.Y)));

    private static bool SharesLine(PointF[] first, PointF[] second)
    {
        var u = new PointF(first[1].X - first[0].X, first[1].Y - first[0].Y);
        var v = new PointF(second[1].X - second[0].X, second[1].Y - second[0].Y);
        float length = MathF.Sqrt(u.X * u.X + u.Y * u.Y);
        float otherLength = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
        if (MathF.Abs(u.X * v.X + u.Y * v.Y) < 0.97f * length * otherLength) return false;
        float ux = u.X / length, uy = u.Y / length;
        float dx = (second[0].X + second[2].X - first[0].X - first[2].X) / 2;
        float dy = (second[0].Y + second[2].Y - first[0].Y - first[2].Y) / 2;
        float height = Math.Max(Distance(first[0], first[3]), Distance(second[0], second[3]));
        if (MathF.Abs(dx * uy - dy * ux) > height * 0.5f) return false;
        float along = MathF.Abs(dx * ux + dy * uy);
        return along < (length + otherLength) * 0.5f - Math.Min(length, otherLength) * 0.58f;
    }

    private static float Distance(PointF a, PointF b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static bool IsAdjacent(PointF[] symbol, PointF[] line)
    {
        var center = new PointF((symbol[0].X + symbol[2].X) / 2, (symbol[0].Y + symbol[2].Y) / 2);
        var offset = new PointF(line[3].X - line[0].X, line[3].Y - line[0].Y);
        float height = MathF.Sqrt(offset.X * offset.X + offset.Y * offset.Y);
        return Near(line[0]) || Near(line[1]);

        bool Near(PointF end)
        {
            float dx = center.X - end.X - offset.X / 2, dy = center.Y - end.Y - offset.Y / 2;
            return dx * dx + dy * dy <= height * height * 2.7f;
        }
    }

    private static List<Line> Detect(Bitmap view, InferenceSession session, int longestSide)
    {
        float scale = Math.Min(1.5f, (float)longestSide / Math.Max(view.Width, view.Height));
        int w = Math.Max(32, (int)MathF.Round(view.Width * scale / 32) * 32);
        int h = Math.Max(32, (int)MathF.Round(view.Height * scale / 32) * 32);
        using var resized = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(view, new Rectangle(0, 0, w, h));
        }

        using var results = session.Run([NamedOnnxValue.CreateFromTensor(session.InputNames[0], Input(resized, true))]);
        var output = results.Single().AsTensor<float>();
        if (output.Dimensions.Length != 4 || output.Dimensions[0] != 1 || output.Dimensions[1] != 1)
            throw new InvalidOperationException("The text detector returned an unexpected shape.");
        int mapHeight = output.Dimensions[2], mapWidth = output.Dimensions[3];
        var probabilities = output.ToArray();
        var visited = new bool[probabilities.Length];
        var component = new List<int>();
        var lines = new List<Line>();
        float sx = (float)view.Width / mapWidth, sy = (float)view.Height / mapHeight;
        for (int seed = 0; seed < probabilities.Length; seed++)
        {
            if (visited[seed] || probabilities[seed] <= 0.20f) continue;
            component.Clear();
            visited[seed] = true;
            component.Add(seed);
            float confidence = 0;
            for (int i = 0; i < component.Count; i++)
            {
                int at = component[i], x = at % mapWidth, y = at / mapWidth;
                confidence += probabilities[at];
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((uint)(x + dx) >= (uint)mapWidth || (uint)(y + dy) >= (uint)mapHeight) continue;
                    int next = at + dy * mapWidth + dx;
                    if (visited[next] || probabilities[next] <= 0.20f) continue;
                    visited[next] = true;
                    component.Add(next);
                }
            }

            if (component.Count >= 15 && confidence / component.Count >= 0.37f && MakeLine(component, mapWidth, sx, sy) is { } line)
                lines.Add(line);
        }
        return lines;
    }

    private static Line? MakeLine(List<int> component, int stride, float sx, float sy)
    {
        double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0, sumYY = 0;
        foreach (int index in component)
        {
            double x = (index % stride + 0.5) * sx, y = (index / stride + 0.5) * sy;
            sumX += x; sumY += y; sumXX += x * x; sumXY += x * y; sumYY += y * y;
        }
        double n = component.Count;
        float angle = (float)(0.5 * Math.Atan2(2 * (sumXY - sumX * sumY / n), (sumXX - sumX * sumX / n) - (sumYY - sumY * sumY / n)));
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
        foreach (int index in component)
        {
            float x = (index % stride + 0.5f) * sx, y = (index / stride + 0.5f) * sy;
            float u = c * x + s * y, v = -s * x + c * y;
            left = Math.Min(left, u); right = Math.Max(right, u);
            top = Math.Min(top, v); bottom = Math.Max(bottom, v);
        }
        float height = bottom - top;
        if (height < 2 || right - left < Math.Max(8, 0.8f * height)) return null;
        float pad = Math.Max(3, height * 0.40f);
        return new Line(c, s, left - pad, top - pad, right + pad, bottom + pad);
    }

    private static string Read(Bitmap source, Line line, InferenceSession session, bool reverse)
    {
        const int height = 48;
        int width = Math.Clamp((int)MathF.Ceiling(line.Width * height / line.Height), 48, 2048);
        float sx = width / line.Width, sy = height / line.Height;
        using var crop = new Bitmap((width + 15) / 16 * 16, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(crop))
        {
            graphics.Clear(Color.Gray);
            graphics.SetClip(new Rectangle(0, 0, width, height));
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            using var matrix = new Matrix(line.Cos * sx, -line.Sin * sy, line.Sin * sx, line.Cos * sy, -line.Left * sx, -line.Top * sy);
            if (reverse)
            {
                using var flip = new Matrix(-1, 0, 0, -1, width, height);
                matrix.Multiply(flip, MatrixOrder.Append);
            }
            graphics.Transform = matrix;
            graphics.DrawImageUnscaled(source, 0, 0);
        }
        using var results = session.Run([NamedOnnxValue.CreateFromTensor(session.InputNames[0], Input(crop, false))]);
        var tensor = results.Single().AsTensor<float>();
        if (tensor.Dimensions.Length != 3 || tensor.Dimensions[0] != 1)
            throw new InvalidOperationException("The text recognizer returned an unexpected shape.");
        var data = tensor.ToArray();
        int steps = tensor.Dimensions[1], classes = tensor.Dimensions[2], previous = 0;
        var text = new System.Text.StringBuilder();
        for (int j = 0; j < steps; j++)
        {
            int first = j * classes, best = 0;
            for (int i = 1; i < classes; i++)
                if (data[first + i] > data[first + best]) best = i;
            if (best != previous && best > 0 && best <= Alphabet.Length) text.Append(Alphabet[best - 1]);
            previous = best;
        }
        return text.ToString();
    }

    private static bool IsWatermark(string recognized)
    {
        string text = Normalize(recognized);
        return text.Contains("examplewater", StringComparison.Ordinal) || text.Contains("watermarkcom", StringComparison.Ordinal)
            || text.Contains("sampleimage", StringComparison.Ordinal) || text.Contains("previewonly", StringComparison.Ordinal)
            || text.Contains("demoimage", StringComparison.Ordinal) || text.Contains("demoiamge", StringComparison.Ordinal)
            || (text.Contains("watermark", StringComparison.Ordinal) && text.Length <= 24)
            || (text.Length >= 12 && text.EndsWith("com", StringComparison.Ordinal) && Distance(text, "examplewatermarkcom") <= 5)
            || (text.StartsWith("de", StringComparison.Ordinal) && text.Length is >= 8 and <= 11 && Distance(text, "demoimage") <= 3);
    }

    private static bool LooksLikeFragment(string recognized)
    {
        string text = Normalize(recognized);
        return (text.Length >= 8 && text.StartsWith("exampl", StringComparison.Ordinal))
            || (text.Length >= 10 && text.EndsWith("com", StringComparison.Ordinal) && Distance(text[..Math.Min(text.Length, 8)], "demoimag") <= 4);
    }

    private static string Normalize(string value) => new(value.Where(char.IsAsciiLetter).Select(char.ToLowerInvariant).ToArray());

    private static bool IsShortFragment(string text) => text.Trim().ToLowerInvariant() is "exa" or "exam" or "example" or "mark.com" or "rk.com";

    private static Bitmap BrightenShadows(Bitmap image)
    {
        var bounds = new Rectangle(Point.Empty, image.Size);
        byte[] pixels = Inpainting.ReadPixels(image, bounds);
        var curve = new byte[256];
        for (int i = 0; i < curve.Length; i++) curve[i] = (byte)MathF.Round(255 * MathF.Sqrt(i / 255f));
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = curve[pixels[i]];
            pixels[i + 1] = curve[pixels[i + 1]];
            pixels[i + 2] = curve[pixels[i + 2]];
        }
        var brighter = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        try
        {
            Inpainting.WritePixels(brighter, bounds, pixels);
            return brighter;
        }
        catch
        {
            brighter.Dispose();
            throw;
        }
    }

    private static int Distance(string a, string b)
    {
        Span<int> row = stackalloc int[b.Length + 1];
        for (int j = 0; j < row.Length; j++) row[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int diagonal = row[0];
            row[0] = i;
            for (int j = 1; j < row.Length; j++)
            {
                int above = row[j];
                row[j] = Math.Min(Math.Min(above, row[j - 1]) + 1, diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[^1];
    }

    private static DenseTensor<float> Input(Bitmap image, bool detection)
    {
        var bytes = Inpainting.ReadPixels(image, new Rectangle(Point.Empty, image.Size));
        var tensor = new DenseTensor<float>([1, 3, image.Height, image.Width]);
        ReadOnlySpan<float> means = detection ? [0.485f, 0.456f, 0.406f] : [0.5f, 0.5f, 0.5f];
        ReadOnlySpan<float> deviations = detection ? [0.229f, 0.224f, 0.225f] : [0.5f, 0.5f, 0.5f];
        int area = image.Width * image.Height;
        var input = tensor.Buffer.Span;
        for (int c = 0; c < 3; c++)
        for (int i = 0; i < area; i++)
            input[c * area + i] = (bytes[i * 4 + c] / 255f - means[c]) / deviations[c];
        return tensor;
    }
}
