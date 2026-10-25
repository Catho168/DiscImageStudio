using System.Windows;
using System.Windows.Controls;

namespace DiscImageStudio.Controls;

/// <summary>Shows or hides its content from one bound bool. One implementation for every
/// family/mode branch (CD/DVD, project/calibration source, read-back/calibration preview),
/// replacing the per-view DataTrigger style copies that drifted apart. Because the gated
/// content sits in ContentPresenter and not under a replaced Style, controls that rely on
/// implicit templates (ComboBox, TextBox, Button) keep rendering normally inside it.</summary>
public class VisibilityGate : ContentControl
{
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(VisibilityGate),
        new PropertyMetadata(false, static (sender, _) => ((VisibilityGate)sender).UpdateVisibility()));

    public static readonly DependencyProperty InvertedProperty = DependencyProperty.Register(
        nameof(Inverted), typeof(bool), typeof(VisibilityGate),
        new PropertyMetadata(false, static (sender, _) => ((VisibilityGate)sender).UpdateVisibility()));

    public VisibilityGate()
    {
        UpdateVisibility();
    }

    /// <summary>The bound flag that decides whether the content is shown.</summary>
    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>When true the gate flips: the content shows while the flag is false.</summary>
    public bool Inverted
    {
        get => (bool)GetValue(InvertedProperty);
        set => SetValue(InvertedProperty, value);
    }

    private void UpdateVisibility()
    {
        Visibility = IsOpen != Inverted ? Visibility.Visible : Visibility.Collapsed;
    }
}
