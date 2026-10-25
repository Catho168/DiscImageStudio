namespace DiscImageStudio.BluRay.Encoding;

public sealed record BluRayBalancedFrameResult(
    BluRayRecordingFrameResult Frame, byte[] DcControlBits, long FinalDigitalSum);

/// <summary>Deterministic look-ahead selection of the dc-control positions in 13.17.3.</summary>
/// <remarks>
/// Both values of the preceding control bit are compared at the end of the following block.
/// Whole-frame modulation preserves variable-length 17PP codewords across control boundaries.
/// Future, undecided controls are provisionally zero. This is a greedy diagnostic policy; it does
/// not predict a recorder's proprietary balancing or write strategy.
/// </remarks>
public static class BluRayGreedyDcControlEncoder
{
    public static BluRayBalancedFrameResult Encode(ReadOnlySpan<byte> eccFrame, int frameWithinAddressUnit,
        ReadOnlySpan<byte> nextEccFrame = default, bool precedingFrameUsedTermination = false,
        byte initialChannelLevel = 0, long initialDigitalSum = 0)
    {
        BluRayDigitalSum.ValidateInitial(initialDigitalSum);
        if (!nextEccFrame.IsEmpty && nextEccFrame.Length != BluRayEccEncoder.EccBytesPerFrame)
            throw new ArgumentException("Next-frame lookahead requires 155 ECC bytes.", nameof(nextEccFrame));
        byte[] controls = new byte[BluRayRecordingFrameEncoder.DcControlBitsPerFrame];
        BluRayRecordingFrameResult? selected = null;
        for (int control = 0; control < controls.Length; control++)
        {
            controls[control] = 0;
            BluRayRecordingFrameResult zero = BluRayRecordingFrameEncoder.Encode(eccFrame, frameWithinAddressUnit,
                controls, precedingFrameUsedTermination, initialChannelLevel);
            controls[control] = 1;
            BluRayRecordingFrameResult one = BluRayRecordingFrameEncoder.Encode(eccFrame, frameWithinAddressUnit,
                controls, precedingFrameUsedTermination, initialChannelLevel);
            long scoreZero = Score(zero, control, nextEccFrame, default, frameWithinAddressUnit, initialDigitalSum);
            long scoreOne = Score(one, control, nextEccFrame, default, frameWithinAddressUnit, initialDigitalSum);
            bool chooseOne = scoreOne < scoreZero;
            controls[control] = chooseOne ? (byte)1 : (byte)0;
            selected = chooseOne ? one : zero;
        }
        return new(selected!, controls, initialDigitalSum + selected!.DigitalSum);
    }

    /// <summary>
    /// Counts final decisions whose following-block |DSV| would improve by flipping just that control.
    /// This diagnoses the greedy policy's provisional-future limitation; zero is not a disc-conformance certificate.
    /// </summary>
    public static int CountSuboptimalDecisions(ReadOnlySpan<byte> eccFrame, int frameWithinAddressUnit,
        ReadOnlySpan<byte> dcControlBits, ReadOnlySpan<byte> nextEccFrame = default,
        ReadOnlySpan<byte> nextDcControlBits = default, bool precedingFrameUsedTermination = false,
        byte initialChannelLevel = 0, long initialDigitalSum = 0)
    {
        BluRayDigitalSum.ValidateInitial(initialDigitalSum);
        if (nextEccFrame.IsEmpty != nextDcControlBits.IsEmpty
            || (!nextEccFrame.IsEmpty && (nextEccFrame.Length != 155 || nextDcControlBits.Length != 28)))
            throw new ArgumentException("Supply the next frame and all its final control bits together.");
        byte[] controls = dcControlBits.ToArray();
        BluRayRecordingFrameResult actual = BluRayRecordingFrameEncoder.Encode(eccFrame, frameWithinAddressUnit,
            controls, precedingFrameUsedTermination, initialChannelLevel);
        int count = 0;
        for (int control = 0; control < controls.Length; control++)
        {
            controls[control] ^= 1;
            BluRayRecordingFrameResult alternate = BluRayRecordingFrameEncoder.Encode(eccFrame, frameWithinAddressUnit,
                controls, precedingFrameUsedTermination, initialChannelLevel);
            controls[control] ^= 1;
            if (Score(alternate, control, nextEccFrame, nextDcControlBits, frameWithinAddressUnit, initialDigitalSum)
                < Score(actual, control, nextEccFrame, nextDcControlBits, frameWithinAddressUnit, initialDigitalSum)) count++;
        }
        return count;
    }

    private static long Score(BluRayRecordingFrameResult frame, int control, ReadOnlySpan<byte> nextEccFrame,
        ReadOnlySpan<byte> nextDcControlBits, int frameWithinAddressUnit, long initialDigitalSum)
    {
        int endpoint = Math.Min((control + 2) * 69, BluRayRecordingFrameEncoder.ChannelBitsPerFrame);
        long sum = initialDigitalSum;
        foreach (byte level in frame.ChannelLevels.AsSpan(0, endpoint)) sum += level == 0 ? -1 : 1;
        if (control == 27)
        {
            byte[] nextModulation;
            if (!nextEccFrame.IsEmpty)
            {
                byte[] nextData = BluRayRecordingFrameEncoder.CreateDataBits(nextEccFrame,
                    nextDcControlBits.IsEmpty ? new byte[28] : nextDcControlBits);
                byte[] nextSync = BluRayRecordingFrameEncoder.CreateFrameSync((frameWithinAddressUnit + 1) % 31, frame.UsedTermination);
                BluRay17PpResult encoded = BluRay17PpEncoder.Encode(nextData, nextSync[^1]);
                nextModulation = new byte[69];
                nextSync.CopyTo(nextModulation, 0);
                encoded.ModulationBits.AsSpan(0, 39).CopyTo(nextModulation.AsSpan(30));
            }
            else
            {
                // The last frame is followed by PoA: FS0 plus the 9T stop-of-user-data pattern.
                nextModulation = new byte[69];
                BluRayRecordingFrameEncoder.CreateFrameSync(0, frame.UsedTermination).CopyTo(nextModulation, 0);
                for (int i = 31; i < nextModulation.Length; i += 9) nextModulation[i] = 1;
            }
            byte level = frame.FinalChannelLevel;
            foreach (byte transition in nextModulation)
            {
                level ^= transition;
                sum += level == 0 ? -1 : 1;
            }
        }
        return Math.Abs(sum);
    }
}

internal static class BluRayDigitalSum
{
    // One full RUB and frame lookahead fit comfortably in this headroom, including Math.Abs.
    private const long MaximumInitialMagnitude = long.MaxValue - 2_000_000;

    internal static void ValidateInitial(long initialDigitalSum)
    {
        if (initialDigitalSum < -MaximumInitialMagnitude || initialDigitalSum > MaximumInitialMagnitude)
            throw new ArgumentOutOfRangeException(nameof(initialDigitalSum), "Initial digital sum is too close to Int64 limits.");
    }
}
