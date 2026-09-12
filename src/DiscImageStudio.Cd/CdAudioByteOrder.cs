using System.IO;

namespace DiscImageStudio.Cd;

public enum CdAudioByteOrder
{
    BigEndian,
    LittleEndian,
}

internal static class CdAudioSamples
{
    internal static void Validate(CdAudioByteOrder byteOrder)
    {
        if (!Enum.IsDefined(byteOrder))
        {
            throw new ArgumentOutOfRangeException(nameof(byteOrder));
        }
    }

    internal static void SwapByteOrder(Span<byte> samples)
    {
        if ((samples.Length & 1) != 0)
        {
            throw new InvalidDataException("CD audio must contain complete 16-bit samples.");
        }

        for (int index = 0; index < samples.Length; index += 2)
        {
            (samples[index], samples[index + 1]) = (samples[index + 1], samples[index]);
        }
    }

    internal static void WriteSector(Stream output, byte[] sector, CdAudioByteOrder byteOrder)
    {
        // The delay compensation produces the existing cdrecord big-endian order.
        // Convert only after compensation: swapping its input changes the picture.
        if (byteOrder == CdAudioByteOrder.LittleEndian)
        {
            SwapByteOrder(sector);
        }

        output.Write(sector);
    }
}
