using System.Windows.Controls;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Views;

public partial class DvdView : UserControl
{
    public DvdView(DvdViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
