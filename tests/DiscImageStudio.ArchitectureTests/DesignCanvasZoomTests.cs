using System.Runtime.ExceptionServices;
using System.Windows;
using DiscImageStudio.Controls;

/// <summary>Covers the design canvas zoom bar: the +/- buttons step on top of the fit scale in
/// both directions (zoom-out used to be clamped away at 100%), the 25%-1200% bounds hold, and
/// resetting returns to the fit view.</summary>
internal static class DesignCanvasZoomTests
{
    internal static void Run()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                TestButtonSteps();
                TestBounds();
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        Console.WriteLine("design-canvas-zoom: passed (+/- steps, 25%-1200% bounds, fit reset)");
    }

    private static void TestButtonSteps()
    {
        DiscDesignCanvas canvas = CreateCanvas();
        Require(canvas.ZoomPercent == "100%", "a new canvas starts at fit zoom");
        canvas.ZoomOut();
        Near(0.8, canvas.ZoomFactor, "shrinking goes below the fit scale");
        Require(canvas.ZoomPercent == "80%", "shrinking updates the readout");
        canvas.ZoomIn();
        Near(1.0, canvas.ZoomFactor, "growing returns to the fit scale");
        canvas.ZoomIn();
        Near(1.25, canvas.ZoomFactor, "growing continues above the fit scale");
        canvas.ResetView();
        Near(1.0, canvas.ZoomFactor, "reset returns to fit zoom");
        Require(canvas.ZoomPercent == "100%", "reset restores the readout");
    }

    private static void TestBounds()
    {
        DiscDesignCanvas canvas = CreateCanvas();
        canvas.ZoomFactor = 0.01;
        Near(0.25, canvas.ZoomFactor, "zoom-out stops at 25%");
        Require(canvas.ZoomPercent == "25%", "the minimum zoom is reported");
        for (int index = 0; index < 32; index++)
        {
            canvas.ZoomOut();
        }

        Near(0.25, canvas.ZoomFactor, "repeated shrinking cannot pass the minimum");
        canvas.ZoomFactor = 99;
        Near(12.0, canvas.ZoomFactor, "zoom-in stops at 1200%");
    }

    private static DiscDesignCanvas CreateCanvas()
    {
        DiscDesignCanvas canvas = new();
        canvas.Measure(new Size(900, 700));
        canvas.Arrange(new Rect(0, 0, 900, 700));
        canvas.UpdateLayout();
        return canvas;
    }

    private static void Near(double expected, double actual, string name)
    {
        if (Math.Abs(expected - actual) > 1e-9)
        {
            throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}.");
        }
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{name}: condition was false.");
        }
    }
}
