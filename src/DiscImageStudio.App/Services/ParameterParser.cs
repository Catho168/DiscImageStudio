using System.Globalization;
using DiscImageStudio.Imaging;

namespace DiscImageStudio.Services;

/// Input parsing with dual-culture tolerance; messages are identical to the original UI.
internal static class ParameterParser
{
    internal static double ParseDouble(string raw, string fieldName)
    {
        string value = raw.Trim();
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
            || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result))
        {
            return result;
        }

        throw new FormatException($"{fieldName}不是有效数字。");
    }

    internal static double ParsePositiveDouble(string raw, string fieldName)
    {
        double result = ParseDouble(raw, fieldName);
        return double.IsFinite(result) && result > 0
            ? result
            : throw new ArgumentOutOfRangeException(fieldName, $"{fieldName}必须大于 0。");
    }

    internal static double ParseNonNegativeDouble(string raw, string fieldName)
    {
        double result = ParseDouble(raw, fieldName);
        return double.IsFinite(result) && result >= 0
            ? result
            : throw new ArgumentOutOfRangeException(fieldName, $"{fieldName}不能小于 0。");
    }

    internal static int ParsePositiveInt(string raw, string fieldName)
    {
        string value = raw.Trim();
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) && result > 0
            ? result
            : throw new FormatException($"{fieldName}必须是正整数。");
    }

    internal static long ParsePositiveLong(string raw, string fieldName)
    {
        string value = raw.Trim();
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) && result > 0
            ? result
            : throw new FormatException($"{fieldName}必须是正整数。");
    }

    internal static RingImageLayoutOptions CreateRingLayoutOptions(
        double canvasOuterRadius,
        double generatedInnerRadius,
        double generatedOuterRadius,
        string rawInnerMargin,
        string rawOuterMargin,
        int outputSize)
    {
        double innerMargin = ParseNonNegativeDouble(rawInnerMargin, "内圈安全边界");
        double outerMargin = ParseNonNegativeDouble(rawOuterMargin, "外圈安全边界");
        double contentInner = generatedInnerRadius + innerMargin;
        double contentOuter = generatedOuterRadius - outerMargin;
        if (contentOuter <= contentInner)
        {
            throw new ArgumentOutOfRangeException(
                "安全边界",
                "内外安全边界之和必须小于可用环带宽度。");
        }

        RingImageLayoutOptions options = new(
            canvasOuterRadius,
            contentInner,
            contentOuter,
            Math.Clamp(outputSize, 512, RingImageQuality.GenerationSize));
        options.Validate();
        return options;
    }
}
