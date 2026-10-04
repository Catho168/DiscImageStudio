using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Services;

internal static class CalibrationPhotoTests
{
    internal static void Run()
    {
        TestJointPerspective();
        TestInvalidContoursAndCancellation();
        TestPhotoPixelsAndLoading();
        Console.WriteLine("calibration-photo: passed (joint concentric-circle perspective, invalid contours, pixels, cancellation)");
    }

    private static void TestJointPerspective()
    {
        foreach (double radius in new[] { 60.0, 40.0 })
        foreach (double perspective in new[] { 0.0, 0.003, 0.006 })
        {
            var original = new CalibrationPhotoTransform(
                [8.4, 1.2, 680, -0.7, 5.5, 490, perspective, -perspective * 0.75, 1]);
            CalibrationPoint[] outer = Circle(original, radius, 12, 0.04);
            CalibrationPoint[] hole = Circle(original, 7.5, 9, 0.21);
            // Deliberately shuffle the points: the fitter must not infer corner
            // correspondences or right angles from their order in the UI.
            outer = outer.OrderBy(p => p.Y).ToArray();
            hole = hole.OrderByDescending(p => p.X).ToArray();
            CalibrationPhotoFitResult fit = CalibrationPhotoRectifier.Fit(outer, hole, radius);
            Require(fit.Succeeded && fit.Transform is not null, $"joint fit succeeds at perspective {perspective}: {fit.Message}");
            CalibrationPhotoTransform fitted = fit.Transform!;
            Require(fit.OuterRmsPixels < 1e-5 && fit.HoleRmsPixels < 1e-5,
                "both physical circles fit, not just the outer ellipse");
            Require(fitted.IsValidForDisc(radius), "the fitted whole disc stays on the finite side of the horizon");
            CalibrationPoint basis = fitted.SourceToDisc(original.DiscToSource(new(radius * 0.4, 0)));
            double rotation = Math.Atan2(basis.Y, basis.X);
            foreach (CalibrationPoint expected in new CalibrationPoint[]
                { new(0, 0), new(radius * 0.3, -radius * 0.5), new(-radius * 0.6, radius * 0.2), new(4, 3) })
            {
                CalibrationPoint source = original.DiscToSource(expected);
                CalibrationPoint result = fitted.SourceToDisc(source);
                CalibrationPoint rotated = Rotate(expected, rotation);
                Near(rotated, result, 1e-5, "interior geometry is recovered up to the unconstrained whole-disc rotation");
                Near(source, fitted.DiscToSource(result), 1e-7, "the perspective map works in both directions");
            }
            string json = JsonSerializer.Serialize(fitted);
            CalibrationPhotoTransform restored = JsonSerializer.Deserialize<CalibrationPhotoTransform>(json)
                ?? throw new InvalidOperationException("Photo transform JSON was empty.");
            CalibrationPoint probe = new(7, -18);
            Near(fitted.DiscToSource(probe), restored.DiscToSource(probe), 1e-9,
                "saved transform reproduces the same source pixels");
            double[] copy = restored.DiscToSourceMatrix9;
            Array.Clear(copy);
            Near(fitted.DiscToSource(probe), restored.DiscToSource(probe), 1e-9,
                "the serialized matrix accessor cannot mutate a captured transform");
        }

        var camera = new CalibrationPhotoTransform([8.4, 1.2, 680, -0.7, 5.5, 490, 0.004, -0.003, 1]);
        CalibrationPhotoFitResult minimum = CalibrationPhotoRectifier.Fit(Circle(camera, 60, 5, 0.04), Circle(camera, 7.5, 5, 0.23));
        Require(minimum.Succeeded && minimum.OuterRmsPixels < 1e-5 && minimum.HoleRmsPixels < 1e-5,
            "five well-spread points on each boundary determine the joint perspective");
        CalibrationPoint[] noisyOuter = Circle(camera, 60, 16, 0).Select((p, index) =>
            new CalibrationPoint(p.X + 0.4 * Math.Sin(index * 1.7), p.Y + 0.4 * Math.Cos(index * 2.1))).ToArray();
        CalibrationPoint[] noisyHole = Circle(camera, 7.5, 12, 0.17).Select((p, index) =>
            new CalibrationPoint(p.X + 0.3 * Math.Sin(index * 2.3), p.Y + 0.3 * Math.Cos(index * 1.9))).ToArray();
        CalibrationPhotoFitResult noisy = CalibrationPhotoRectifier.Fit(noisyOuter, noisyHole);
        Require(noisy.Succeeded && noisy.OuterRmsPixels < 1 && noisy.HoleRmsPixels < 1,
            "subpixel manual boundary noise yields finite, small residuals on both rings");
        Near(new(0, 0), noisy.Transform!.SourceToDisc(camera.DiscToSource(new(0, 0))), 0.15,
            "joint fitting keeps the physical hole centre aligned despite boundary noise");
    }

    private static void TestInvalidContoursAndCancellation()
    {
        var camera = new CalibrationPhotoTransform([7.5, 0.6, 500, -0.4, 5.9, 450, 0.004, -0.002, 1]);
        CalibrationPoint[] outer = Circle(camera, 60, 10, 0);
        CalibrationPoint[] hole = Circle(camera, 7.5, 10, 0.13);
        Require(!CalibrationPhotoRectifier.Fit(outer.Take(4).ToArray(), hole).Succeeded,
            "an underconstrained outer boundary is rejected");
        Require(!CalibrationPhotoRectifier.Fit(outer, Enumerable.Repeat(hole[0], 8).ToArray()).Succeeded,
            "duplicate hole points are rejected");
        Require(!CalibrationPhotoRectifier.Fit(outer, Enumerable.Range(0, 8).Select(i => new CalibrationPoint(i, i * 2)).ToArray()).Succeeded,
            "a collinear hole is rejected");
        CalibrationPhotoFitResult wrongHole = CalibrationPhotoRectifier.Fit(outer, Circle(camera, 14, 10, 0.13));
        Require(!wrongHole.Succeeded && wrongHole.Transform is null,
            "a printed ring of the wrong physical size cannot stand in for the centre hole");
        CalibrationPoint[] partial = Enumerable.Range(0, 8).Select(i =>
            camera.DiscToSource(new(60 * Math.Cos(i * 0.09), 60 * Math.Sin(i * 0.09)))).ToArray();
        Require(!CalibrationPhotoRectifier.Fit(partial, hole).Succeeded,
            "points confined to a small outer arc do not qualify as a full-circle correction");
        Throws<ArgumentException>(() => new CalibrationPhotoTransform(new double[9]), "singular transform rejected");
        Throws<ArgumentException>(() => new CalibrationPhotoTransform([1, 0, 0, 0, 1, 0, 0, 0, double.NaN]),
            "nonfinite saved matrix rejected");
        var horizon = new CalibrationPhotoTransform([1, 0, 0, 0, 1, 0, 0.03, 0, 1]);
        Require(!horizon.IsValidForDisc(60), "a homography crossing the physical disc is rejected");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Throws<OperationCanceledException>(() => CalibrationPhotoRectifier.Fit(outer, hole, cancellationToken: cancelled.Token),
            "pre-cancelled perspective fitting returns no result");
    }

    private static void TestPhotoPixelsAndLoading()
    {
        BitmapSource source = Gradient(320, 240);
        var transform = new CalibrationPhotoTransform([2, 0.35, 160, -0.25, 1.4, 120, 0.002, -0.0015, 1]);
        BitmapSource corrected = CalibrationPhotoService.Rectify(source, transform, 60, 240);
        Require(source.IsFrozen && corrected.IsFrozen, "photos can safely cross worker and UI threads");
        byte[] result = new byte[240 * 240 * 4];
        corrected.CopyPixels(result, 240 * 4, 0);
        foreach ((int x, int y) in new[] { (120, 120), (35, 70), (190, 180), (70, 205) })
        {
            CalibrationPoint mm = new((x + 0.5) * 0.5 - 60, (y + 0.5) * 0.5 - 60);
            CalibrationPoint pixel = transform.DiscToSource(mm);
            int offset = (y * 240 + x) * 4;
            Require(Math.Abs(result[offset] - pixel.X / 2) <= 1
                    && Math.Abs(result[offset + 1] - pixel.Y / 2) <= 1
                    && Math.Abs(result[offset + 2] - (pixel.X + pixel.Y) / 4) <= 1
                    && result[offset + 3] == 255,
                "rectification bilinearly samples the projective source coordinate with the preview's Y-down pixel-centre convention");
        }
        CalibrationPhotoContours contours = CalibrationPhotoService.SuggestContours(source);
        Require(contours.Outer.Count == 8 && contours.Hole.Count == 8 && contours.Message.Contains("尚未", StringComparison.Ordinal),
            "initial boundary handles are clearly disclosed as unconfirmed suggestions");
        Require(contours.Outer.Concat(contours.Hole).All(p => p.X >= 0 && p.Y >= 0
                && p.X < source.PixelWidth && p.Y < source.PixelHeight), "initial handles stay inside the source image");
        string directory = Path.Combine(Path.GetTempPath(), $"disc-calibration-photo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach ((string name, BitmapSource image) in new[] { ("landscape", source), ("portrait", Gradient(80, 240)) })
            {
                string path = Path.Combine(directory, name + ".png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (FileStream file = File.Create(path)) encoder.Save(file);
                BitmapSource loaded = CalibrationPhotoService.Load(path, 128);
                Require(loaded.IsFrozen && Math.Max(loaded.PixelWidth, loaded.PixelHeight) == 128,
                    "photo loading bounds the long edge for both portrait and landscape images");
                File.Delete(path);
                byte[] copied = new byte[checked(loaded.PixelWidth * loaded.PixelHeight * 4)];
                new FormatConvertedBitmap(loaded, PixelFormats.Bgra32, null, 0).CopyPixels(copied, loaded.PixelWidth * 4, 0);
                Require(copied.Any(value => value != 0), "loaded photo remains usable after its file is closed and removed");
            }
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Throws<OperationCanceledException>(() => CalibrationPhotoService.Rectify(source, transform, 60,
            cancellationToken: cancellation.Token), "rectification honors cancellation");
        Throws<OperationCanceledException>(() => CalibrationPhotoService.Load("not-opened.png",
            cancellationToken: cancellation.Token), "cancelled loading does not read a file");
    }

    private static BitmapSource Gradient(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            pixels[offset] = (byte)(x / 2);
            pixels[offset + 1] = (byte)(y / 2);
            pixels[offset + 2] = (byte)((x + y) / 4);
            pixels[offset + 3] = 255;
        }
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static CalibrationPoint[] Circle(CalibrationPhotoTransform transform, double radius, int count, double phase)
        => Enumerable.Range(0, count).Select(index =>
        {
            double angle = 2 * Math.PI * index / count + phase;
            return transform.DiscToSource(new(radius * Math.Cos(angle), radius * Math.Sin(angle)));
        }).ToArray();

    private static CalibrationPoint Rotate(CalibrationPoint point, double angle)
        => new(point.X * Math.Cos(angle) - point.Y * Math.Sin(angle), point.X * Math.Sin(angle) + point.Y * Math.Cos(angle));

    private static void Near(CalibrationPoint expected, CalibrationPoint actual, double tolerance, string message)
        => Require(Math.Sqrt(Math.Pow(expected.X - actual.X, 2) + Math.Pow(expected.Y - actual.Y, 2)) < tolerance, message);

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Calibration photo: {message}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Calibration photo: {message}.");
    }
}
