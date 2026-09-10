using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DiscImageStudio.ViewModels;

public partial class LogViewModel : ObservableObject
{
    public LogViewModel(ShellViewModel shell)
    {
        ClearDisplayCommand = new RelayCommand(shell.ClearLog);
    }

    /// <summary>Clears only the on-screen log; engine output keeps flowing afterwards.</summary>
    public RelayCommand ClearDisplayCommand { get; }
}
