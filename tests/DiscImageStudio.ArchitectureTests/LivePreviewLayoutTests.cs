using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscImageStudio.ViewModels;
using DiscImageStudio.Views;

internal static class LivePreviewLayoutTests
{
    // Run within LivePreviewViewportTests' existing STA/Application lifetime.
    // No Window, optical device, image-generation worker or new Application is needed.
    internal static void Run()
    {
        Require(Application.Current is not null && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA,
            "layout checks reuse an existing STA WPF application");
        ShellViewModel shell = (ShellViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ShellViewModel));
        DiscParametersState state = new();
        LivePreviewViewModel model = new(Dispatcher.CurrentDispatcher, shell, state);
        SetShellProperty(shell, nameof(ShellViewModel.State), state);
        SetShellProperty(shell, nameof(ShellViewModel.LivePreview), model);
        SetShellProperty(shell, nameof(ShellViewModel.BluRay), new BluRayViewModel(shell));
        BitmapSource bitmap = BitmapSource.Create(1024, 1024, 96, 96, PixelFormats.Gray8, null, new byte[1024 * 1024], 1024);
        bitmap.Freeze();
        model.ResultImage = bitmap;
        model.HasResultImage = true;
        LivePreviewView view = new(model);
        try
        {
            Layout(view, 1200, 900);
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            // These are available page sizes in DIP, including constrained work
            // areas produced by 125/150/200% scaling and an expanded shell sidebar.
            foreach ((double width, double height) in new[]
            {
                (1632d, 1048d), (1200d, 900d), (960d, 680d), (780d, 528d),
                (704d, 440d), (648d, 528d), (520d, 420d), (420d, 340d),
            })
            {
                model.ResultPreviewTitle = "实时预览 — 很长的盘片和结果名称";
                model.ResultPreviewPath = "C:\\example\\" + new string('x', 260) + ".png";
                model.StatusText = "预览已完成：" + new string('测', 120);
                Layout(view, width, height);
                bool stacked = width < 700;
                ScrollViewer page = Named<ScrollViewer>(view, "PageScroll");
                page.ScrollToTop();
                Layout(view, width, height);
                Border card = Named<Border>(view, "PreviewCard");
                Canvas viewport = Named<Canvas>(view, "PreviewViewport");
                Require(Grid.GetRow(card) == (stacked ? 2 : 0), "narrow pages reflow the canvas below the parameter pane");
                Inside(page, view, "the page scroll viewport stays within the work area");
                FrameworkElement contentBounds = stacked ? Named<Grid>(view, "PreviewLayout") : view;
                foreach (string name in new[] { "ParameterPane", "InputModeSwitch", "RefreshPreviewButton", "PreviewCard",
                    "PreviewViewport", "FitPreviewButton", "ActualPixelsButton", "ZoomLabel", "DownloadButton" })
                    Inside(Named<FrameworkElement>(view, name), contentBounds, $"{name} stays within reachable page content at {width}×{height} DIP");
                Require(viewport.ActualWidth > 0 && viewport.ActualHeight >= 120,
                    "the canvas retains a usable viewport after reflow");
                Require(Named<TextBlock>(view, "PreviewStatus").TextWrapping == TextWrapping.NoWrap
                    && Named<TextBlock>(view, "PreviewPath").TextWrapping == TextWrapping.NoWrap,
                    "long status and path text trim without growing fixed chrome rows");
                ScrollViewer parameters = Named<ScrollViewer>(view, "ParameterScroll");
                Require(parameters.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled,
                    "parameter content is measured at the available column width");
                Require(parameters.ViewportHeight >= 180 && parameters.ActualHeight >= 180,
                    "parameters retain at least 180 DIP of scrollable editing space, not just a visible footer");

                Invoke(view, "ActualPixelsButton_Click", view, new RoutedEventArgs());
                Image image = Named<Image>(view, "PreviewImage");
                Near(1, image.RenderTransform.Value.M11 * VisualTreeHelper.GetDpi(viewport).DpiScaleX,
                    "1:1 remains device-pixel accurate after responsive layout");
                Invoke(view, "FitButton_Click", view, new RoutedEventArgs());
                Near(Math.Min(viewport.ActualWidth / 1024, viewport.ActualHeight / 1024), image.RenderTransform.Value.M11,
                    "fit continues to use the actual canvas viewport");
                UiLayoutSnapshot.Save(view, $"preview-{width:0}x{height:0}");
                if (stacked)
                {
                    Require(page.ScrollableHeight > 0, "short stacked pages scroll instead of collapsing their pane viewports");
                    Named<Button>(view, "RefreshPreviewButton").BringIntoView();
                    Layout(view, width, height);
                    Inside(Named<Button>(view, "RefreshPreviewButton"), view, "the refresh action can be brought into the work area");
                    page.ScrollToBottom();
                    Layout(view, width, height);
                    Inside(Named<Button>(view, "FitPreviewButton"), view, "the bottom fit action is reachable by page scrolling");
                    Inside(Named<Button>(view, "ActualPixelsButton"), view, "the bottom 1:1 action is reachable by page scrolling");
                    Inside(Named<Button>(view, "DownloadButton"), view, "the canvas export action is reachable by page scrolling");
                    UiLayoutSnapshot.Save(view, $"preview-{width:0}x{height:0}-canvas");

                    page.ScrollToTop();
                    parameters.ScrollToBottom();
                    Layout(view, width, height);
                    Require(parameters.ScrollableHeight > 0, "the parameter fixture exercises an actual nested scroll range");
                    MouseWheelEventArgs down = ForwardWheel(view, parameters, -120);
                    Layout(view, width, height);
                    Require(down.Handled && page.VerticalOffset > 0,
                        "a downward wheel at the parameter boundary continues into the page");
                    page.ScrollToBottom();
                    parameters.ScrollToTop();
                    Layout(view, width, height);
                    double before = page.VerticalOffset;
                    MouseWheelEventArgs up = ForwardWheel(view, parameters, 120);
                    Layout(view, width, height);
                    Require(up.Handled && page.VerticalOffset < before,
                        "an upward wheel at the parameter boundary returns to the upper pane");
                }
            }

            foreach (int disc in new[] { 0, 1, 2 })
            {
                model.SelectedDiscTypeIndex = disc;
                model.IsReadbackPreview = true;
                Layout(view, 704, 400);
                Border card = Named<Border>(view, "PreviewCard");
                Border precision = Named<Border>(view, "PrecisionPanel");
                Size before = card.RenderSize;
                // Force the same visible panel state as hover/focus without relying
                // on the desktop cursor or a second popup window.
                precision.Visibility = Visibility.Visible;
                Layout(view, 704, 400);
                Near(before.Width, card.ActualWidth, "opening precision settings cannot widen the canvas card");
                Near(before.Height, card.ActualHeight, "opening precision settings cannot increase page height");
                Inside(precision, card, "precision settings remain within the card");
                Require(precision.ActualHeight <= precision.MaxHeight + 1,
                    "precision fields use the bounded scrollable overlay");
                Require(Named<ScrollViewer>(view, "PrecisionScroll").VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                    "short work areas retain access to all precision fields through scrolling");
                UiLayoutSnapshot.Save(view, $"preview-precision-{disc}");
                precision.ClearValue(UIElement.VisibilityProperty);
            }
            Layout(view, 520, 420);
            ScrollViewer outer = Named<ScrollViewer>(view, "PageScroll");
            Border precisionPanel = Named<Border>(view, "PrecisionPanel");
            precisionPanel.Visibility = Visibility.Visible;
            outer.ScrollToBottom();
            Layout(view, 520, 420);
            ScrollViewer precisionScroll = Named<ScrollViewer>(view, "PrecisionScroll");
            precisionScroll.ScrollToTop();
            Layout(view, 520, 420);
            double previousOffset = outer.VerticalOffset;
            MouseWheelEventArgs precisionUp = ForwardWheel(view, precisionScroll, 120);
            Layout(view, 520, 420);
            Require(precisionUp.Handled && outer.VerticalOffset < previousOffset,
                "precision settings also hand off wheel scrolling at their boundary");
            precisionPanel.ClearValue(UIElement.VisibilityProperty);
        }
        finally { view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
        Console.WriteLine("live-preview-layout: passed (DIP work areas, responsive panes, bounded precision overlay, long labels, fit/1:1)");
    }

    private static void Layout(LivePreviewView view, double width, double height)
    {
        // SizeChanged can switch rows/columns; complete the resulting second pass.
        for (int pass = 0; pass < 3; pass++)
        {
            view.Measure(new Size(width, height));
            view.Arrange(new Rect(0, 0, width, height));
            view.UpdateLayout();
        }
    }
    private static T Named<T>(LivePreviewView view, string name) where T : FrameworkElement
        => (T)view.FindName(name);
    private static MouseWheelEventArgs ForwardWheel(LivePreviewView view, ScrollViewer inner, int delta)
    {
        MouseWheelEventArgs args = new(Mouse.PrimaryDevice, 0, delta)
        {
            RoutedEvent = Mouse.PreviewMouseWheelEvent,
            Source = inner,
        };
        Invoke(view, "NestedScroll_PreviewMouseWheel", inner, args);
        return args;
    }
    private static void Inside(FrameworkElement child, FrameworkElement parent, string message)
    {
        Rect bounds = child.TransformToAncestor(parent).TransformBounds(new Rect(child.RenderSize));
        Require(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= parent.ActualWidth + 1
            && bounds.Bottom <= parent.ActualHeight + 1, message + $"; bounds={bounds}, parent={parent.RenderSize}");
    }
    private static void Invoke(LivePreviewView view, string method, params object[] arguments)
        => typeof(LivePreviewView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, arguments);
    private static void SetShellProperty(ShellViewModel shell, string name, object value)
        => typeof(ShellViewModel).GetField($"<{name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, value);
    private static void Near(double expected, double actual, string message)
        => Require(Math.Abs(expected - actual) < 0.000001, message);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Live preview layout: " + message);
    }
}
