namespace DiscImageStudio.Burning;

public enum OpticalBurnMediaKind
{
    CdAudio,
    DvdData,
}

public sealed record OpticalBurnDevice(
    string Id,
    string DisplayName,
    IReadOnlyList<string> VolumePaths);

public sealed record OpticalWriteSpeed(
    int SectorsPerSecond,
    bool RotationTypeIsPureCav)
{
    private const double DvdBytesPerSecondAtOneX = 1_385_000.0;
    private const double CdSectorsPerSecondAtOneX = 75.0;

    public double GetMultiplier(OpticalBurnMediaKind mediaKind)
        => mediaKind == OpticalBurnMediaKind.CdAudio
            ? SectorsPerSecond / CdSectorsPerSecondAtOneX
            : SectorsPerSecond * 2048.0 / DvdBytesPerSecondAtOneX;

    public double GetMegabytesPerSecond(OpticalBurnMediaKind mediaKind)
    {
        int sectorBytes = mediaKind == OpticalBurnMediaKind.CdAudio ? 2352 : 2048;
        return SectorsPerSecond * sectorBytes / 1_000_000.0;
    }

    internal void Validate()
    {
        if (SectorsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SectorsPerSecond),
                "Write speed must be greater than zero sectors per second.");
        }
    }
}

public sealed record OpticalBurnProgress(
    string Stage,
    string Message,
    long CompletedBytes,
    long TotalBytes)
{
    public double Fraction => TotalBytes == 0 ? 0 : (double)CompletedBytes / TotalBytes;
}

/// <summary>
/// CD producers supply headerless 44.1 kHz, 16-bit stereo little-endian PCM,
/// as required by IMAPI_CD_SECTOR_AUDIO. ContentLength excludes any WAV header.
/// DVD producers supply complete 2048-byte data sectors.
/// </summary>
public sealed record OpticalBurnRequest(
    string DeviceId,
    OpticalBurnMediaKind MediaKind,
    long ContentLength,
    Action<Stream, CancellationToken> ProduceContent,
    OpticalWriteSpeed? WriteSpeed = null)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DeviceId);
        ArgumentNullException.ThrowIfNull(ProduceContent);
        int sectorBytes = MediaKind == OpticalBurnMediaKind.CdAudio ? 2352 : 2048;
        if (ContentLength <= 0 || ContentLength % sectorBytes != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ContentLength),
                $"Stream length must be a positive multiple of {sectorBytes} bytes.");
        }

        WriteSpeed?.Validate();
    }
}

public sealed record OpticalBurnResult(
    string DeviceId,
    OpticalBurnMediaKind MediaKind,
    long BytesWritten,
    TimeSpan Elapsed,
    OpticalWriteSpeed? ActualWriteSpeed = null);

public interface IOpticalDiscBurner
{
    Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(
        string deviceId,
        OpticalBurnMediaKind mediaKind,
        CancellationToken cancellationToken = default);

    Task<OpticalBurnResult> BurnAsync(
        OpticalBurnRequest request,
        IProgress<OpticalBurnProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
