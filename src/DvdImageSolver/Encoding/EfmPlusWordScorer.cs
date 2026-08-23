namespace DvdImageSolver.Encoding;

internal readonly record struct EfmPlusWordScore(
    int BinaryErrors,
    int GrayError,
    ModulationBoundary Boundary);

internal sealed class EfmPlusWordScorer
{
    private sealed class StreamCandidate
    {
        internal readonly ushort[] Words = new ushort[EfmPlusEncoder.BytesPerSyncFrame];
        internal int Length;
        internal byte State;
        internal int Dsv;
        internal bool IsLand;
        internal bool SyncPrimary;
        internal int BinaryErrors;
        internal int GrayError;
        internal int TrailingZeros;

        internal void Initialize(
            ModulationBoundary boundary,
            uint sync,
            bool primary,
            sbyte firstSyncDesiredLandCount,
            sbyte secondSyncDesiredLandCount)
        {
            Length = 0;
            State = boundary.State;
            Dsv = boundary.Dsv;
            IsLand = boundary.IsLand;
            SyncPrimary = primary;
            BinaryErrors = 0;
            GrayError = 0;
            TrailingZeros = 0;
            AppendRaw((ushort)(sync >> 16), firstSyncDesiredLandCount);
            AppendRaw((ushort)sync, secondSyncDesiredLandCount);
            State = 1;
        }

        internal void CopyFrom(StreamCandidate source)
        {
            source.Words.AsSpan(0, source.Length).CopyTo(Words);
            Length = source.Length;
            State = source.State;
            Dsv = source.Dsv;
            IsLand = source.IsLand;
            SyncPrimary = source.SyncPrimary;
            BinaryErrors = source.BinaryErrors;
            GrayError = source.GrayError;
            TrailingZeros = source.TrailingZeros;
        }

        internal void Append(ushort word, byte nextState, sbyte desiredLandCount)
        {
            NrziCodewordMetric metric = NrziCodewordMetrics.Get(word, IsLand);
            int combined = TrailingZeros + metric.LeadingZeros;
            if (combined is < 2 or > 10)
            {
                throw new InvalidOperationException("EFMPlus code word would violate the RLL(2,10) boundary.");
            }

            Words[Length++] = word;
            Apply(metric);
            if (desiredLandCount >= 0)
            {
                BinaryErrors += BinaryError(metric.LandCount, desiredLandCount);
                GrayError += Math.Abs(metric.LandCount - desiredLandCount);
            }

            State = nextState;
        }

        internal bool CanAppend(ushort word)
        {
            NrziCodewordMetric metric = NrziCodewordMetrics.Get(word, IsLand);
            int combined = TrailingZeros + metric.LeadingZeros;
            return combined is >= 2 and <= 10;
        }

        private void AppendRaw(ushort word, sbyte desiredLandCount)
        {
            NrziCodewordMetric metric = NrziCodewordMetrics.Get(word, IsLand);
            Apply(metric);
            if (desiredLandCount >= 0)
            {
                BinaryErrors += BinaryError(metric.LandCount, desiredLandCount);
                GrayError += Math.Abs(metric.LandCount - desiredLandCount);
            }
        }

        private void Apply(NrziCodewordMetric metric)
        {
            Dsv += metric.DsvDelta;
            IsLand = metric.FinalIsLand;
            TrailingZeros = metric.TrailingZeros == 16
                ? TrailingZeros + 16
                : metric.TrailingZeros;
        }
    }

    private readonly StreamCandidate _stream1 = new();
    private readonly StreamCandidate _stream2 = new();

    internal EfmPlusWordScore ScoreSyncFrame(
        ReadOnlySpan<byte> bytes,
        int syncCategory,
        ModulationBoundary input,
        ReadOnlySpan<sbyte> desiredLandCounts,
        sbyte firstSyncDesiredLandCount = -1,
        sbyte secondSyncDesiredLandCount = -1)
    {
        if (bytes.Length != EfmPlusEncoder.BytesPerSyncFrame
            || desiredLandCounts.Length != EfmPlusEncoder.BytesPerSyncFrame)
        {
            throw new ArgumentException("A word-scored Sync Frame needs 91 bytes and 91 gray targets.");
        }

        StreamCandidate stream1 = _stream1;
        StreamCandidate stream2 = _stream2;
        stream1.Initialize(
            input,
            EfmPlusTables.Sync(input.State, syncCategory, primary: true),
            primary: true,
            firstSyncDesiredLandCount,
            secondSyncDesiredLandCount);
        stream2.Initialize(
            input,
            EfmPlusTables.Sync(input.State, syncCategory, primary: false),
            primary: false,
            firstSyncDesiredLandCount,
            secondSyncDesiredLandCount);

        for (int index = 0; index < bytes.Length; index++)
        {
            byte value = bytes[index];
            sbyte target = desiredLandCounts[index];
            if (value <= 87)
            {
                StreamCandidate selected = Select(stream1, stream2);
                CopySelectedToOther(selected, stream1, stream2);
                var main = EfmPlusTables.LookupMain(value, selected.State);
                var substitution = EfmPlusTables.Substitution(value, selected.State);
                stream1.Append(main.Word, main.NextState, target);
                stream2.Append(substitution.Word, substitution.NextState, target);
                continue;
            }

            bool firstCanBranch = HasTwoStateOneFourRepresentations(value, stream1);
            bool secondCanBranch = HasTwoStateOneFourRepresentations(value, stream2);
            if (firstCanBranch && secondCanBranch)
            {
                StreamCandidate selected = Select(stream1, stream2);
                CopySelectedToOther(selected, stream1, stream2);
                AppendStateOneAndFourAlternatives(value, target, stream1, stream2);
            }
            else if (firstCanBranch || secondCanBranch)
            {
                StreamCandidate eligible = firstCanBranch ? stream1 : stream2;
                StreamCandidate other = firstCanBranch ? stream2 : stream1;
                bool chooseEligible = Math.Abs(eligible.Dsv) < Math.Abs(other.Dsv)
                    || (Math.Abs(eligible.Dsv) == Math.Abs(other.Dsv) && ReferenceEquals(eligible, stream1));
                if (chooseEligible)
                {
                    CopySelectedToOther(eligible, stream1, stream2);
                    AppendStateOneAndFourAlternatives(value, target, stream1, stream2);
                }
                else
                {
                    AppendMain(value, target, stream1);
                    AppendMain(value, target, stream2);
                }
            }
            else
            {
                AppendMain(value, target, stream1);
                AppendMain(value, target, stream2);
            }
        }

        StreamCandidate winner = Select(stream1, stream2);
        ModulationBoundary boundary = new(winner.State, winner.Dsv, winner.IsLand);
        int binaryErrors = winner.BinaryErrors;
        int grayError = winner.GrayError;
        if (winner.Dsv is > 63 or < -64)
        {
            bool alternatePrimary = !winner.SyncPrimary;
            EfmPlusWordScore alternate = Replay(
                winner,
                input,
                EfmPlusTables.Sync(input.State, syncCategory, alternatePrimary),
                desiredLandCounts,
                firstSyncDesiredLandCount,
                secondSyncDesiredLandCount);
            if (Math.Abs(alternate.Boundary.Dsv) < Math.Abs(winner.Dsv))
            {
                boundary = alternate.Boundary;
                binaryErrors = alternate.BinaryErrors;
                grayError = alternate.GrayError;
            }
        }

        return new EfmPlusWordScore(binaryErrors, grayError, boundary);
    }

    private static void CopySelectedToOther(
        StreamCandidate selected,
        StreamCandidate stream1,
        StreamCandidate stream2)
    {
        if (ReferenceEquals(selected, stream1))
        {
            stream2.CopyFrom(stream1);
        }
        else
        {
            stream1.CopyFrom(stream2);
        }
    }

    private static EfmPlusWordScore Replay(
        StreamCandidate candidate,
        ModulationBoundary input,
        uint sync,
        ReadOnlySpan<sbyte> desiredLandCounts,
        sbyte firstSyncDesiredLandCount,
        sbyte secondSyncDesiredLandCount)
    {
        bool isLand = input.IsLand;
        int dsv = input.Dsv;
        int binaryErrors = 0;
        int grayError = 0;
        ApplyWord(
            (ushort)(sync >> 16),
            ref isLand,
            ref dsv,
            firstSyncDesiredLandCount,
            ref binaryErrors,
            ref grayError);
        ApplyWord(
            (ushort)sync,
            ref isLand,
            ref dsv,
            secondSyncDesiredLandCount,
            ref binaryErrors,
            ref grayError);
        for (int index = 0; index < candidate.Length; index++)
        {
            ApplyWord(
                candidate.Words[index],
                ref isLand,
                ref dsv,
                desiredLandCounts[index],
                ref binaryErrors,
                ref grayError);
        }

        return new EfmPlusWordScore(
            binaryErrors,
            grayError,
            new ModulationBoundary(candidate.State, dsv, isLand));
    }

    private static void ApplyWord(
        ushort word,
        ref bool isLand,
        ref int dsv,
        sbyte desiredLandCount,
        ref int binaryErrors,
        ref int grayError)
    {
        NrziCodewordMetric metric = NrziCodewordMetrics.Get(word, isLand);
        dsv += metric.DsvDelta;
        isLand = metric.FinalIsLand;
        if (desiredLandCount >= 0)
        {
            binaryErrors += BinaryError(metric.LandCount, desiredLandCount);
            grayError += Math.Abs(metric.LandCount - desiredLandCount);
        }
    }

    private static int BinaryError(byte landCount, sbyte desiredLandCount)
        => desiredLandCount switch
        {
            < 0 => 0,
            < 8 => landCount < 8 ? 0 : 1,
            > 8 => landCount > 8 ? 0 : 1,
            _ => 0,
        };

    private static void AppendStateOneAndFourAlternatives(
        byte value,
        sbyte target,
        StreamCandidate stream1,
        StreamCandidate stream2)
    {
        var stateOne = EfmPlusTables.LookupMain(value, 1);
        var stateFour = EfmPlusTables.LookupMain(value, 4);
        if (!stream1.CanAppend(stateOne.Word) || !stream2.CanAppend(stateFour.Word))
        {
            throw new InvalidOperationException("Invalid State 1/4 EFMPlus branch.");
        }

        stream1.Append(stateOne.Word, stateOne.NextState, target);
        stream2.Append(stateFour.Word, stateFour.NextState, target);
    }

    private static bool HasTwoStateOneFourRepresentations(byte value, StreamCandidate stream)
    {
        if (stream.State is not (1 or 4))
        {
            return false;
        }

        var stateOne = EfmPlusTables.LookupMain(value, 1);
        var stateFour = EfmPlusTables.LookupMain(value, 4);
        return stream.CanAppend(stateOne.Word) && stream.CanAppend(stateFour.Word);
    }

    private static void AppendMain(byte value, sbyte target, StreamCandidate stream)
    {
        var main = EfmPlusTables.LookupMain(value, stream.State);
        stream.Append(main.Word, main.NextState, target);
    }

    private static StreamCandidate Select(StreamCandidate stream1, StreamCandidate stream2)
        => Math.Abs(stream1.Dsv) <= Math.Abs(stream2.Dsv) ? stream1 : stream2;
}
