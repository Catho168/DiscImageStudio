using System.Runtime.Intrinsics;

namespace DiscImageStudio.Cd;

/// <summary>Four independent nearest-neighbour samples, preserving the scalar pixel decisions.</summary>
internal sealed class CdImageSampler
{
    private readonly RasterImage _source;
    private readonly CdDiscParameters _parameters;
    private readonly double _innerSquared;
    private readonly double _radiusSquaredRange;
    private readonly double _angleScale;

    internal CdImageSampler(RasterImage source, CdDiscParameters parameters)
    {
        _source = source;
        _parameters = parameters;
        _innerSquared = parameters.InnerRadiusMm * parameters.InnerRadiusMm;
        _radiusSquaredRange = (parameters.OuterRadiusMm * parameters.OuterRadiusMm) - _innerSquared;
        _angleScale = 2.0 * parameters.LinearVelocityMmPerSecond * parameters.TotalTimeSeconds / _radiusSquaredRange;
    }

    internal bool SupportsSimd => Vector256.IsHardwareAccelerated
        && double.IsFinite(_innerSquared) && double.IsFinite(_radiusSquaredRange)
        && _radiusSquaredRange > 0 && double.IsFinite(_angleScale);

    internal byte Sample(long globalByte)
    {
        (double x, double y) = _parameters.ImagePointFromByte(globalByte, _source.Width);
        return _source.GrayAt(x, y);
    }

    internal uint SampleFour(long firstByte)
    {
        // Each lane keeps the original operation order. In particular, division
        // is not replaced by multiplication with a cached reciprocal or FMA.
        Vector256<double> indices = Vector256.Create(
            (double)firstByte, (double)(firstByte + 1), (double)(firstByte + 2), (double)(firstByte + 3));
        Vector256<double> fraction = indices / Vector256.Create((double)_parameters.TotalBytes);
        Vector256<double> radius = Vector256.Sqrt(Vector256.Create(_innerSquared)
            + fraction * Vector256.Create(_radiusSquaredRange));
        Vector256<double> angle = Vector256.Create(_parameters.StartAngleRadians)
            + Vector256.Create(_angleScale) * (radius - Vector256.Create(_parameters.InnerRadiusMm));
        if (!Vector256.LessThanOrEqualAll(Vector256.Abs(angle), Vector256.Create(1_000_000d)))
            return SampleFourScalar(firstByte);

        (Vector256<double> sin, Vector256<double> cos) = Vector256.SinCos(angle);
        double centre = _source.Width / 2.0;
        Vector256<double> pixelRadius = Vector256.Create(centre) * radius
            / Vector256.Create(_parameters.ImageOuterRadiusMm);
        Vector256<double> x = Vector256.Create(centre) + pixelRadius * cos;
        Vector256<double> y = Vector256.Create(centre) + pixelRadius * sin;
        uint samples = 0;
        for (int lane = 0; lane < 4; lane++)
        {
            if (!_source.TryGrayAtSimd(x.GetElement(lane), y.GetElement(lane), out byte gray))
                gray = Sample(firstByte + lane);
            samples |= (uint)gray << (lane * 8);
        }
        return samples;
    }

    private uint SampleFourScalar(long firstByte) => (uint)Sample(firstByte)
        | ((uint)Sample(firstByte + 1) << 8)
        | ((uint)Sample(firstByte + 2) << 16)
        | ((uint)Sample(firstByte + 3) << 24);
}
