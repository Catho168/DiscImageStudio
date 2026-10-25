using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DiscImageStudio.Controls;

/// <summary>Expanding a disclosure only helps if the opened body is on screen. Advanced
/// expanders sit at the end of long panels, so their fresh content opens below the fold and
/// reads as broken — nothing seems to happen, or rows are cut in half. When enabled, the
/// nearest ancestor ScrollViewer scrolls the expanded header (plus as much body as fits)
/// into view.</summary>
public static class ExpanderAutoScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ExpanderAutoScroll), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Expander expander)
        {
            return;
        }

        if ((bool)e.NewValue) expander.Expanded += OnExpanded;
        else expander.Expanded -= OnExpanded;
    }

    private static void OnExpanded(object sender, RoutedEventArgs e)
    {
        Expander expander = (Expander)sender;
        // Loaded priority: the body's visibility change must be measured first, or the
        // scroll target covers only the collapsed bounds.
        expander.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!expander.IsExpanded) return;
            expander.UpdateLayout();
            ScrollViewer? scroller = FindAncestorScrollViewer(expander);
            if (scroller is null || scroller.ScrollableHeight <= 0) return;
            double top = expander.TransformToVisual(scroller).Transform(new Point(0, 0)).Y
                + scroller.VerticalOffset;
            scroller.ScrollToVerticalOffset(Math.Max(0, top - 4));
        });
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject start)
    {
        DependencyObject current = start;
        while (current is not null)
        {
            if (current is ScrollViewer scroller) return scroller;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
