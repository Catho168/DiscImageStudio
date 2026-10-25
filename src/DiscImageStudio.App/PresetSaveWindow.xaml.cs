using System.Windows;
using System.Windows.Input;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio;

/// <summary>Asks for a user preset's display name and optional note before the parameter set
/// lands in the catalog. The name is required; the note is free-form. This window validates
/// nothing about the geometry — the caller parses it before opening here.</summary>
public partial class PresetSaveWindow : Window
{
    public PresetSaveWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowCorners.Apply(this);
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                Close();
            }
        };
        NameBox.TextChanged += (_, _) => SaveButton.IsEnabled = PresetName.Length > 0;
        Loaded += (_, _) => NameBox.Focus();
        SaveButton.IsEnabled = false;
    }

    public string PresetName => NameBox.Text.Trim();

    public string PresetComment => CommentBox.Text.Trim();

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Shows the dialog over its owner and returns the confirmed name and note, or
    /// null when the user cancelled.</summary>
    public static PresetSaveResult? Show(Window owner)
    {
        PresetSaveWindow window = new() { Owner = owner };
        return window.ShowDialog() == true
            ? new PresetSaveResult(window.PresetName, window.PresetComment)
            : null;
    }
}
