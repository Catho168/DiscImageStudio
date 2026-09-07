using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DiscImageStudio.Controls;

/// Two-option selector (CD/DVD) rendered as a 12px-radius rounded-rectangle track with a
/// thumb that slides to the checked option. Selection state surfaces as a two-way bool so
/// pages bind the existing VM flags directly (no inverse binding needed for the first option).
public partial class SegmentedControl : UserControl
{
    public static readonly DependencyProperty LabelAProperty = DependencyProperty.Register(
        nameof(LabelA), typeof(string), typeof(SegmentedControl), new PropertyMetadata("CD"));

    public static readonly DependencyProperty LabelBProperty = DependencyProperty.Register(
        nameof(LabelB), typeof(string), typeof(SegmentedControl), new PropertyMetadata("DVD"));

    public static readonly DependencyProperty IsSecondSelectedProperty = DependencyProperty.Register(
        nameof(IsSecondSelected), typeof(bool), typeof(SegmentedControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            static (sender, _) => ((SegmentedControl)sender).SyncVisuals()));

    // Motion and color idiom mirrored from SideNavItem: 150 ms cubic ease-out for label
    // emphasis, a slightly slower slide so the thumb visibly travels the shared track.
    private static readonly Duration SlideDuration = new(TimeSpan.FromMilliseconds(180));
    private static readonly Duration ColorDuration = new(TimeSpan.FromMilliseconds(150));
    private static readonly EasingFunctionBase TransitionEase = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly Color ActiveTextColor = Color.FromRgb(0xFA, 0xFA, 0xFA);
    private static readonly Color IdleTextColor = Color.FromRgb(0xA1, 0xA1, 0xAA);

    private readonly SolidColorBrush _labelABrush = new(IdleTextColor);
    private readonly SolidColorBrush _labelBBrush = new(IdleTextColor);
    private bool _hoverA;
    private bool _hoverB;

    public SegmentedControl()
    {
        InitializeComponent();
        OptionAText.Foreground = _labelABrush;
        OptionBText.Foreground = _labelBBrush;
        Loaded += (_, _) => ApplyLayout(animate: false);
        Cells.SizeChanged += (_, _) => ApplyLayout(animate: false);
    }

    public string LabelA
    {
        get => (string)GetValue(LabelAProperty);
        set => SetValue(LabelAProperty, value);
    }

    public string LabelB
    {
        get => (string)GetValue(LabelBProperty);
        set => SetValue(LabelBProperty, value);
    }

    /// True when the right-hand option is the active one (the left option is its inverse),
    /// so a "DVD selected" VM flag binds one-to-one.
    public bool IsSecondSelected
    {
        get => (bool)GetValue(IsSecondSelectedProperty);
        set => SetValue(IsSecondSelectedProperty, value);
    }

    private bool Realized => IsLoaded && Cells.ActualWidth > 0;

    private void SyncVisuals()
    {
        // No GroupName: several instances can stay alive across pages, and a shared group
        // name would let an invisible page uncheck the visible one. State is owned here.
        OptionARadio.IsChecked = !IsSecondSelected;
        OptionBRadio.IsChecked = IsSecondSelected;
        AnimateThumb(IsSecondSelected);
        UpdateLabelColors();
    }

    private void ApplyLayout(bool animate)
    {
        if (!Realized)
        {
            return;
        }

        // Retarget instantly on first layout and on live resizes; only user-driven
        // selection changes travel along the animated slide.
        AnimateThumb(IsSecondSelected, animate);
        UpdateLabelColors(animate);
    }

    private void AnimateThumb(bool second, bool animate = true)
    {
        double target = second ? Cells.ActualWidth / 2.0 : 0.0;
        if (!animate || !Realized)
        {
            ThumbShift.BeginAnimation(TranslateTransform.XProperty, null);
            ThumbShift.X = target;
            return;
        }

        ThumbShift.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(target, SlideDuration) { EasingFunction = TransitionEase });
    }

    private void UpdateLabelColors(bool animate = true)
    {
        AnimateColor(_labelABrush, !IsSecondSelected || _hoverA ? ActiveTextColor : IdleTextColor, animate);
        AnimateColor(_labelBBrush, IsSecondSelected || _hoverB ? ActiveTextColor : IdleTextColor, animate);
    }

    private void AnimateColor(SolidColorBrush brush, Color target, bool animate)
    {
        if (animate && Realized)
        {
            if (brush.Color == target)
            {
                return;
            }

            brush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(target, ColorDuration) { EasingFunction = TransitionEase });
        }
        else
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = target;
        }
    }

    private void Select(bool second)
    {
        if (IsSecondSelected == second)
        {
            return;
        }

        IsSecondSelected = second; // DP callback applies visuals
    }

    private void OptionAClick(object sender, RoutedEventArgs e)
    {
        Select(false);
    }

    private void OptionBClick(object sender, RoutedEventArgs e)
    {
        Select(true);
    }

    private void OptionKeyDown(object sender, KeyEventArgs e)
    {
        // Without a radio group, arrow keys do not move the check natively; keep the
        // native two-arrow gesture: Right from option A and Left from option B swap.
        bool moveToSecond = (sender == OptionARadio && e.Key == Key.Right)
                            || (sender == OptionBRadio && e.Key == Key.Left);
        if (!moveToSecond)
        {
            return;
        }

        e.Handled = true;
        Select(sender == OptionARadio);
        RadioButton target = sender == OptionARadio ? OptionBRadio : OptionARadio;
        target.Focus();
    }

    private void OptionMouseEnter(object sender, MouseEventArgs e)
    {
        _hoverA = sender == OptionARadio;
        _hoverB = sender == OptionBRadio;
        UpdateLabelColors();
    }

    private void OptionMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender == OptionARadio)
        {
            _hoverA = false;
        }
        else
        {
            _hoverB = false;
        }

        UpdateLabelColors();
    }
}
