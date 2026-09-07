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

    private static readonly Duration ExpandDuration = new(TimeSpan.FromMilliseconds(250));
    private static readonly Duration CollapseDuration = new(TimeSpan.FromMilliseconds(200));
    private static readonly IEasingFunction ExpandEase = new CubicEase { EasingMode = EasingMode.EaseOut };

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

    private void UpdateExpansion()
    {
        double target = IsExpanded ? MeasureContentHeight() : 0;
        double from = Height;
        // Set the base value first so the panel is correct even if no composition
        // tick ever samples the animation (headless renders, hidden pages).
        BeginAnimation(HeightProperty, null);
        BeginAnimation(OpacityProperty, null);
        Height = target;
        Opacity = IsExpanded ? 1 : 0;
        DoubleAnimation heightAnimation = new(from, target, IsExpanded ? ExpandDuration : CollapseDuration)
        {
            EasingFunction = ExpandEase,
        };
        BeginAnimation(HeightProperty, heightAnimation);
        BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(IsExpanded ? 0 : 1, IsExpanded ? 1 : 0, CollapseDuration));
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
