using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace DiscImageStudio.Views;

public partial class CalibrationView : UserControl
{
    public CalibrationView()
    {
        InitializeComponent();
        DataContextChanged += OnCalibrationContextChanged;
    }

    public CalibrationView(object viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnCalibrationContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged old) PropertyChangedEventManager.RemoveHandler(old, OnCalibrationPropertyChanged, string.Empty);
        if (e.NewValue is INotifyPropertyChanged current) PropertyChangedEventManager.AddHandler(current, OnCalibrationPropertyChanged, string.Empty);
        TargetPreviewToggle.IsChecked = false;
    }

    private void OnCalibrationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "PhotoImage" or "IsTraceStep") TargetPreviewToggle.IsChecked = false;
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateResponsiveLayout();

    private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateResponsiveLayout();

    private void UpdateResponsiveLayout()
    {
        if (PreviewShell is null || SettingsScroll is null || WorkScroll is null) return;
        var narrow = ActualWidth < 990;
        PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
        GapColumn.Width = new GridLength(narrow ? 0 : 16);
        SettingsColumn.Width = new GridLength(narrow ? 0 : 304);
        Grid.SetColumn(SettingsScroll, narrow ? 0 : 2);
        Grid.SetRow(SettingsScroll, narrow ? 1 : 0);
        double availableHeight = WorkScroll.ActualHeight > 0 ? WorkScroll.ActualHeight - 2 : ActualHeight - 160;
        PreviewShell.Height = narrow ? Math.Clamp(availableHeight, 340, 700) : Math.Max(320, availableHeight);
        WorkScroll.VerticalScrollBarVisibility = narrow || availableHeight < 320
            ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        SettingsScroll.MaxHeight = narrow ? double.PositiveInfinity : PreviewShell.Height;
        SettingsScroll.VerticalScrollBarVisibility = narrow ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        SettingsScroll.Margin = new Thickness(0, narrow ? 16 : 0, 0, 0);
    }

    private void OnZoomOut(object sender, RoutedEventArgs e) => PhotoCanvas.ZoomBy(1 / 1.3);
    private void OnZoomIn(object sender, RoutedEventArgs e) => PhotoCanvas.ZoomBy(1.3);
    private void OnResetView(object sender, RoutedEventArgs e) => PhotoCanvas.ResetView();
    private void OnRemoveTracePoint(object sender, RoutedEventArgs e) => PhotoCanvas.RemoveSelectedTracePoint();

}
