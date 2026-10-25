using System.Windows.Controls;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Views;

public partial class BluRayView : UserControl
{
    public BluRayView(BluRayViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
