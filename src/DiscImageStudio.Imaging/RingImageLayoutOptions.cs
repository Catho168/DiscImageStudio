namespace DiscImageStudio.Imaging;

public sealed record RingImageLayoutOptions(
    double CanvasOuterRadiusMm,
    double ContentInnerRadiusMm,
    double ContentOuterRadiusMm,
    int OutputSize = 2048,
    double AngularGapDegrees = 3.0)
{
    public void Validate()
    {
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
    double ContentInnerRadiusMm,
    double ContentOuterRadiusMm,
    double CopyWidthMm,
    double CopyHeightMm,
    double CopyCentreRadiusMm,
    double AngularGapDegrees);
