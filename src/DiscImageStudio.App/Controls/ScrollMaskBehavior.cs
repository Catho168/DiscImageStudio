using System.Windows;
using System.Windows.Controls;

namespace DiscImageStudio.Controls;

/// Shows and hides the 40px "PART_TopFade"/"PART_BottomFade" gradient overlays declared
/// in the ScrollViewer template, matching the reference layout's mask-image behavior.
/// An OpacityMask cannot be used here: WPF blanks the viewport of a scrolling
/// ScrollContentPresenter whenever a mask sits on it or any of its ancestors.
public static class ScrollMaskBehavior
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ScrollMaskBehavior),
        new PropertyMetadata(false, static (sender, args) =>
        {
            ScrollViewer viewer = (ScrollViewer)sender;
            if ((bool)args.NewValue)
            {
                viewer.ScrollChanged += OnScrollChanged;
                viewer.Loaded += static (loadedSender, _) => UpdateFades((ScrollViewer)loadedSender);
            }
            else
            {
                viewer.ScrollChanged -= OnScrollChanged;
            }
        }));

    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs args)
        => UpdateFades((ScrollViewer)sender);

    private static void UpdateFades(ScrollViewer viewer)
    {
        bool canScrollUp = viewer.VerticalOffset > 0.5;
        bool canScrollDown = viewer.ScrollableHeight > 0.5
            && viewer.VerticalOffset < viewer.ScrollableHeight - 0.5;
        SetFadeVisible(viewer, "PART_TopFade", canScrollUp);
        SetFadeVisible(viewer, "PART_BottomFade", canScrollDown);
    }

    private static void SetFadeVisible(ScrollViewer viewer, string partName, bool visible)
    {
        if (viewer.Template.FindName(partName, viewer) is FrameworkElement fade)
        {
            fade.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
