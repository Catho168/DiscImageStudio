using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DiscImageStudio.Controls;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;
using DiscImageStudio.Views;

namespace DiscImageStudio;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly UserControl[] _pages;
    private readonly (SideNavItem Item, int PageIndex)[] _navItems;
    private bool _isSidebarCollapsed;
    private bool _isManuallyCollapsed;

    public MainWindow()
    {
        InitializeComponent();
        _shell = new ShellViewModel(Dispatcher)
        {
            Dialogs = new FileDialogService(() => this),
        };
        _shell.ConfirmHandler = (title, message, confirmLabel)
            => BurnConfirmModal.ShowAsync(title, message, confirmLabel);
        DataContext = _shell;

        _pages =
        [
            new HomeView(_shell.Home),
            new DvdView(_shell.Dvd),
            new CdView(_shell.Cd),
            new BurnView(_shell.Burn),
            new LivePreviewView(_shell.LivePreview),
            new LogView(_shell, _shell.Log),
            new AboutView(_shell.About),
            new CalibrationView(_shell.Calibration),
        ];
        _navItems =
        [
            (NavHome, ShellViewModel.HomeTabIndex),
            (NavDvd, ShellViewModel.DvdTabIndex),
            (NavCd, ShellViewModel.CdTabIndex),
            (NavPreview, ShellViewModel.PreviewTabIndex),
            (NavBurn, ShellViewModel.BurnTabIndex),
            (NavLog, ShellViewModel.LogTabIndex),
            (NavAbout, ShellViewModel.AboutTabIndex),
            (NavCalibration, ShellViewModel.CalibrationTabIndex),
        ];
        foreach ((SideNavItem item, int pageIndex) in _navItems)
        {
            item.Activated += (_, _) => _shell.NavigateTo(pageIndex);
        }

        NavDiscGroup.HeaderActivated += NavDiscGroup_HeaderActivated;

        _shell.PropertyChanged += Shell_PropertyChanged;
        UpdateNavActive(ShellViewModel.HomeTabIndex);
        PageHost.Content = _pages[ShellViewModel.HomeTabIndex];

        SizeChanged += (_, _) => UpdateSidebarCollapse();
        PreviewKeyDown += (_, args) => BurnConfirmModal.HandleKeyDown(args.Key);
        SourceInitialized += (_, _) => ApplyWindowRoundCorners();
        StateChanged += (_, _) => UpdateMaximizedMargin();
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        UpdateMaximizedMargin();
    }

    // When maximized, a borderless WindowChrome window extends past the work area by the
    // system resize border on each side; padding the root back keeps the title bar fully
    // on screen.
    private void UpdateMaximizedMargin()
    {
        RootGrid.Margin = WindowState == WindowState.Maximized
            ? SystemParameters.WindowResizeBorderThickness
            : default;
    }

    // ---- Snapshot hooks (used by UiPreviewRenderer / CLI ui-snapshot) ----

    internal bool IsLivePreviewReady => _shell.LivePreview.IsLivePreviewReady;

    internal void ConfigureSnapshot(
        int selectedTab,
        string? previewPath,
        string? liveInputPath = null,
        string? liveDisc = null,
        string? liveMode = null,
        string? liveProcessing = null)
    {
        if (selectedTab < 0 || selectedTab >= ShellViewModel.PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(selectedTab));
        }

        _shell.SelectPage(selectedTab);
        if (selectedTab == ShellViewModel.CalibrationTabIndex)
        {
            _shell.Calibration.EnsureTarget();
        }
        if (!string.IsNullOrWhiteSpace(previewPath))
        {
            string fullPath = Path.GetFullPath(previewPath);
            _shell.SelectPage(ShellViewModel.PreviewTabIndex);
            _shell.LivePreview.PrepareResultPreview(fullPath);
            _shell.LivePreview.ShowResultPreview(fullPath);
        }

        if (!string.IsNullOrWhiteSpace(liveInputPath))
        {
            bool dvd = string.Equals(liveDisc, "dvd", StringComparison.OrdinalIgnoreCase);
            // Omitting --live-mode keeps the page default (source picture); scripts probe the
            // other mode by naming it.
            bool calibration = liveMode is null
                || string.Equals(liveMode, "calibrate", StringComparison.OrdinalIgnoreCase);
            bool ring = string.Equals(liveProcessing, "ring", StringComparison.OrdinalIgnoreCase);
            _shell.LivePreview.SetDiscType(dvd, clearResult: false);
            _shell.LivePreview.SetPreviewMode(calibration, clearResult: false);
            if (ring)
            {
                _shell.State.CdImageProcessingModeIndex = 1;
                _shell.State.DvdImageProcessingModeIndex = 1;
            }

            string fullInput = Path.GetFullPath(liveInputPath);
            if (calibration)
            {
                if (dvd)
                {
                    _shell.State.DvdImagePath = fullInput;
                }
                else
                {
                    _shell.State.CdImagePath = fullInput;
                }
            }
            else if (dvd)
            {
                _shell.State.DvdIsoPath = fullInput;
            }
            else
            {
                _shell.State.CdTrackPath = fullInput;
            }

            _shell.SelectPage(ShellViewModel.PreviewTabIndex);
        }
    }

    internal async Task LoadCalibrationSnapshotAsync(string path)
    {
        _shell.SelectPage(ShellViewModel.CalibrationTabIndex);
        await _shell.Calibration.LoadSessionFromAsync(path);
        if (_shell.Calibration.SolveCommand.CanExecute(null))
            await _shell.Calibration.SolveCommand.ExecuteAsync(null);
    }

    internal void ConfigureCalibrationSnapshotView(string mode)
    {
        if (mode is not ("pattern" or "closeup"))
            throw new ArgumentException("Calibration snapshot view must be pattern or closeup.");
        _shell.SelectPage(ShellViewModel.CalibrationTabIndex);
        var view = (CalibrationView)_pages[ShellViewModel.CalibrationTabIndex];
        var toggle = (CheckBox)view.FindName("TargetPreviewToggle");
        toggle.IsChecked = mode == "pattern";
        UpdateLayout();
        var canvas = (CalibrationPhotoCanvas)view.FindName("PhotoCanvas");
        canvas.ResetView();
        if (mode != "closeup") return;
        var points = _shell.Calibration.TracePoints;
        if (points.Count < 2) throw new InvalidOperationException("Closeup needs a loaded calibration trace.");
        var minimum = new Point(points.Min(p => p.XMm), points.Min(p => p.YMm));
        var maximum = new Point(points.Max(p => p.XMm), points.Max(p => p.YMm));
        Point a = canvas.WorldToView(minimum), b = canvas.WorldToView(maximum);
        double zoom = Math.Min((canvas.ActualWidth - 90) / Math.Max(1, b.X - a.X),
            (canvas.ActualHeight - 90) / Math.Max(1, b.Y - a.Y));
        canvas.ZoomBy(Math.Clamp(zoom, 1, 8));
        Point center = canvas.WorldToView(new Point((minimum.X + maximum.X) / 2, (minimum.Y + maximum.Y) / 2));
        canvas.PanBy(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2) - center);
    }

    // ---- Window lifecycle ----

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        _shell.MarkWindowLoaded();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _shell.LivePreview.OnWindowClosed();
        _shell.Calibration.OnWindowClosed();
    }

    private void Shell_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentPageIndex))
        {
            UpdateNavActive(_shell.CurrentPageIndex);
            ShowPage(_shell.CurrentPageIndex);
        }
    }

    // ---- Navigation chrome: active states, page swap, entrance animation, scroll reset ----

    private void UpdateNavActive(int pageIndex)
    {
        foreach ((SideNavItem item, int itemPage) in _navItems)
        {
            item.IsActive = itemPage == pageIndex;
        }
    }

    private void ShowPage(int index)
    {
        UserControl page = _pages[index];
        if (ReferenceEquals(PageHost.Content, page))
        {
            return;
        }

        PageHost.Content = page;
        ResetPageScroll(page);
        PlayEntrance(page);
    }

    private static void ResetPageScroll(DependencyObject root)
    {
        if (root is ScrollViewer scrollViewer)
        {
            scrollViewer.ScrollToVerticalOffset(0);
            scrollViewer.ScrollToHorizontalOffset(0);
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            ResetPageScroll(VisualTreeHelper.GetChild(root, index));
        }
    }

    private static void PlayEntrance(UIElement page)
    {
        TranslateTransform translate = new(0, 8);
        page.RenderTransform = translate;
        page.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        translate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    // ---- Sidebar collapse (window width < 920, or the manual toggle) ----

    private void SidebarCollapseToggle_Click(object sender, RoutedEventArgs e)
    {
        // Narrow windows own the collapsed layout via auto-collapse; manual expand is a no-op there.
        if (ActualWidth < 920)
        {
            return;
        }

        _isManuallyCollapsed = !_isManuallyCollapsed;
        UpdateSidebarCollapse();
    }

    private void NavDiscGroup_HeaderActivated(object sender, RoutedEventArgs e)
    {
        // Narrow windows have no manual expand: the header toggles the child icon list
        // instead, giving DVD/CD access while the sidebar stays at 72px.
        if (ActualWidth < 920)
        {
            NavDiscGroup.ToggleIconList();
            return;
        }

        if (!_isManuallyCollapsed)
        {
            return;
        }

        _isManuallyCollapsed = false;
        UpdateSidebarCollapse();
    }

    private void UpdateSidebarCollapse()
    {
        bool narrow = ActualWidth < 920;
        bool collapsed = narrow || _isManuallyCollapsed;
        // Auto-collapse owns the narrow layout, so the manual toggle is meaningless there;
        // it comes back once the window widens past the breakpoint.
        SidebarCollapseToggle.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        if (collapsed == _isSidebarCollapsed)
        {
            return;
        }

        double from = _isSidebarCollapsed ? 72 : 288;
        double to = collapsed ? 72 : 288;
        _isSidebarCollapsed = collapsed;
        SidebarBorder.BeginAnimation(
            WidthProperty,
            new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        foreach ((SideNavItem item, _) in _navItems)
        {
            item.IsCollapsed = collapsed;
        }

        NavDiscGroup.IsCollapsed = collapsed;
        TopNavPanel.Margin = collapsed ? new Thickness(0, 20, 0, 0) : new Thickness(0, 24, 0, 0);
        SidebarGrid.Margin = collapsed ? new Thickness(0) : new Thickness(24, 0, 24, 0);
        CollapseLeftIcon.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseRightIcon.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        SidebarCollapseToggle.ToolTip = collapsed ? "展开侧边栏" : "收起侧边栏";
    }

    // ---- Win11 rounded corners ----

    private void ApplyWindowRoundCorners()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int preference = DwmWindowCornerPreferenceRound;
        _ = DwmSetWindowAttribute(handle, DwmWindowAttributeCornerPreference, ref preference, sizeof(int));
    }

    private const int DwmWindowAttributeCornerPreference = 33;
    private const int DwmWindowCornerPreferenceRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
