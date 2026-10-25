namespace DiscImageStudio.Imaging;

public static class RingImageQuality
{
    // Keep formal generation at the highest size accepted by the WPF renderer.
    // A separate, smaller live-preview size keeps parameter changes responsive.
    public const int GenerationSize = 8192;
    public const int SavedPreviewSize = 4096;
    public const int LivePreviewSize = 2048;
}

public sealed record RingImageLayoutOptions(
    double CanvasOuterRadiusMm,
    double ContentInnerRadiusMm,
    double ContentOuterRadiusMm,
    int OutputSize = 2048,
    double AngularGapDegrees = 3.0)
{
    /// <summary>
    /// When the source contains only opaque pixels from this grayscale palette, redither
    /// the high-quality transformed ring back to these levels. Empty preserves normal rendering.
    /// </summary>
    public ReadOnlyMemory<byte> PreserveDitherPalette { get; init; }

    public void Validate()
    {
        ReadOnlySpan<byte> palette = PreserveDitherPalette.Span;
        if (!palette.IsEmpty)
        {
            if (palette.Length < 2)
                throw new ArgumentException("A preserved dither palette needs at least two gray levels.", nameof(PreserveDitherPalette));
            for (int index = 1; index < palette.Length; index++)
            {
                if (palette[index] <= palette[index - 1])
                    throw new ArgumentException("Preserved gray levels must be distinct and sorted from dark to light.", nameof(PreserveDitherPalette));
            }
        }

        if (!double.IsFinite(CanvasOuterRadiusMm) || CanvasOuterRadiusMm <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CanvasOuterRadiusMm),
                "Canvas outer radius must be positive.");
        }

        if (!double.IsFinite(ContentInnerRadiusMm)
            || !double.IsFinite(ContentOuterRadiusMm)
            || ContentInnerRadiusMm <= 0
            || ContentOuterRadiusMm <= ContentInnerRadiusMm
            || ContentOuterRadiusMm > CanvasOuterRadiusMm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ContentOuterRadiusMm),
                "Ring content radii must form a positive band inside the canvas.");
        }

        if (OutputSize is < 256 or > 8192)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OutputSize),
                "Ring layout output size must be 256..8192 pixels.");
        }

        if (!double.IsFinite(AngularGapDegrees) || AngularGapDegrees is < 0 or > 30)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AngularGapDegrees),
                "Angular gap must be 0..30 degrees.");
        }
    }
}

public sealed record RingImageLayoutSummary(
    string SourceImage,
    string OutputImage,
    int SourceWidth,
    int SourceHeight,
    int OutputSize,
    int CopyCount,
    int CopyWidthPixels,
    int CopyHeightPixels,
    double ContentInnerRadiusMm,
    double ContentOuterRadiusMm,
    double CopyWidthMm,
    double CopyHeightMm,
    double CopyCentreRadiusMm,
    double AngularGapDegrees);
