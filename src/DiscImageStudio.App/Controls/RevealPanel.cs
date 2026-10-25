using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace DiscImageStudio.Controls;

/// Collapsible parameter group: animates height 0↔content height with a
/// CubicEase-out curve (the spring-stiffness-200 feel, without distortion).
public class RevealPanel : ContentControl
{
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(RevealPanel),
        new PropertyMetadata(false, static (sender, _) => ((RevealPanel)sender).UpdateExpansion()));

    private static readonly Duration ExpandDuration = Motion.Slow;
    private static readonly Duration CollapseDuration = Motion.Base;
    private static readonly IEasingFunction ExpandEase = Motion.EaseOut;

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    static RevealPanel()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(RevealPanel),
            new FrameworkPropertyMetadata(typeof(RevealPanel)));
    }

    public RevealPanel()
    {
        Height = 0;
        Opacity = 0;
        SizeChanged += (_, _) => UpdateExpansion();
        Loaded += (_, _) => UpdateExpansion();
    }

    /// <summary>Last expansion state, or null before the first pass.</summary>
    private bool? _lastExpanded;

    /// <summary>Only a real expand/collapse animates. A first layout, a re-measure after a
    /// resize, or a panel that opens already expanded jumps straight to its final state: an
    /// opacity animation started there would fade in content the user did not ask to reveal,
    /// and a headless render would sample it at an arbitrary progress.</summary>
    private void UpdateExpansion()
    {
        bool expanded = IsExpanded;
        double target = expanded ? MeasureContentHeight() : 0;
        double from = Height;
        double fromOpacity = Opacity;
        bool animate = IsLoaded && _lastExpanded is { } previous && previous != expanded;
        _lastExpanded = expanded;

        // Set the base values first so the panel is correct even if no composition tick ever
        // samples the animation (headless renders, hidden pages).
        BeginAnimation(HeightProperty, null);
        BeginAnimation(OpacityProperty, null);
        Height = target;
        Opacity = expanded ? 1 : 0;
        if (!animate)
        {
            return;
        }

        DoubleAnimation heightAnimation = new(from, target, expanded ? ExpandDuration : CollapseDuration)
        {
            EasingFunction = ExpandEase,
        };
        BeginAnimation(HeightProperty, heightAnimation);
        BeginAnimation(OpacityProperty, new DoubleAnimation(fromOpacity, expanded ? 1 : 0, CollapseDuration));
    }

    private double MeasureContentHeight()
    {
        if (Content is not UIElement content)
        {
            return 0;
        }

        content.Measure(new Size(
            ActualWidth > 0 ? ActualWidth : double.PositiveInfinity,
            double.PositiveInfinity));
        return Math.Max(content.DesiredSize.Height, 1);
    }
}
