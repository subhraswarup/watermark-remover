using System.Drawing.Imaging;

namespace WatermarkRemover;

internal static class GrokWatermark
{
    private const int ReferenceWidth = 152;
    private const int ReferenceHeight = 57;
    private static readonly Lazy<byte[]> Reference = new(LoadReference);

    internal static (Bitmap Mask, Rectangle Region)? FindMask(Bitmap source)
    {
        var reference = Reference.Value;
        if (reference.Length == 0) return null;
        int shortSide = Math.Min(source.Width, source.Height);
        int longSide = Math.Max(source.Width, source.Height);
        // Try resolution-scaled exports and exports that keep the logo at a fixed pixel size.
        int[] widths = new[] { ReferenceWidth * shortSide / 1008, ReferenceWidth * longSide / 1792, ReferenceWidth }
            .SelectMany(width => new[] { width, width - 3, width + 3 })
            .Where(width => width >= 45 && width <= 600 && width < source.Width).Distinct().ToArray();
        if (widths.Length == 0) return null;

        int largest = widths.Max();
        int scanWidth = Math.Min(source.Width, largest + Inset(largest, 8) + Tolerance(largest));
        int scanHeight = Math.Min(source.Height, Inset(largest, ReferenceHeight + 10) + Tolerance(largest) + 1);
        var corner = new Rectangle(source.Width - scanWidth, source.Height - scanHeight, scanWidth, scanHeight);
        var pixels = Inpainting.ReadPixels(source, corner);
        var luma = new float[scanWidth * scanHeight];
        for (int i = 0; i < luma.Length; i++)
            luma[i] = (pixels[4 * i + 2] * 0.2126f + pixels[4 * i + 1] * 0.7152f + pixels[4 * i] * 0.0722f) / 255;

        float strongest = 0.68f;
        Rectangle match = Rectangle.Empty;
        byte[]? matchedPattern = null;
        foreach (int width in widths)
        {
            int height = Inset(width, ReferenceHeight);
            if (height > scanHeight) continue;
            var pattern = Scale(reference, width, height);
            var edges = Edges(pattern, width, height, scanWidth);
            float patternEnergy = edges.Sum(edge => edge.Difference * edge.Difference);
            int expectedX = scanWidth - width - Inset(width, 8);
            int expectedY = scanHeight - height - Inset(width, 10);
            int tolerance = Tolerance(width);

            for (int y = Math.Max(0, expectedY - tolerance); y <= Math.Min(scanHeight - height, expectedY + tolerance); y++)
            for (int x = Math.Max(0, expectedX - tolerance); x <= Math.Min(scanWidth - width, expectedX + tolerance); x++)
            {
                int origin = y * scanWidth + x;
                float cross = 0, energy = 0;
                foreach (var edge in edges)
                {
                    float difference = luma[origin + edge.Second] - luma[origin + edge.First];
                    cross += difference * edge.Difference;
                    energy += difference * difference;
                }
                if (energy < edges.Length * 0.0004f || patternEnergy == 0) continue;
                float correlation = MathF.Abs(cross) / MathF.Sqrt(energy * patternEnergy);
                if (correlation <= strongest) continue;
                strongest = correlation;
                match = new Rectangle(corner.X + x, corner.Y + y, width, height);
                matchedPattern = pattern;
            }
        }

        return matchedPattern is null ? null : (MakeMask(source.Size, match, matchedPattern), match);
    }

    private static int Inset(int width, int value) => Math.Max(1, (int)Math.Round((double)width * value / ReferenceWidth));
    private static int Tolerance(int width) => Math.Max(5, width / 16);

    private static (int First, int Second, float Difference)[] Edges(byte[] pattern, int width, int height, int stride)
    {
        var edges = new List<(int, int, float)>();
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int pixel = y * width + x, offset = y * stride + x;
            if (x + 1 < width) Add(pixel, pixel + 1, offset, offset + 1);
            if (y + 1 < height) Add(pixel, pixel + width, offset, offset + stride);
        }
        return edges.ToArray();

        void Add(int a, int b, int first, int second)
        {
            int difference = pattern[b] - pattern[a];
            if (Math.Abs(difference) >= 23) edges.Add((first, second, difference / 255f));
        }
    }

    private static byte[] Scale(byte[] reference, int width, int height)
    {
        if (width == ReferenceWidth && height == ReferenceHeight) return reference;
        var result = new byte[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            float sx = Math.Clamp((x + 0.5f) * ReferenceWidth / width - 0.5f, 0, ReferenceWidth - 1);
            float sy = Math.Clamp((y + 0.5f) * ReferenceHeight / height - 0.5f, 0, ReferenceHeight - 1);
            int x0 = (int)sx, y0 = (int)sy;
            int x1 = Math.Min(x0 + 1, ReferenceWidth - 1), y1 = Math.Min(y0 + 1, ReferenceHeight - 1);
            float a = float.Lerp(reference[y0 * ReferenceWidth + x0], reference[y0 * ReferenceWidth + x1], sx - x0);
            float b = float.Lerp(reference[y1 * ReferenceWidth + x0], reference[y1 * ReferenceWidth + x1], sx - x0);
            result[y * width + x] = (byte)MathF.Round(float.Lerp(a, b, sy - y0));
        }
        return result;
    }

    private static Bitmap MakeMask(Size size, Rectangle region, byte[] pattern)
    {
        int width = region.Width, height = region.Height;
        var pixels = new byte[width * height * 4];
        // Include the anti-aliased outline and JPEG ringing around each letter.
        int radius = Math.Clamp(width / 45, 2, 8);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (pattern[y * width + x] < 18) continue;
            for (int py = Math.Max(0, y - radius); py <= Math.Min(height - 1, y + radius); py++)
            for (int px = Math.Max(0, x - radius); px <= Math.Min(width - 1, x + radius); px++)
                pixels[(py * width + px) * 4 + 3] = 255;
        }

        var mask = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        try
        {
            Inpainting.WritePixels(mask, region, pixels);
            return mask;
        }
        catch
        {
            mask.Dispose();
            throw;
        }
    }

    private static byte[] LoadReference()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Grok", "corner-wordmark.png");
        if (!File.Exists(path)) return [];
        using var bitmap = new Bitmap(path);
        var pixels = Inpainting.ReadPixels(bitmap, new Rectangle(0, 0, ReferenceWidth, ReferenceHeight));
        var reference = new byte[ReferenceWidth * ReferenceHeight];
        for (int i = 0; i < reference.Length; i++) reference[i] = pixels[4 * i];
        return reference;
    }
}
