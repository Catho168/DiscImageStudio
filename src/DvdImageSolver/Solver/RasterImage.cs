using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DvdImageSolver.Solver;

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
        string fullPath = Path.GetFullPath(path);
        using FileStream stream = File.OpenRead(fullPath);
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

    internal static RasterImage CreateForTest(int width, int height, byte blue, byte green, byte red, byte alpha)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = alpha;
        }

        return new RasterImage(width, height, pixels);
    }

    internal PixelSample SampleDisc(double xMm, double yMm, double outerRadiusMm)
    {
        double scale = (Math.Min(Width, Height) - 1) / (2.0 * outerRadiusMm);
        double pixelX = ((Width - 1) / 2.0) + (xMm * scale);
        double pixelY = ((Height - 1) / 2.0) - (yMm * scale);
        if (pixelX < 0 || pixelY < 0 || pixelX > Width - 1 || pixelY > Height - 1)
        {
            return default;
        }

        int x0 = (int)Math.Floor(pixelX);
        int y0 = (int)Math.Floor(pixelY);
        int x1 = Math.Min(x0 + 1, Width - 1);
        int y1 = Math.Min(y0 + 1, Height - 1);
        double horizontal = pixelX - x0;
        double vertical = pixelY - y0;

        double totalAlpha = 0;
        double weightedBlue = 0;
        double weightedGreen = 0;
        double weightedRed = 0;
        Accumulate(x0, y0, (1 - horizontal) * (1 - vertical));
        Accumulate(x1, y0, horizontal * (1 - vertical));
        Accumulate(x0, y1, (1 - horizontal) * vertical);
        Accumulate(x1, y1, horizontal * vertical);

        if (totalAlpha <= 0)
        {
            return default;
        }

        byte alpha = (byte)Math.Clamp((int)Math.Round(totalAlpha), 0, 255);
        double inverseAlpha = 1.0 / totalAlpha;
        double blue = weightedBlue * inverseAlpha;
        double green = weightedGreen * inverseAlpha;
        double red = weightedRed * inverseAlpha;
        byte luminance = (byte)Math.Clamp(
            (int)Math.Round((0.2126 * red) + (0.7152 * green) + (0.0722 * blue)),
            0,
            255);
        return new PixelSample(luminance, alpha);

        void Accumulate(int x, int y, double weight)
        {
            int offset = ((y * Width) + x) * 4;
            double weightedAlpha = weight * _bgra[offset + 3];
            totalAlpha += weightedAlpha;
            weightedBlue += weightedAlpha * _bgra[offset];
            weightedGreen += weightedAlpha * _bgra[offset + 1];
            weightedRed += weightedAlpha * _bgra[offset + 2];
        }
    }
}

internal readonly record struct PixelSample(byte Luminance, byte Alpha);
