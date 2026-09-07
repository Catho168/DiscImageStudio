using System.Windows.Controls;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Views;

public partial class BurnView : UserControl
{
    public BurnView(BurnViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
