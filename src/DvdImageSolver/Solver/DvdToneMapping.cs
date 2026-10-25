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
        return tone is { } profile
            ? profile.UseWhite(luminance, sampleIndex, threshold)
            : luminance >= threshold;
    }
}
