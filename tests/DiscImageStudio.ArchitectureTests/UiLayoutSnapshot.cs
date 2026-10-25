using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class UiLayoutSnapshot
{
    // Optional visual evidence for the same in-memory pages exercised by the
    // layout checks. This does not create a desktop window or access a recorder.
    internal static void Save(FrameworkElement view, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("DISC_IMAGE_STUDIO_UI_SNAPSHOTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        DrawingVisual background = new();
        using (DrawingContext drawing = background.RenderOpen())
            drawing.DrawRectangle((Brush)Application.Current.FindResource("BackgroundBrush"), null,
                new Rect(view.RenderSize));
        RenderTargetBitmap bitmap = new((int)Math.Ceiling(view.ActualWidth),
            (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(background);
        bitmap.Render(view);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(output);
    }
}
