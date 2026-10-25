using System.Windows;
using System.Windows.Controls;

namespace DiscImageStudio.Controls;

/// <summary>Tone module used by the design page's side rail. In the rail it is a single
/// 280 px column; when the page stacks the rail under its canvas (page narrower than the
/// design breakpoint), the module inherits the full card width and switches to a preview
/// left / controls right layout so the sliders do not stretch across the page.</summary>
public partial class TonePanel : UserControl
{
    private const double WideBreakpoint = 520;
    private const double CompactDiscSize = 128;
    private const double WideDiscSize = 200;
    private const double WidePreviewColumn = 240;

    // Preview card padding (10 per side) plus its 1px border. In the rail the medallion
    // grows to whichever is smaller: the card interior width or the height the rail's
    // budget leaves after the title and the controls.
    private const double PreviewCardChrome = 22;

    private double? _railHeightBudget;

    /// <summary>Height the rail can offer the module's interior, set by the design view from
    /// the rail viewport minus the property card and the tone card's own chrome. Null in the
    /// stacked page flow, where the module keeps its natural height.</summary>
    public double? RailHeightBudget
    {
        get => _railHeightBudget;
        set
        {
            if (_railHeightBudget == value)
            {
                return;
            }

            _railHeightBudget = value;
            UpdateResponsiveLayout();
        }
    }

    public TonePanel()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout()
    {
        if (ToneLayout is null || ActualWidth <= 0)
        {
            return;
        }

        bool wide = ActualWidth >= WideBreakpoint;
        if (wide)
        {
            PreviewColumn.Width = new GridLength(WidePreviewColumn);
            GapColumn.Width = new GridLength(20);
            ControlsColumn.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(ToneControls, 1);
            Grid.SetColumn(ToneControls, 2);
            ToneControls.Margin = new Thickness(0);

            PreviewImageHost.Width = WideDiscSize;
            PreviewImageHost.Height = WideDiscSize;
            Grid.SetRow(PreviewImageHost, 1);
            Grid.SetColumn(PreviewImageHost, 0);
            Grid.SetColumnSpan(PreviewImageHost, 1);
            return;
        }

        PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
        GapColumn.Width = new GridLength(0);
        ControlsColumn.Width = new GridLength(0);
        Grid.SetRow(ToneControls, 2);
        Grid.SetColumn(ToneControls, 0);
        ToneControls.Margin = new Thickness(0, 14, 0, 0);

        Grid.SetRow(PreviewImageHost, 1);
        Grid.SetColumn(PreviewImageHost, 0);
        Grid.SetColumnSpan(PreviewImageHost, 1);

        // 128 floor keeps very short windows readable; above that the disc tracks the card
        // width, so the preview always spans the rail, and the height budget keeps the whole
        // column flush with the viewport instead of spilling into a scrollbar. The budgeted
        // size is floored so fractional rounding can never push the column past the viewport.
        double width = Math.Floor(ToneLayout.ActualWidth - PreviewCardChrome);
        double disc = width;
        if (RailHeightBudget is double budget)
        {
            double reserved = ModuleTitle.ActualHeight + ModuleTitle.Margin.Top + ModuleTitle.Margin.Bottom
                + ToneControls.ActualHeight + ToneControls.Margin.Top
                + PreviewCardChrome;
            disc = Math.Min(width, Math.Floor(Math.Max(CompactDiscSize, budget - reserved)));
        }

        PreviewImageHost.Width = Math.Max(CompactDiscSize, disc);
        PreviewImageHost.Height = Math.Max(CompactDiscSize, disc);
    }
}
