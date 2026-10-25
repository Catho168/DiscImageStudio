namespace DiscImageStudio.BluRay;

/// <summary>Calibration pattern confined to one drawing range; every other payload byte
/// stays zero so the burn still covers the whole disc address space.</summary>
public sealed class BluRayPatternRangeMapping : IBluRayPayloadMapping
{
    private readonly BluRayImagePayloadMapping _image;
    private readonly uint _startSector;
    private readonly uint _endSectorExclusive;

    public BluRayPatternRangeMapping(
        BluRayDiscParameters disc,
        Func<double, double, byte> sampleLuminance,
        uint startSector,
        uint endSectorExclusive,
        bool invertMapping,
        bool allowParallelSampling = true)
    {
        ArgumentNullException.ThrowIfNull(disc);
        ArgumentNullException.ThrowIfNull(sampleLuminance);
        disc.Validate();
        if (startSector >= endSectorExclusive || endSectorExclusive > disc.TotalSectors
            || startSector % BluRayDiscParameters.SectorsPerCluster != 0
            || endSectorExclusive % BluRayDiscParameters.SectorsPerCluster != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endSectorExclusive),
                "The calibration drawing range must be 32-sector aligned and lie inside the disc.");
        }

        _image = new BluRayImagePayloadMapping(
            disc, sampleLuminance, new BluRayRunLengthMapping(invertMapping), allowParallelSampling);
        _startSector = startSector;
        _endSectorExclusive = endSectorExclusive;
    }

    public void FillScrambledPayload(BluRaySectorContext context, Span<byte> destination)
    {
        if (context.Sector < _startSector || context.Sector >= _endSectorExclusive)
        {
            destination.Clear();
            return;
        }

        _image.FillScrambledPayload(context, destination);
    }
}
