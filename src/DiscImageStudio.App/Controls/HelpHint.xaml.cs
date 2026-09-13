using System.Windows;
using System.Windows.Controls;

namespace DiscImageStudio.Controls;

/// <summary>
/// Hover help hotspot: the "?" badge explains a non-obvious option without spending
/// permanent space on the explanation.
/// </summary>
public partial class HelpHint : UserControl
{
    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(HelpHint),
        new PropertyMetadata(string.Empty, static (sender, _) => ((HelpHint)sender).UpdateHint()));

    public HelpHint()
    {
        InitializeComponent();
        UpdateHint();
    }

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    private void UpdateHint()
        => Root.ToolTip = string.IsNullOrEmpty(Hint) ? null : Hint;
}
