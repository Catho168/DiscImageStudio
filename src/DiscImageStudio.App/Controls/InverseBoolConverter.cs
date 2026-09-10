using System.Globalization;
using System.Windows.Data;

namespace DiscImageStudio.Controls;

/// Binds the second radio button of a pair to the same inverted boolean.
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is false;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is false;
}
