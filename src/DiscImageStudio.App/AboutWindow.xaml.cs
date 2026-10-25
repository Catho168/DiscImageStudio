using System.Windows;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio;

/// <summary>About dialog, opened from the sidebar footer. Shares the window chrome recipe with
/// the project dialog so both modals read as the same application — rounded Win11 corners, the
/// app title bar, and no minimize/maximize buttons: About is a fixed-size read-only card.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow(AboutViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += (_, _) => WindowCorners.Apply(this);
    }
}
