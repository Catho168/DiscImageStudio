namespace DvdImageSolver.Encoding;

internal static class ReedSolomon
{
    private static readonly byte[] PiGenerator = BuildGenerator(10);
    private static readonly byte[] PoGenerator = BuildGenerator(16);
    private static readonly byte[] IedGenerator = BuildGenerator(2);

    internal static void AppendPi(ReadOnlySpan<byte> data, Span<byte> parity) => Encode(data, parity, PiGenerator);

    internal static void AppendPo(ReadOnlySpan<byte> data, Span<byte> parity) => Encode(data, parity, PoGenerator);

    internal static void AppendIed(ReadOnlySpan<byte> data, Span<byte> parity) => Encode(data, parity, IedGenerator);

    internal static bool HasZeroRemainder(ReadOnlySpan<byte> codeword, int parityBytes)
    {
        byte[] remainder = new byte[parityBytes];
        byte[] generator = parityBytes switch
        {
            2 => IedGenerator,
            10 => PiGenerator,
            16 => PoGenerator,
            _ => BuildGenerator(parityBytes),
        };

        Encode(codeword, remainder, generator);
        return remainder.AsSpan().IndexOfAnyExcept((byte)0) < 0;
    }

    private static void Encode(ReadOnlySpan<byte> data, Span<byte> parity, ReadOnlySpan<byte> generator)
    {
        if (parity.Length != generator.Length - 1)
        {
            throw new ArgumentException("Parity and generator sizes do not match.", nameof(parity));
        }

        parity.Clear();
        foreach (byte value in data)
        {
            byte feedback = (byte)(value ^ parity[0]);
            for (int index = 0; index < parity.Length - 1; index++)
            {
                parity[index] = (byte)(parity[index + 1] ^ GaloisField256.Multiply(feedback, generator[index + 1]));
            }

            parity[^1] = GaloisField256.Multiply(feedback, generator[^1]);
        }
    }

    private static byte[] BuildGenerator(int parityBytes)
    {
        byte[] polynomial = [1];
        for (int root = 0; root < parityBytes; root++)
        {
            byte[] next = new byte[polynomial.Length + 1];
            byte alpha = GaloisField256.AlphaPower(root);
            for (int index = 0; index < polynomial.Length; index++)
            {
                next[index] ^= polynomial[index];
                next[index + 1] ^= GaloisField256.Multiply(polynomial[index], alpha);
            }

            polynomial = next;
        }

        return polynomial;
    }

}
