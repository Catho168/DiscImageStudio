namespace DiscImageStudio.BluRay.Encoding;

public sealed record BluRay17PpResult(byte[] ModulationBits, bool UsedTermination)
{
    public byte LastModulationBit => ModulationBits.Length == 0 ? (byte)0 : ModulationBits[^1];
}

/// <summary>17PP variable-length RLL(1,7) conversion, ISO/IEC 30190:2021, 13.17.2, Figure 41.</summary>
public static class BluRay17PpEncoder
{
    /// <summary>
    /// Encodes an even number of MSB-first data bits (each byte is 0 or 1).
    /// The previous modulation bit supplies the context for data pair 11.
    /// The caller supplies dc-control bits and frame-sync boundaries.
    /// </summary>
    public static BluRay17PpResult Encode(ReadOnlySpan<byte> dataBits, byte previousModulationBit = 0)
    {
        if ((dataBits.Length & 1) != 0)
            throw new ArgumentException("17PP consumes complete pairs of data bits.", nameof(dataBits));
        if (previousModulationBit > 1) throw new ArgumentOutOfRangeException(nameof(previousModulationBit));
        foreach (byte bit in dataBits)
            if (bit > 1) throw new ArgumentException("Data bits must be represented as 0 or 1.", nameof(dataBits));

        byte[] result = new byte[checked(dataBits.Length / 2 * 3)];
        int source = 0;
        int output = 0;
        bool terminated = false;
        while (source < dataBits.Length)
        {
            Codeword word;
            // Figure 41's substitution breaks long repetitions of minimum run lengths.
            // Its following 010 prefix is unchanged by the substituted word's final bit.
            if (dataBits.Length - source >= 8 && ReadBits(dataBits.Slice(source, 6)) == 0b110111
                && BeginsWith010(ConvertNext(dataBits[(source + 6)..], 1)))
            {
                word = new Codeword(6, 0b001000000, false);
            }
            else word = ConvertNext(dataBits[source..], previousModulationBit);

            int channelLength = word.DataLength / 2 * 3;
            for (int bit = channelLength - 1; bit >= 0; bit--)
                result[output++] = (byte)((word.ChannelValue >> bit) & 1);
            previousModulationBit = result[output - 1];
            source += word.DataLength;
            terminated = word.IsTerminating;
        }
        return new(result, terminated);
    }

    /// <summary>NRZI conversion; a modulation ONE toggles the recorded level.</summary>
    public static byte[] ToChannelLevels(ReadOnlySpan<byte> modulationBits, byte initialLevel = 0)
    {
        if (initialLevel > 1) throw new ArgumentOutOfRangeException(nameof(initialLevel));
        byte[] result = new byte[modulationBits.Length];
        byte level = initialLevel;
        for (int i = 0; i < result.Length; i++)
        {
            if (modulationBits[i] > 1)
                throw new ArgumentException("Modulation bits must be represented as 0 or 1.", nameof(modulationBits));
            level ^= modulationBits[i];
            result[i] = level;
        }
        return result;
    }

    private static Codeword ConvertNext(ReadOnlySpan<byte> bits, byte previous)
    {
        int firstPair = ReadBits(bits[..2]);
        if (firstPair == 1) return new(2, 0b010, false);
        if (firstPair == 2) return new(2, 0b001, false);
        if (firstPair == 3) return new(2, previous == 1 ? 0b000 : 0b101, false);
        if (bits.Length == 2) return new(2, 0b000, true);

        int nextPair = ReadBits(bits.Slice(2, 2));
        if (nextPair == 1) return new(4, 0b000100, false);
        if (nextPair == 2) return new(4, 0b010000, false);
        if (nextPair == 3) return new(4, 0b010100, false);
        if (bits.Length == 4) return new(4, 0b010100, true);

        int thirdPair = ReadBits(bits.Slice(4, 2));
        if (bits.Length >= 8 && ReadBits(bits.Slice(6, 2)) == 0)
        {
            if (thirdPair == 0) return new(8, 0b010100100100, false);
            if (thirdPair == 2) return new(8, 0b000100100100, false);
        }
        return thirdPair switch
        {
            0 => new(6, 0b010100000, false),
            1 => new(6, 0b010100100, false),
            2 => new(6, 0b000100000, false),
            _ => new(6, 0b000100100, false),
        };
    }

    private static bool BeginsWith010(Codeword word)
        => (word.ChannelValue >> (word.DataLength / 2 * 3 - 3)) == 0b010;

    private static int ReadBits(ReadOnlySpan<byte> bits)
    {
        int value = 0;
        foreach (byte bit in bits) value = (value << 1) | bit;
        return value;
    }

    private readonly record struct Codeword(int DataLength, int ChannelValue, bool IsTerminating);
}
