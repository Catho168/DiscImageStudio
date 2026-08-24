using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DiscImageStudio.Imaging;

public static class RingImageProcessor
{
    private const int MinimumCopies = 3;
    private const int MaximumCopies = 64;

    public static RingImageLayoutSummary Render(
        string sourcePath,
        string outputPath,
        RingImageLayoutOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        string sourceFullPath = Path.GetFullPath(sourcePath);
        string outputFullPath = Path.GetFullPath(outputPath);
        BitmapFrame source = LoadBitmap(sourceFullPath);
        double aspectRatio = source.PixelWidth / (double)source.PixelHeight;
        double angularGapRadians = options.AngularGapDegrees * Math.PI / 180.0;
        double pixelSizeMm = 2.0 * options.CanvasOuterRadiusMm / options.OutputSize;
        double layoutInsetMm = Math.Max(0.05, pixelSizeMm * 1.5);
        double layoutInnerRadiusMm = options.ContentInnerRadiusMm + layoutInsetMm;
        double layoutOuterRadiusMm = options.ContentOuterRadiusMm - layoutInsetMm;
        if (layoutOuterRadiusMm <= layoutInnerRadiusMm)
        {
            throw new ArgumentException("The safety band is too narrow for the selected output size.");
        }

        // Find the largest complete, undistorted rectangle that can fit inside the annulus.
        double radialEquation = 1.0 + ((aspectRatio * aspectRatio) / 4.0);
        double radialFitHeightMm =
            (-layoutInnerRadiusMm
                + Math.Sqrt(
                    (layoutInnerRadiusMm * layoutInnerRadiusMm)
                    + (radialEquation
                        * ((layoutOuterRadiusMm * layoutOuterRadiusMm)
                            - (layoutInnerRadiusMm * layoutInnerRadiusMm)))))
            / radialEquation;
        double radialFitWidthMm = radialFitHeightMm * aspectRatio;
        double nominalBandHeightMm = layoutOuterRadiusMm - layoutInnerRadiusMm;
        double nominalMiddleRadiusMm =
            (layoutInnerRadiusMm + layoutOuterRadiusMm) / 2.0;
        double nominalCopyWidthMm = nominalBandHeightMm * aspectRatio;
        double nominalGapArcMm = angularGapRadians * nominalMiddleRadiusMm;
        int copyCount = Math.Clamp(
            (int)Math.Floor(
                (2.0 * Math.PI * nominalMiddleRadiusMm)
                / (nominalCopyWidthMm + nominalGapArcMm)),
            MinimumCopies,
            MaximumCopies);
        double availableSectorAngle = ((2.0 * Math.PI) / copyCount) - angularGapRadians;
        double availableWidthMm = 2.0
            * layoutInnerRadiusMm
            * Math.Tan(availableSectorAngle / 2.0);
        double copyWidthMm = Math.Min(radialFitWidthMm, availableWidthMm);
        double copyHeightMm = copyWidthMm / aspectRatio;
        double minimumCentreRadiusMm = layoutInnerRadiusMm + (copyHeightMm / 2.0);
        double maximumCentreRadiusMm =
            Math.Sqrt(
                (layoutOuterRadiusMm * layoutOuterRadiusMm)
                - ((copyWidthMm * copyWidthMm) / 4.0))
            - (copyHeightMm / 2.0);
        double copyCentreRadiusMm =
            (minimumCentreRadiusMm + maximumCentreRadiusMm) / 2.0;

        int size = options.OutputSize;
        double centre = size / 2.0;
        double pixelsPerMm = size / (2.0 * options.CanvasOuterRadiusMm);
        double copyCentreRadiusPixels = copyCentreRadiusMm * pixelsPerMm;
        double copyWidthPixels = copyWidthMm * pixelsPerMm;
        double copyHeightPixels = copyHeightMm * pixelsPerMm;
        DrawingVisual visual = new();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, size, size));
            GeometryGroup ringClip = new() { FillRule = FillRule.EvenOdd };
            ringClip.Children.Add(new EllipseGeometry(
                new Point(centre, centre),
                options.ContentOuterRadiusMm * pixelsPerMm,
                options.ContentOuterRadiusMm * pixelsPerMm));
            ringClip.Children.Add(new EllipseGeometry(
                new Point(centre, centre),
                options.ContentInnerRadiusMm * pixelsPerMm,
                options.ContentInnerRadiusMm * pixelsPerMm));
            drawing.PushClip(ringClip);
            for (int copyIndex = 0; copyIndex < copyCount; copyIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double angleDegrees = -90.0 + ((360.0 * copyIndex) / copyCount);
                double angleRadians = angleDegrees * Math.PI / 180.0;
                double copyCentreX = centre + (copyCentreRadiusPixels * Math.Cos(angleRadians));
                double copyCentreY = centre + (copyCentreRadiusPixels * Math.Sin(angleRadians));
                Matrix transform = Matrix.Identity;
                transform.Rotate(angleDegrees + 90.0);
                transform.Translate(copyCentreX, copyCentreY);
                drawing.PushTransform(new MatrixTransform(transform));
                drawing.DrawImage(
                    source,
                    new Rect(
                        -copyWidthPixels / 2.0,
                        -copyHeightPixels / 2.0,
                        copyWidthPixels,
                        copyHeightPixels));
                drawing.Pop();
            }

            drawing.Pop();
        }

        RenderTargetBitmap bitmap = new(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        WritePng(outputFullPath, bitmap);
        return new RingImageLayoutSummary(
            sourceFullPath,
            outputFullPath,
            source.PixelWidth,
            source.PixelHeight,
            size,
            copyCount,
            Math.Max(1, (int)Math.Round(copyWidthPixels)),
            Math.Max(1, (int)Math.Round(copyHeightPixels)),
            options.ContentInnerRadiusMm,
            options.ContentOuterRadiusMm,
            copyWidthMm,
            copyHeightMm,
            copyCentreRadiusMm,
            options.AngularGapDegrees);
    }

    private static BitmapFrame LoadBitmap(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        BitmapFrame frame = BitmapFrame.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }

    private static void WritePng(string path, BitmapSource bitmap)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(output);
    }
}
