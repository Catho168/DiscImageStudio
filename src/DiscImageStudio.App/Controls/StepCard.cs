using System.Windows;
using System.Windows.Controls;

namespace DiscImageStudio.Controls;

public enum StepCardState
{
    /// <summary>Not reachable yet; the body is collapsed and the chip reads 待开始.</summary>
    Pending,

    /// <summary>The step the user is on; chip reads 进行中 with the primary color.</summary>
    Active,

    /// <summary>Finished; chip reads 已完成 with the success color and the dot becomes a check.</summary>
    Done,
}

/// <summary>Numbered step card for guided workflows (the calibration workspace's three-stage
/// column): a state chip beside the title and a collapsible body. The owning view model
/// expands the active step and collapses the others, so the column reads as one progressive
/// disclosure instead of three always-open forms; the user can still toggle any step.</summary>
public class StepCard : Expander
{
    public static readonly DependencyProperty StepNumberProperty = DependencyProperty.Register(
        nameof(StepNumber), typeof(int), typeof(StepCard), new PropertyMetadata(1));

    public static readonly DependencyProperty StepTitleProperty = DependencyProperty.Register(
        nameof(StepTitle), typeof(string), typeof(StepCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(StepCardState), typeof(StepCard), new PropertyMetadata(StepCardState.Pending));

    static StepCard()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(StepCard),
            new FrameworkPropertyMetadata(typeof(StepCard)));
    }

    public int StepNumber
    {
        get => (int)GetValue(StepNumberProperty);
        set => SetValue(StepNumberProperty, value);
    }

    public string StepTitle
    {
        get => (string)GetValue(StepTitleProperty);
        set => SetValue(StepTitleProperty, value);
    }

    public StepCardState State
    {
        get => (StepCardState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }
}
