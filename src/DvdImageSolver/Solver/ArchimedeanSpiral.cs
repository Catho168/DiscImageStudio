using DiscImageStudio.Core;

namespace DvdImageSolver.Solver;

// Existing callers keep this name; cubic coefficients enable a varying radial pitch.
internal sealed class ArchimedeanSpiral(DvdTrackGeometry geometry)
{
    internal double TrackLengthMm => geometry.TrackLengthMm;
    internal double InnerRadiusMm => geometry.InnerRadiusMm;
    internal double OuterRadiusMm => geometry.OuterRadiusMm;
    internal double RadialGrowthPerRadianMm => geometry.RadialGrowthPerRadianMm;
    internal double TrackPitchMm => geometry.TrackPitchMm;
    internal double TotalAngleRadians => geometry.TotalAngleRadians;
    internal static ArchimedeanSpiral Create(double innerRadiusMm, double outerRadiusMm,
        double trackLengthMm, double pitchLinear = 0, double pitchQuadratic = 0, double pitchCubic = 0)
        => new(DvdTrackGeometry.Create(innerRadiusMm, outerRadiusMm, trackLengthMm,
            pitchLinear, pitchQuadratic, pitchCubic));
    internal double RadiusAtArcLength(double arcLengthMm) => geometry.RadiusAtArcLength(arcLengthMm);
    internal double RadiusAtArcLengthFast(double arcLengthMm) => geometry.RadiusAtArcLengthFast(arcLengthMm);
    internal double AngleAtArcLength(double arcLengthMm) => geometry.AngleAtArcLength(arcLengthMm);
    internal (double Radius, double Angle) AtArcLengthFast(double arcLengthMm) => geometry.AtArcLengthFast(arcLengthMm);
    internal double AngleAtRadius(double radiusMm) => geometry.AngleAtRadius(radiusMm);
    internal double ArcLengthAtRadius(double radiusMm) => geometry.ArcLengthAtRadius(radiusMm);
    internal double ArcLengthAtAngle(double angleRadians) => geometry.ArcLengthAtAngle(angleRadians);
    internal double RadialGrowthAtRadius(double radiusMm) => geometry.RadialGrowthAtRadius(radiusMm);
}
