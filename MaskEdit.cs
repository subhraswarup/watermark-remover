namespace WatermarkRemover;

internal sealed class MaskEdit
{
    private const int TileSize = 128;
    private readonly Dictionary<Rectangle, byte[]> tiles = new();
    internal long Bytes { get; private set; }

    internal void Capture(Bitmap mask, Rectangle area)
    {
        var image = new Rectangle(0, 0, mask.Width, mask.Height);
        area.Intersect(image);
        if (area.Width == 0 || area.Height == 0) return;
        for (int y = area.Top / TileSize; y <= (area.Bottom - 1) / TileSize; y++)
        for (int x = area.Left / TileSize; x <= (area.Right - 1) / TileSize; x++)
        {
            var key = new Point(x * TileSize, y * TileSize);
            var bounds = Rectangle.Intersect(new Rectangle(key, new Size(TileSize, TileSize)), image);
            if (tiles.ContainsKey(bounds)) continue;
            byte[] pixels = Inpainting.ReadPixels(mask, bounds);
            tiles.Add(bounds, pixels);
            Bytes += pixels.Length;
        }
    }

    internal void Restore(Bitmap mask)
    {
        foreach (var (bounds, pixels) in tiles) Inpainting.WritePixels(mask, bounds, pixels);
    }
}
