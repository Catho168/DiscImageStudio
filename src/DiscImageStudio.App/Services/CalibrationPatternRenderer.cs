using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Core.Calibration;

namespace DiscImageStudio.Services;

internal static class CalibrationPatternRenderer
{
    internal static BitmapSource Render(CalibrationTarget target, double canvasRadius, int size,
        CalibrationParameters? actual = null, double rotation = 0, CancellationToken cancellationToken = default)
    {
        Func<CalibrationPoint, CalibrationPoint?>? inverse = actual is null
            ? null : target.CreateInverseMapping(actual, rotation);
        var bounds = actual ?? target.Parameters;
        // Canvas size controls the physical background only. Target.Sample owns
        // the source-image scale, which is captured in its generation parameters.
        byte[] pixels = new byte[checked(size * size)];
        double step = 2 * canvasRadius / size;
        Parallel.For(0, size, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            double ym = (y + 0.5) * step - canvasRadius;
            for (int x = 0; x < size; x++)
            {
                double xm = (x + 0.5) * step - canvasRadius;
                double r2 = xm * xm + ym * ym;
                byte value = 255;
                if (r2 >= bounds.InnerRadiusMm * bounds.InnerRadiusMm - 1e-10 && r2 <= bounds.OuterRadiusMm * bounds.OuterRadiusMm + 1e-10)
                {
                    CalibrationPoint? p = inverse is null ? new(xm, ym) : inverse(new(xm, ym));
                    if (p is not null) value = target.Sample(p.X, p.Y);
                }
                pixels[y * size + x] = value;
            }
        });
        BitmapSource image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
        image.Freeze();
        return image;
    }

    /// <summary>Original PNG pixels cropped in the ordinary encoder's image coordinates.</summary>
    internal static BitmapSource RenderEncodingImage(CalibrationTarget target, CancellationToken cancellationToken = default)
    {
        const int size = CalibrationReferencePattern.PixelSize;
        byte[] pixels = CalibrationReferencePattern.ReadGrayPixels();
        CalibrationParameters parameters = target.Parameters;
        double inner2 = parameters.InnerRadiusMm * parameters.InnerRadiusMm;
        double outer2 = parameters.OuterRadiusMm * parameters.OuterRadiusMm;
        Parallel.For(0, size, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            for (int x = 0; x < size; x++)
            {
                CalibrationPoint p = CalibrationReferencePattern.PixelToDisc(x, y, parameters.OuterRadiusMm, parameters.Kind);
                double r2 = p.X * p.X + p.Y * p.Y;
                if (r2 < inner2 - 1e-10 || r2 > outer2 + 1e-10) pixels[y * size + x] = 255;
            }
        });
        BitmapSource image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
        image.Freeze();
        return image;
    }

    internal static void SavePng(BitmapSource image, string path)
    {
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }
}
