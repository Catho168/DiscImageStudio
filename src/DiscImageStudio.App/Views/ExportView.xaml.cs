using System.Windows.Controls;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Views;

public partial class ExportView : UserControl
{
    public ExportView(ExportViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
