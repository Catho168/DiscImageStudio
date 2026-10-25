using System.Windows;
using System.Windows.Media.Animation;

namespace DiscImageStudio.Controls;

/// <summary>Canonical motion values for code-driven animations. Three durations and a single
/// ease-out curve keep hover feedback, panel reveals and page transitions on one feel;
/// XAML storyboards use the same numbers (150 / 200 / 250 ms, cubic ease-out).</summary>
internal static class Motion
{
    /// <summary>150 ms: hover and selection color changes.</summary>
    public static readonly Duration Fast = new(TimeSpan.FromMilliseconds(150));

    /// <summary>200 ms: page transitions and collapse animations.</summary>
    public static readonly Duration Base = new(TimeSpan.FromMilliseconds(200));

    /// <summary>250 ms: panel expansion and toast entrance.</summary>
    public static readonly Duration Slow = new(TimeSpan.FromMilliseconds(250));

    public static IEasingFunction EaseOut { get; } = new CubicEase { EasingMode = EasingMode.EaseOut };
}
