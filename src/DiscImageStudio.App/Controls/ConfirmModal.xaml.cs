using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace DiscImageStudio.Controls;

public partial class ConfirmModal : UserControl
{
    private TaskCompletionSource<bool>? _pending;
    private TaskCompletionSource<bool> Pending => _pending
        ?? throw new InvalidOperationException("No confirmation is pending.");

    public ConfirmModal()
    {
        InitializeComponent();
        IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => CancelButton.Focus());
            }
        };
    }

    /// Shows the modal and completes with the user's choice; the default is refusal.
    public Task<bool> ShowAsync(string title, string message, string confirmLabel)
    {
        if (_pending is not null)
        {
            throw new InvalidOperationException("A confirmation is already pending.");
        }

        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmLabel;
        Root.Visibility = Visibility.Visible;
        _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return Pending.Task;
    }

    private void Complete(bool confirmed)
    {
        Root.Visibility = Visibility.Collapsed;
        TaskCompletionSource<bool>? pending = _pending;
        _pending = null;
        pending?.TrySetResult(confirmed);
    }

    /// Lets the host window route Escape to the open modal; returns false when hidden.
    public bool HandleKeyDown(Key key)
    {
        if (_pending is null || key != Key.Escape)
        {
            return false;
        }

        Complete(confirmed: false);
        return true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => Complete(confirmed: true);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Complete(confirmed: false);
}
