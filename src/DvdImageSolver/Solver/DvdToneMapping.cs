using DiscImageStudio.Core;

namespace DvdImageSolver.Solver;

/// <summary>
/// Binary texture decision shared by the dispersion writer, the constraint mapper and the
/// calibration preview. Dithering shifts the threshold per payload sample, so the sampled
/// picture keeps its midtones while still using only the two proven textures.
/// </summary>
public static class DvdToneMapping
{
    public static bool UseWhite(
        byte luminance,
        byte threshold,
        ToneProfile? tone,
        long sampleIndex)
    {
        if (tone is not { } profile || profile.IsLegacyBinary)
        {
            return luminance >= threshold;
        }

        // Profiles are validated by the caller (DvdStreamingOptions.Validate / the CLI); this
        // runs once per payload byte, so it deliberately does not re-normalize.
        double adjusted = Math.Clamp(luminance + profile.Brightness, 0, 255);
        double strength = profile.DitherStrength > 1 ? 1 : profile.DitherStrength;
        double shiftedThreshold = threshold
            + (strength * 255.0 * (ToneDither.Phase(sampleIndex) - 0.5));
        return adjusted >= shiftedThreshold;
    }
}
