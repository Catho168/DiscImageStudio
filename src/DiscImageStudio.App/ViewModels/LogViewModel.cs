using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DiscImageStudio.ViewModels;

public partial class LogViewModel : ObservableObject
{
    public LogViewModel(ShellViewModel shell)
    {
        ClearDisplayCommand = new RelayCommand(shell.ClearLog);
        OpenOutputCommand = shell.OpenOutputCommand;
    }

    /// <summary>Clears only the on-screen log; engine output keeps flowing afterwards.</summary>
    public RelayCommand ClearDisplayCommand { get; }

    /// <summary>Opens the last job's output folder; the shell owns the path and its enablement.</summary>
    public RelayCommand OpenOutputCommand { get; }
}
