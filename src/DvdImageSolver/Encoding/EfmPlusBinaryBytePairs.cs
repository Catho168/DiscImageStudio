namespace DvdImageSolver.Encoding;

internal readonly record struct EfmPlusBinaryBytePair(
    byte BlackByte,
    byte WhiteByte,
    byte BlackWorstLandCount,
    byte WhiteWorstLandCount,
    int ReferenceDsv,
    long BlackWorstAbsoluteOutputDsv,
    long WhiteWorstAbsoluteOutputDsv);

internal static class EfmPlusBinaryBytePairs
{
    // A one-level gray error is deliberately more expensive than up to 15 DSV units.
    // This lets DSV choose among similarly visible bytes without turning a white target dark.
    private const int GrayErrorDsvUnits = 16;
    private const int MinimumCachedDsv = -256;
    private const int MaximumCachedDsv = 256;
    private const int DsvBucketCount = MaximumCachedDsv - MinimumCachedDsv + 1;
    private static readonly ByteLevels[][] Measurements = BuildMeasurements();
    private static readonly EfmPlusBinaryBytePair[] Pairs = Build();

    internal static EfmPlusBinaryBytePair Get(byte state, bool initialIsLand, int dsv)
    {
        if (state is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        int stateLevel = ((state - 1) * 2) + (initialIsLand ? 1 : 0);
        if (dsv < MinimumCachedDsv || dsv > MaximumCachedDsv)
        {
            return FindPair(Measurements[stateLevel], dsv);
        }

        return Pairs[(stateLevel * DsvBucketCount) + dsv - MinimumCachedDsv];
    }

    internal static IReadOnlyList<(byte State, bool InitialIsLand, EfmPlusBinaryBytePair Pair)> AtZeroDsv
        => Enumerable.Range(1, 4)
            .SelectMany(state => new[] { false, true }.Select(initialIsLand =>
                (checked((byte)state), initialIsLand, Get(checked((byte)state), initialIsLand, 0))))
            .ToArray();

    private static EfmPlusBinaryBytePair[] Build()
    {
        EfmPlusBinaryBytePair[] result = new EfmPlusBinaryBytePair[8 * DsvBucketCount];
        for (int stateLevel = 0; stateLevel < Measurements.Length; stateLevel++)
        {
            for (int dsv = MinimumCachedDsv; dsv <= MaximumCachedDsv; dsv++)
            {
                result[(stateLevel * DsvBucketCount) + dsv - MinimumCachedDsv] =
                    FindPair(Measurements[stateLevel], dsv);
            }
        }

        return result;
    }

    private static ByteLevels[][] BuildMeasurements()
    {
        ByteLevels[][] result = new ByteLevels[8][];
        for (byte state = 1; state <= 4; state++)
        {
            for (int level = 0; level < 2; level++)
            {
                int stateLevel = ((state - 1) * 2) + level;
                result[stateLevel] = new ByteLevels[256];
                for (int value = 0; value < 256; value++)
                {
                    result[stateLevel][value] = Measure(
                        checked((byte)value),
                        state,
                        initialIsLand: level != 0);
                }
            }
        }

        return result;
    }

    private static EfmPlusBinaryBytePair FindPair(ByteLevels[] measurements, int dsv)
    {
        CandidateScore bestBlack = default;
        CandidateScore secondBlack = default;
        CandidateScore bestWhite = default;
        CandidateScore secondWhite = default;
        bool hasBlack = false;
        bool hasSecondBlack = false;
        bool hasWhite = false;
        bool hasSecondWhite = false;
        for (int value = 0; value < measurements.Length; value++)
        {
            byte byteValue = checked((byte)value);
            ByteLevels levels = measurements[value];
            Track(
                CandidateScore.Create(byteValue, levels, dsv, white: false),
                ref bestBlack,
                ref secondBlack,
                ref hasBlack,
                ref hasSecondBlack);
            Track(
                CandidateScore.Create(byteValue, levels, dsv, white: true),
                ref bestWhite,
                ref secondWhite,
                ref hasWhite,
                ref hasSecondWhite);
        }

        PairScore bestPair = default;
        bool hasPair = false;
        Consider(bestBlack, bestWhite, ref bestPair, ref hasPair);
        if (hasSecondWhite)
        {
            Consider(bestBlack, secondWhite, ref bestPair, ref hasPair);
        }

        if (hasSecondBlack)
        {
            Consider(secondBlack, bestWhite, ref bestPair, ref hasPair);
        }

        if (!hasPair)
        {
            throw new InvalidOperationException("Could not construct a distinct EFMPlus binary byte pair.");
        }

        return new EfmPlusBinaryBytePair(
            bestPair.Black.Value,
            bestPair.White.Value,
            checked((byte)bestPair.Black.GrayWorst),
            checked((byte)(16 - bestPair.White.GrayWorst)),
            dsv,
            bestPair.Black.DsvWorst,
            bestPair.White.DsvWorst);
    }

    private static void Track(
        CandidateScore candidate,
        ref CandidateScore best,
        ref CandidateScore second,
        ref bool hasBest,
        ref bool hasSecond)
    {
        if (!hasBest || candidate.CompareTo(best) < 0)
        {
            second = best;
            hasSecond = hasBest;
            best = candidate;
            hasBest = true;
        }
        else if (!hasSecond || candidate.CompareTo(second) < 0)
        {
            second = candidate;
            hasSecond = true;
        }
    }

    private static void Consider(
        CandidateScore black,
        CandidateScore white,
        ref PairScore best,
        ref bool hasBest)
    {
        if (black.Value == white.Value)
        {
            return;
        }

        PairScore candidate = new(black, white);
        if (!hasBest || candidate.CompareTo(best) < 0)
        {
            best = candidate;
            hasBest = true;
        }
    }

    private static ByteLevels Measure(byte value, byte state, bool initialIsLand)
    {
        var main = EfmPlusTables.LookupMain(value, state);
        int first = NrziCodewordMetrics.Get(main.Word, initialIsLand).LandCount;
        int second = first;
        int count = 1;
        if (value <= 87)
        {
            var substitution = EfmPlusTables.Substitution(value, state);
            second = NrziCodewordMetrics.Get(substitution.Word, initialIsLand).LandCount;
            count = 2;
        }
        else if (state is 1 or 4)
        {
            var stateOne = EfmPlusTables.LookupMain(value, 1);
            var stateFour = EfmPlusTables.LookupMain(value, 4);
            first = NrziCodewordMetrics.Get(stateOne.Word, initialIsLand).LandCount;
            second = NrziCodewordMetrics.Get(stateFour.Word, initialIsLand).LandCount;
            count = 2;
        }

        return new ByteLevels(first, second, count);
    }

    private readonly record struct ByteLevels(int FirstLandCount, int SecondLandCount, int Count);

    private readonly record struct CandidateScore(
        byte Value,
        int BinaryWorst,
        long Combined,
        int GrayWorst,
        long DsvWorst,
        int GrayTotal,
        long DsvTotal) : IComparable<CandidateScore>
    {
        internal static CandidateScore Create(byte value, ByteLevels levels, int dsv, bool white)
        {
            int firstGray = white ? 16 - levels.FirstLandCount : levels.FirstLandCount;
            int secondGray = white ? 16 - levels.SecondLandCount : levels.SecondLandCount;
            int firstBinary = white
                ? (levels.FirstLandCount > 8 ? 0 : 1)
                : (levels.FirstLandCount < 8 ? 0 : 1);
            int secondBinary = white
                ? (levels.SecondLandCount > 8 ? 0 : 1)
                : (levels.SecondLandCount < 8 ? 0 : 1);
            long firstDsv = Math.Abs((long)dsv + ((levels.FirstLandCount * 2) - 16));
            long secondDsv = Math.Abs((long)dsv + ((levels.SecondLandCount * 2) - 16));
            int grayWorst = Math.Max(firstGray, secondGray);
            long dsvWorst = Math.Max(firstDsv, secondDsv);
            return new CandidateScore(
                value,
                Math.Max(firstBinary, secondBinary),
                (grayWorst * GrayErrorDsvUnits) + dsvWorst,
                grayWorst,
                dsvWorst,
                firstGray + (levels.Count == 2 ? secondGray : 0),
                firstDsv + (levels.Count == 2 ? secondDsv : 0));
        }

        public int CompareTo(CandidateScore other)
        {
            int comparison = BinaryWorst.CompareTo(other.BinaryWorst);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = Combined.CompareTo(other.Combined);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = GrayWorst.CompareTo(other.GrayWorst);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = DsvWorst.CompareTo(other.DsvWorst);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = GrayTotal.CompareTo(other.GrayTotal);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = DsvTotal.CompareTo(other.DsvTotal);
            return comparison != 0 ? comparison : Value.CompareTo(other.Value);
        }
    }

    private readonly record struct PairScore(CandidateScore Black, CandidateScore White)
        : IComparable<PairScore>
    {
        public int CompareTo(PairScore other)
        {
            int comparison = (Black.BinaryWorst + White.BinaryWorst)
                .CompareTo(other.Black.BinaryWorst + other.White.BinaryWorst);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = (Black.Combined + White.Combined)
                .CompareTo(other.Black.Combined + other.White.Combined);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = (Black.GrayWorst + White.GrayWorst)
                .CompareTo(other.Black.GrayWorst + other.White.GrayWorst);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = (Black.DsvWorst + White.DsvWorst)
                .CompareTo(other.Black.DsvWorst + other.White.DsvWorst);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = Black.Value.CompareTo(other.Black.Value);
            return comparison != 0 ? comparison : White.Value.CompareTo(other.White.Value);
        }
    }
}
