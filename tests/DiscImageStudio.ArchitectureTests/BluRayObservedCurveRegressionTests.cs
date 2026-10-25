using DiscImageStudio.Core.Calibration;

internal static class BluRayObservedCurveRegressionTests
{
    internal static void Run()
    {
        CalibrationParameters legacy = LegacyParameters();
        CalibrationFitResult? reference = null;
        foreach (CalibrationParameters parameters in new[]
        {
            legacy,
            legacy with { DrawingStartSector = 0, DrawingEndSectorExclusive = legacy.Sectors, BluRayIsoTimestamp = null },
        })
        {
            var source = CalibrationTarget.Create(parameters);
            // Restore the captured numeric state without embedding the user's photo.
            // The actual rectified trace below is independent of the rendering fixture.
            var session = CalibrationSessionJson.Deserialize(CalibrationSessionJson.Serialize(
                new(source, [], FitOptions(parameters))));
            CalibrationTarget target = session.Target;
            CalibrationCrossTrace trace = RealTrace();
            CalibrationPoint[] originalPoints = trace.Points.ToArray();
            double low = trace.Points.Min(Radius), high = trace.Points.Max(Radius);
            Check(trace.Points.Count == 31 && low < target.DrawingInnerRadiusMm && high > target.DrawingOuterRadiusMm,
                "the complete real trace extends beyond both captured generation boundaries");

            var fit = CalibrationCrossFitter.Fit(target, [trace], session.FitOptions);
            Check(fit.Succeeded && fit.BestParameters is not null && fit.Candidates.Count > 0,
                "all 31 observed points produce a valid physical fit for both UDF and full-zone targets");
            Check(fit.RmsErrorMm is > 0.05 and < 0.15,
                "the real trace fits near 0.098 mm rather than the erroneous thousand-mm boundary-constrained fit");
            Check(fit.Warnings.Any(warning => warning.Contains("超出原图案环带", StringComparison.Ordinal)),
                "observations beyond the captured annulus produce a review warning rather than rejection or clipping");
            Near(23.988236631852867, fit.BestParameters!.InnerRadiusMm, 5e-7, "real BD inner-radius fit");
            Near(58.02322629697187, fit.BestParameters.OuterRadiusMm, 5e-7, "real BD outer-radius fit");
            Check(fit.BestParameters.InnerRadiusMm > low + 0.18 && fit.BestParameters.OuterRadiusMm < high - 0.06,
                "fit parameters are not forced onto the first and last observed radii");
            Check(trace.Points.SequenceEqual(originalPoints) && target.Parameters == parameters,
                "fitting leaves every observation and the captured generation bounds unchanged");

            foreach (var candidate in fit.Candidates)
            {
                var outline = CalibrationCrossPrediction.GetOutline(target, candidate.Parameters, low, high,
                    candidate.RotationRadians);
                Check(outline.Count > 170 && outline.Count <= 20_000
                    && outline.All(point => double.IsFinite(point.X) && double.IsFinite(point.Y)),
                    "every returned candidate yields a complete finite preview within the unchanged default point budget");
                Near(low, Radius(outline[0]), 1e-10, "prediction includes the inner observed endpoint");
                Near(high, Radius(outline[^1]), 1e-10, "prediction includes the outer observed endpoint");
                // A noisy endpoint's nearest normal projection may lie just outside
                // the observed radial interval. Here the first point projects at
                // r=23.6639 mm: its curve distance is 0.1418 mm, but clipping the
                // curve at the observed r=23.8033 mm raises that to 0.7693 mm.
                // Check shape against a continuation within the saved Huber noise
                // tolerance, while the displayed outline above still ends exactly
                // at the observed endpoints. All 31 points remain in this check.
                double endpointMargin = session.FitOptions!.HuberDeltaMm;
                var continuation = CalibrationCrossPrediction.GetOutline(target, candidate.Parameters,
                    low - endpointMargin, high + endpointMargin, candidate.RotationRadians);
                Check(trace.Points.All(point => DistanceToPolyline(point, continuation) < 0.3),
                    "all real points lie within 0.3 mm of the fitted curve, including endpoint normal projections");
                double signed = 0, absolute = 0;
                for (int index = 1; index < outline.Count; index++)
                {
                    double step = Normalize(Angle(outline[index]) - Angle(outline[index - 1]));
                    Check(Math.Abs(step) <= Math.PI / 36 + 1e-7,
                        "the full preview retains the existing five-degree unwrapped segment limit");
                    signed += step; absolute += Math.Abs(step);
                }
                Near(-0.1086832244970911, signed / (2 * Math.PI), 0.002,
                    "the fitted direction and net winding agree with the observed curve");
                Check(absolute / (2 * Math.PI) is > 0.4 and < 0.5,
                    "prediction retains the real bend instead of inventing hundreds of turns");
            }
            if (reference is null)
            {
                reference = fit;
                foreach (int excluded in new[] { 0, trace.Points.Count - 1 })
                {
                    var reduced = new CalibrationCrossTrace(0, trace.Points.Where((_, index) => index != excluded).ToArray());
                    var withoutEndpoint = CalibrationCrossFitter.Fit(target, [reduced], session.FitOptions);
                    Check(withoutEndpoint.Succeeded && withoutEndpoint.BestParameters != fit.BestParameters
                        && Math.Abs(withoutEndpoint.RmsErrorMm - fit.RmsErrorMm) > 1e-5,
                        "each outside endpoint contributes to the objective instead of being silently discarded");
                }
            }
            else
            {
                Near(reference.BestParameters!.InnerRadiusMm, fit.BestParameters.InnerRadiusMm, 1e-9,
                    "filesystem metadata bounds do not change the fitted inner radius");
                Near(reference.BestParameters.OuterRadiusMm, fit.BestParameters.OuterRadiusMm, 1e-9,
                    "filesystem metadata bounds do not change the fitted outer radius");
                Near(reference.RmsErrorMm, fit.RmsErrorMm, 1e-8,
                    "old UDF and new raw targets use the same complete observed-radius objective");
            }
        }
        Console.WriteLine("blu-ray-observed-curve: real 31-point record, UDF/raw bounds, full preview, winding and endpoint participation passed");
    }

    internal static CalibrationParameters LegacyParameters() => new(CalibrationDiscKind.BluRay,
        23.9885, 58.022875, 12_219_392, 74.5,
        DrawingStartSector: 320,
        BluRayIsoTimestamp: new DateTimeOffset(2026, 10, 25, 2, 49, 17, TimeSpan.FromHours(8)).AddTicks(9_188_603),
        DrawingEndSectorExclusive: 12_219_104);

    internal static CalibrationFitOptions FitOptions(CalibrationParameters parameters)
        => new(SearchCenter: parameters, RadiusRangeMm: 0.5, HuberDeltaMm: 0.8,
            MaxCandidates: 8, FitDvdPitch: false, PitchCoefficientRange: 0.05);

    // Numeric trace from calibration-831945b0.calibration.json. No photograph,
    // filesystem path, or live optical-device dependency is included in this test.
    internal static CalibrationCrossTrace RealTrace() => new(0,
    [
        new(23.789067098809156, -0.8225881044750825),
        new(24.403490511977047, 2.7669381514004945),
        new(24.435828586354305, 5.871393291617211),
        new(24.063940731015844, 8.830327097136271),
        new(23.417179243470695, 12.48452950176636),
        new(22.511713160907487, 15.993210571698796),
        new(20.894809442044615, 20.164822166365006),
        new(19.45576513225666, 23.430967678468008),
        new(18.453284826561678, 25.92099940551683),
        new(18.049058896845956, 27.74810060783188),
        new(18.55029904969345, 29.575201810146925),
        new(19.229398611615856, 30.723203450539565),
        new(20.95948559079913, 31.22444360338706),
        new(22.948277165000462, 31.58016242153689),
        new(24.904730664824537, 31.40230301246197),
        new(27.766650247211825, 30.739372487728197),
        new(30.693245978353623, 29.478187587015157),
        new(33.441982300420506, 27.958298091284053),
        new(38.082495973556945, 24.772997765124195),
        new(42.38345986573219, 20.326512538251293),
        new(45.84363382409874, 15.621322716360336),
        new(48.252820365204414, 11.595232456391782),
        new(49.62718852623786, 8.021875237704831),
        new(50.953049575705414, 3.995784977736278),
        new(51.98786795577765, -0.6932358069660527),
        new(52.66696751770006, -6.271553637042961),
        new(52.55378425737966, -11.397138425838268),
        new(51.98786795577765, -18.188134045062334),
        new(50.6620069063101, -24.21918491642085),
        new(48.834905703995055, -29.005219924254952),
        new(46.134676493494055, -35.29497539063153),
    ]);

    private static double Radius(CalibrationPoint point) => Math.Sqrt(point.X * point.X + point.Y * point.Y);
    private static double Angle(CalibrationPoint point) => Math.Atan2(point.Y, point.X);
    private static double Normalize(double angle) => Math.Atan2(Math.Sin(angle), Math.Cos(angle));
    private static double DistanceToPolyline(CalibrationPoint point, IReadOnlyList<CalibrationPoint> outline)
    {
        double minimumSquared = double.PositiveInfinity;
        for (int index = 1; index < outline.Count; index++)
        {
            CalibrationPoint first = outline[index - 1], last = outline[index];
            double dx = last.X - first.X, dy = last.Y - first.Y;
            double fraction = Math.Clamp(((point.X - first.X) * dx + (point.Y - first.Y) * dy)
                / Math.Max(1e-30, dx * dx + dy * dy), 0, 1);
            double ex = point.X - first.X - fraction * dx, ey = point.Y - first.Y - fraction * dy;
            minimumSquared = Math.Min(minimumSquared, ex * ex + ey * ey);
        }
        return Math.Sqrt(minimumSquared);
    }
    private static void Near(double expected, double actual, double tolerance, string message)
        => Check(double.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance,
            $"{message}: expected {expected:G17}, actual {actual:G17}");
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Blu-ray observed curve: " + message);
    }
}
