namespace DiscImageStudio.BluRay.Encoding;

public sealed record BluRayRecordingUnitResult(
    uint FirstPhysicalSectorNumber, byte[] ModulationBits, byte[] ChannelLevels,
    byte[] DcControlBits, bool IncludesGuard3, long FinalDigitalSum, int SuboptimalDcControlDecisions)
{
    public byte FinalChannelLevel => ChannelLevels[^1];
}

/// <summary>Diagnostic BD-R recording-unit simulation, ISO/IEC 30190:2021, Clauses 13 and 14.</summary>
/// <remarks>
/// Includes nominal guard/preamble/postamble patterns and a deterministic greedy dc-control policy.
/// The result is one byte per channel bit, not a drive command, sector image or optical reflectance model.
/// APC overrides, write pulses, wobble/ADIP and whole-disc management areas are recorder/media concerns.
/// </remarks>
public static class BluRayRecordingUnitEncoder
{
    public const int RunInChannelBits = 2760;
    public const int PhysicalClusterChannelBits = 496 * BluRayRecordingFrameEncoder.ChannelBitsPerFrame;
    public const int RunOutChannelBits = 1104;
    public const int RecordingUnitChannelBits = RunInChannelBits + PhysicalClusterChannelBits + RunOutChannelBits;
    public const int Guard3ChannelBits = 540;
    private static readonly byte[] GuardPattern = "01001001010100001000".Select(bit => (byte)(bit - '0')).ToArray();

    public static BluRayRecordingUnitResult Encode(ReadOnlySpan<byte> userData, uint firstPhysicalSectorNumber,
        ReadOnlySpan<byte> addressFlags = default, ReadOnlySpan<byte> userControlData = default,
        bool appendGuard3 = true, byte initialChannelLevel = 0, long initialDigitalSum = 0,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] ecc = BluRayEccEncoder.Encode(userData, firstPhysicalSectorNumber, addressFlags, userControlData);
        return EncodeEccCluster(ecc, firstPhysicalSectorNumber, appendGuard3, initialChannelLevel,
            initialDigitalSum, cancellationToken);
    }

    public static BluRayRecordingUnitResult EncodeEccCluster(ReadOnlySpan<byte> eccCluster, uint firstPhysicalSectorNumber,
        bool appendGuard3 = true, byte initialChannelLevel = 0, long initialDigitalSum = 0,
        CancellationToken cancellationToken = default)
    {
        if (eccCluster.Length != BluRayEccEncoder.EccClusterBytes)
            throw new ArgumentException("An ECC cluster contains 496 rows of 155 bytes.", nameof(eccCluster));
        if ((firstPhysicalSectorNumber & 31) != 0 || firstPhysicalSectorNumber > 0x07ffffe0)
            throw new ArgumentOutOfRangeException(nameof(firstPhysicalSectorNumber));
        if (initialChannelLevel > 1) throw new ArgumentOutOfRangeException(nameof(initialChannelLevel));
        BluRayDigitalSum.ValidateInitial(initialDigitalSum);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] modulation = new byte[RecordingUnitChannelBits + (appendGuard3 ? Guard3ChannelBits : 0)];
        byte[] levels = new byte[modulation.Length];
        byte[] controls = new byte[496 * 28];
        int cursor = 0;
        byte level = initialChannelLevel;
        long sum = initialDigitalSum;

        AppendGuard(55); // Guard_1
        AppendGuard(77); // PrA, first 1540 channel bits
        Append(BluRayRecordingFrameEncoder.CreateFrameSync(6)); // FS4
        AppendGuard(2);
        Append(BluRayRecordingFrameEncoder.CreateFrameSync(13)); // FS6
        AppendGuard(1);
        bool previousTermination = false;
        bool[] terminationFlags = new bool[496];
        long[] startingDigitalSums = new long[496];
        for (int frame = 0; frame < 496; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> next = frame == 495 ? default
                : eccCluster.Slice((frame + 1) * 155, 155);
            startingDigitalSums[frame] = sum;
            terminationFlags[frame] = previousTermination;
            BluRayBalancedFrameResult encoded = BluRayGreedyDcControlEncoder.Encode(eccCluster.Slice(frame * 155, 155),
                frame % 31, next, previousTermination, level, sum);
            encoded.DcControlBits.CopyTo(controls, frame * 28);
            Append(encoded.Frame.ModulationBits);
            previousTermination = encoded.Frame.UsedTermination;
        }
        Append(BluRayRecordingFrameEncoder.CreateFrameSync(0, previousTermination));
        byte[] stopPattern = new byte[54];
        for (int i = 1; i < stopPattern.Length; i += 9) stopPattern[i] = 1;
        Append(stopPattern);
        AppendGuard(24); // PoA remainder
        AppendGuard(27); // Guard_2
        if (appendGuard3) AppendGuard(27);
        if (cursor != modulation.Length) throw new InvalidOperationException("Incorrect recording-unit length.");
        int suboptimalDecisions = 0;
        for (int frame = 0; frame < 496; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> next = frame == 495 ? default : eccCluster.Slice((frame + 1) * 155, 155);
            ReadOnlySpan<byte> nextControls = frame == 495 ? default : controls.AsSpan((frame + 1) * 28, 28);
            suboptimalDecisions += BluRayGreedyDcControlEncoder.CountSuboptimalDecisions(eccCluster.Slice(frame * 155, 155),
                frame % 31, controls.AsSpan(frame * 28, 28), next, nextControls, terminationFlags[frame],
                levels[RunInChannelBits + frame * 1932 - 1], startingDigitalSums[frame]);
        }
        return new(firstPhysicalSectorNumber, modulation, levels, controls, appendGuard3, sum, suboptimalDecisions);

        void Append(ReadOnlySpan<byte> bits)
        {
            foreach (byte transition in bits)
            {
                modulation[cursor] = transition;
                level ^= transition;
                levels[cursor++] = level;
                sum += level == 0 ? -1 : 1;
            }
        }
        void AppendGuard(int repeats)
        {
            for (int i = 0; i < repeats; i++) Append(GuardPattern);
        }
    }
}
