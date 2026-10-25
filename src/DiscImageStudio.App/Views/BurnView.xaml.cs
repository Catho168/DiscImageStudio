using System.Windows.Controls;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Views;

public partial class BurnView : UserControl
{
    public BurnView(BurnViewModel viewModel)
    {
        InitializeComponent();
        // Binds the burn view model directly, like every other workspace page; the shared
        // state and shell navigation are exposed on the view model.
        DataContext = viewModel;
    }
}
