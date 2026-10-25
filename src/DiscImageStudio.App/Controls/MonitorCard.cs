using System.Windows;
using System.Windows.Controls;
using MahApps.Metro.IconPacks;

namespace DiscImageStudio.Controls;

/// <summary>Monitor surface: header (icon + title + subtitle + action slot), body content,
/// optional status footer. One implementation keeps the monitor chrome identical across the
/// workspaces that display a visual result. A templated ContentControl rather than a
/// UserControl so pages can put named elements inside the body — a UserControl would
/// quarantine them in its own name scope and keep canvas wiring and snapshot hooks from
/// reaching them.</summary>
public class MonitorCard : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(MonitorCard),
        new PropertyMetadata(string.Empty, static (sender, _) => ((MonitorCard)sender).UpdateHeader()));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(MonitorCard),
        new PropertyMetadata(string.Empty, static (sender, _) => ((MonitorCard)sender).UpdateHeader()));

    public static readonly DependencyProperty IconKindProperty = DependencyProperty.Register(
        nameof(IconKind), typeof(PackIconLucideKind?), typeof(MonitorCard),
        new PropertyMetadata(null, static (sender, _) => ((MonitorCard)sender).UpdateHeader()));

    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(MonitorCard),
        new PropertyMetadata(null, static (sender, _) => ((MonitorCard)sender).UpdateActions()));

    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(
        nameof(StatusText), typeof(string), typeof(MonitorCard),
        new PropertyMetadata(string.Empty, static (sender, _) => ((MonitorCard)sender).UpdateStatus()));

    public static readonly DependencyProperty StatusContentProperty = DependencyProperty.Register(
        nameof(StatusContent), typeof(object), typeof(MonitorCard),
        new PropertyMetadata(null, static (sender, _) => ((MonitorCard)sender).UpdateStatus()));

    private TextBlock? _titleText;
    private TextBlock? _subtitleText;
    private PackIconLucide? _headerIcon;
    private ContentPresenter? _actionHost;
    private Border? _statusStrip;
    private TextBlock? _statusTextBlock;
    private ContentPresenter? _statusContent;

    static MonitorCard()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(MonitorCard),
            new FrameworkPropertyMetadata(typeof(MonitorCard)));
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Right-aligned detail line in the header — typically the artifact's path.</summary>
    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public PackIconLucideKind? IconKind
    {
        get => (PackIconLucideKind?)GetValue(IconKindProperty);
        set => SetValue(IconKindProperty, value);
    }

    /// <summary>Header-level actions (download, zoom, view toggles) for this monitor.</summary>
    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    /// <summary>Status line in the footer; the footer hides while this is empty.</summary>
    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    /// <summary>Rich footer content (e.g. a full-width action) that replaces the plain status
    /// line when set; the footer then shows while either is present.</summary>
    public object? StatusContent
    {
        get => GetValue(StatusContentProperty);
        set => SetValue(StatusContentProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _titleText = GetTemplateChild(PartTitle) as TextBlock;
        _subtitleText = GetTemplateChild(PartSubtitle) as TextBlock;
        _headerIcon = GetTemplateChild(PartHeaderIcon) as PackIconLucide;
        _actionHost = GetTemplateChild(PartActions) as ContentPresenter;
        _statusStrip = GetTemplateChild(PartStatusStrip) as Border;
        _statusTextBlock = GetTemplateChild(PartStatusText) as TextBlock;
        _statusContent = GetTemplateChild(PartStatusContent) as ContentPresenter;
        UpdateHeader();
        UpdateActions();
        UpdateStatus();
    }

    private const string PartTitle = "PART_Title";
    private const string PartSubtitle = "PART_Subtitle";
    private const string PartHeaderIcon = "PART_HeaderIcon";
    private const string PartActions = "PART_Actions";
    private const string PartStatusStrip = "PART_StatusStrip";
    private const string PartStatusText = "PART_StatusText";
    private const string PartStatusContent = "PART_StatusContent";

    private void UpdateHeader()
    {
        if (_titleText is null || _subtitleText is null || _headerIcon is null)
        {
            return;
        }

        _titleText.Text = Title;
        PackIconLucideKind? iconKind = IconKind;
        if (iconKind is not null)
        {
            _headerIcon.Kind = iconKind.Value;
        }

        _headerIcon.Visibility = iconKind is not null ? Visibility.Visible : Visibility.Collapsed;
        _titleText.Margin = iconKind is not null ? new Thickness(8, 0, 0, 0) : new Thickness(0);
        _subtitleText.Text = Subtitle;
        _subtitleText.Visibility =
            string.IsNullOrWhiteSpace(Subtitle) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateActions()
    {
        if (_actionHost is not null)
        {
            _actionHost.Content = Actions;
        }
    }

    private void UpdateStatus()
    {
        if (_statusStrip is null || _statusTextBlock is null)
        {
            return;
        }

        bool hasContent = StatusContent is not null;
        if (_statusContent is not null)
        {
            _statusContent.Content = StatusContent;
            _statusContent.Visibility = hasContent ? Visibility.Visible : Visibility.Collapsed;
        }

        _statusTextBlock.Visibility = hasContent ? Visibility.Collapsed : Visibility.Visible;
        _statusTextBlock.Text = StatusText;
        _statusStrip.Visibility = hasContent || !string.IsNullOrWhiteSpace(StatusText)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
