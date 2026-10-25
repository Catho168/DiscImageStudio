namespace DiscImageStudio.Imaging;

/// <summary>Formal generation renders the design composite at the highest size accepted by
/// the WPF renderer, so the pixels the engine consumes match the design page's own quality.</summary>
public static class RingImageQuality
{
    public const int GenerationSize = 8192;
}
