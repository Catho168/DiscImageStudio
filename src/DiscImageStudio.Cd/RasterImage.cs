using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DiscImageStudio.Cd;

internal sealed class RasterImage
{
    private readonly byte[] _bgra;

    private RasterImage(int width, int height, byte[] bgra)
    {
        Width = width;
        Height = height;
        _bgra = bgra;
    }

    internal int Width { get; }

    internal int Height { get; }

    internal static RasterImage Load(string path)
    {
        using FileStream stream = File.OpenRead(Path.GetFullPath(path));
        BitmapFrame frame = BitmapFrame.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        FormatConvertedBitmap converted = new(frame, PixelFormats.Bgra32, null, 0);
        int stride = checked(converted.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        return new RasterImage(converted.PixelWidth, converted.PixelHeight, pixels);
    }

    internal byte GrayAt(double pixelX, double pixelY, byte outside = 255)
    {
        int x = (int)Math.Round(pixelX);
        int y = (int)Math.Round(pixelY);
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return outside;
        }

        int offset = ((y * Width) + x) * 4;
        int blue = _bgra[offset];
        int green = _bgra[offset + 1];
        int red = _bgra[offset + 2];
        return (byte)(((red * 299) + (green * 587) + (blue * 114)) / 1000);
    }
}
