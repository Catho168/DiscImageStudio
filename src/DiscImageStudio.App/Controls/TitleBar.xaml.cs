using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DiscImageStudio.Controls;

public partial class TitleBar : UserControl
{
    private Window? _window;
    private Path? _maximizeGlyph;
    private Path? _restoreGlyph;

    public TitleBar()
    {
        InitializeComponent();
        MaximizeButton.Loaded += (_, _) =>
        {
            _maximizeGlyph = (Path)MaximizeButton.Template.FindName("MaximizeGlyph", MaximizeButton);
            _restoreGlyph = (Path)MaximizeButton.Template.FindName("RestoreGlyph", MaximizeButton);
            UpdateGlyph();
        };
        Loaded += (_, _) => AttachWindow();
        Unloaded += (_, _) => DetachWindow();
    }

    private void AttachWindow()
    {
        DetachWindow();
        _window = Window.GetWindow(this);
        if (_window is null)
        {
            return;
        }

        _window.StateChanged += Window_StateChanged;
        UpdateGlyph();
    }

    private void DetachWindow()
    {
        if (_window is not null)
        {
            _window.StateChanged -= Window_StateChanged;
            _window = null;
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e) => UpdateGlyph();

    private void UpdateGlyph()
    {
        bool maximized = _window?.WindowState == WindowState.Maximized;
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
        if (_maximizeGlyph is null || _restoreGlyph is null)
        {
            return;
        }

        _maximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        _restoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.WindowState = WindowState.Minimized;
        }
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (_window is null)
        {
            return;
        }

        _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => _window?.Close();
}
