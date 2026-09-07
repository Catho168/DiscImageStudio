using System.IO;
using System.Text;
using System.Windows.Threading;

namespace DiscImageStudio.Services;

/// Redirects engine Console output to the UI log, line by line, on the UI dispatcher.
internal sealed class DispatcherTextWriter : TextWriter
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _append;
    private readonly StringBuilder _buffer = new();
    private readonly object _gate = new();

    internal DispatcherTextWriter(Dispatcher dispatcher, Action<string> append)
    {
        _dispatcher = dispatcher;
        _append = append;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        string? completed = null;
        lock (_gate)
        {
            _buffer.Append(value);
            if (value == '\n')
            {
                completed = _buffer.ToString();
                _buffer.Clear();
            }
        }

        if (completed is not null)
        {
            Dispatch(completed);
        }
    }

    public override void Write(string? value)
    {
        if (value is null)
        {
            return;
        }

        foreach (char character in value)
        {
            Write(character);
        }
    }

    public override void Flush()
    {
        string remaining;
        lock (_gate)
        {
            remaining = _buffer.ToString();
            _buffer.Clear();
        }

        if (remaining.Length != 0)
        {
            Dispatch(remaining);
        }
    }

    private void Dispatch(string text)
        => _dispatcher.BeginInvoke(() => _append(text), DispatcherPriority.Background);
}
