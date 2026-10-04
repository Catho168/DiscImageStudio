namespace DiscImageStudio.Core;

/// <summary>
/// DVD spiral whose local radial pitch is a cubic in normalized radius:
/// dr/dθ = b (1 + c1 x + c2 x² + c3 x³), x = (r - ri) / (ro - ri).
/// The scale b is solved from the full physical track length. Zero coefficients
/// retain the original Archimedean calculation, including its fast inverse.
/// </summary>
public sealed class DvdTrackGeometry
{
    private readonly double _linear;
    private readonly double _quadratic;
    private readonly double _cubic;
    private readonly double _range;
    private readonly Segment[] _segments;
    private readonly double _lengthScale = 1;

    // Positive half of the eight-point Gauss-Legendre rule.
    private static readonly double[] Nodes =
        [0.1834346424956498, 0.5255324099163290, 0.7966664774136267, 0.9602898564975363];
    private static readonly double[] Weights =
        [0.3626837833783620, 0.3137066458778873, 0.2223810344533745, 0.1012285362903763];

    private DvdTrackGeometry(double innerRadiusMm, double outerRadiusMm, double trackLengthMm,
        double linear, double quadratic, double cubic)
    {
        InnerRadiusMm = innerRadiusMm;
        OuterRadiusMm = outerRadiusMm;
        TrackLengthMm = trackLengthMm;
        _range = outerRadiusMm - innerRadiusMm;
        _linear = linear;
        _quadratic = quadratic;
        _cubic = cubic;
        IsConstantPitch = linear == 0 && quadratic == 0 && cubic == 0;
        _segments = [];

        if (IsConstantPitch)
        {
            double low = 1e-15;
            double high = Math.Max(outerRadiusMm, 1.0);
            // Preserve the old solve for normal DVD lengths while also allowing
            // valid, unusually short spirals whose growth exceeds the outer radius.
            while (ConstantArc(innerRadiusMm, outerRadiusMm, high) > trackLengthMm)
                high *= 2;
            for (int iteration = 0; iteration < 120; iteration++)
            {
                double middle = (low + high) / 2;
                if (ConstantArc(innerRadiusMm, outerRadiusMm, middle) > trackLengthMm) low = middle;
                else high = middle;
            }
            RadialGrowthPerRadianMm = (low + high) / 2;
            TotalAngleRadians = _range / RadialGrowthPerRadianMm;
            return;
        }

        // Solve in reciprocal growth. Its length integral is increasing and is
        // well conditioned even for small DVD pitches. The thin-track integral
        // gives an upper bracket, since sqrt(1+z²) >= z.
        double thinLength = Integrate(innerRadiusMm, outerRadiusMm, 1, thin: true).Length;
        double lower = 0;
        double upper = trackLengthMm / thinLength;
        double inverseGrowth = upper;
        for (int iteration = 0; iteration < 80; iteration++)
        {
            Integral value = Integrate(innerRadiusMm, outerRadiusMm, inverseGrowth);
            double error = value.Length - trackLengthMm;
            if (Math.Abs(error) <= trackLengthMm * 2e-15) break;
            if (error > 0) upper = inverseGrowth; else lower = inverseGrowth;
            double next = inverseGrowth - error / value.Derivative;
            if (!double.IsFinite(next) || next <= lower || next >= upper)
                next = lower + (upper - lower) / 2;
            if (next == inverseGrowth) break;
            inverseGrowth = next;
        }
        RadialGrowthPerRadianMm = 1 / inverseGrowth;
        if (!double.IsFinite(RadialGrowthPerRadianMm) || RadialGrowthPerRadianMm <= 0)
            throw new ArgumentException("The pitch coefficients cannot be resolved at this track length.");

        List<Segment> segments = [];
        BuildSegments(innerRadiusMm, outerRadiusMm, inverseGrowth, segments, 0);
        _segments = segments.ToArray();
        double arc = 0, angle = 0, arcError = 0, angleError = 0;
        for (int i = 0; i < _segments.Length; i++)
        {
            Segment segment = _segments[i];
            _segments[i] = segment with { ArcStart = arc, AngleStart = angle };
            AddCompensated(ref arc, ref arcError, segment.ArcDelta);
            AddCompensated(ref angle, ref angleError, segment.AngleDelta);
        }
        // Remove the final few rounding bits so logical progress ends exactly at ro.
        _lengthScale = trackLengthMm / arc;
        TotalAngleRadians = angle;
    }

    public double TrackLengthMm { get; }
    public double InnerRadiusMm { get; }
    public double OuterRadiusMm { get; }
    public double RadialGrowthPerRadianMm { get; }
    public double TrackPitchMm => 2 * Math.PI * RadialGrowthPerRadianMm;
    public double TotalAngleRadians { get; }
    public bool IsConstantPitch { get; }

    public static DvdTrackGeometry Create(double innerRadiusMm, double outerRadiusMm,
        double trackLengthMm, double pitchLinear = 0, double pitchQuadratic = 0, double pitchCubic = 0)
    {
        if (!double.IsFinite(innerRadiusMm) || innerRadiusMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(innerRadiusMm), "Inner radius must be positive.");
        if (!double.IsFinite(outerRadiusMm) || outerRadiusMm <= innerRadiusMm)
            throw new ArgumentOutOfRangeException(nameof(outerRadiusMm), "Outer radius must exceed inner radius.");
        if (!double.IsFinite(trackLengthMm) || trackLengthMm <= outerRadiusMm - innerRadiusMm)
            throw new ArgumentOutOfRangeException(nameof(trackLengthMm),
                "Track length must exceed the radial distance between the two radii.");
        ValidatePitchCoefficients(pitchLinear, pitchQuadratic, pitchCubic);
        return new DvdTrackGeometry(innerRadiusMm, outerRadiusMm, trackLengthMm,
            pitchLinear, pitchQuadratic, pitchCubic);
    }

    /// <summary>Checks both edges and every stationary point, including interior minima.</summary>
    public static void ValidatePitchCoefficients(double linear, double quadratic, double cubic)
    {
        if (!double.IsFinite(linear) || !double.IsFinite(quadratic) || !double.IsFinite(cubic)
            || !PositiveOnInterval(linear, quadratic, cubic, 0, 1))
            throw new ArgumentOutOfRangeException(nameof(linear),
                "DVD pitch coefficients must be finite and 1 + c1*x + c2*x² + c3*x³ must stay positive for 0 <= x <= 1.");
    }

    public double RadiusAtArcLength(double arcLengthMm)
    {
        if (!double.IsFinite(arcLengthMm) || arcLengthMm < 0 || arcLengthMm > TrackLengthMm)
            throw new ArgumentOutOfRangeException(nameof(arcLengthMm));
        if (!IsConstantPitch) return RadiusAtArcLengthFast(arcLengthMm);
        double b = RadialGrowthPerRadianMm;
        double radius = Math.Clamp(Math.Sqrt(InnerRadiusMm * InnerRadiusMm + 2 * b * arcLengthMm),
            InnerRadiusMm, OuterRadiusMm);
        for (int iteration = 0; iteration < 5; iteration++)
        {
            double error = ConstantArc(InnerRadiusMm, radius, b) - arcLengthMm;
            double derivative = Math.Sqrt(radius * radius + b * b) / b;
            radius = Math.Clamp(radius - error / derivative, InnerRadiusMm, OuterRadiusMm);
        }
        return radius;
    }

    public double RadiusAtArcLengthFast(double arcLengthMm)
    {
        if (IsConstantPitch)
        {
            double b = RadialGrowthPerRadianMm;
            double radius = Math.Clamp(Math.Sqrt(InnerRadiusMm * InnerRadiusMm + 2 * b * arcLengthMm),
                InnerRadiusMm, OuterRadiusMm);
            double error = ConstantArc(InnerRadiusMm, radius, b) - arcLengthMm;
            double derivative = Math.Sqrt(radius * radius + b * b) / b;
            return Math.Clamp(radius - error / derivative, InnerRadiusMm, OuterRadiusMm);
        }
        if (arcLengthMm <= 0) return InnerRadiusMm;
        if (arcLengthMm >= TrackLengthMm) return OuterRadiusMm;
        if (!double.IsFinite(arcLengthMm)) throw new ArgumentOutOfRangeException(nameof(arcLengthMm));
        double unscaled = arcLengthMm / _lengthScale;
        Segment segment = _segments[FindSegment(unscaled, byAngle: false)];
        double t = InvertHermite(unscaled - segment.ArcStart, segment.ArcDelta,
            segment.Width * segment.ArcDerivativeStart, segment.Width * segment.ArcDerivativeEnd);
        return segment.RadiusStart + segment.Width * t;
    }

    public double AngleAtArcLength(double arcLengthMm) => AngleAtRadius(RadiusAtArcLength(arcLengthMm));

    /// <summary>Returns both coordinates with one table search in generation hot loops.</summary>
    public (double Radius, double Angle) AtArcLengthFast(double arcLengthMm)
    {
        if (IsConstantPitch)
        {
            double radius = RadiusAtArcLengthFast(arcLengthMm);
            return (radius, AngleAtRadius(radius));
        }
        if (arcLengthMm <= 0) return (InnerRadiusMm, 0);
        if (arcLengthMm >= TrackLengthMm) return (OuterRadiusMm, TotalAngleRadians);
        if (!double.IsFinite(arcLengthMm)) throw new ArgumentOutOfRangeException(nameof(arcLengthMm));
        double unscaled = arcLengthMm / _lengthScale;
        Segment segment = _segments[FindSegment(unscaled, byAngle: false)];
        double t = InvertHermite(unscaled - segment.ArcStart, segment.ArcDelta,
            segment.Width * segment.ArcDerivativeStart, segment.Width * segment.ArcDerivativeEnd);
        return (segment.RadiusStart + segment.Width * t,
            segment.AngleStart + Hermite(t, segment.AngleDelta,
                segment.Width * segment.AngleDerivativeStart, segment.Width * segment.AngleDerivativeEnd));
    }

    public double AngleAtRadius(double radius)
    {
        if (IsConstantPitch) return (radius - InnerRadiusMm) / RadialGrowthPerRadianMm;
        if (radius < InnerRadiusMm || radius > OuterRadiusMm)
        {
            if (!TryExtendedIntegral(radius, out Integral value)) throw new ArgumentOutOfRangeException(nameof(radius));
            return value.Angle;
        }
        Segment segment = _segments[FindRadiusSegment(radius)];
        double t = (radius - segment.RadiusStart) / segment.Width;
        return segment.AngleStart + Hermite(t, segment.AngleDelta,
            segment.Width * segment.AngleDerivativeStart, segment.Width * segment.AngleDerivativeEnd);
    }

    public double ArcLengthAtRadius(double radius)
    {
        if (IsConstantPitch) return ConstantArc(InnerRadiusMm, radius, RadialGrowthPerRadianMm);
        if (radius < InnerRadiusMm || radius > OuterRadiusMm)
        {
            if (!TryExtendedIntegral(radius, out Integral value)) throw new ArgumentOutOfRangeException(nameof(radius));
            return value.Length;
        }
        if (radius == OuterRadiusMm) return TrackLengthMm;
        Segment segment = _segments[FindRadiusSegment(radius)];
        double t = (radius - segment.RadiusStart) / segment.Width;
        return _lengthScale * (segment.ArcStart + Hermite(t, segment.ArcDelta,
            segment.Width * segment.ArcDerivativeStart, segment.Width * segment.ArcDerivativeEnd));
    }

    public double ArcLengthAtAngle(double angleRadians)
    {
        double angle = Math.Clamp(angleRadians, 0, TotalAngleRadians);
        if (IsConstantPitch)
            return ConstantArc(InnerRadiusMm, InnerRadiusMm + RadialGrowthPerRadianMm * angle,
                RadialGrowthPerRadianMm);
        if (angle == 0) return 0;
        if (angle == TotalAngleRadians) return TrackLengthMm;
        Segment segment = _segments[FindSegment(angle, byAngle: true)];
        double t = InvertHermite(angle - segment.AngleStart, segment.AngleDelta,
            segment.Width * segment.AngleDerivativeStart, segment.Width * segment.AngleDerivativeEnd);
        return _lengthScale * (segment.ArcStart + Hermite(t, segment.ArcDelta,
            segment.Width * segment.ArcDerivativeStart, segment.Width * segment.ArcDerivativeEnd));
    }

    public double RadialGrowthAtRadius(double radius) => RadialGrowthPerRadianMm * Shape(radius);

    public bool TryProgressAtRadiusExtended(double radius, out double progress)
    {
        progress = double.NaN;
        if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(radius * radius)) return false;
        if (radius >= InnerRadiusMm && radius <= OuterRadiusMm)
            progress = ArcLengthAtRadius(radius) / TrackLengthMm;
        else if (IsConstantPitch)
            progress = ConstantArc(InnerRadiusMm, radius, RadialGrowthPerRadianMm) / TrackLengthMm;
        else if (TryExtendedIntegral(radius, out Integral value)) progress = value.Length / TrackLengthMm;
        return double.IsFinite(progress);
    }

    public bool TryAtProgressExtended(double progress, out (double Radius, double Angle) point)
    {
        point = (double.NaN, double.NaN);
        if (!double.IsFinite(progress) || !double.IsFinite(progress * TrackLengthMm)) return false;
        if (progress >= 0 && progress <= 1)
        {
            double r = RadiusAtArcLength(progress * TrackLengthMm);
            point = (r, AngleAtRadius(r));
            return true;
        }
        if (IsConstantPitch) return TryConstantAtProgressExtended(progress, out point);

        bool outward = progress > 1;
        double target = progress * TrackLengthMm;
        double anchor = outward ? OuterRadiusMm : InnerRadiusMm;
        double validRadius = anchor;
        double boundary = outward ? anchor + _range : 0;
        double low = 0, high = 0;
        bool bracketed = false;
        // Never step across a zero of q: the entire connecting radial interval
        // must have positive pitch, not just the candidate endpoint.
        for (int iteration = 0; iteration < 100; iteration++)
        {
            double candidate = boundary;
            if (candidate <= 0 || !TryExtendedIntegral(candidate, out Integral integral))
            {
                boundary = validRadius + (boundary - validRadius) / 2;
                if (boundary == validRadius) return false;
                continue;
            }
            bool reached = outward ? integral.Length >= target : integral.Length <= target;
            if (reached)
            {
                low = Math.Min(validRadius, candidate);
                high = Math.Max(validRadius, candidate);
                bracketed = true;
                break;
            }
            validRadius = candidate;
            boundary = outward ? anchor + 2 * (candidate - anchor) : candidate / 2;
            if (!double.IsFinite(boundary)) return false;
        }
        if (!bracketed) return false;
        double radius = low + (high - low) / 2;
        for (int iteration = 0; iteration < 64; iteration++)
        {
            if (!TryExtendedIntegral(radius, out Integral integral)) return false;
            double error = integral.Length - target;
            if (Math.Abs(error) <= Math.Max(1, Math.Abs(target)) * 4e-15)
            {
                point = (radius, integral.Angle);
                return true;
            }
            if (error > 0) high = radius; else low = radius;
            double growth = RadialGrowthAtRadius(radius);
            double derivative = Hypot(radius, growth) / growth;
            double next = radius - error / derivative;
            if (!double.IsFinite(next) || next <= low || next >= high) next = low + (high - low) / 2;
            if (next == radius)
            {
                point = (radius, integral.Angle);
                return true;
            }
            radius = next;
        }
        return false;
    }

    private bool TryConstantAtProgressExtended(double progress, out (double Radius, double Angle) point)
    {
        point = (double.NaN, double.NaN);
        double b = RadialGrowthPerRadianMm;
        double primitive = Primitive(InnerRadiusMm, b) + progress * (b * TrackLengthMm);
        if (!double.IsFinite(primitive) || primitive <= 0) return false;
        double low = 0;
        double high = Math.Min(Math.Sqrt(primitive) * Math.Sqrt(2), primitive / b);
        double radius = high;
        for (int iteration = 0; iteration < 64; iteration++)
        {
            double error = Primitive(radius, b) - primitive;
            if (Math.Abs(error) <= primitive * 4e-15)
            {
                point = (radius, AngleAtRadius(radius));
                return double.IsFinite(point.Angle);
            }
            if (error > 0) high = radius; else low = radius;
            double next = radius - error / Hypot(radius, b);
            if (!double.IsFinite(next) || next <= low || next >= high) next = low + (high - low) / 2;
            if (next == radius) return false;
            radius = next;
        }
        return false;
    }

    private bool TryExtendedIntegral(double radius, out Integral value)
    {
        value = default;
        if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(radius * radius)) return false;
        double x = (radius - InnerRadiusMm) / _range;
        if (!PositiveOnInterval(_linear, _quadratic, _cubic, Math.Min(0, x), Math.Max(1, x))) return false;
        double edge = radius < InnerRadiusMm ? InnerRadiusMm : OuterRadiusMm;
        Integral extra;
        try
        {
            extra = Integrate(Math.Min(edge, radius), Math.Max(edge, radius), 1 / RadialGrowthPerRadianMm);
        }
        catch (PitchResolutionException)
        {
            return false;
        }
        value = radius < InnerRadiusMm
            ? new Integral(-extra.Length, -extra.Angle, 0)
            : new Integral(TrackLengthMm + extra.Length, TotalAngleRadians + extra.Angle, 0);
        return double.IsFinite(value.Length) && double.IsFinite(value.Angle);
    }

    private Integral Integrate(double first, double last, double inverseGrowth, bool thin = false)
    {
        int evaluations = 0;
        return IntegrateAdaptive(first, last, inverseGrowth, thin, 0, ref evaluations);
    }

    private Integral IntegrateAdaptive(double first, double last, double inverseGrowth, bool thin,
        int depth, ref int evaluations)
    {
        evaluations += 3;
        if (evaluations > 32768)
            throw new PitchResolutionException("The DVD pitch integral exceeded its numerical resolution budget.");
        Integral whole = Quadrature(first, last, inverseGrowth, thin);
        if (!double.IsFinite(whole.Length) || !double.IsFinite(whole.Angle))
            throw new PitchResolutionException("The DVD pitch integral cannot be resolved numerically.");
        double middle = first + (last - first) / 2;
        Integral left = Quadrature(first, middle, inverseGrowth, thin);
        Integral right = Quadrature(middle, last, inverseGrowth, thin);
        Integral split = left + right;
        if (Math.Abs(split.Length - whole.Length) <= Math.Abs(split.Length) * 8e-15
            && Math.Abs(split.Angle - whole.Angle) <= Math.Abs(split.Angle) * 8e-15)
            return split;
        if (depth >= 24 || middle == first || middle == last)
            throw new PitchResolutionException("The DVD pitch integral did not converge at the requested accuracy.");
        return IntegrateAdaptive(first, middle, inverseGrowth, thin, depth + 1, ref evaluations)
            + IntegrateAdaptive(middle, last, inverseGrowth, thin, depth + 1, ref evaluations);
    }

    private Integral Quadrature(double first, double last, double inverseGrowth, bool thin = false)
    {
        double middle = first + (last - first) / 2;
        double half = (last - first) / 2;
        double length = 0, angle = 0, derivative = 0;
        for (int i = 0; i < Nodes.Length; i++)
        {
            for (int sign = -1; sign <= 1; sign += 2)
            {
                double radius = middle + sign * half * Nodes[i];
                double inverseShape = 1 / Shape(radius);
                double radial = radius * inverseShape;
                double z = inverseGrowth * radial;
                double ds = thin ? radial : Hypot(1, z);
                length += Weights[i] * ds;
                angle += Weights[i] * inverseShape * inverseGrowth;
                if (!thin) derivative += Weights[i] * radial * (z / ds);
            }
        }
        return new Integral(half * length, half * angle, half * derivative);
    }

    private void BuildSegments(double first, double last, double inverseGrowth, List<Segment> segments, int depth)
    {
        if (segments.Count >= 32768)
            throw new PitchResolutionException("The DVD pitch curve is too close to zero to resolve numerically.");
        double width = last - first;
        double middle = first + width / 2;
        Integral whole = Quadrature(first, last, inverseGrowth);
        Integral left = Quadrature(first, middle, inverseGrowth);
        Integral right = Quadrature(middle, last, inverseGrowth);
        Integral split = left + right;
        double a0 = inverseGrowth / Shape(first), a1 = inverseGrowth / Shape(last);
        double s0 = Hypot(1, first * a0), s1 = Hypot(1, last * a1);
        // Validate at quarter points as well as the midpoint: an odd interpolation
        // error can vanish at the midpoint of a cubic-pitch interval.
        const double angleTolerance = 5e-10;
        double lengthTolerance = angleTolerance * InnerRadiusMm;
        bool accepted = Math.Abs(split.Angle - whole.Angle) < angleTolerance / 16
            && Math.Abs(split.Length - whole.Length) < lengthTolerance / 16;
        for (int i = 1; accepted && i <= 3; i++)
        {
            double t = i / 4.0;
            Integral actual = i == 2 ? left : Quadrature(first, first + width * t, inverseGrowth);
            accepted = Math.Abs(Hermite(t, split.Angle, width * a0, width * a1) - actual.Angle) < angleTolerance
                && Math.Abs(Hermite(t, split.Length, width * s0, width * s1) - actual.Length) < lengthTolerance;
        }
        if (!accepted && depth < 24 && middle != first && middle != last)
        {
            BuildSegments(first, middle, inverseGrowth, segments, depth + 1);
            BuildSegments(middle, last, inverseGrowth, segments, depth + 1);
            return;
        }
        if (!accepted || !double.IsFinite(split.Length) || !double.IsFinite(split.Angle) || split.Length <= 0)
            throw new PitchResolutionException("The DVD pitch curve cannot be resolved numerically.");
        segments.Add(new Segment(first, width, 0, 0, split.Length, split.Angle, s0, s1, a0, a1));
    }

    private int FindRadiusSegment(double radius)
    {
        int low = 0, high = _segments.Length - 1;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (_segments[middle].RadiusStart <= radius) low = middle; else high = middle - 1;
        }
        return low;
    }

    private int FindSegment(double value, bool byAngle)
    {
        int low = 0, high = _segments.Length - 1;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            double start = byAngle ? _segments[middle].AngleStart : _segments[middle].ArcStart;
            if (start <= value) low = middle; else high = middle - 1;
        }
        return low;
    }

    private static double Hermite(double t, double delta, double derivativeStart, double derivativeEnd)
        => t * (derivativeStart + t * (3 * delta - 2 * derivativeStart - derivativeEnd
            + t * (derivativeStart + derivativeEnd - 2 * delta)));

    private static double InvertHermite(double value, double delta, double derivativeStart, double derivativeEnd)
    {
        double t = Math.Clamp(value / delta, 0, 1);
        double quadratic = 3 * delta - 2 * derivativeStart - derivativeEnd;
        double cubic = derivativeStart + derivativeEnd - 2 * delta;
        for (int i = 0; i < 4; i++)
        {
            double error = t * (derivativeStart + t * (quadratic + t * cubic)) - value;
            double derivative = derivativeStart + t * (2 * quadratic + 3 * t * cubic);
            t = Math.Clamp(t - error / derivative, 0, 1);
        }
        return t;
    }

    private double Shape(double radius) => Polynomial(_linear, _quadratic, _cubic, (radius - InnerRadiusMm) / _range);
    private static double Polynomial(double linear, double quadratic, double cubic, double x)
        => ((cubic * x + quadratic) * x + linear) * x + 1;

    private static bool PositiveOnInterval(double linear, double quadratic, double cubic, double first, double last)
    {
        bool Positive(double x)
        {
            double value = Polynomial(linear, quadratic, cubic, x);
            return double.IsFinite(value) && value > 0;
        }
        if (!Positive(first) || !Positive(last)) return false;
        // Scale the derivative before solving, avoiding overflow of its discriminant.
        double scale = Math.Max(Math.Abs(linear), Math.Max(Math.Abs(quadratic), Math.Abs(cubic)));
        if (scale == 0) return true;
        double a = 3 * (cubic / scale), b = 2 * (quadratic / scale), c = linear / scale;
        bool Check(double x) => !(x > first && x < last) || Positive(x);
        if (a == 0) return b == 0 || Check(-c / b);
        double discriminant = b * b - 4 * a * c;
        if (discriminant < 0) return true;
        double q = -0.5 * (b + Math.CopySign(Math.Sqrt(discriminant), b));
        if (q == 0) return Check(-b / (2 * a));
        return Check(q / a) && Check(c / q);
    }

    private static double Hypot(double first, double second)
    {
        first = Math.Abs(first);
        second = Math.Abs(second);
        double maximum = Math.Max(first, second);
        if (maximum == 0) return 0;
        double ratio = Math.Min(first, second) / maximum;
        return maximum * Math.Sqrt(1 + ratio * ratio);
    }

    private static void AddCompensated(ref double sum, ref double error, double value)
    {
        double adjusted = value - error;
        double next = sum + adjusted;
        error = (next - sum) - adjusted;
        sum = next;
    }

    private static double ConstantArc(double first, double second, double b)
        => (Primitive(second, b) - Primitive(first, b)) / b;
    private static double Primitive(double radius, double b)
        => 0.5 * (radius * Math.Sqrt(radius * radius + b * b) + b * b * Math.Asinh(radius / b));

    private readonly record struct Integral(double Length, double Angle, double Derivative)
    {
        public static Integral operator +(Integral a, Integral b)
            => new(a.Length + b.Length, a.Angle + b.Angle, a.Derivative + b.Derivative);
    }

    private readonly record struct Segment(double RadiusStart, double Width, double ArcStart, double AngleStart,
        double ArcDelta, double AngleDelta, double ArcDerivativeStart, double ArcDerivativeEnd,
        double AngleDerivativeStart, double AngleDerivativeEnd);

    private sealed class PitchResolutionException(string message) : ArgumentException(message);
}
