using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiscImageStudio.ViewModels;

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error,
}

public partial class ToastItem : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly EventHandler _onTick;

    [ObservableProperty]
    private bool _entered;

    public string Title { get; }

    public string Message { get; }

    public ToastKind Kind { get; }

    public TimeSpan Timeout { get; }

    public event EventHandler? Closed;

    public ToastItem(Dispatcher dispatcher, string title, string message, ToastKind kind, TimeSpan timeout)
    {
        _dispatcher = dispatcher;
        Title = title;
        Message = message;
        Kind = kind;
        Timeout = timeout;
        _onTick = (_, _) => Close();
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = timeout,
        };
        _timer.Tick += _onTick;
    }

    public void BeginLifetime() => _timer.Start();

    public void Close()
    {
        _timer.Stop();
        _timer.Tick -= _onTick;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
