namespace DvdImageSolver.Encoding;

internal sealed class EfmPlusEncoder
{
    internal const int BytesPerSyncFrame = 91;
    internal const int BitsPerSyncFrame = 1488;
    private readonly StreamCandidate _stream1 = new();
    private readonly StreamCandidate _stream2 = new();
    private readonly byte[] _alternateNrz = new byte[BitsPerSyncFrame];

    private sealed class StreamCandidate
    {
        internal readonly byte[] Nrz = new byte[BitsPerSyncFrame];
        internal int Length;
        internal byte State;
        internal int Dsv;
        internal bool IsLand;
        internal bool SyncPrimary;

        internal void Initialize(ModulationBoundary boundary, uint sync, bool primary)
        {
            Length = 0;
            State = boundary.State;
            Dsv = boundary.Dsv;
            IsLand = boundary.IsLand;
            SyncPrimary = primary;
            AppendBits(sync, 32);
            State = 1; // Every SYNC code ends in a ONE, therefore the next state is State 1.
        }

        internal void CopyFrom(StreamCandidate source)
        {
            source.Nrz.AsSpan(0, source.Length).CopyTo(Nrz);
            Length = source.Length;
            State = source.State;
            Dsv = source.Dsv;
            IsLand = source.IsLand;
            SyncPrimary = source.SyncPrimary;
        }

        internal void Append(ushort word, byte nextState)
        {
            if (!CanAppend(word))
            {
                throw new InvalidOperationException("EFMPlus code word would violate the RLL(2,10) boundary.");
            }

            AppendBits(word, 16);
            State = nextState;
        }

        internal bool CanAppend(ushort word)
        {
            int trailingZeros = 0;
            for (int index = Length - 1; index >= 0 && Nrz[index] == 0; index--)
            {
                trailingZeros++;
            }

            int leadingZeros = 0;
            for (int bit = 15; bit >= 0 && ((word >> bit) & 1) == 0; bit--)
            {
                leadingZeros++;
            }

            int combined = trailingZeros + leadingZeros;
            return combined is >= 2 and <= 10;
        }

        internal void AppendBits(uint bits, int count)
        {
            for (int bit = count - 1; bit >= 0; bit--)
            {
                byte transition = (byte)((bits >> bit) & 1);
                Nrz[Length++] = transition;
                if (transition != 0)
                {
                    IsLand = !IsLand;
                }

                Dsv += IsLand ? 1 : -1;
            }
        }
    }

    internal ModulationBoundary EncodeSyncFrame(
        ReadOnlySpan<byte> bytes,
        int syncCategory,
        ModulationBoundary input,
        Span<byte> physicalLevels)
    {
        if (bytes.Length != BytesPerSyncFrame)
        {
            throw new ArgumentException($"A Sync Frame contains {BytesPerSyncFrame} bytes.", nameof(bytes));
        }

        if (physicalLevels.Length < BitsPerSyncFrame)
        {
            throw new ArgumentException($"Output needs {BitsPerSyncFrame} level entries.", nameof(physicalLevels));
        }

        StreamCandidate stream1 = _stream1;
        StreamCandidate stream2 = _stream2;
        stream1.Initialize(input, EfmPlusTables.Sync(input.State, syncCategory, primary: true), primary: true);
        stream2.Initialize(input, EfmPlusTables.Sync(input.State, syncCategory, primary: false), primary: false);

        foreach (byte value in bytes)
        {
            if (value <= 87)
            {
                StreamCandidate selected = Select(stream1, stream2);
                stream1.CopyFrom(selected);
                stream2.CopyFrom(selected);
                var main = EfmPlusTables.LookupMain(value, selected.State);
                var substitution = EfmPlusTables.Substitution(value, selected.State);
                stream1.Append(main.Word, main.NextState);
                stream2.Append(substitution.Word, substitution.NextState);
                continue;
            }

            bool firstCanBranch = HasTwoStateOneFourRepresentations(value, stream1);
            bool secondCanBranch = HasTwoStateOneFourRepresentations(value, stream2);
            if (firstCanBranch && secondCanBranch)
            {
                StreamCandidate selected = Select(stream1, stream2);
                stream1.CopyFrom(selected);
                stream2.CopyFrom(selected);
                AppendStateOneAndFourAlternatives(value, stream1, stream2);
            }
            else if (firstCanBranch || secondCanBranch)
            {
                StreamCandidate eligible = firstCanBranch ? stream1 : stream2;
                StreamCandidate other = firstCanBranch ? stream2 : stream1;
                bool chooseEligible = Math.Abs(eligible.Dsv) < Math.Abs(other.Dsv)
                    || (Math.Abs(eligible.Dsv) == Math.Abs(other.Dsv) && ReferenceEquals(eligible, stream1));
                if (chooseEligible)
                {
                    stream1.CopyFrom(eligible);
                    stream2.CopyFrom(eligible);
                    AppendStateOneAndFourAlternatives(value, stream1, stream2);
                }
                else
                {
                    AppendMain(value, stream1);
                    AppendMain(value, stream2);
                }
            }
            else
            {
                AppendMain(value, stream1);
                AppendMain(value, stream2);
            }
        }

        StreamCandidate winner = Select(stream1, stream2);
        if (winner.Dsv is > 63 or < -64)
        {
            bool alternatePrimary = !winner.SyncPrimary;
            winner.Nrz.AsSpan().CopyTo(_alternateNrz);
            WriteBits(EfmPlusTables.Sync(input.State, syncCategory, alternatePrimary), 32, _alternateNrz);
            ModulationBoundary alternateBoundary = Replay(_alternateNrz, input, physicalLevels: default);
            if (Math.Abs(alternateBoundary.Dsv) < Math.Abs(winner.Dsv))
            {
                _alternateNrz.AsSpan().CopyTo(winner.Nrz);
                winner.Dsv = alternateBoundary.Dsv;
                winner.IsLand = alternateBoundary.IsLand;
                winner.SyncPrimary = alternatePrimary;
            }
        }

        ModulationBoundary output = Replay(winner.Nrz, input, physicalLevels);
        return output with { State = winner.State };
    }

    private static void AppendStateOneAndFourAlternatives(byte value, StreamCandidate stream1, StreamCandidate stream2)
    {
        var stateOne = EfmPlusTables.LookupMain(value, 1);
        var stateFour = EfmPlusTables.LookupMain(value, 4);
        if (!stream1.CanAppend(stateOne.Word) || !stream2.CanAppend(stateFour.Word))
        {
            throw new InvalidOperationException("Invalid State 1/4 EFMPlus branch.");
        }

        stream1.Append(stateOne.Word, stateOne.NextState);
        stream2.Append(stateFour.Word, stateFour.NextState);
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

    private static void AppendMain(byte value, StreamCandidate stream)
    {
        var main = EfmPlusTables.LookupMain(value, stream.State);
        stream.Append(main.Word, main.NextState);
    }

    private static StreamCandidate Select(StreamCandidate stream1, StreamCandidate stream2)
        => Math.Abs(stream1.Dsv) <= Math.Abs(stream2.Dsv) ? stream1 : stream2;

    private static void WriteBits(uint bits, int count, Span<byte> destination)
    {
        for (int bit = count - 1, index = 0; bit >= 0; bit--, index++)
        {
            destination[index] = (byte)((bits >> bit) & 1);
        }
    }

    private static ModulationBoundary Replay(
        ReadOnlySpan<byte> nrz,
        ModulationBoundary input,
        Span<byte> physicalLevels)
    {
        bool level = input.IsLand;
        int dsv = input.Dsv;
        for (int index = 0; index < nrz.Length; index++)
        {
            if (nrz[index] != 0)
            {
                level = !level;
            }

            if (!physicalLevels.IsEmpty)
            {
                physicalLevels[index] = level ? (byte)1 : (byte)0;
            }

            dsv += level ? 1 : -1;
        }

        return new ModulationBoundary(input.State, dsv, level);
    }
}
