using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiscImageStudio.ViewModels;

namespace DiscImageStudio.Views;

public partial class LivePreviewView : UserControl
{
    private readonly LivePreviewViewModel _viewModel;
    private int _pixelWidth;
    private int _pixelHeight;
    private double _zoom = 1;
    private Vector _pan;
    private Point _dragStart;
    private Vector _panBeforeDrag;
    private bool _dragging;
    private bool _listening;
    private string? _inputKey;

    public LivePreviewView(LivePreviewViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateResponsiveLayout();
        if (!_listening)
        {
            _viewModel.PropertyChanged += PreviewPropertyChanged;
            _viewModel.State.PropertyChanged += InputPropertyChanged;
            _viewModel.BluRay.PropertyChanged += InputPropertyChanged;
            _listening = true;
        }
        SynchronizeInput();
        SynchronizeImage();
    }

    private void UpdateResponsiveLayout()
    {
        if (PageLayout is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        // WPF sizes are device-independent pixels. Use the page's actual space
        // after the shell/sidebar, rather than physical screen resolution.
        bool compact = ActualWidth < 900;
        bool stacked = ActualWidth < 700;
        bool shortPage = ActualHeight < 600;
        double horizontalMargin = compact ? 16 : 40;
        PageLayout.Margin = new Thickness(horizontalMargin, shortPage ? 16 : 24,
            horizontalMargin, shortPage ? 16 : 32);
        PageHeading.Margin = new Thickness(0, 0, 0, shortPage ? 12 : 24);

        ParameterColumn.Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(compact ? 280 : 300);
        PaneGapColumn.Width = new GridLength(stacked ? 0 : 16);
        CanvasColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ParameterRow.Height = new GridLength(stacked ? 2 : 1, GridUnitType.Star);
        PaneGapRow.Height = new GridLength(stacked ? 12 : 0);
        CanvasRow.Height = stacked ? new GridLength(3, GridUnitType.Star) : new GridLength(0);
        // Stacked panes need usable viewports, not just visible borders. If the
        // work area is shorter than both panes, scroll the page body instead of
        // shrinking the parameter scroll viewer to zero below its fixed footer.
        ParameterRow.MinHeight = stacked ? 320 : 0;
        CanvasRow.MinHeight = stacked ? 260 : 0;
        PageScroll.VerticalScrollBarVisibility = stacked ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        Grid.SetColumnSpan(ParameterPane, stacked ? 3 : 1);
        Grid.SetRow(PreviewCard, stacked ? 2 : 0);
        Grid.SetColumn(PreviewCard, stacked ? 0 : 2);
        Grid.SetColumnSpan(PreviewCard, stacked ? 3 : 1);
        InputModeSwitch.Margin = new Thickness(0, 0, 0, stacked ? 8 : 16);
        UpdateScrollableContentSize();
        UpdatePreviewChrome();
    }

    private void PageScroll_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateScrollableContentSize();

    private void UpdateScrollableContentSize()
    {
        if (PageScroll is null || PreviewLayout is null) return;
        PreviewLayout.Height = PageScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto
            ? Math.Max(PageScroll.ActualHeight, 320 + 12 + 260)
            : double.NaN;
    }

    private void NestedScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer inner
            || PageScroll.VerticalScrollBarVisibility != ScrollBarVisibility.Auto || PageScroll.ScrollableHeight <= 0)
            return;
        bool atBoundary = e.Delta > 0 ? inner.VerticalOffset <= 0
            : e.Delta < 0 && inner.VerticalOffset >= inner.ScrollableHeight;
        if (!atBoundary) return;
        // ScrollViewer normally consumes the wheel even at its limits. Forward
        // only boundary gestures to the enclosing page, preserving ordinary
        // parameter scrolling and the canvas's separate zoom gestures.
        e.Handled = true;
        PageScroll.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = Mouse.MouseWheelEvent,
            Source = PageScroll,
        });
    }

    private void PreviewCard_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePreviewChrome();

    private void UpdatePreviewChrome()
    {
        if (PrecisionPanel is null || PreviewCard.ActualWidth <= 0) return;
        // The overlay can scroll within the canvas card; opening it never grows
        // a star row or pushes the download/zoom controls outside the page.
        PrecisionPanel.Width = Math.Clamp(PreviewCard.ActualWidth - 70, 32, 248);
        PrecisionPanel.MaxHeight = Math.Max(0, PreviewCard.ActualHeight - 56);
        bool narrowFooter = PreviewCard.ActualWidth < 360;
        Grid.SetRow(PreviewStatus, narrowFooter ? 1 : 0);
        Grid.SetColumnSpan(PreviewStatus, narrowFooter ? 2 : 1);
        Grid.SetColumn(ZoomControls, narrowFooter ? 0 : 1);
        Grid.SetColumnSpan(ZoomControls, narrowFooter ? 2 : 1);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_listening)
        {
            _viewModel.PropertyChanged -= PreviewPropertyChanged;
            _viewModel.State.PropertyChanged -= InputPropertyChanged;
            _viewModel.BluRay.PropertyChanged -= InputPropertyChanged;
            _listening = false;
        }
        EndDrag();
    }

    private void PreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LivePreviewViewModel.ResultImage)) SynchronizeImage();
        if (e.PropertyName is nameof(LivePreviewViewModel.SelectedDiscTypeIndex)
            or nameof(LivePreviewViewModel.IsCalibrationPreview) or nameof(LivePreviewViewModel.BluRayIsoPath))
            SynchronizeInput();
    }

    private void InputPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DiscParametersState.CdImagePath) or nameof(DiscParametersState.CdTrackPath)
            or nameof(DiscParametersState.DvdImagePath) or nameof(DiscParametersState.DvdIsoPath)
            or nameof(BluRayViewModel.ImagePath)) SynchronizeInput();
    }

    private void SynchronizeInput()
    {
        string path = _viewModel.IsBluRaySelected
            ? (_viewModel.IsCalibrationPreview ? _viewModel.BluRay.ImagePath : _viewModel.BluRayIsoPath)
            : _viewModel.IsDvdSelected
                ? (_viewModel.IsCalibrationPreview ? _viewModel.State.DvdImagePath : _viewModel.State.DvdIsoPath)
                : (_viewModel.IsCalibrationPreview ? _viewModel.State.CdImagePath : _viewModel.State.CdTrackPath);
        string key = $"{_viewModel.SelectedDiscTypeIndex}:{_viewModel.IsCalibrationPreview}:{path.Trim()}";
        if (string.Equals(key, _inputKey, StringComparison.OrdinalIgnoreCase)) return;
        _inputKey = key;
        ResetView();
    }

    private void SynchronizeImage()
    {
        int width = _viewModel.ResultImage?.PixelWidth ?? 0;
        int height = _viewModel.ResultImage?.PixelHeight ?? 0;
        bool dimensionsChanged = width != _pixelWidth || height != _pixelHeight;
        _pixelWidth = width;
        _pixelHeight = height;
        // Explicit pixel dimensions make the transform independent of PNG DPI metadata.
        PreviewImage.Width = width;
        PreviewImage.Height = height;
        if (dimensionsChanged) ResetView();
        else UpdateViewport();
    }

    private double FitScale => _pixelWidth > 0 && _pixelHeight > 0
        ? Math.Min(PreviewViewport.ActualWidth / _pixelWidth, PreviewViewport.ActualHeight / _pixelHeight) : 0;

    private double DeviceScale => VisualTreeHelper.GetDpi(PreviewViewport).DpiScaleX;

    private void UpdateViewport()
    {
        if (PreviewViewport is null || PreviewScale is null || ZoomLabel is null) return;
        double scale = FitScale * _zoom;
        if (scale <= 0)
        {
            ZoomLabel.Text = "—";
            return;
        }

        // Keep the enlarged image over the viewport, so dragging cannot lose it off-screen.
        double limitX = Math.Max(0, (_pixelWidth * scale - PreviewViewport.ActualWidth) / 2);
        double limitY = Math.Max(0, (_pixelHeight * scale - PreviewViewport.ActualHeight) / 2);
        _pan = new Vector(Math.Clamp(_pan.X, -limitX, limitX), Math.Clamp(_pan.Y, -limitY, limitY));
        PreviewScale.ScaleX = PreviewScale.ScaleY = scale;
        PreviewTranslation.X = (PreviewViewport.ActualWidth - _pixelWidth * scale) / 2 + _pan.X;
        PreviewTranslation.Y = (PreviewViewport.ActualHeight - _pixelHeight * scale) / 2 + _pan.Y;
        double percent = scale * DeviceScale * 100;
        RenderOptions.SetBitmapScalingMode(PreviewImage,
            percent >= 100 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        ZoomLabel.Text = percent < 10 ? $"{percent:0.#}%" : $"{percent:0}%";
    }

    private void ResetView()
    {
        EndDrag();
        _zoom = 1;
        _pan = default;
        UpdateViewport();
    }

    private void ZoomAt(double requestedZoom, Point anchor)
    {
        double fit = FitScale;
        if (fit <= 0 || !_viewModel.HasResultImage) return;
        EndDrag();
        double actualPixelsZoom = 1 / (fit * DeviceScale);
        double zoom = Math.Clamp(requestedZoom, Math.Min(1, actualPixelsZoom), Math.Max(32, 8 * actualPixelsZoom));
        Vector relativeAnchor = anchor - new Point(PreviewViewport.ActualWidth / 2, PreviewViewport.ActualHeight / 2);
        _pan = relativeAnchor - (relativeAnchor - _pan) * (zoom / _zoom);
        _zoom = zoom;
        UpdateViewport();
    }

    private void FitButton_Click(object sender, RoutedEventArgs e) => ResetView();

    private void ActualPixelsButton_Click(object sender, RoutedEventArgs e)
    {
        if (FitScale <= 0) return;
        ZoomAt(1 / (FitScale * DeviceScale), new Point(PreviewViewport.ActualWidth / 2, PreviewViewport.ActualHeight / 2));
    }

    private void PreviewViewport_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateViewport();

    private void PreviewViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_viewModel.HasResultImage) return;
        ZoomAt(_zoom * Math.Pow(1.2, e.Delta / 120.0), e.GetPosition(PreviewViewport));
        e.Handled = true;
    }

    private void PreviewViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.HasResultImage) return;
        if (e.ClickCount == 2) ResetView();
        else
        {
            _dragStart = e.GetPosition(PreviewViewport);
            _panBeforeDrag = _pan;
            _dragging = PreviewViewport.CaptureMouse();
            if (_dragging) PreviewViewport.Cursor = Cursors.SizeAll;
        }
        e.Handled = true;
    }

    private void PreviewViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _pan = _panBeforeDrag + (e.GetPosition(PreviewViewport) - _dragStart);
        UpdateViewport();
        e.Handled = true;
    }

    private void PreviewViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        EndDrag();
        e.Handled = true;
    }

    private void PreviewViewport_LostMouseCapture(object sender, MouseEventArgs e) => EndDrag();

    private void EndDrag()
    {
        _dragging = false;
        if (PreviewViewport is null) return;
        PreviewViewport.Cursor = null;
        if (PreviewViewport.IsMouseCaptured) PreviewViewport.ReleaseMouseCapture();
    }
}
