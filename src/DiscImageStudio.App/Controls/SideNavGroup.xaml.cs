using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MahApps.Metro.IconPacks;

namespace DiscImageStudio.Controls;

/// Sidebar group entry ("制作盘片"): the header only expands or collapses the children;
/// navigation stays on the SideNavItem children. Reuses SideNavItem for the header row so
/// hover visuals, the icon-only collapsed mode, and its tooltip come for free.
/// Built in code instead of XAML on purpose: a UserControl with its own XAML creates a
/// nested namescope, which would forbid x:Name on the SideNavItem children declared
/// inside the group from MainWindow.xaml.
[ContentProperty(nameof(Items))]
public class SideNavGroup : UserControl
{
    public static readonly DependencyProperty IconKindProperty = DependencyProperty.Register(
        nameof(IconKind), typeof(PackIconLucideKind?), typeof(SideNavGroup), new PropertyMetadata(null));

    public static readonly DependencyProperty LabelTextProperty = DependencyProperty.Register(
        nameof(LabelText), typeof(string), typeof(SideNavGroup), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(SideNavGroup),
        new PropertyMetadata(true, static (sender, _) => ((SideNavGroup)sender).OnIsExpandedChanged()));

    public static readonly DependencyProperty IsCollapsedProperty = DependencyProperty.Register(
        nameof(IsCollapsed), typeof(bool), typeof(SideNavGroup),
        new PropertyMetadata(false, static (sender, _) => ((SideNavGroup)sender).OnIsCollapsedChanged()));

    public static readonly RoutedEvent HeaderActivatedEvent = EventManager.RegisterRoutedEvent(
        nameof(HeaderActivated), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SideNavGroup));

    private static readonly Duration TransitionDuration = new(TimeSpan.FromMilliseconds(150));

    private readonly SideNavItem _headerItem;
    private readonly PackIconLucide _chevron;
    private readonly RotateTransform _chevronRotation;
    private readonly Grid _childrenHost;
    private readonly ItemsControl _childrenItems;
    private bool _initialized;
    private bool _iconListOpen;

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

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public bool IsCollapsed
    {
        get => (bool)GetValue(IsCollapsedProperty);
        set => SetValue(IsCollapsedProperty, value);
    }

    public ObservableCollection<UIElement> Items { get; } = [];

    /// Fired when the header is clicked in the 72px icon bar; the window toggles the
    /// child icon list on a responsive collapse, or un-collapses a manual collapse.
    public event RoutedEventHandler HeaderActivated
    {
        add => AddHandler(HeaderActivatedEvent, value);
        remove => RemoveHandler(HeaderActivatedEvent, value);
    }

    public SideNavGroup()
    {
        _headerItem = new SideNavItem();
        _headerItem.SetBinding(
            SideNavItem.IconKindProperty,
            new Binding(nameof(IconKind)) { Source = this });
        _headerItem.SetBinding(
            SideNavItem.LabelTextProperty,
            new Binding(nameof(LabelText)) { Source = this });
        _headerItem.Activated += (_, _) =>
        {
            if (IsCollapsed)
            {
                RaiseEvent(new RoutedEventArgs(HeaderActivatedEvent, this));
            }
            else
            {
                IsExpanded = !IsExpanded;
            }
        };

        _chevronRotation = new RotateTransform();
        // Compact group chevron sized inside the row hairline; rotates with expand state.
        _chevron = new PackIconLucide
        {
            Kind = PackIconLucideKind.ChevronDown,
            Width = 16,
            Height = 16,
            Foreground = (Brush)FindResource("TextMutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _chevronRotation,
        };

        _childrenItems = new ItemsControl { ItemsSource = Items };
        _childrenHost = new Grid { ClipToBounds = true };
        _childrenHost.Children.Add(_childrenItems);

        Grid headerRow = new();
        headerRow.Children.Add(_headerItem);
        headerRow.Children.Add(_chevron);

        StackPanel layout = new();
        layout.Children.Add(headerRow);
        layout.Children.Add(_childrenHost);
        Content = layout;

        Loaded += (_, _) =>
        {
            _initialized = true;
            ApplyState(animate: false);
        };
    }

    /// Narrow-mode accordion: clicking the header in the 72px icon bar stacks the child
    /// entries as an icon-only list under the header; clicking again closes it.
    public void ToggleIconList()
    {
        if (!IsCollapsed)
        {
            return;
        }

        _iconListOpen = !_iconListOpen;
        AnimateChildrenHeight(_iconListOpen);
    }

    private void OnIsExpandedChanged()
    {
        if (_initialized)
        {
            ApplyExpansionState(animate: true);
        }
    }

    private void OnIsCollapsedChanged()
    {
        if (_initialized)
        {
            ApplyState(animate: false);
        }
    }

    private void ApplyState(bool animate)
    {
        _headerItem.IsCollapsed = IsCollapsed;
        _chevron.Visibility = IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        if (IsCollapsed)
        {
            // Icon-bar mode: the children stay in a 0-height host until the header toggles
            // the icon list; Height=0 + ClipToBounds renders nothing, so no Collapsed swap.
            _childrenHost.Visibility = Visibility.Visible;
            _childrenHost.BeginAnimation(HeightProperty, null);
            _childrenHost.Height = _iconListOpen ? double.NaN : 0;
            return;
        }

        _iconListOpen = false;
        _childrenHost.Visibility = Visibility.Visible;
        ApplyExpansionState(animate);
    }

    private void ApplyExpansionState(bool animate)
    {
        if (!animate)
        {
            _chevronRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            _chevronRotation.Angle = IsExpanded ? 0 : -90;
            _childrenHost.BeginAnimation(HeightProperty, null);
            _childrenHost.Height = IsExpanded ? double.NaN : 0;
            return;
        }

        _chevronRotation.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(IsExpanded ? 0 : -90, TransitionDuration));
        AnimateChildrenHeight();
    }

    private void AnimateChildrenHeight()
    {
        AnimateChildrenHeight(IsExpanded);
    }

    private void AnimateChildrenHeight(bool open)
    {
        // DesiredSize keeps the natural measured height even while ChildrenHost is pinned,
        // so the collapse animation can always target the full content height.
        double target = open ? _childrenItems.DesiredSize.Height : 0;
        double from = _childrenHost.ActualHeight;
        if (target <= 0)
        {
            _childrenHost.BeginAnimation(HeightProperty, null);
            _childrenHost.Height = open ? double.NaN : 0;
            return;
        }

        DoubleAnimation animation = new(from, target, TransitionDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) =>
        {
            _childrenHost.BeginAnimation(HeightProperty, null);
            _childrenHost.Height = open ? double.NaN : 0;
        };
        _childrenHost.BeginAnimation(HeightProperty, animation);
    }
}
