namespace DvdImageSolver;

internal static class BitPacking
{
    internal static byte[] PackMsbFirst(ReadOnlySpan<byte> bits)
    {
        byte[] packed = new byte[(bits.Length + 7) / 8];
        for (int index = 0; index < bits.Length; index++)
        {
            if (bits[index] != 0)
            {
                packed[index / 8] |= (byte)(0x80 >> (index & 7));
            }
        }

        return packed;
    }
}
