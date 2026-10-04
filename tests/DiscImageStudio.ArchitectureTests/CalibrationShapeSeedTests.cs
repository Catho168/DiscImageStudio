using DiscImageStudio.Core.Calibration;

internal static class CalibrationShapeSeedTests
{
    internal static void Run()
    {
        foreach (CalibrationDiscKind kind in Enum.GetValues<CalibrationDiscKind>())
            TestWideOffCentreSearch(kind);
        TestMultipleArmOffsets();
        TestNonThinDvdFallback();
        TestInvalidShapeApproximationFallback();
        TestExistingInputGuards();
        Console.WriteLine("calibration-shape-seed: wide off-centre searches, independent geometry, multiple arms and exact fallback passed");
    }

    private static CalibrationParameters Parameters(CalibrationDiscKind kind) => kind == CalibrationDiscKind.Cd
        ? new(kind, 24, 58, 359849, 1200) : new(kind, 24, 58, 2297888, 133.3);

    private static void TestWideOffCentreSearch(CalibrationDiscKind kind)
    {
        var generated = Parameters(kind);
        var actual = generated with { InnerRadiusMm = 24.0071, OuterRadiusMm = 57.9947 };
        var center = generated with { InnerRadiusMm = 25.7071, OuterRadiusMm = 56.6947 };
        var target = CalibrationTarget.Create(generated);
        const double rotation = -1.037;
        var trace = IndependentTrace(generated, actual, rotation, 0, 257);
        var fit = CalibrationCrossFitter.Fit(target, [trace], new(center, RadiusRangeMm: 2));
        CheckRecovery(fit, actual, rotation, 3e-7, $"{kind} broad search with displaced search centre");
        // Observed endpoints intentionally extend beyond the generation band.
        var outline = CalibrationCrossPrediction.GetOutline(target, fit.BestParameters!,
            Radius(trace.Points[0]), Radius(trace.Points[^1]), fit.RotationRadians);
        Near(Radius(trace.Points[0]), Radius(outline[0]), 1e-10, "inner observation is retained");
        Near(Radius(trace.Points[^1]), Radius(outline[^1]), 1e-10, "outer observation is retained");
    }

    private static void TestMultipleArmOffsets()
    {
        var generated = Parameters(CalibrationDiscKind.Dvd);
        var actual = generated with { InnerRadiusMm = 24.012, OuterRadiusMm = 58.0064 };
        var center = generated with { InnerRadiusMm = 24.45, OuterRadiusMm = 57.65 };
        const double rotation = 1.271;
        CalibrationCrossTrace[] traces =
        [
            IndependentTrace(generated, actual, rotation, 0, 137),
            IndependentTrace(generated, actual, rotation, 1, 211),
            IndependentTrace(generated, actual, rotation, 3, 97),
        ];
        var original = traces.SelectMany(trace => trace.Points).ToArray();
        var fit = CalibrationCrossFitter.Fit(CalibrationTarget.Create(generated), traces,
            new(center, RadiusRangeMm: 0.5));
        CheckRecovery(fit, actual, rotation, 3e-7, "unequal sampling and different arm branches");
        Check(original.SequenceEqual(traces.SelectMany(trace => trace.Points)), "shape initialization never changes observations");
    }

    private static void TestNonThinDvdFallback()
    {
        // Sixteen sectors make b large enough that the area approximation is
        // measurably wrong. Final recovery must still use exact arc geometry.
        var generated = Parameters(CalibrationDiscKind.Dvd) with { Sectors = 16 };
        var actual = generated with { InnerRadiusMm = 24.09, OuterRadiusMm = 57.94 };
        const double rotation = 0.43;
        var trace = IndependentTrace(generated, actual, rotation, 0, 65);
        var fit = CalibrationCrossFitter.Fit(CalibrationTarget.Create(generated), [trace],
            new(RadiusRangeMm: 0.2));
        CheckRecovery(fit, actual, rotation, 2e-5, "non-thin DVD exact refinement");
    }

    private static void TestInvalidShapeApproximationFallback()
    {
        var parameters = Parameters(CalibrationDiscKind.Dvd) with { Sectors = 16 };
        // This deliberate model mismatch has a negative quadratic discriminant
        // for the approximate seed. Existing bounded search must remain usable.
        var points = Enumerable.Range(0, 65).Select(i =>
        {
            double radius = 24 + 34 * i / 64.0;
            return Polar(radius, -Math.PI / 2 + 0.2 - 0.1 * radius);
        }).ToArray();
        var fit = CalibrationCrossFitter.Fit(CalibrationTarget.Create(parameters), [new(0, points)],
            new(RadiusRangeMm: 0.02));
        Check(fit.Succeeded && fit.Candidates.Count > 0 && double.IsFinite(fit.RmsErrorMm),
            "invalid approximate seed falls back to finite exact candidates");
    }

    private static void TestExistingInputGuards()
    {
        var parameters = Parameters(CalibrationDiscKind.Dvd);
        var target = CalibrationTarget.Create(parameters);
        var trace = IndependentTrace(parameters, parameters, 0.2, 0, 33);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Throws<OperationCanceledException>(() => CalibrationCrossFitter.Fit(target, [trace],
            cancellationToken: cancellation.Token), "cancellation precedes shape initialization");
        Throws<ArgumentException>(() => CalibrationCrossFitter.Fit(target, [trace],
            new(RadiusRangeMm: 6)), "invalid search range remains rejected");
        var narrow = Enumerable.Range(0, 6).Select(i => Polar(30 + i * .001, 0.2)).ToArray();
        Check(!CalibrationCrossFitter.Fit(target, [new(0, narrow)]).Succeeded,
            "degenerate coverage does not create an analytical candidate");
    }

    private static void CheckRecovery(CalibrationFitResult fit, CalibrationParameters expected,
        double rotation, double tolerance, string name)
    {
        Check(fit.Succeeded && fit.BestParameters is not null, name + " succeeds");
        Near(expected.InnerRadiusMm, fit.BestParameters!.InnerRadiusMm, tolerance, name + " inner radius");
        Near(expected.OuterRadiusMm, fit.BestParameters.OuterRadiusMm, tolerance, name + " outer radius");
        Near(0, Normalize(fit.RotationRadians - rotation), 8e-5, name + " rotation");
        Check(fit.RmsErrorMm < 2e-5, name + " exact forward residual");
    }

    private static CalibrationCrossTrace IndependentTrace(CalibrationParameters generated,
        CalibrationParameters actual, double rotation, int arm, int count)
    {
        double bg = IndependentGrowth(generated), ba = IndependentGrowth(actual);
        return new(arm, Enumerable.Range(0, count).Select(i =>
        {
            double radius = 23.5 + 35.1 * Math.Pow(i / (count - 1.0), 1.15);
            double source;
            if (generated.Kind == CalibrationDiscKind.Cd)
            {
                double fraction = (radius * radius - actual.InnerRadiusMm * actual.InnerRadiusMm)
                    / (actual.OuterRadiusMm * actual.OuterRadiusMm - actual.InnerRadiusMm * actual.InnerRadiusMm);
                source = Math.Sqrt(generated.InnerRadiusMm * generated.InnerRadiusMm
                    + fraction * (generated.OuterRadiusMm * generated.OuterRadiusMm - generated.InnerRadiusMm * generated.InnerRadiusMm));
            }
            else
            {
                double length = IntegratedArc(actual.InnerRadiusMm, radius, ba);
                double low = 0, high = 120;
                for (int iteration = 0; iteration < 75; iteration++)
                {
                    double middle = (low + high) / 2;
                    if (IntegratedArc(generated.InnerRadiusMm, middle, bg) < length) low = middle;
                    else high = middle;
                }
                source = (low + high) / 2;
            }
            double phase = (radius - actual.InnerRadiusMm) / ba - (source - generated.InnerRadiusMm) / bg;
            return Polar(radius, -Math.PI / 2 + arm * Math.PI / 2 + rotation + phase);
        }).ToArray());
    }

    private static double IndependentGrowth(CalibrationParameters parameters)
    {
        if (parameters.Kind == CalibrationDiscKind.Cd)
            return (parameters.OuterRadiusMm * parameters.OuterRadiusMm - parameters.InnerRadiusMm * parameters.InnerRadiusMm)
                / (2 * parameters.TrackLengthMm);
        double low = 1e-12, high = 120;
        for (int i = 0; i < 90; i++)
        {
            double middle = (low + high) / 2;
            if (IntegratedArc(parameters.InnerRadiusMm, parameters.OuterRadiusMm, middle) > parameters.TrackLengthMm) low = middle;
            else high = middle;
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
            double radius = first + i * step;
            sum += (i % 2 == 0 ? 2 : 4) * Math.Sqrt(radius * radius + b * b);
        }
        return sum * step / (3 * b);
    }

    private static CalibrationPoint Polar(double radius, double angle) => new(radius * Math.Cos(angle), radius * Math.Sin(angle));
    private static double Radius(CalibrationPoint point) => Math.Sqrt(point.X * point.X + point.Y * point.Y);
    private static double Normalize(double angle) => Math.Atan2(Math.Sin(angle), Math.Cos(angle));
    private static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }
    private static void Near(double expected, double actual, double tolerance, string name)
        => Check(Math.Abs(expected - actual) <= tolerance, $"{name}: expected {expected:R}, actual {actual:R}");
    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException(name);
    }
}
