using DiscImageStudio.Core.Calibration;

internal static class CalibrationCrossExtensionTests
{
    internal static void Run()
    {
        foreach (CalibrationDiscKind kind in Enum.GetValues<CalibrationDiscKind>())
        {
            TestIdentityAndDirection(kind);
            TestExtendedRecovery(kind);
            TestInvalidDomain(kind);
            TestOutsidePointsContribute(kind);
            TestSubtleAndWindingOutline(kind);
        }
        TestNonThinDvdContinuation();
        TestCompleteUserRecord();
        Console.WriteLine("calibration-cross-extension: full observed support, exact continuation, forward curves and real 10-point regression passed");
    }

    private static CalibrationParameters Parameters(CalibrationDiscKind kind) => kind == CalibrationDiscKind.Cd
        ? new(kind, 24.5, 56.8, 359849, 1200) : new(kind, 24, 58, 2295104, 133.33);

    private static void TestIdentityAndDirection(CalibrationDiscKind kind)
    {
        var p = Parameters(kind);
        var target = CalibrationTarget.Create(p);
        const double rotation = 0.613;
        var identity = CalibrationCrossPrediction.CreateMapping(target, p, rotation, armIndex: 2);
        foreach (double radius in new[] { 0.2, 7.5, p.InnerRadiusMm - 1, p.InnerRadiusMm,
            40, p.OuterRadiusMm, p.OuterRadiusMm + 2, 120 })
            Near(0, Distance(identity(radius), Polar(radius, Math.PI / 2 + rotation)), 1e-11,
                $"{kind} equal parameters are exactly straight inside and outside generation support");
        var actual = p with { InnerRadiusMm = p.InnerRadiusMm + 0.0017, OuterRadiusMm = p.OuterRadiusMm - 0.0011 };
        var prediction = CalibrationCrossPrediction.CreateMapping(target, actual, rotation);
        foreach (double radius in new[] { p.InnerRadiusMm + 2, 38, p.OuterRadiusMm - 2 })
        {
            CalibrationPoint source = Polar(radius, -Math.PI / 2);
            CalibrationPoint encodedForward = target.MapPoint(source, actual, rotation);
            Near(0, Distance(encodedForward, prediction(Radius(encodedForward))), 2e-6,
                $"{kind} prediction uses actual-minus-generated phase, not the inverse correction sign");
        }
        // Bounded engraving mappings retain their original behavior.
        Throws<ArgumentException>(() => target.MapPoint(Polar(p.InnerRadiusMm - 0.1, 0), actual), "source engraving bounds unchanged");
        Check(target.CreateInverseMapping(actual)(Polar(actual.InnerRadiusMm - 0.1, 0)) is null, "inverse engraving bounds unchanged");
    }

    private static void TestExtendedRecovery(CalibrationDiscKind kind)
    {
        var p = Parameters(kind);
        var actual = p with { InnerRadiusMm = p.InnerRadiusMm + 0.0017, OuterRadiusMm = p.OuterRadiusMm - 0.0011 };
        var target = CalibrationTarget.Create(p);
        const double rotation = -0.71;
        Func<double, double> phase = ReferencePhase(p, actual);
        double low = p.InnerRadiusMm - 0.7, high = p.OuterRadiusMm + 0.8;
        CalibrationPoint[] points = Enumerable.Range(0, 33).Select(i =>
        {
            double radius = low + (high - low) * Math.Pow(i / 32.0, 1.2);
            return Polar(radius, -Math.PI / 2 + rotation + phase(radius));
        }).ToArray();
        var fit = CalibrationCrossFitter.Fit(target, [new(0, points)]);
        Check(fit.Succeeded && fit.BestParameters is not null, $"{kind} all observed radii participate beyond both preset bounds");
        Near(actual.InnerRadiusMm, fit.BestParameters!.InnerRadiusMm, 5e-7, $"{kind} extended inner recovery");
        Near(actual.OuterRadiusMm, fit.BestParameters.OuterRadiusMm, 5e-7, $"{kind} extended outer recovery");
        Near(0, Normalize(fit.RotationRadians - rotation), 8e-5, $"{kind} extended rotation recovery");
        Check(fit.RmsErrorMm < 2e-5, "independent numerical arc integration agrees with fitted continuation");
        var outline = CalibrationCrossPrediction.GetOutline(target, fit.BestParameters, low, high, fit.RotationRadians);
        Near(low, Radius(outline[0]), 1e-10, "prediction starts at the measured inner radius");
        Near(high, Radius(outline[^1]), 1e-10, "prediction finishes at the measured outer radius");
        Check(points.All(point => DistanceToPolyline(point, outline) < 0.02), "the entire fitted trace agrees with forward prediction");
    }

    private static void TestInvalidDomain(CalibrationDiscKind kind)
    {
        var p = Parameters(kind);
        var target = CalibrationTarget.Create(p);
        var actual = p with { InnerRadiusMm = p.InnerRadiusMm + 5 };
        var mapping = CalibrationCrossPrediction.CreateMapping(target, actual);
        Throws<ArgumentException>(() => mapping(1), "negative equivalent source-radius domain is rejected, not clamped");
        Throws<ArgumentException>(() => mapping(0), "disc-centre polar singularity is explicit");
        Throws<ArgumentException>(() => mapping(double.NaN), "nonfinite support is invalid");
        Throws<ArgumentException>(() => CalibrationCrossPrediction.GetOutline(target, actual, 1, 17), "outline cannot skip an invalid endpoint");
        CalibrationPoint[] points = new[] { 1.0, 4, 8, 10, 14, 17 }.Select(radius => Polar(radius, 0.3)).ToArray();
        var fit = CalibrationCrossFitter.Fit(target, [new(0, points)], new(SearchCenter: actual, RadiusRangeMm: 0.005));
        Check(!fit.Succeeded && fit.Candidates.Count == 0 && fit.Warnings.Any(w => w.Contains("正半径", StringComparison.Ordinal)),
            "all-candidate domain failure is clear and retains every observation");
    }

    private static void TestOutsidePointsContribute(CalibrationDiscKind kind)
    {
        var p = Parameters(kind);
        var target = CalibrationTarget.Create(p);
        double[] radii = [p.InnerRadiusMm - 0.8, p.InnerRadiusMm - 0.2, p.InnerRadiusMm + 3, 35, 43,
            p.OuterRadiusMm - 2, p.OuterRadiusMm + 0.2, p.OuterRadiusMm + 0.8];
        CalibrationPoint[] original = radii.Select(radius => Polar(radius, 0.31)).ToArray();
        var baseline = CalibrationCrossFitter.Fit(target, [new(0, original)]);
        Check(baseline.Succeeded && baseline.RmsErrorMm < 1e-8, "straight observation across both boundaries remains an identity model");
        foreach (int index in new[] { 0, original.Length - 1 })
        {
            CalibrationPoint[] changed = original.ToArray();
            changed[index] = Polar(radii[index], 0.313);
            var fit = CalibrationCrossFitter.Fit(target, [new(0, changed)]);
            Check(fit.Succeeded && fit.RmsErrorMm > 0.001 && fit.BestParameters != baseline.BestParameters,
                "a changed outside point affects the objective and parameters instead of being silently removed");
            Check(changed[index] == Polar(radii[index], 0.313), "input coordinates remain untouched");
        }
    }

    private static void TestSubtleAndWindingOutline(CalibrationDiscKind kind)
    {
        var p = Parameters(kind);
        var target = CalibrationTarget.Create(p);
        double low = p.InnerRadiusMm - 0.5, high = p.OuterRadiusMm + 0.5;
        var subtle = p with { InnerRadiusMm = p.InnerRadiusMm + 0.000004, OuterRadiusMm = p.OuterRadiusMm + 0.000004 };
        var mapping = CalibrationCrossPrediction.CreateMapping(target, subtle, 0.2);
        var outline = CalibrationCrossPrediction.GetOutline(target, subtle, low, high, 0.2);
        Check(outline.Count >= Math.Ceiling((high - low) / 0.2) + 1,
            "subtle curvature is sampled even when total phase changes less than five degrees");
        var chord = new[] { outline[0], outline[^1] };
        Check(outline.Max(point => DistanceToPolyline(point, chord)) > 0.02, "a visibly bowed parameter prediction is not replaced by its straight chord");
        foreach (double radius in Enumerable.Range(0, 501).Select(i => low + (high - low) * i / 500))
            Check(DistanceToPolyline(mapping(radius), outline) < 0.011, "fine forward curve remains within the chord error tolerance");

        var winding = p with { InnerRadiusMm = p.InnerRadiusMm + 0.04, OuterRadiusMm = p.OuterRadiusMm + 0.04 };
        Func<double, double> phase = ReferencePhase(p, winding);
        var wrapped = CalibrationCrossPrediction.GetOutline(target, winding, low, high, 0.8);
        double unwrapped = wrapped.Zip(wrapped.Skip(1)).Sum(pair => Normalize(Angle(pair.Second) - Angle(pair.First)));
        Near(phase(high) - phase(low), unwrapped, 2e-5, "full winding count survives coincident or nearby angular endpoints");
        foreach (var pair in wrapped.Zip(wrapped.Skip(1)))
            Check(Math.Abs(Normalize(Angle(pair.Second) - Angle(pair.First))) <= Math.PI / 36 + 1e-7, "unwrapped phase sampling bound");
        Throws<InvalidOperationException>(() => CalibrationCrossPrediction.GetOutline(target, winding, low, high, maxPoints: 16), "point budget exhaustion is explicit");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Throws<OperationCanceledException>(() => CalibrationCrossPrediction.GetOutline(target, p, low, high,
            cancellationToken: cancellation.Token), "prediction cancellation");
    }

    private static void TestNonThinDvdContinuation()
    {
        // With very short total length, b is not negligible. Numerical quadrature is an
        // independent reference which detects a hidden r² approximation in DVD continuation.
        var p = new CalibrationParameters(CalibrationDiscKind.Dvd, 24, 58, 16, 133.33);
        var actual = p with { InnerRadiusMm = 24.07, OuterRadiusMm = 57.95 };
        var target = CalibrationTarget.Create(p);
        Func<double, double> phase = ReferencePhase(p, actual);
        var mapping = CalibrationCrossPrediction.CreateMapping(target, actual, 0.3);
        foreach (double radius in new[] { 20.0, 23.5, 24, 38, 58, 61 })
            Near(0, Distance(mapping(radius), Polar(radius, -Math.PI / 2 + 0.3 + phase(radius))), 1e-7,
                "DVD continuation retains exact arc length on both sides of generation bounds");
    }

    private static void TestCompleteUserRecord()
    {
        var p = new CalibrationParameters(CalibrationDiscKind.Dvd, 23.9968875, 57.9779875, 2297888, 133.33);
        var target = CalibrationTarget.Create(p);
        CalibrationPoint[] points =
        [
            new(5.351503459785284, -23.20680086238514), new(6.56368510376428, -26.5854106189566),
            new(7.454068070802904, -30.312922955106217), new(8.298604485217327, -34.38626843977292),
            new(8.948837997727669, -38.2033721484917), new(9.411218632270403, -42.07827674273897),
            new(10.18426580168618, -45.75087823763653), new(11.164423994350658, -49.59688238911955),
            new(12.659955783187392, -53.17557066987591), new(14.271766707805234, -56.010394939672025),
        ];
        var original = points.ToArray();
        var fit = CalibrationCrossFitter.Fit(target, [new(0, points)], new(RadiusRangeMm: 0.005));
        Check(fit.Succeeded && fit.BestParameters is not null && fit.RmsErrorMm is > 0.05 and < 0.6,
            "all ten real photo points fit in the normal search range without a forced inner-domain boundary");
        Check(points.SequenceEqual(original), "the complete user trace remains unchanged");
        Check(fit.BestParameters!.InnerRadiusMm > Radius(points[0]) + 0.17,
            "the preset inner parameter is not forced down onto the measured first point");
        Check(Math.Abs(fit.BestParameters.InnerRadiusMm - p.InnerRadiusMm) < 0.001
            && Math.Abs(fit.BestParameters.OuterRadiusMm - p.OuterRadiusMm) < 0.001, "real record optimum remains inside the intended parameter search");
        var nine = CalibrationCrossFitter.Fit(target, [new(0, points.Skip(1).ToArray())], new(RadiusRangeMm: 0.005));
        Check(fit.BestParameters != nine.BestParameters && Math.Abs(fit.RmsErrorMm - nine.RmsErrorMm) > 0.001,
            "the formerly rejected first point changes the fitted result");
        var outline = CalibrationCrossPrediction.GetOutline(target, fit.BestParameters, Radius(points[0]), Radius(points[^1]), fit.RotationRadians);
        Near(Radius(points[0]), Radius(outline[0]), 1e-10, "real inner point is included in prediction support");
        Near(Radius(points[^1]), Radius(outline[^1]), 1e-10, "real outer point is included in prediction support");
        Check(outline.Max(point => DistanceToPolyline(point, [outline[0], outline[^1]])) > 0.02,
            "the fitted full-record prediction retains its physical curvature");
    }

    private static Func<double, double> ReferencePhase(CalibrationParameters generated, CalibrationParameters actual)
    {
        double bg = Growth(generated), ba = Growth(actual);
        return radius =>
        {
            double source;
            if (generated.Kind == CalibrationDiscKind.Cd)
            {
                double progress = (radius * radius - actual.InnerRadiusMm * actual.InnerRadiusMm)
                    / (actual.OuterRadiusMm * actual.OuterRadiusMm - actual.InnerRadiusMm * actual.InnerRadiusMm);
                source = Math.Sqrt(generated.InnerRadiusMm * generated.InnerRadiusMm + progress
                    * (generated.OuterRadiusMm * generated.OuterRadiusMm - generated.InnerRadiusMm * generated.InnerRadiusMm));
            }
            else
            {
                double distance = IntegratedArc(actual.InnerRadiusMm, radius, ba);
                double low = 0, high = Math.Max(generated.OuterRadiusMm * 2, radius * 2);
                for (int i = 0; i < 90; i++)
                {
                    double middle = (low + high) / 2;
                    if (IntegratedArc(generated.InnerRadiusMm, middle, bg) < distance) low = middle; else high = middle;
                }
                source = (low + high) / 2;
            }
            return (radius - actual.InnerRadiusMm) / ba - (source - generated.InnerRadiusMm) / bg;
        };
    }

    private static double Growth(CalibrationParameters p)
    {
        if (p.Kind == CalibrationDiscKind.Cd)
            return (p.OuterRadiusMm * p.OuterRadiusMm - p.InnerRadiusMm * p.InnerRadiusMm) / (2 * p.TrackLengthMm);
        double low = 1e-12, high = p.OuterRadiusMm;
        for (int i = 0; i < 90; i++)
        {
            double middle = (low + high) / 2;
            if (IntegratedArc(p.InnerRadiusMm, p.OuterRadiusMm, middle) > p.TrackLengthMm) low = middle; else high = middle;
        }
        return (low + high) / 2;
    }

    private static double IntegratedArc(double first, double last, double b)
    {
        const int intervals = 256;
        double step = (last - first) / intervals;
        double sum = Math.Sqrt(first * first + b * b) + Math.Sqrt(last * last + b * b);
        for (int i = 1; i < intervals; i++)
        {
            double r = first + i * step;
            sum += (i % 2 == 0 ? 2 : 4) * Math.Sqrt(r * r + b * b);
        }
        return sum * step / (3 * b);
    }

    private static double Radius(CalibrationPoint p) => Math.Sqrt(p.X * p.X + p.Y * p.Y);
    private static double Angle(CalibrationPoint p) => Math.Atan2(p.Y, p.X);
    private static double Normalize(double a) => Math.Atan2(Math.Sin(a), Math.Cos(a));
    private static CalibrationPoint Polar(double r, double a) => new(r * Math.Cos(a), r * Math.Sin(a));
    private static double Distance(CalibrationPoint p, CalibrationPoint q) => Math.Sqrt(Math.Pow(p.X - q.X, 2) + Math.Pow(p.Y - q.Y, 2));
    private static double DistanceToPolyline(CalibrationPoint point, IReadOnlyList<CalibrationPoint> line)
    {
        double best = double.PositiveInfinity;
        for (int i = 1; i < line.Count; i++)
        {
            double dx = line[i].X - line[i - 1].X, dy = line[i].Y - line[i - 1].Y;
            double t = Math.Clamp(((point.X - line[i - 1].X) * dx + (point.Y - line[i - 1].Y) * dy) / Math.Max(1e-30, dx * dx + dy * dy), 0, 1);
            best = Math.Min(best, Distance(point, new(line[i - 1].X + t * dx, line[i - 1].Y + t * dy)));
        }
        return best;
    }
    private static void Near(double expected, double actual, double tolerance, string name)
        => Check(double.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance, $"{name}: expected {expected:G17}, actual {actual:G17}");
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("calibration-cross-extension: " + name);
    }
    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("calibration-cross-extension: expected " + typeof(T).Name + "; " + name);
    }
}
