using System.Globalization;

namespace DiscImageStudio.Services;

/// <summary>Keeps editable numeric values lossless and read-only summaries compact.</summary>
internal static class ParameterNumberText
{
    // R chooses the shortest decimal that parses back to the identical double.
    // G17 adds binary-representation tails even to ordinary values such as 56.8.
    internal static string ForInput(double value)
        => value.ToString("R", CultureInfo.InvariantCulture);

    // Display rounding must never be written back to the parameter store or
    // persisted calibration model. Very small nonzero coefficients remain visible.
    internal static string ForSummary(double value, int decimalPlaces = 6)
    {
        if (decimalPlaces is < 0 or > 15)
            throw new ArgumentOutOfRangeException(nameof(decimalPlaces));
        string format = decimalPlaces == 0 ? "0" : "0." + new string('#', decimalPlaces);
        string text = value.ToString(format, CultureInfo.InvariantCulture);
        if (text is "0" or "-0")
            return value == 0 ? "0" : value.ToString("G6", CultureInfo.InvariantCulture);
        return text;
    }
}
