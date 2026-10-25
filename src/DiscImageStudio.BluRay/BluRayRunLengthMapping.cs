using DiscImageStudio.Core;
using System.Numerics;

namespace DiscImageStudio.BluRay;

/// <summary>Binary image mapping favouring the number of 3T or 6T runs in equal-length
/// physical clusters. Values are post-scrambler payload bytes, not host bytes.</summary>
/// <remarks>The repeated-byte sweep selects AA for 3T and 44 for 6T by absolute run
/// counts, not transition density or run-length share. Dark-to-3T is a configurable
/// image convention, not a measured optical polarity. ECC, DC control and image
/// boundaries introduce other run lengths; this does not produce pure 3T/6T tracks.</remarks>
public sealed class BluRayRunLengthMapping(bool invert = false) : IBluRayLuminanceMapping
{
    public const byte ThreeTByte = 0xAA;
    public const byte SixTByte = 0x44;

    public bool Invert { get; } = invert;

    public byte Map(byte luminance, BluRayPayloadBytePosition position)
    {
        bool useThreeT = BinaryGrayscale.GetLevelIndex(luminance) == 0;
        if (Invert) useThreeT = !useThreeT;
        return useThreeT ? ThreeTByte : SixTByte;
    }

    /// <summary>Maps a completed luminance span with the same exact 128 threshold.
    /// Integer SIMD does not approximate geometry or move image boundaries.</summary>
    internal void MapLuminances(Span<byte> luminances)
    {
        byte dark = Invert ? SixTByte : ThreeTByte;
        byte light = Invert ? ThreeTByte : SixTByte;
        int index = 0;
        if (Vector.IsHardwareAccelerated)
        {
            Vector<byte> threshold = new(128);
            Vector<byte> darkValues = new(dark);
            Vector<byte> lightValues = new(light);
            for (; index <= luminances.Length - Vector<byte>.Count; index += Vector<byte>.Count)
            {
                Vector<byte> pixels = new(luminances[index..]);
                Vector.ConditionalSelect(Vector.LessThan(pixels, threshold), darkValues, lightValues)
                    .CopyTo(luminances[index..]);
            }
        }
        for (; index < luminances.Length; index++)
            luminances[index] = luminances[index] < 128 ? dark : light;
    }
}
