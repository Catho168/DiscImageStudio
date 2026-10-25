using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiscImageStudio.ViewModels;
using DiscImageStudio.Views;

internal static class CalibrationLayoutTests
{
    // Called from LivePreviewViewportTests after its Application and production
    // theme dictionaries have been created, on the same STA dispatcher.
    internal static void RunInCurrentApplication()
    {
        Require(Application.Current is not null && Application.Current.Dispatcher.CheckAccess(),
            "layout checks share the existing WPF Application and dispatcher");
        ShellViewModel shell = (ShellViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ShellViewModel));
        CalibrationViewModel model = new(shell, new DiscParametersState())
        {
            IsPhotoStep = false,
            FitSummary = string.Join(Environment.NewLine, Enumerable.Repeat(
                "候选参数与实拍曲线的残差详情；所有有效点均参与反推。", 14)),
            TargetSummary = "当前标靶和生成参数保持不变；输出与保存操作应在小窗口中仍可到达。",
        };
        CalibrationView view = new(model);
        try
        {
            Layout(view, new Size(1200, 900));
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            foreach (Expander expander in Descendants<Expander>(view)) expander.IsExpanded = true;

            bool checkedInnerWheel = false, checkedOuterWheel = false;
            foreach (Size size in new[] { new Size(1200, 900), new Size(620, 740), new Size(1100, 430) })
            {
                Layout(view, size);
                ScrollViewer outer = Named<ScrollViewer>(view, "WorkScroll");
                ScrollViewer settings = Named<ScrollViewer>(view, "SettingsScroll");
                Border preview = Named<Border>(view, "PreviewShell");
                Require(outer.ActualHeight > 0 && outer.ActualWidth > 0,
                    $"workspace has a nonempty viewport at {size}");
                Require(outer.ScrollableWidth < 1 && settings.ScrollableWidth < 1,
                    $"page controls do not require inaccessible horizontal scrolling at {size}");
                AssertHorizontalBounds(outer, view, $"workspace at {size}");
                AssertHorizontalBounds(preview, outer, $"photo card at {size}");
                AssertHorizontalBounds(settings, outer, $"settings at {size}");

                Button bottomAction = ButtonWithContent(view, "打开标定记录");
                // Scroll the actual containers, rather than inspecting only the
                // logical tree or accepting a clipped button as present.
                settings.ScrollToEnd();
                outer.ScrollToEnd();
                Layout(view, size);
                AssertVisibleThroughScrollers(bottomAction, view, $"bottom output/session action at {size}");
                UiLayoutSnapshot.Save(view, $"calibration-bottom-{size.Width:0}x{size.Height:0}");

                Button apply = ButtonWithContent(view, "应用标定参数");
                apply.BringIntoView();
                Layout(view, size);
                AssertVisibleThroughScrollers(apply, view, $"apply-calibration action at {size}");

                CheckWheelRouting(view, size, outer, settings, ref checkedInnerWheel, ref checkedOuterWheel);
            }
            Require(checkedInnerWheel, "a scrollable settings pane exercised normal wheel consumption");
            Require(checkedOuterWheel, "a constrained page exercised wheel handoff in both directions");

            // A resize back to the ordinary page must undo the stacked layout.
            model.IsPhotoStep = true;
            Layout(view, new Size(1200, 900));
            Button photoAction = ButtonWithContent(view, "确认矫正，开始描线");
            photoAction.BringIntoView();
            Layout(view, new Size(1200, 900));
            AssertVisibleThroughScrollers(photoAction, view, "photo-step action after restoring the normal page size");
        }
        finally
        {
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            view.DataContext = null;
        }
        Console.WriteLine("calibration-layout: passed (normal/narrow/short page, reachable actions, wheel consumption and handoff)");
    }

    private static void CheckWheelRouting(CalibrationView view, Size size, ScrollViewer outer,
        ScrollViewer settings, ref bool checkedInnerWheel, ref bool checkedOuterWheel)
    {
        FrameworkElement target = (FrameworkElement)settings.Content;
        outer.ScrollToTop();
        settings.ScrollToTop();
        Layout(view, size);
        if (settings.ScrollableHeight > 1 && settings.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled)
        {
            double before = settings.VerticalOffset;
            Wheel(target, -120);
            Layout(view, size);
            Require(settings.VerticalOffset > before,
                $"the settings pane consumes wheel input while it still has room at {size}");
            Require(outer.VerticalOffset < 1,
                $"ordinary settings scrolling does not also move the outer page at {size}");
            checkedInnerWheel = true;
        }
        if (outer.ScrollableHeight <= 1) return;

        settings.ScrollToEnd();
        outer.ScrollToTop();
        Layout(view, size);
        double outerBefore = outer.VerticalOffset;
        Wheel(target, -120);
        Layout(view, size);
        Require(outer.VerticalOffset > outerBefore,
            $"downward wheel at the nested pane's end reaches the outer page at {size}");

        settings.ScrollToTop();
        outer.ScrollToEnd();
        Layout(view, size);
        outerBefore = outer.VerticalOffset;
        Wheel(target, 120);
        Layout(view, size);
        Require(outer.VerticalOffset < outerBefore,
            $"upward wheel at the nested pane's start reaches the outer page at {size}");
        checkedOuterWheel = true;

        // The drawing surface intentionally owns its wheel gesture for zoom.
        // A global scroll handoff must not steal that existing interaction.
        FrameworkElement canvas = Named<FrameworkElement>(view, "PhotoCanvas");
        outerBefore = outer.VerticalOffset;
        MouseWheelEventArgs canvasWheel = Wheel(canvas, -120);
        Layout(view, size);
        Require(canvasWheel.Handled && Math.Abs(outer.VerticalOffset - outerBefore) < 1,
            "the photo canvas retains its wheel-to-zoom gesture");
    }

    private static MouseWheelEventArgs Wheel(UIElement source, int delta)
    {
        MouseWheelEventArgs args = new(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = Mouse.PreviewMouseWheelEvent,
        };
        source.RaiseEvent(args);
        // A synthetic preview event does not create the paired bubbling event;
        // emulate the real input pipeline only when preview left it unhandled.
        if (!args.Handled)
        {
            args.RoutedEvent = Mouse.MouseWheelEvent;
            source.RaiseEvent(args);
        }
        return args;
    }

    private static void Layout(FrameworkElement view, Size size)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            view.Measure(size);
            view.Arrange(new Rect(new Point(), size));
            view.UpdateLayout();
            view.Dispatcher.Invoke(static () => { }, DispatcherPriority.Background);
        }
    }

    private static void AssertVisibleThroughScrollers(FrameworkElement element, FrameworkElement page, string label)
    {
        Require(element.Visibility == Visibility.Visible && element.ActualWidth > 0 && element.ActualHeight > 0,
            label + " is laid out");
        AssertContained(element, page, label + " is inside the page");
        for (DependencyObject? parent = VisualTreeHelper.GetParent(element); parent is not null && parent != page;
            parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer viewer
                && viewer.Template.FindName("PART_ScrollContentPresenter", viewer) is FrameworkElement viewport)
                AssertContained(element, viewport, label + " is visible through every scrolling viewport");
        }
    }

    private static void AssertContained(FrameworkElement element, FrameworkElement viewport, string label)
    {
        Rect bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(element.RenderSize));
        Require(bounds.Left >= -1 && bounds.Top >= -1
            && bounds.Right <= viewport.ActualWidth + 1 && bounds.Bottom <= viewport.ActualHeight + 1,
            $"{label}: bounds={bounds}, viewport={viewport.RenderSize}");
    }

    private static void AssertHorizontalBounds(FrameworkElement element, FrameworkElement viewport, string label)
    {
        Rect bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(element.RenderSize));
        Require(bounds.Left >= -1 && bounds.Right <= viewport.ActualWidth + 1,
            $"{label} fits horizontally: bounds={bounds}, viewport width={viewport.ActualWidth}");
    }

    private static T Named<T>(FrameworkElement root, string name) where T : FrameworkElement
        => root.FindName(name) as T ?? throw new InvalidOperationException("Missing calibration control: " + name);

    private static Button ButtonWithContent(DependencyObject root, string content)
        => Descendants<Button>(root).Single(button => Equals(button.Content, content));

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Calibration layout: " + message);
    }
}
