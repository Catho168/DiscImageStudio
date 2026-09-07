using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using MahApps.Metro.IconPacks;

namespace DiscImageStudio.Controls;

[ContentProperty(nameof(Body))]
public partial class SectionCard : UserControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SectionCard),
        new PropertyMetadata(string.Empty, static (sender, _) => ((SectionCard)sender).UpdateHeader()));

    public static readonly DependencyProperty IconKindProperty = DependencyProperty.Register(
        nameof(IconKind), typeof(PackIconLucideKind?), typeof(SectionCard),
        new PropertyMetadata(null, static (sender, _) => ((SectionCard)sender).UpdateHeader()));

    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body), typeof(object), typeof(SectionCard),
        new PropertyMetadata(null, static (sender, _) => ((SectionCard)sender).UpdateBody()));

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public PackIconLucideKind? IconKind
    {
        get => (PackIconLucideKind?)GetValue(IconKindProperty);
        set => SetValue(IconKindProperty, value);
    }

    public object? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public SectionCard()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UpdateHeader();
            UpdateBody();
        };
    }

    private void UpdateHeader()
    {
        HeaderText.Text = Header;
        PackIconLucideKind? iconKind = IconKind;
        if (iconKind is not null)
        {
            HeaderIcon.Kind = iconKind.Value;
        }

        HeaderIcon.Visibility = iconKind is not null ? Visibility.Visible : Visibility.Collapsed;
        HeaderText.Margin = iconKind is not null ? new Thickness(8, 0, 0, 0) : new Thickness(0);
    }

    private void UpdateBody()
    {
        if (BodyPresenter.Content is not null && ReferenceEquals(BodyPresenter.Content, Body))
        {
            return;
        }

        BodyPresenter.Content = Body;
    }
}
