using System.Drawing.Imaging;

namespace WatermarkRemover;

internal static class GeminiWatermark
{
    private const int PositionTolerance = 5;
    private const float MinimumCorrelation = 0.57f;
    private static readonly Lazy<Profile[]> Profiles = new(LoadProfiles);

    internal sealed record Match(Rectangle Region, Profile Profile, float Strength, float Correlation);
    internal sealed record Profile(int Size, float[] Alpha, int[] Margins, Edge[] Edges);
    internal readonly record struct Edge(int X, int Y, int Dx, int Dy, float Difference, float MeanAlpha);

    internal static Match? Detect(Bitmap image)
    {
        int searchSize = Profiles.Value.Max(profile => profile.Size + profile.Margins.Max()) + PositionTolerance;
        int width = Math.Min(image.Width, searchSize), height = Math.Min(image.Height, searchSize);
        var corner = new Rectangle(image.Width - width, image.Height - height, width, height);
        var pixels = Inpainting.ReadPixels(image, corner);
        var luminance = new float[width * height];
        for (int i = 0; i < luminance.Length; i++)
            luminance[i] = (0.2126f * pixels[4 * i + 2] + 0.7152f * pixels[4 * i + 1] + 0.0722f * pixels[4 * i]) / 255f;

        Match? best = null;
        foreach (var profile in Profiles.Value)
        foreach (int margin in profile.Margins)
        {
            int size = profile.Size;
            for (int y = Math.Max(0, height - size - margin - PositionTolerance); y <= Math.Min(height - size, height - size - margin + PositionTolerance); y++)
            for (int x = Math.Max(0, width - size - margin - PositionTolerance); x <= Math.Min(width - size, width - size - margin + PositionTolerance); x++)
            {
                var (correlation, strength) = Score(luminance, width, x, y, profile.Edges);
                if (correlation < MinimumCorrelation || strength is < 0.40f or > 1.50f) continue;
                if (best is not null && correlation <= best.Correlation) continue;
                best = new Match(new Rectangle(corner.X + x, corner.Y + y, size, size), profile, strength, correlation);
            }
        }
        return best;
    }

    internal static Bitmap Remove(Bitmap original, Match match)
    {
        var pixels = Inpainting.ReadPixels(original, match.Region);
        for (int i = 0; i < match.Profile.Alpha.Length; i++)
        {
            float alpha = Math.Min(0.85f, match.Profile.Alpha[i] * match.Strength);
            if (alpha < 0.012f) continue;
            for (int channel = 0; channel < 3; channel++)
            {
                int offset = 4 * i + channel;
                pixels[offset] = (byte)Math.Clamp(MathF.Round((pixels[offset] - 255f * alpha) / (1 - alpha)), 0, 255);
            }
        }

        var output = original.Clone(new Rectangle(Point.Empty, original.Size), PixelFormat.Format32bppArgb);
        try
        {
            Inpainting.WritePixels(output, match.Region, pixels);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static (float Correlation, float Strength) Score(float[] luminance, int width, int x, int y, Edge[] edges)
    {
        float cross = 0, actualEnergy = 0, expectedEnergy = 0, alphaEnergy = 0;
        foreach (var edge in edges)
        {
            int first = (y + edge.Y) * width + x + edge.X;
            float a = luminance[first], b = luminance[first + edge.Dy * width + edge.Dx];
            float actual = b - a;
            float expected = (1 - (a + b) / 2) * edge.Difference;
            cross += actual * expected;
            actualEnergy += actual * actual;
            expectedEnergy += expected * expected;
            alphaEnergy += expected * expected * edge.MeanAlpha;
        }
        if (cross <= 0 || expectedEnergy < 0.01f || actualEnergy < 0.01f) return default;

        float correlation = cross / MathF.Sqrt(actualEnergy * expectedEnergy);
        // The observed pair already contains some white overlay. Compensate for it when fitting opacity.
        float gain = cross / expectedEnergy;
        return (correlation, gain / (1 + gain * alphaEnergy / expectedEnergy));
    }

    private static Profile[] LoadProfiles()
    {
        var currentLarge = ReadAlpha("current-96.png", 96);
        var halfScale = new float[48 * 48];
        for (int y = 0; y < 48; y++)
        for (int x = 0; x < 48; x++)
        {
            int i = 2 * y * 96 + 2 * x;
            halfScale[y * 48 + x] = (currentLarge[i] + currentLarge[i + 1] + currentLarge[i + 96] + currentLarge[i + 97]) / 4;
        }

        // Exports use different corner insets. A matching edge pattern is required at each candidate.
        return [
            Build(36, ReadAlpha("current-36.png", 36), [54, 70, 72]),
            Build(48, halfScale, [64, 72, 96]),
            Build(96, currentLarge, [144, 192]),
            Build(48, ReadAlpha("legacy-48.png", 48), [32, 72]),
            Build(96, ReadAlpha("legacy-96.png", 96), [64, 144])
        ];
    }

    private static float[] ReadAlpha(string name, int size)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Gemini", name);
        using var bitmap = new Bitmap(path);
        var pixels = Inpainting.ReadPixels(bitmap, new Rectangle(0, 0, size, size));
        var alpha = new float[size * size];
        for (int i = 0; i < alpha.Length; i++)
            alpha[i] = Math.Max(pixels[4 * i], Math.Max(pixels[4 * i + 1], pixels[4 * i + 2])) / 255f;
        return alpha;
    }

    private static Profile Build(int size, float[] alpha, int[] margins)
    {
        var edges = new List<Edge>();
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            if (x + 1 < size) Add(x, y, 1, 0);
            if (y + 1 < size) Add(x, y, 0, 1);
        }
        return new Profile(size, alpha, margins, edges.ToArray());

        void Add(int x, int y, int dx, int dy)
        {
            float a = alpha[y * size + x], b = alpha[(y + dy) * size + x + dx];
            if (Math.Abs(b - a) >= 0.035f) edges.Add(new Edge(x, y, dx, dy, b - a, (a + b) / 2));
        }
    }
}
