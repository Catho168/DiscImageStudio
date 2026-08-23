namespace DvdImageSolver.Encoding;

internal readonly record struct EfmStreamPrefix(
    byte State,
    int Dsv,
    bool IsLand,
    int BinaryErrors,
    int GrayError,
    int TrailingZeros)
{
    internal static EfmStreamPrefix Initialize(ModulationBoundary boundary, uint sync)
    {
        EfmStreamPrefix result = new(
            boundary.State,
            boundary.Dsv,
            boundary.IsLand,
            BinaryErrors: 0,
            GrayError: 0,
            TrailingZeros: 0);
        result = result.AppendRaw((ushort)(sync >> 16));
        result = result.AppendRaw((ushort)sync);
        return result with { State = 1 };
    }

    internal bool CanAppend(ushort word)
    {
        NrziCodewordMetric metric = NrziCodewordMetrics.Get(word, IsLand);
        int combined = TrailingZeros + metric.LeadingZeros;
        return combined is >= 2 and <= 10;
    }

    internal EfmStreamPrefix Append(ushort word, byte nextState, sbyte desiredLandCount)
    {
        NrziCodewordMetric metric = NrziCodewordMetrics.Get(word, IsLand);
        int combined = TrailingZeros + metric.LeadingZeros;
        if (combined is < 2 or > 10)
        {
            throw new InvalidOperationException("EFMPlus code word would violate the RLL(2,10) boundary.");
        }

        return Apply(metric) with
        {
            State = nextState,
            BinaryErrors = BinaryErrors + BinaryError(metric.LandCount, desiredLandCount),
            GrayError = GrayError + (desiredLandCount < 0
                ? 0
                : Math.Abs(metric.LandCount - desiredLandCount)),
        };
    }

    private EfmStreamPrefix AppendRaw(ushort word)
        => Apply(NrziCodewordMetrics.Get(word, IsLand));

    private EfmStreamPrefix Apply(NrziCodewordMetric metric)
        => this with
        {
            Dsv = Dsv + metric.DsvDelta,
            IsLand = metric.FinalIsLand,
            TrailingZeros = metric.TrailingZeros == 16
                ? TrailingZeros + 16
                : metric.TrailingZeros,
        };

    private static int BinaryError(byte landCount, sbyte desiredLandCount)
        => desiredLandCount switch
        {
            < 0 => 0,
            < 8 => landCount < 8 ? 0 : 1,
            > 8 => landCount > 8 ? 0 : 1,
            _ => 0,
        };
}

internal readonly record struct EfmPlusPrefixState(
    EfmStreamPrefix Stream1,
    EfmStreamPrefix Stream2)
{
    internal static EfmPlusPrefixState Initialize(
        ModulationBoundary input,
        int syncCategory)
        => new(
            EfmStreamPrefix.Initialize(
                input,
                EfmPlusTables.Sync(input.State, syncCategory, primary: true)),
            EfmStreamPrefix.Initialize(
                input,
                EfmPlusTables.Sync(input.State, syncCategory, primary: false)));

    internal EfmStreamPrefix Selected
        => Math.Abs(Stream1.Dsv) <= Math.Abs(Stream2.Dsv) ? Stream1 : Stream2;

    internal EfmPlusPrefixState Advance(byte value, sbyte desiredLandCount)
    {
        EfmStreamPrefix stream1 = Stream1;
        EfmStreamPrefix stream2 = Stream2;
        if (value <= 87)
        {
            EfmStreamPrefix selected = Select(stream1, stream2);
            var main = EfmPlusTables.LookupMain(value, selected.State);
            var substitution = EfmPlusTables.Substitution(value, selected.State);
            return new EfmPlusPrefixState(
                selected.Append(main.Word, main.NextState, desiredLandCount),
                selected.Append(substitution.Word, substitution.NextState, desiredLandCount));
        }

        bool firstCanBranch = HasTwoStateOneFourRepresentations(value, stream1);
        bool secondCanBranch = HasTwoStateOneFourRepresentations(value, stream2);
        if (firstCanBranch && secondCanBranch)
        {
            EfmStreamPrefix selected = Select(stream1, stream2);
            return AppendStateOneAndFourAlternatives(value, desiredLandCount, selected, selected);
        }

        if (firstCanBranch || secondCanBranch)
        {
            EfmStreamPrefix eligible = firstCanBranch ? stream1 : stream2;
            EfmStreamPrefix other = firstCanBranch ? stream2 : stream1;
            bool chooseEligible = Math.Abs(eligible.Dsv) < Math.Abs(other.Dsv)
                || (Math.Abs(eligible.Dsv) == Math.Abs(other.Dsv) && firstCanBranch);
            if (chooseEligible)
            {
                return AppendStateOneAndFourAlternatives(value, desiredLandCount, eligible, eligible);
            }
        }

        return new EfmPlusPrefixState(
            AppendMain(value, desiredLandCount, stream1),
            AppendMain(value, desiredLandCount, stream2));
    }

    private static EfmPlusPrefixState AppendStateOneAndFourAlternatives(
        byte value,
        sbyte target,
        EfmStreamPrefix stream1,
        EfmStreamPrefix stream2)
    {
        var stateOne = EfmPlusTables.LookupMain(value, 1);
        var stateFour = EfmPlusTables.LookupMain(value, 4);
        if (!stream1.CanAppend(stateOne.Word) || !stream2.CanAppend(stateFour.Word))
        {
            throw new InvalidOperationException("Invalid State 1/4 EFMPlus branch.");
        }

        return new EfmPlusPrefixState(
            stream1.Append(stateOne.Word, stateOne.NextState, target),
            stream2.Append(stateFour.Word, stateFour.NextState, target));
    }

    private static bool HasTwoStateOneFourRepresentations(byte value, EfmStreamPrefix stream)
    {
        if (stream.State is not (1 or 4))
        {
            return false;
        }

        var stateOne = EfmPlusTables.LookupMain(value, 1);
        var stateFour = EfmPlusTables.LookupMain(value, 4);
        return stream.CanAppend(stateOne.Word) && stream.CanAppend(stateFour.Word);
    }

    private static EfmStreamPrefix AppendMain(
        byte value,
        sbyte target,
        EfmStreamPrefix stream)
    {
        var main = EfmPlusTables.LookupMain(value, stream.State);
        return stream.Append(main.Word, main.NextState, target);
    }

    private static EfmStreamPrefix Select(EfmStreamPrefix stream1, EfmStreamPrefix stream2)
        => Math.Abs(stream1.Dsv) <= Math.Abs(stream2.Dsv) ? stream1 : stream2;
}
