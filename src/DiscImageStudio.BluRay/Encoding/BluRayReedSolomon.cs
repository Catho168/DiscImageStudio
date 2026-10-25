namespace DiscImageStudio.BluRay.Encoding;

/// <summary>Shortened systematic RS over GF(256), polynomial 0x11d, roots alpha^0 onward.</summary>
public static class BluRayReedSolomon
{
    private static readonly byte[] Exponents = BuildExponents();
    private static readonly byte[] Logarithms = BuildLogarithms();
    private static readonly byte[] Generator4 = BuildGenerator(4);
    private static readonly byte[] Generator32 = BuildGenerator(32);

    /// <summary>Writes the 4 address-field or 32 LDC/BIS parity bytes in on-disc order.</summary>
    public static void ComputeParity(ReadOnlySpan<byte> information, Span<byte> parity)
    {
        if (parity.Length is not (4 or 32))
            throw new ArgumentException("BD address and LDC/BIS codes use 4 or 32 parity bytes.", nameof(parity));
        if (information.Length == 0 || information.Length + parity.Length > 255)
            throw new ArgumentException("The shortened code word must contain 1..255 symbols.", nameof(information));
        byte[] generator = parity.Length == 4 ? Generator4 : Generator32;
        Span<byte> work = stackalloc byte[255];
        work.Clear();
        information.CopyTo(work);
        for (int i = 0; i < information.Length; i++)
        {
            byte leading = work[i];
            if (leading == 0) continue;
            for (int coefficient = 1; coefficient < generator.Length; coefficient++)
                work[i + coefficient] ^= Multiply(leading, generator[coefficient]);
        }
        work.Slice(information.Length, parity.Length).CopyTo(parity);
    }

    private static byte Multiply(byte left, byte right)
        => left == 0 || right == 0 ? (byte)0 : Exponents[Logarithms[left] + Logarithms[right]];

    private static byte[] BuildGenerator(int degree)
    {
        byte[] result = [1];
        for (int root = 0; root < degree; root++)
        {
            byte[] next = new byte[result.Length + 1];
            for (int i = 0; i < result.Length; i++)
            {
                next[i] ^= result[i];
                next[i + 1] ^= Multiply(result[i], Exponents[root]);
            }
            result = next;
        }
        return result;
    }

    private static byte[] BuildExponents()
    {
        byte[] result = new byte[510];
        int value = 1;
        for (int i = 0; i < 255; i++)
        {
            result[i] = (byte)value;
            value <<= 1;
            if ((value & 0x100) != 0) value ^= 0x11d;
        }
        for (int i = 255; i < result.Length; i++) result[i] = result[i - 255];
        return result;
    }

    private static byte[] BuildLogarithms()
    {
        byte[] result = new byte[256];
        for (int i = 0; i < 255; i++) result[Exponents[i]] = (byte)i;
        return result;
    }
}
