using System.Windows.Controls;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Views;

public partial class CdView : UserControl
{
    public CdView(CdViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
