using System.Windows.Controls;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Views;

public partial class LivePreviewView : UserControl
{
    public LivePreviewView(LivePreviewViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
