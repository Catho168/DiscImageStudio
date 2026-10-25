using DiscImageStudio.Core;

namespace DiscImageStudio.Cd;

/// <summary>
/// CD palette encoding of <see cref="ToneProfile"/>. The black/white pair is the higher
/// contrast 0x0D/0xAA pair from upstream 1.1; the 4-level palette keeps the reference
/// cdimage intermediate patterns (0x21 / 0x28).
/// </summary>
public static class CdToneMapping
{
    /// <summary>Display grey of the palette bytes; also the tone-mapping endpoints.</summary>
    public const byte BlackGray = 0;
    public const byte WhiteGray = 255;

    private static readonly byte[] TwoLevelPalette = [0x0D, 0xAA];
    private static readonly byte[] FourLevelPalette = [0x0D, 0x10, 0x63, 0xAA];

    public static ReadOnlySpan<byte> Palette(int levels)
        => (levels == ToneProfile.FourLevels ? FourLevelPalette : TwoLevelPalette).AsSpan();

    /// <summary>Maps one source grey to the palette byte that encodes it. The profile must
    /// already be within range (the generators normalize once per job); this runs once per
    /// audio byte, so it deliberately does not re-validate.</summary>
    public static byte Map(byte gray, ToneProfile tone, long sampleIndex)
    {
        if (tone.IsLegacyBinary)
        {
            // Hot path of every existing project: byte-identical to the pre-tone generator.
            return gray < 128 ? TwoLevelPalette[0] : TwoLevelPalette[1];
        }

        ReadOnlySpan<byte> palette = Palette(tone.Levels);
        int last = palette.Length - 1;
        double position = Math.Clamp(
            (gray + tone.Brightness - BlackGray) / (double)(WhiteGray - BlackGray),
            0.0,
            1.0) * last;
        int index = (int)position;
        if (index >= last)
        {
            return palette[last];
        }

        double fraction = position - index;
        double threshold = 0.5;
        if (tone.DitherStrength > 0)
        {
            threshold += (tone.DitherStrength > 1 ? 1 : tone.DitherStrength)
                * (ToneDither.Phase(sampleIndex) - 0.5);
        }

        return palette[fraction >= threshold ? index + 1 : index];
    }

    /// <summary>Display grey of a palette byte, so every preview uses the same levels.</summary>
    public static byte DisplayGray(byte paletteByte) => paletteByte switch
    {
        0x0D => 0,
        0x10 => 85,
        0x21 => 96,
        0x28 => 160,
        0x63 => 170,
        0xAA => 255,
        _ => paletteByte,
    };
}
