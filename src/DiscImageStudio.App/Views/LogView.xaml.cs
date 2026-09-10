using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Views;

public partial class LogView : UserControl
{
    private readonly ShellViewModel _shell;
    private bool _isAttached;

    public LogView(ShellViewModel shell, LogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _shell = shell;
        // The shell outlives every page swap, so these subscriptions are never dropped: leaving
        // the page must not stop the log from collecting engine output.
        shell.LogAppended += OnLogAppended;
        shell.LogCleared += OnLogCleared;
        Loaded += (_, _) =>
        {
            // Re-render everything collected while the page was hidden, then follow the tail.
            LogTextBox.Text = _shell.LogText;
            _isAttached = true;
            ScrollToEnd();
        };
        Unloaded += (_, _) => _isAttached = false;
    }

    // Presentation glue only: append engine output lines and keep the view pinned to bottom.
    private void OnLogAppended(string text)
    {
        if (!_isAttached)
        {
            return;
        }

        LogTextBox.AppendText(text);
        ScrollToEnd();
    }

    private void OnLogCleared()
    {
        if (_isAttached)
        {
            LogTextBox.Clear();
        }
    }

    // The box can only scroll to text it has already measured, so wait for the layout pass.
    private void ScrollToEnd()
        => Dispatcher.InvokeAsync(LogTextBox.ScrollToEnd, DispatcherPriority.Background);
}
