using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace WatermarkRemover;

internal static class ImageFiles
{
    internal const string OpenFilter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp";

    internal static bool HasSupportedExtension(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp";

    internal static Bitmap Load(string path)
    {
        using var input = Decode(path);
        if (!(input.RawFormat.Equals(ImageFormat.Png) || input.RawFormat.Equals(ImageFormat.Jpeg) || input.RawFormat.Equals(ImageFormat.Bmp)))
            throw new InvalidDataException("Choose a PNG, JPEG, or BMP image.");
        if ((long)input.Width * input.Height > 16_000_000)
            throw new InvalidDataException("Choose an image with 16 megapixels or fewer.");
        OrientImage(input);

        var bitmap = new Bitmap(input.Width, input.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImage(input, new Rectangle(0, 0, input.Width, input.Height));
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static Image Decode(string path)
    {
        try
        {
            return Image.FromFile(path, useEmbeddedColorManagement: true);
        }
        catch (Exception error) when (error is ArgumentException or OutOfMemoryException)
        {
            throw new InvalidDataException("Could not decode this image.", error);
        }
    }

    private static void OrientImage(Image image)
    {
        const int orientationTag = 0x112;
        if (!image.PropertyIdList.Contains(orientationTag)) return;
        var value = image.GetPropertyItem(orientationTag)?.Value;
        if (value is not { Length: >= 2 }) return;
        var operation = BitConverter.ToUInt16(value) switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.Rotate180FlipX,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone
        };
        if (operation != RotateFlipType.RotateNoneFlipNone) image.RotateFlip(operation);
    }

    internal static string SaveCopy(Bitmap image, string path, bool keepExisting = false)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg"))
            throw new IOException("Use a .png, .jpg, or .jpeg filename.");
        string directory = Path.GetDirectoryName(path)!;
        string temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            if (extension == ".png")
            {
                image.Save(temporary, ImageFormat.Png);
            }
            else
            {
                using var flattened = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(flattened))
                {
                    graphics.Clear(Color.White);
                    graphics.DrawImageUnscaled(image, 0, 0);
                }
                var codec = ImageCodecInfo.GetImageEncoders().First(encoder => encoder.FormatID == ImageFormat.Jpeg.Guid);
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L);
                flattened.Save(temporary, codec, parameters);
            }

            if (!keepExisting)
            {
                File.Move(temporary, path, overwrite: true);
                return path;
            }

            string stem = Path.GetFileNameWithoutExtension(path);
            string destination = path;
            for (int suffix = 2; ; suffix++)
            {
                try
                {
                    File.Move(temporary, destination);
                    return destination;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException &&
                    (File.Exists(destination) || Directory.Exists(destination)))
                {
                    destination = Path.Combine(directory, $"{stem} ({suffix}){extension}");
                }
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
