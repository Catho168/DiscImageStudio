using System.IO.Compression;
using System.Security.Cryptography;

namespace DiscImageStudio.Core.Calibration;

/// <summary>
/// The user's exact target_slim.png. Its pixel coordinates are mapped by the same
/// conventions as the ordinary image encoder for the selected disc type.
/// </summary>
public static class CalibrationReferencePattern
{
    public const int PixelSize = 2400;
    // A physical-disc background for previews, never the image's encoding scale.
    public const double CanvasRadiusMm = 60;
    public const double NominalLineWidthPixels = 10;
    public const string FileName = "target_slim.png";
    public const string PngSha256 = "c98ffe87db7cf9c3f132352814e1ce611c44f704de5b7212da4083188257c829";
    private const string GraySha256 = "3d351bdfb817b943c1d1b4aedc97fc4f0a111e7e1537748f70cb5b180ba96852";
    private const string ResourcePrefix = "DiscImageStudio.Core.Calibration.";
    private static readonly Lazy<byte[]> Gray = new(LoadGray);

    /// <summary>Circle radii in the unchanged reference PNG, before physical scaling.</summary>
    public static IReadOnlyList<double> RingRadiiPixels { get; } =
        Array.AsReadOnly(new[] { 490.0, 570, 650, 730, 810, 890, 970, 1050, 1130 });

    public static double MillimetresPerPixel(double outerRadiusMm, CalibrationDiscKind kind)
        => outerRadiusMm / (kind == CalibrationDiscKind.Cd ? PixelSize / 2.0 : (PixelSize - 1) / 2.0);

    /// <summary>Inverse of the ordinary encoder's physical-to-image coordinate mapping.</summary>
    public static CalibrationPoint PixelToDisc(double x, double y, double outerRadiusMm, CalibrationDiscKind kind)
    {
        double centre = kind == CalibrationDiscKind.Cd ? PixelSize / 2.0 : (PixelSize - 1) / 2.0;
        double scale = MillimetresPerPixel(outerRadiusMm, kind);
        return new((x - centre) * scale, (y - centre) * scale);
    }

    public static byte[] ReadPngBytes()
    {
        using Stream input = Open(FileName);
        using var output = new MemoryStream();
        input.CopyTo(output);
        byte[] bytes = output.ToArray();
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(PngSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("标定图案 PNG 与固定参考文件不一致。");
        return bytes;
    }

    /// <summary>A defensive copy of the exact PNG grey samples, in row-major order.</summary>
    public static byte[] ReadGrayPixels() => (byte[])Gray.Value.Clone();

    /// <summary>
    /// Samples the original PNG using DVD bilinear or CD nearest-pixel interpolation.
    /// CD uses pixel 1200 as its centre; DVD uses 1199.5. Y increases downwards.
    /// Clipping to the encoded annulus is performed by CalibrationTarget.Sample.
    /// </summary>
    public static byte Sample(double xMm, double yMm, double outerRadiusMm, CalibrationDiscKind kind)
    {
        if (!double.IsFinite(xMm) || !double.IsFinite(yMm)
            || !double.IsFinite(outerRadiusMm) || outerRadiusMm <= 0 || !Enum.IsDefined(kind)) return 255;
        double centre = kind == CalibrationDiscKind.Cd ? PixelSize / 2.0 : (PixelSize - 1) / 2.0;
        double scale = centre / outerRadiusMm;
        double x = centre + xMm * scale, y = centre + yMm * scale;
        byte[] pixels = Gray.Value;
        if (kind == CalibrationDiscKind.Cd)
        {
            int nearestX = (int)Math.Round(x), nearestY = (int)Math.Round(y);
            return (uint)nearestX < PixelSize && (uint)nearestY < PixelSize
                ? pixels[nearestY * PixelSize + nearestX] : (byte)255;
        }
        if (x < 0 || y < 0 || x > PixelSize - 1 || y > PixelSize - 1) return 255;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        int x1 = Math.Min(x0 + 1, PixelSize - 1), y1 = Math.Min(y0 + 1, PixelSize - 1);
        double fx = x - x0, fy = y - y0;
        // Keep the ordinary DVD sampler's arithmetic order as well as its mapping;
        // tiny rounding differences at a 127.5 threshold can otherwise alter bytes.
        double alpha00 = ((1 - fx) * (1 - fy)) * 255, alpha10 = (fx * (1 - fy)) * 255;
        double alpha01 = ((1 - fx) * fy) * 255, alpha11 = (fx * fy) * 255;
        double totalAlpha = alpha00 + alpha10 + alpha01 + alpha11;
        double gray = (pixels[y0 * PixelSize + x0] * alpha00 + pixels[y0 * PixelSize + x1] * alpha10
            + pixels[y1 * PixelSize + x0] * alpha01 + pixels[y1 * PixelSize + x1] * alpha11) * (1.0 / totalAlpha);
        double value = (0.2126 * gray) + (0.7152 * gray) + (0.0722 * gray);
        return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }

    private static byte[] LoadGray()
    {
        using Stream resource = Open("target_slim.gray.gz");
        using var compressed = new GZipStream(resource, CompressionMode.Decompress);
        byte[] pixels = new byte[PixelSize * PixelSize];
        compressed.ReadExactly(pixels);
        if (compressed.ReadByte() != -1
            || !Convert.ToHexString(SHA256.HashData(pixels)).Equals(GraySha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("标定图案像素与固定参考文件不一致。");
        return pixels;
    }

    private static Stream Open(string name) => typeof(CalibrationReferencePattern).Assembly
        .GetManifestResourceStream(ResourcePrefix + name)
        ?? throw new InvalidOperationException("缺少内置标定参考图案资源。");
}
