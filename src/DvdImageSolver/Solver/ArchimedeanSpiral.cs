namespace DvdImageSolver.Solver;

internal sealed class ArchimedeanSpiral
{
    private readonly double _innerRadiusMm;
    private readonly double _outerRadiusMm;

    private ArchimedeanSpiral(
        double innerRadiusMm,
        double outerRadiusMm,
        double trackLengthMm,
        double radialGrowthPerRadianMm)
    {
        _innerRadiusMm = innerRadiusMm;
        _outerRadiusMm = outerRadiusMm;
        TrackLengthMm = trackLengthMm;
        RadialGrowthPerRadianMm = radialGrowthPerRadianMm;
    }

    internal double TrackLengthMm { get; }

    internal double InnerRadiusMm => _innerRadiusMm;

    internal double OuterRadiusMm => _outerRadiusMm;

    internal double RadialGrowthPerRadianMm { get; }

    internal double TrackPitchMm => 2 * Math.PI * RadialGrowthPerRadianMm;

    internal double TotalAngleRadians => (_outerRadiusMm - _innerRadiusMm) / RadialGrowthPerRadianMm;

    internal static ArchimedeanSpiral Create(
        double innerRadiusMm,
        double outerRadiusMm,
        double trackLengthMm)
    {
        if (!double.IsFinite(innerRadiusMm) || innerRadiusMm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(innerRadiusMm), "Inner radius must be positive.");
        }

        if (!double.IsFinite(outerRadiusMm) || outerRadiusMm <= innerRadiusMm)
        {
            throw new ArgumentOutOfRangeException(nameof(outerRadiusMm), "Outer radius must exceed inner radius.");
        }

        if (!double.IsFinite(trackLengthMm) || trackLengthMm <= outerRadiusMm - innerRadiusMm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(trackLengthMm),
                "Track length must exceed the radial distance between the two radii.");
        }

        double low = 1e-15;
        double high = Math.Max(outerRadiusMm, 1.0);
        for (int iteration = 0; iteration < 120; iteration++)
        {
            double middle = (low + high) / 2;
            double length = ArcLengthBetween(innerRadiusMm, outerRadiusMm, middle);
            if (length > trackLengthMm)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return new ArchimedeanSpiral(innerRadiusMm, outerRadiusMm, trackLengthMm, (low + high) / 2);
    }

    internal double RadiusAtArcLength(double arcLengthMm)
    {
        if (!double.IsFinite(arcLengthMm) || arcLengthMm < 0 || arcLengthMm > TrackLengthMm)
        {
            throw new ArgumentOutOfRangeException(nameof(arcLengthMm));
        }

        double b = RadialGrowthPerRadianMm;
        double estimatedSquared = (_innerRadiusMm * _innerRadiusMm) + (2 * b * arcLengthMm);
        double radius = Math.Clamp(Math.Sqrt(estimatedSquared), _innerRadiusMm, _outerRadiusMm);
        for (int iteration = 0; iteration < 5; iteration++)
        {
            double error = ArcLengthBetween(_innerRadiusMm, radius, b) - arcLengthMm;
            double derivative = Math.Sqrt((radius * radius) + (b * b)) / b;
            radius = Math.Clamp(radius - (error / derivative), _innerRadiusMm, _outerRadiusMm);
        }

        return radius;
    }

    internal double AngleAtArcLength(double arcLengthMm)
        => (RadiusAtArcLength(arcLengthMm) - _innerRadiusMm) / RadialGrowthPerRadianMm;

    internal double RadiusAtArcLengthFast(double arcLengthMm)
    {
        double b = RadialGrowthPerRadianMm;
        double radius = Math.Clamp(
            Math.Sqrt((_innerRadiusMm * _innerRadiusMm) + (2 * b * arcLengthMm)),
            _innerRadiusMm,
            _outerRadiusMm);
        double error = ArcLengthBetween(_innerRadiusMm, radius, b) - arcLengthMm;
        double derivative = Math.Sqrt((radius * radius) + (b * b)) / b;
        return Math.Clamp(radius - (error / derivative), _innerRadiusMm, _outerRadiusMm);
    }

    internal double ArcLengthAtAngle(double angleRadians)
    {
        double angle = Math.Clamp(angleRadians, 0, TotalAngleRadians);
        double radius = _innerRadiusMm + (RadialGrowthPerRadianMm * angle);
        return ArcLengthBetween(_innerRadiusMm, radius, RadialGrowthPerRadianMm);
    }

    private static double ArcLengthBetween(double firstRadius, double secondRadius, double b)
        => (Primitive(secondRadius, b) - Primitive(firstRadius, b)) / b;

    private static double Primitive(double radius, double b)
        => 0.5 * ((radius * Math.Sqrt((radius * radius) + (b * b)))
            + (b * b * Math.Asinh(radius / b)));
}
