using System.Windows.Controls;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Views;

public partial class HomeView : UserControl
{
    public HomeView(HomeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
