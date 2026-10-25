using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MahApps.Metro.IconPacks;

namespace DiscImageStudio.Controls;

/// <summary>In-place empty state: an icon, one line about what is missing and an optional
/// action that jumps to the workspace which fills the gap. Pages use it instead of bare
/// placeholder text so the "what now?" always has an answer on screen.</summary>
public partial class EmptyStateCard : UserControl
{
    public static readonly DependencyProperty IconKindProperty = DependencyProperty.Register(
        nameof(IconKind), typeof(PackIconLucideKind), typeof(EmptyStateCard),
        new PropertyMetadata(PackIconLucideKind.Disc, static (sender, _) => ((EmptyStateCard)sender).UpdateVisuals()));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmptyStateCard),
        new PropertyMetadata(string.Empty, static (sender, _) => ((EmptyStateCard)sender).UpdateVisuals()));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(EmptyStateCard),
        new PropertyMetadata(string.Empty, static (sender, _) => ((EmptyStateCard)sender).UpdateVisuals()));

    public static readonly DependencyProperty ActionLabelProperty = DependencyProperty.Register(
        nameof(ActionLabel), typeof(string), typeof(EmptyStateCard),
        new PropertyMetadata(string.Empty, static (sender, _) => ((EmptyStateCard)sender).UpdateVisuals()));

    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(
        nameof(ActionCommand), typeof(ICommand), typeof(EmptyStateCard),
        new PropertyMetadata(null, static (sender, _) => ((EmptyStateCard)sender).UpdateVisuals()));

    public static readonly DependencyProperty ActionCommandParameterProperty = DependencyProperty.Register(
        nameof(ActionCommandParameter), typeof(object), typeof(EmptyStateCard),
        new PropertyMetadata(null, static (sender, _) => ((EmptyStateCard)sender).UpdateVisuals()));

    public EmptyStateCard()
    {
        InitializeComponent();
        UpdateVisuals();
    }

    public PackIconLucideKind IconKind
    {
        get => (PackIconLucideKind)GetValue(IconKindProperty);
        set => SetValue(IconKindProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string ActionLabel
    {
        get => (string)GetValue(ActionLabelProperty);
        set => SetValue(ActionLabelProperty, value);
    }

    public ICommand? ActionCommand
    {
        get => (ICommand?)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    public object? ActionCommandParameter
    {
        get => GetValue(ActionCommandParameterProperty);
        set => SetValue(ActionCommandParameterProperty, value);
    }

    private void UpdateVisuals()
    {
        IconHost.Kind = IconKind;
        TitleText.Text = Title;
        TitleText.Visibility = string.IsNullOrWhiteSpace(Title) ? Visibility.Collapsed : Visibility.Visible;
        DescriptionText.Text = Description;
        DescriptionText.Visibility = string.IsNullOrWhiteSpace(Description) ? Visibility.Collapsed : Visibility.Visible;
        ActionButton.Content = ActionLabel;
        ActionButton.Command = ActionCommand;
        ActionButton.CommandParameter = ActionCommandParameter;
        // The action only exists when it has somewhere to go; a bare description stays valid.
        ActionButton.Visibility = ActionCommand is not null && !string.IsNullOrWhiteSpace(ActionLabel)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
