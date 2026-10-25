using System.Buffers.Binary;

namespace DiscImageStudio.BluRay.Encoding;

/// <summary>BD-R data-frame EDC, ISO/IEC 30190:2021, 13.2 and 13.3.</summary>
public static class BluRayEdc
{
    public const int UserDataBytes = 2048;
    public const int DataFrameBytes = 2052;
    private const uint Polynomial = 0x80000011;
    private static readonly uint[] Table = BuildTable();

    /// <summary>MSB-first polynomial remainder; initial value and final xor are zero.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint remainder = 0;
        foreach (byte value in data)
            remainder = (remainder << 8) ^ Table[(remainder >> 24) ^ value];
        return remainder;
    }

    public static void WriteDataFrame(ReadOnlySpan<byte> userData, Span<byte> destination)
    {
        if (userData.Length != UserDataBytes)
            throw new ArgumentException("A BD user-data frame contains exactly 2048 bytes.", nameof(userData));
        if (destination.Length != DataFrameBytes)
            throw new ArgumentException("A BD data frame contains exactly 2052 bytes.", nameof(destination));
        uint edc = Compute(userData);
        userData.CopyTo(destination);
        BinaryPrimitives.WriteUInt32BigEndian(destination[UserDataBytes..], edc);
    }

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i << 24;
            for (int bit = 0; bit < 8; bit++)
                value = (value << 1) ^ ((value & 0x80000000) != 0 ? Polynomial : 0);
            table[i] = value;
        }
        return table;
    }
}
