namespace DiscImageStudio.Burning;

/// <summary>Bounded host-side queue settings; these do not change the recorder's hardware buffer.</summary>
public sealed record OpticalBurnBufferOptions(int CapacityBytes, int PrebufferBytes)
{
    internal const int ChunkBytes = 256 * 1024;
    public static OpticalBurnBufferOptions Default { get; } = new(64 * 1024 * 1024, 32 * 1024 * 1024);
    public static OpticalBurnBufferOptions BluRayDefault { get; } = new(256 * 1024 * 1024, 128 * 1024 * 1024);

    public void Validate()
    {
        if (CapacityBytes <= 0 || CapacityBytes % ChunkBytes != 0)
            throw new ArgumentOutOfRangeException(nameof(CapacityBytes), "Buffer capacity must be a positive multiple of 256 KiB.");
        if (PrebufferBytes <= 0 || PrebufferBytes > CapacityBytes || PrebufferBytes % ChunkBytes != 0)
            throw new ArgumentOutOfRangeException(nameof(PrebufferBytes), "Prebuffer must be a positive multiple of 256 KiB, no larger than capacity.");
    }
}
