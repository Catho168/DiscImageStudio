namespace DiscImageStudio.Core.Calibration;

/// <summary>Continuous track envelope matching the CD and DVD encoders.</summary>
internal sealed class CalibrationGeometry
{
    private readonly CalibrationParameters _parameters;
    private readonly double _b;
    private readonly double _squaredRange;
    private readonly DvdTrackGeometry? _dvd;

    internal double RadialGrowthPerRadianMm => _b;
    internal bool IsConstantPitch => _dvd?.IsConstantPitch ?? true;

    internal CalibrationGeometry(CalibrationParameters parameters)
    {
        _parameters = parameters;
        _squaredRange = parameters.OuterRadiusMm * parameters.OuterRadiusMm
            - parameters.InnerRadiusMm * parameters.InnerRadiusMm;
        _b = _squaredRange / (2 * parameters.TrackLengthMm);
        if (parameters.Kind == CalibrationDiscKind.Dvd)
        {
            _dvd = DvdTrackGeometry.Create(parameters.InnerRadiusMm, parameters.OuterRadiusMm,
                parameters.TrackLengthMm, parameters.PitchLinear, parameters.PitchQuadratic, parameters.PitchCubic);
            _b = _dvd.RadialGrowthPerRadianMm;
        }
    }

    internal double RadialGrowthAtRadius(double radius) => _dvd?.RadialGrowthAtRadius(radius) ?? _b;

    internal (double Minimum, double Maximum) RadialGrowthBounds(double first, double last)
    {
        if (IsConstantPitch) return (_b, _b);
        double width = _parameters.OuterRadiusMm - _parameters.InnerRadiusMm;
        double low = (first - _parameters.InnerRadiusMm) / width;
        double high = (last - _parameters.InnerRadiusMm) / width;
        double a = _parameters.PitchLinear, b = _parameters.PitchQuadratic, c = _parameters.PitchCubic;
        double Q(double x) => 1 + x * (a + x * (b + x * c));
        double minimum = Math.Min(Q(low), Q(high)), maximum = Math.Max(Q(low), Q(high));
        void Include(double x)
        {
            if (x <= low || x >= high || !double.IsFinite(x)) return;
            minimum = Math.Min(minimum, Q(x)); maximum = Math.Max(maximum, Q(x));
        }
        if (c == 0) { if (b != 0) Include(-a / (2 * b)); }
        else
        {
            double discriminant = b * b - 3 * c * a;
            if (discriminant >= 0)
            {
                double root = Math.Sqrt(discriminant);
                Include((-b - root) / (3 * c)); Include((-b + root) / (3 * c));
            }
        }
        return (minimum * _b, maximum * _b);
    }

    internal double ProgressAtRadius(double radius) => Math.Clamp(_dvd is null
        ? (radius * radius - _parameters.InnerRadiusMm * _parameters.InnerRadiusMm) / _squaredRange
        : _dvd.ArcLengthAtRadius(radius) / _parameters.TrackLengthMm, 0, 1);

    internal double AngleAtRadius(double radius) => _dvd?.AngleAtRadius(radius)
        ?? (radius - _parameters.InnerRadiusMm) / _b;

    internal (double Radius, double Angle) AtProgress(double progress)
    {
        double radius = _dvd is null
            ? Math.Sqrt(_parameters.InnerRadiusMm * _parameters.InnerRadiusMm + progress * _squaredRange)
            : _dvd.RadiusAtArcLength(progress * _parameters.TrackLengthMm);
        return (radius, AngleAtRadius(radius));
    }

    /// <summary>Continue the model only while radius and local pitch stay positive.</summary>
    internal bool TryProgressAtRadiusExtended(double radius, out double progress)
    {
        if (_dvd is not null) return _dvd.TryProgressAtRadiusExtended(radius, out progress);
        progress = double.NaN;
        if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(radius * radius)) return false;
        progress = (radius - _parameters.InnerRadiusMm) * (radius + _parameters.InnerRadiusMm) / _squaredRange;
        return double.IsFinite(progress);
    }

    internal bool TryAtProgressExtended(double progress, out (double Radius, double Angle) point)
    {
        if (_dvd is not null) return _dvd.TryAtProgressExtended(progress, out point);
        point = (double.NaN, double.NaN);
        if (!double.IsFinite(progress)) return false;
        double squared = _parameters.InnerRadiusMm * _parameters.InnerRadiusMm + progress * _squaredRange;
        if (!double.IsFinite(squared) || squared <= 0) return false;
        double radius = Math.Sqrt(squared), angle = AngleAtRadius(radius);
        if (!double.IsFinite(angle)) return false;
        point = (radius, angle);
        return true;
    }
}
