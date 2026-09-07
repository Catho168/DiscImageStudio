using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MahApps.Metro.IconPacks;

namespace DiscImageStudio.Controls;

public partial class SideNavItem : UserControl
{
    public static readonly DependencyProperty IconKindProperty = DependencyProperty.Register(
        nameof(IconKind), typeof(PackIconLucideKind?), typeof(SideNavItem),
        new PropertyMetadata(null, static (sender, _) => ((SideNavItem)sender).UpdateVisual()));

    public static readonly DependencyProperty LabelTextProperty = DependencyProperty.Register(
        nameof(LabelText), typeof(string), typeof(SideNavItem), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(SideNavItem),
        new PropertyMetadata(false, static (sender, _) => ((SideNavItem)sender).UpdateVisual()));

    public static readonly DependencyProperty IsCollapsedProperty = DependencyProperty.Register(
        nameof(IsCollapsed), typeof(bool), typeof(SideNavItem),
        new PropertyMetadata(false, static (sender, _) => ((SideNavItem)sender).UpdateVisual()));

    public static readonly DependencyProperty IndentProperty = DependencyProperty.Register(
        nameof(Indent), typeof(double), typeof(SideNavItem),
        new PropertyMetadata(0.0, static (sender, _) => ((SideNavItem)sender).UpdateVisual(animate: false)));

    public static readonly DependencyProperty LabelFontSizeProperty = DependencyProperty.Register(
        nameof(LabelFontSize), typeof(double), typeof(SideNavItem),
        new PropertyMetadata(16.0, static (sender, args) => ((SideNavItem)sender).LabelTextBlock.FontSize = (double)args.NewValue));

    public static readonly RoutedEvent ActivatedEvent = EventManager.RegisterRoutedEvent(
        nameof(Activated), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SideNavItem));

    private static readonly Duration TransitionDuration = new(TimeSpan.FromMilliseconds(150));
    private static readonly EasingFunctionBase TransitionEase = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly Color ActiveTextColor = Color.FromRgb(0xFA, 0xFA, 0xFA);
    private static readonly Color IdleTextColor = Color.FromRgb(0xA1, 0xA1, 0xAA);
    // The idle background keeps the hover color's RGB with alpha 0 instead of
    // Colors.Transparent (RGB white, alpha 0): a ColorAnimation interpolates every ARGB
    // channel independently, so transparent->dark would pass ~50% white at ~50% alpha
    // mid-flight and render a gray pop over the dark sidebar before settling darker,
    // on enter and on leave alike. Fixed RGB makes the fade alpha-only and monotonic.
    private static readonly Color ActiveBackgroundColor = Color.FromArgb(0xFF, 0x26, 0x26, 0x2B);
    private static readonly Color HoverBackgroundColor = Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1C);
    private static readonly Color IdleBackgroundColor = Color.FromArgb(0x00, 0x1B, 0x1B, 0x1C);

    private readonly SolidColorBrush _textBrush;
    private readonly SolidColorBrush _backgroundBrush;
    private Color? _textBrushTarget;
    private Color? _backgroundBrushTarget;

    public PackIconLucideKind? IconKind
    {
        get => (PackIconLucideKind?)GetValue(IconKindProperty);
        set => SetValue(IconKindProperty, value);
    }

    public string LabelText
    {
        get => (string)GetValue(LabelTextProperty);
        set => SetValue(LabelTextProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsCollapsed
    {
        get => (bool)GetValue(IsCollapsedProperty);
        set => SetValue(IsCollapsedProperty, value);
    }

    /// Extra left inset for nested entries; the label margin becomes 12 + Indent.
    public double Indent
    {
        get => (double)GetValue(IndentProperty);
        set => SetValue(IndentProperty, value);
    }

    public double LabelFontSize
    {
        get => (double)GetValue(LabelFontSizeProperty);
        set => SetValue(LabelFontSizeProperty, value);
    }

    public event RoutedEventHandler Activated
    {
        add => AddHandler(ActivatedEvent, value);
        remove => RemoveHandler(ActivatedEvent, value);
    }

    public SideNavItem()
    {
        InitializeComponent();
        // The ToolTip lives in its own popup visual tree, so FindAncestor back to this
        // UserControl cannot resolve there; hand it this item as its DataContext instead.
        NavToolTip.DataContext = this;
        _textBrush = new SolidColorBrush(IdleTextColor);
        _backgroundBrush = new SolidColorBrush(IdleBackgroundColor);
        Root.Background = _backgroundBrush;
        Icon.Foreground = _textBrush;
        LabelTextBlock.Foreground = _textBrush;
        // Enter/leave re-run the transition; DP callbacks also re-run UpdateVisual, which
        // reads IsMouseOver live so a pointer resting on the item never falls back to idle.
        MouseEnter += (_, _) => UpdateVisual();
        MouseLeave += (_, _) => UpdateVisual();
        MouseLeftButtonUp += (_, _) => RaiseEvent(new RoutedEventArgs(ActivatedEvent, this));
        Loaded += (_, _) => UpdateVisual(animate: false);
    }

    private void UpdateVisual(bool animate = true)
    {
        bool hovering = IsMouseOver;
        AnimateBrush(
            _textBrush,
            IsActive || hovering ? ActiveTextColor : IdleTextColor,
            ref _textBrushTarget,
            animate);
        AnimateBrush(
            _backgroundBrush,
            IsActive
                ? ActiveBackgroundColor
                : hovering ? HoverBackgroundColor : IdleBackgroundColor,
            ref _backgroundBrushTarget,
            animate);

        // Icon-only entries (with a Lucide kind) flip the label off in the collapsed 72px
        // bar; icon-less entries have nothing to fall back to, so they keep showing their
        // short label text instead and the tooltip stays off.
        PackIconLucideKind? iconKind = IconKind;
        if (iconKind is not null)
        {
            Icon.Kind = iconKind.Value;
        }

        Icon.Visibility = iconKind is not null ? Visibility.Visible : Visibility.Collapsed;
        LabelTextBlock.Visibility = iconKind is null || !IsCollapsed ? Visibility.Visible : Visibility.Collapsed;
        NavToolTip.Visibility = IsCollapsed && iconKind is not null ? Visibility.Visible : Visibility.Collapsed;

        if (IsCollapsed)
        {
            ItemContent.Margin = new Thickness(0);
            ItemContent.HorizontalAlignment = HorizontalAlignment.Center;
        }
        else
        {
            ItemContent.Margin = new Thickness(12 + Indent, 0, 12, 0);
            ItemContent.HorizontalAlignment = HorizontalAlignment.Left;
        }
    }

    private void AnimateBrush(SolidColorBrush brush, Color target, ref Color? playingTarget, bool animate)
    {
        if (animate)
        {
            // Re-issuing an identical animation restarts it from the live value and reads as
            // a flicker, so only begin when the target color actually changed.
            if (playingTarget == target)
            {
                return;
            }

            playingTarget = target;
            brush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(target, TransitionDuration) { EasingFunction = TransitionEase });
        }
        else
        {
            playingTarget = null;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = target;
        }
    }
}
