using System.Windows;
using System.Windows.Controls;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Views;

public partial class LogView : UserControl
{
    private readonly ShellViewModel _shell;

    public LogView(ShellViewModel shell, LogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _shell = shell;
        shell.LogAppended += OnLogAppended;
        shell.LogCleared += OnLogCleared;
        Unloaded += (_, _) =>
        {
            shell.LogAppended -= OnLogAppended;
            shell.LogCleared -= OnLogCleared;
        };
    }

    // Presentation glue only: append engine output lines and keep the view pinned to bottom.
    private void OnLogAppended(string text)
    {
        LogTextBox.AppendText(text);
        LogTextBox.ScrollToEnd();
    }

    private void OnLogCleared() => LogTextBox.Clear();
}
