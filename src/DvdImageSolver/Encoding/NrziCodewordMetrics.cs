namespace DvdImageSolver.Encoding;

internal readonly record struct NrziCodewordMetric(
    byte LandCount,
    bool FinalIsLand,
    sbyte DsvDelta,
    byte LeadingZeros,
    byte TrailingZeros);

internal static class NrziCodewordMetrics
{
    private const int WordCount = 1 << 16;
    private static readonly NrziCodewordMetric[] Metrics = Build();

    internal static NrziCodewordMetric Get(ushort word, bool initialIsLand)
        => Metrics[(initialIsLand ? WordCount : 0) + word];

    private static NrziCodewordMetric[] Build()
    {
        NrziCodewordMetric[] result = new NrziCodewordMetric[WordCount * 2];
        for (int initialLevel = 0; initialLevel < 2; initialLevel++)
        {
            for (int wordValue = 0; wordValue < WordCount; wordValue++)
            {
                ushort word = (ushort)wordValue;
                bool isLand = initialLevel != 0;
                int landCount = 0;
                for (int bit = 15; bit >= 0; bit--)
                {
                    if (((word >> bit) & 1) != 0)
                    {
                        isLand = !isLand;
                    }

                    if (isLand)
                    {
                        landCount++;
                    }
                }

                int leadingZeros = 0;
                for (int bit = 15; bit >= 0 && ((word >> bit) & 1) == 0; bit--)
                {
                    leadingZeros++;
                }

                int trailingZeros = 0;
                for (int bit = 0; bit < 16 && ((word >> bit) & 1) == 0; bit++)
                {
                    trailingZeros++;
                }

                result[(initialLevel * WordCount) + wordValue] = new NrziCodewordMetric(
                    checked((byte)landCount),
                    isLand,
                    checked((sbyte)((landCount * 2) - 16)),
                    checked((byte)leadingZeros),
                    checked((byte)trailingZeros));
            }
        }

        return result;
    }
}
