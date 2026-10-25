namespace DiscImageStudio.BluRay.Encoding;

public sealed record BluRayRecordingFrameResult(
    byte[] ModulationBits, byte[] ChannelLevels, bool UsedTermination)
{
    public byte FinalChannelLevel => ChannelLevels[^1];
    public int DigitalSum => ChannelLevels.Sum(level => level == 0 ? -1 : 1);
}

/// <summary>Recording-frame assembly and sync/NRZI conversion (ISO/IEC 30190:2021, 13.15..13.18).</summary>
/// <remarks>
/// This low-level encoder accepts explicit dc-control bits. It does not select them or construct RUB
/// run-in/run-out. An all-zero dc-control array is useful for inspection, not a DC-balanced recording.
/// </remarks>
public static class BluRayRecordingFrameEncoder
{
    public const int DcControlBitsPerFrame = 28;
    public const int ModulatedDataBitsPerFrame = 1268;
    public const int ChannelBitsPerFrame = 1932;
    private static readonly byte[] SyncSequence =
        [0, 1, 2, 3, 3, 1, 4, 1, 5, 5, 4, 3, 4, 6, 6, 3, 5, 3, 2, 2, 5, 6, 5, 1, 1, 6, 2, 6, 4, 4, 2];
    private static readonly byte[] SyncSignatures = [0b000001, 0b010010, 0b101000, 0b100001, 0b000100, 0b001001, 0b010000];

    public static BluRayRecordingFrameResult Encode(ReadOnlySpan<byte> eccFrame, int frameWithinAddressUnit,
        ReadOnlySpan<byte> dcControlBits, bool precedingFrameUsedTermination = false, byte initialChannelLevel = 0)
    {
        byte[] sync = CreateFrameSync(frameWithinAddressUnit, precedingFrameUsedTermination);
        byte[] data = CreateDataBits(eccFrame, dcControlBits);
        BluRay17PpResult encoded = BluRay17PpEncoder.Encode(data, sync[^1]);
        byte[] modulation = new byte[ChannelBitsPerFrame];
        sync.CopyTo(modulation, 0);
        encoded.ModulationBits.CopyTo(modulation, sync.Length);
        return new(modulation, BluRay17PpEncoder.ToChannelLevels(modulation, initialChannelLevel), encoded.UsedTermination);
    }

    /// <summary>Inserts 28 caller-selected dc-control bits after the initial 25 and subsequent 45 data bits.</summary>
    public static byte[] CreateDataBits(ReadOnlySpan<byte> eccFrame, ReadOnlySpan<byte> dcControlBits)
    {
        if (eccFrame.Length != BluRayEccEncoder.EccBytesPerFrame)
            throw new ArgumentException("A recording frame starts with 155 ECC bytes.", nameof(eccFrame));
        if (dcControlBits.Length != DcControlBitsPerFrame)
            throw new ArgumentException("Supply exactly 28 dc-control bits.", nameof(dcControlBits));
        foreach (byte bit in dcControlBits)
            if (bit > 1) throw new ArgumentException("dc-control bits must be 0 or 1.", nameof(dcControlBits));
        byte[] result = new byte[ModulatedDataBitsPerFrame];
        int input = 0;
        int output = 0;
        for (int block = 0; block < DcControlBitsPerFrame; block++)
        {
            int count = block == 0 ? 25 : 45;
            for (int bit = 0; bit < count; bit++, input++)
                result[output++] = (byte)((eccFrame[input / 8] >> (7 - input % 8)) & 1);
            result[output++] = dcControlBits[block];
        }
        return result;
    }

    /// <summary>Creates the 30-bit sync at frame index 0..30, including the previous-frame termination flag.</summary>
    public static byte[] CreateFrameSync(int frameWithinAddressUnit, bool precedingFrameUsedTermination = false)
    {
        if (frameWithinAddressUnit is < 0 or > 30) throw new ArgumentOutOfRangeException(nameof(frameWithinAddressUnit));
        const uint body = 0b001010000000010000000010;
        uint value = ((body | (precedingFrameUsedTermination ? 1u << 23 : 0)) << 6)
            | SyncSignatures[SyncSequence[frameWithinAddressUnit]];
        byte[] result = new byte[30];
        for (int i = 0; i < result.Length; i++) result[i] = (byte)((value >> (29 - i)) & 1);
        return result;
    }
}
