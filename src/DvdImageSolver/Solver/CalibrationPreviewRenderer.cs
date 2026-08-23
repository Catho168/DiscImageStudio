using System.Windows.Media;
using System.Windows.Media.Imaging;
using DvdImageSolver.Encoding;

namespace DvdImageSolver.Solver;

public sealed record CalibrationPreviewOptions(
    uint TotalSectors,
    uint StartLba,
    uint FillSectors,
    double GeneratedInnerRadiusMm,
    double GeneratedOuterRadiusMm,
    double ActualInnerRadiusMm,
    double ActualOuterRadiusMm,
    double ChannelBitLengthNm,
    bool Clockwise,
    byte LuminanceThreshold,
    byte AlphaThreshold,
    int PreviewSize,
    int SamplesPerSector);

public sealed record CalibrationPreviewSummary(
    string SourceImage,
    string OutputImage,
    int SourceWidth,
    int SourceHeight,
    int PreviewSize,
    uint TotalSectors,
    uint StartLba,
    uint FillSectors,
    double ChannelBitLengthNm,
    double TrackLengthMetres,
    double GeneratedInnerRadiusMm,
    double GeneratedOuterRadiusMm,
    double GeneratedTrackPitchMicrometres,
    double GeneratedTurns,
    double ActualInnerRadiusMm,
    double ActualOuterRadiusMm,
    double ActualTrackPitchMicrometres,
    double ActualTurns,
    string SpiralDirection,
    byte LuminanceThreshold,
    byte AlphaThreshold,
    int SamplesPerSector,
    string MappingMode);

internal static class CalibrationPreviewRenderer
{
    internal static CalibrationPreviewSummary Render(
        string imagePath,
        string outputPath,
        CalibrationPreviewOptions options)
    {
        Validate(options);
        string sourceFullPath = Path.GetFullPath(imagePath);
        string outputFullPath = Path.GetFullPath(outputPath);
        RasterImage source = RasterImage.Load(sourceFullPath);
        double trackLengthMm = options.TotalSectors
            * (double)DvdEccBlockEncoder.ChannelBitsPerSector
            * options.ChannelBitLengthNm
            * 1e-6;
        ArchimedeanSpiral generated = ArchimedeanSpiral.Create(
            options.GeneratedInnerRadiusMm,
            options.GeneratedOuterRadiusMm,
            trackLengthMm);
        ArchimedeanSpiral actual = ArchimedeanSpiral.Create(
            options.ActualInnerRadiusMm,
            options.ActualOuterRadiusMm,
            trackLengthMm);

        int size = options.PreviewSize;
        int stride = checked(size * 4);
        byte[] pixels = new byte[checked(stride * size)];
        int[] weightedLevelSums = new int[checked(size * size)];
        int[] alphaSums = new int[checked(size * size)];
        double centre = (size - 1) / 2.0;
        double millimetresPerPixel = (2 * options.ActualOuterRadiusMm) / (size - 1);
        double direction = options.Clockwise ? -1.0 : 1.0;
        const double fullTurn = 2 * Math.PI;
        double visibleStartMm = options.StartLba
            * (double)DvdEccBlockEncoder.ChannelBitsPerSector
            * options.ChannelBitLengthNm
            * 1e-6;
        double visibleEndMm = (options.StartLba + (double)options.FillSectors)
            * DvdEccBlockEncoder.ChannelBitsPerSector
            * options.ChannelBitLengthNm
            * 1e-6;
        double visibleStartRadiusMm = actual.RadiusAtArcLengthFast(visibleStartMm);
        double visibleEndRadiusMm = actual.RadiusAtArcLengthFast(visibleEndMm);

        // As in the CD preview, walk forward through the logical track: sample the
        // source at the generated position, then splat that same channel position at
        // the guessed actual geometry. Multiple track samples landing in one preview
        // pixel are averaged instead of allowing the last writer to win.
        long totalSamples = checked((long)options.FillSectors * options.SamplesPerSector);
        double channelBitsPerSample = DvdEccBlockEncoder.ChannelBitsPerSector
            / (double)options.SamplesPerSector;
        double firstGlobalChannelBit = options.StartLba
            * (double)DvdEccBlockEncoder.ChannelBitsPerSector;
        int workerCount = Environment.ProcessorCount;
        Parallel.For(0, workerCount, worker =>
        {
            for (long sampleIndex = worker; sampleIndex < totalSamples; sampleIndex += workerCount)
            {
                double globalChannelBit = firstGlobalChannelBit
                    + ((sampleIndex + 0.5) * channelBitsPerSample);
                double arcLengthMm = globalChannelBit * options.ChannelBitLengthNm * 1e-6;
                double generatedRadiusMm = generated.RadiusAtArcLengthFast(arcLengthMm);
                double generatedTrackAngle = (generatedRadiusMm - generated.InnerRadiusMm)
                    / generated.RadialGrowthPerRadianMm;
                double generatedPolarAngle = direction * generatedTrackAngle;
                PixelSample sample = source.SampleDisc(
                    generatedRadiusMm * Math.Cos(generatedPolarAngle),
                    generatedRadiusMm * Math.Sin(generatedPolarAngle),
                    generated.OuterRadiusMm);
                if (sample.Alpha < options.AlphaThreshold)
                {
                    continue;
                }

                double actualRadiusMm = actual.RadiusAtArcLengthFast(arcLengthMm);
                double actualTrackAngle = (actualRadiusMm - actual.InnerRadiusMm)
                    / actual.RadialGrowthPerRadianMm;
                double actualPolarAngle = direction * actualTrackAngle;
                double pixelRadius = centre * actualRadiusMm / actual.OuterRadiusMm;
                int pixelX = (int)Math.Round(
                    centre + (pixelRadius * Math.Cos(actualPolarAngle)));
                int pixelY = (int)Math.Round(
                    centre - (pixelRadius * Math.Sin(actualPolarAngle)));
                if ((uint)pixelX >= (uint)size || (uint)pixelY >= (uint)size)
                {
                    continue;
                }

                int pixelIndex = (pixelY * size) + pixelX;
                int binaryLevel = sample.Luminance < options.LuminanceThreshold ? 0 : 255;
                Interlocked.Add(
                    ref weightedLevelSums[pixelIndex],
                    binaryLevel * sample.Alpha);
                Interlocked.Add(ref alphaSums[pixelIndex], sample.Alpha);
            }
        });

        Parallel.For(0, size, pixelY =>
        {
            double yMm = (centre - pixelY) * millimetresPerPixel;
            int rowOffset = pixelY * stride;
            for (int pixelX = 0; pixelX < size; pixelX++)
            {
                double xMm = (pixelX - centre) * millimetresPerPixel;
                double radiusMm = Math.Sqrt((xMm * xMm) + (yMm * yMm));
                if (radiusMm < visibleStartRadiusMm || radiusMm > visibleEndRadiusMm)
                {
                    continue;
                }

                int pixelIndex = (pixelY * size) + pixelX;
                int alphaSum = alphaSums[pixelIndex];
                byte level = alphaSum == 0
                    ? (byte)255
                    : (byte)Math.Clamp(
                        (weightedLevelSums[pixelIndex] + (alphaSum / 2)) / alphaSum,
                        0,
                        255);
                int offset = rowOffset + (pixelX * 4);
                pixels[offset] = level;
                pixels[offset + 1] = level;
                pixels[offset + 2] = level;
                pixels[offset + 3] = 255;
            }
        });

        WritePng(outputFullPath, size, pixels, stride);
        return new CalibrationPreviewSummary(
            sourceFullPath,
            outputFullPath,
            source.Width,
            source.Height,
            size,
            options.TotalSectors,
            options.StartLba,
            options.FillSectors,
            options.ChannelBitLengthNm,
            trackLengthMm / 1000.0,
            generated.InnerRadiusMm,
            generated.OuterRadiusMm,
            generated.TrackPitchMm * 1000.0,
            generated.TotalAngleRadians / fullTurn,
            actual.InnerRadiusMm,
            actual.OuterRadiusMm,
            actual.TrackPitchMm * 1000.0,
            actual.TotalAngleRadians / fullTurn,
            options.Clockwise ? "cw" : "ccw",
            options.LuminanceThreshold,
            options.AlphaThreshold,
            options.SamplesPerSector,
            "forward-channel-splat-average");
    }

    private static void WritePng(string outputPath, int size, byte[] pixels, int stride)
    {
        string? directory = Path.GetDirectoryName(outputPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        BitmapSource bitmap = BitmapSource.Create(
            size,
            size,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(output);
    }

    private static void Validate(CalibrationPreviewOptions options)
    {
        if (options.TotalSectors == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Total sector count must be positive.");
        }

        if (options.FillSectors == 0
            || (ulong)options.StartLba + options.FillSectors > options.TotalSectors)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Preview sector range exceeds the disc size.");
        }

        if (!double.IsFinite(options.ChannelBitLengthNm) || options.ChannelBitLengthNm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Channel-bit length must be positive.");
        }

        if (options.PreviewSize is < 64 or > 8192)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Preview size must be 64..8192 pixels.");
        }

        if (options.SamplesPerSector is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Preview samples per sector must be 1..4096.");
        }

        if (options.AlphaThreshold == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Alpha threshold must be 1..255.");
        }
    }
}
