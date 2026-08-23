namespace DiscImageStudio.Cd;

public sealed record CdDiscParameters(
    double InnerRadiusMm,
    double OuterRadiusMm,
    long Sectors,
    double LinearVelocityMmPerSecond,
    double StartAngleRadians = 0.0,
    double ImageOuterRadiusMm = 57.5)
{
    public const int BytesPerSector = 2352;

    public long TotalBytes => checked(Sectors * BytesPerSector);

    public double TotalTimeSeconds => Sectors / 75.0;

    private double RadiusSquaredRange =>
        (OuterRadiusMm * OuterRadiusMm) - (InnerRadiusMm * InnerRadiusMm);

    public void Validate()
    {
        if (Sectors <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Sectors), "CD sector count must be positive.");
        }

        if (!double.IsFinite(InnerRadiusMm)
            || !double.IsFinite(OuterRadiusMm)
            || InnerRadiusMm <= 0
            || OuterRadiusMm <= InnerRadiusMm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OuterRadiusMm),
                "CD outer radius must be greater than its positive inner radius.");
        }

        if (!double.IsFinite(LinearVelocityMmPerSecond) || LinearVelocityMmPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LinearVelocityMmPerSecond),
                "CD linear velocity must be positive.");
        }

        if (!double.IsFinite(StartAngleRadians))
        {
            throw new ArgumentOutOfRangeException(nameof(StartAngleRadians));
        }

        if (!double.IsFinite(ImageOuterRadiusMm) || ImageOuterRadiusMm <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ImageOuterRadiusMm),
                "Image outer radius must be positive.");
        }

        _ = TotalBytes;
    }

    public double RadiusFromByte(long globalByte)
    {
        double fraction = Math.Clamp((double)globalByte / TotalBytes, 0.0, 1.0);
        return Math.Sqrt(
            (InnerRadiusMm * InnerRadiusMm) + (fraction * RadiusSquaredRange));
    }

    public double ThetaFromRadius(double radiusMm)
        => StartAngleRadians
            + ((2.0 * LinearVelocityMmPerSecond * TotalTimeSeconds / RadiusSquaredRange)
                * (radiusMm - InnerRadiusMm));

    public (double RadiusMm, double ThetaRadians) PolarFromByte(long globalByte)
    {
        double radius = RadiusFromByte(globalByte);
        return (radius, ThetaFromRadius(radius));
    }

    public (double X, double Y) ImagePointFromByte(long globalByte, int imageSize)
    {
        (double radius, double theta) = PolarFromByte(globalByte);
        double imageRadius = imageSize / 2.0;
        double pixelRadius = imageRadius * radius / ImageOuterRadiusMm;
        double centre = imageSize / 2.0;
        return (
            centre + (pixelRadius * Math.Cos(theta)),
            centre + (pixelRadius * Math.Sin(theta)));
    }
}
