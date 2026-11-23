using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WatermarkRemover;

internal static class Inpainting
{
    public static Bitmap Repair(Bitmap source, Bitmap mask)
    {
        var bounds = new Rectangle(0, 0, source.Width, source.Height);
        var maskPixels = ReadPixels(mask, bounds);
        var area = GetRepairArea(maskPixels, source.Size);

        // Keep one pixel beyond the selection so intersecting scans reach the original image.
        area.Inflate(1, 1);
        area.Intersect(bounds);
        int width = area.Width, height = area.Height;
        var pixels = ReadPixels(source, area);
        var marked = new bool[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                marked[y * width + x] = maskPixels[((area.Y + y) * source.Width + area.X + x) * 4 + 3] != 0;

        // Blend horizontal, vertical, and diagonal estimates before changing any source pixels.
        var estimates = new Estimate[marked.Length];
        for (int y = 0; y < height; y++)
        {
            Scan(0, y, 1, 0);
            Scan(0, y, 1, 1);
            Scan(width - 1, y, -1, 1);
        }
        for (int x = 0; x < width; x++)
        {
            Scan(x, 0, 0, 1);
            if (x > 0) Scan(x, 0, 1, 1);
            if (x < width - 1) Scan(x, 0, -1, 1);
        }

        int unresolved = 0;
        for (int i = 0; i < marked.Length; i++)
        {
            if (!marked[i]) continue;
            ref var estimate = ref estimates[i];
            if (estimate.Weight == 0)
            {
                unresolved++;
                continue;
            }
            estimate.WriteTo(pixels, i * 4);
            marked[i] = false;
        }
        if (unresolved > 0) FillUnresolved(pixels, marked, width, height);

        var result = source.Clone(bounds, PixelFormat.Format32bppArgb);
        try
        {
            AccessPixels(result, area, ImageLockMode.WriteOnly, pixels);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }

        void Scan(int startX, int startY, int dx, int dy)
        {
            int x = startX, y = startY, previous = -1;
            int step = dy * width + dx;
            while (Inside(x, y))
            {
                int first = y * width + x;
                if (!marked[first])
                {
                    previous = first;
                    x += dx;
                    y += dy;
                    continue;
                }

                int count = 0;
                do
                {
                    count++;
                    x += dx;
                    y += dy;
                } while (Inside(x, y) && marked[y * width + x]);

                int next = Inside(x, y) ? y * width + x : -1;
                if (previous < 0 && next < 0) return;
                bool hasBoth = previous >= 0 && next >= 0;
                // Diagonal neighbors are farther apart, and a single endpoint is less reliable.
                float scale = dx != 0 && dy != 0 ? 0.5f : 1f;
                for (int i = 0; i < count; i++)
                {
                    ref var estimate = ref estimates[first + i * step];
                    if (hasBoth)
                    {
                        float fraction = (i + 1f) / (count + 1f);
                        float weight = scale / ((count + 1f) * (count + 1f));
                        estimate.Add(pixels, previous * 4, weight * (1 - fraction));
                        estimate.Add(pixels, next * 4, weight * fraction);
                    }
                    else
                    {
                        float distance = previous >= 0 ? i + 1f : count - i;
                        estimate.Add(pixels, Math.Max(previous, next) * 4, scale * 0.025f / (distance * distance));
                    }
                }
            }
        }

        bool Inside(int x, int y) => (uint)x < (uint)width && (uint)y < (uint)height;
    }

    internal static Rectangle GetRepairArea(byte[] mask, Size size)
    {
        var (area, count) = FindMarkedArea(mask, size.Width, size.Height);
        if (count == 0) throw new InvalidOperationException("Brush over the watermark first.");
        if (count == size.Width * size.Height)
            throw new InvalidOperationException("Leave some of the image unmarked so there are surrounding pixels to use.");
        return area;
    }

    private static (Rectangle Area, int Count) FindMarkedArea(byte[] mask, int width, int height)
    {
        int left = width, top = height, right = -1, bottom = -1, count = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (mask[(y * width + x) * 4 + 3] == 0) continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = y;
                count++;
            }
        }
        return count == 0 ? (Rectangle.Empty, 0) : (Rectangle.FromLTRB(left, top, right + 1, bottom + 1), count);
    }

    private static void FillUnresolved(byte[] pixels, bool[] marked, int width, int height)
    {
        var frontier = new Queue<int>();
        for (int i = 0; i < marked.Length; i++)
        {
            if (marked[i]) continue;
            int x = i % width, y = i / width;
            if ((x > 0 && marked[i - 1]) || (x + 1 < width && marked[i + 1]) ||
                (y > 0 && marked[i - width]) || (y + 1 < height && marked[i + width]))
                frontier.Enqueue(i);
        }
        while (frontier.TryDequeue(out int i))
        {
            int x = i % width, y = i / width;
            if (x > 0) Copy(i, i - 1);
            if (x + 1 < width) Copy(i, i + 1);
            if (y > 0) Copy(i, i - width);
            if (y + 1 < height) Copy(i, i + width);
        }

        void Copy(int from, int to)
        {
            if (!marked[to]) return;
            Buffer.BlockCopy(pixels, from * 4, pixels, to * 4, 4);
            marked[to] = false;
            frontier.Enqueue(to);
        }
    }

    internal static byte[] ReadPixels(Bitmap bitmap, Rectangle area)
    {
        var pixels = new byte[area.Width * area.Height * 4];
        AccessPixels(bitmap, area, ImageLockMode.ReadOnly, pixels);
        return pixels;
    }

    internal static void WritePixels(Bitmap bitmap, Rectangle area, byte[] pixels)
        => AccessPixels(bitmap, area, ImageLockMode.WriteOnly, pixels);

    private static void AccessPixels(Bitmap bitmap, Rectangle area, ImageLockMode mode, byte[] pixels)
    {
        var data = bitmap.LockBits(area, mode, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = area.Width * 4;
            for (int y = 0; y < area.Height; y++)
            {
                var row = IntPtr.Add(data.Scan0, y * data.Stride);
                if (mode == ImageLockMode.ReadOnly)
                    Marshal.Copy(row, pixels, y * rowBytes, rowBytes);
                else
                    Marshal.Copy(pixels, y * rowBytes, row, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private struct Estimate
    {
        private float blue, green, red, alpha;
        public float Weight;

        public void Add(byte[] pixels, int offset, float weight)
        {
            float alphaWeight = pixels[offset + 3] * weight;
            blue += pixels[offset] * alphaWeight;
            green += pixels[offset + 1] * alphaWeight;
            red += pixels[offset + 2] * alphaWeight;
            alpha += alphaWeight;
            Weight += weight;
        }

        public readonly void WriteTo(byte[] pixels, int offset)
        {
            pixels[offset] = alpha > 0 ? ToByte(blue / alpha) : (byte)0;
            pixels[offset + 1] = alpha > 0 ? ToByte(green / alpha) : (byte)0;
            pixels[offset + 2] = alpha > 0 ? ToByte(red / alpha) : (byte)0;
            pixels[offset + 3] = ToByte(alpha / Weight);
        }

        private static byte ToByte(float value) => (byte)Math.Clamp(MathF.Round(value), 0, 255);
    }
}
