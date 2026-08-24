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

public sealed record OpticalBurnProgress(
    string Stage,
    string Message,
    long CompletedBytes,
    long TotalBytes)
{
    public double Fraction => TotalBytes == 0 ? 0 : (double)CompletedBytes / TotalBytes;
}

public sealed record OpticalBurnRequest(
    string DeviceId,
    OpticalBurnMediaKind MediaKind,
    long ContentLength,
    Action<Stream, CancellationToken> ProduceContent)
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
    }
}

public sealed record OpticalBurnResult(
    string DeviceId,
    OpticalBurnMediaKind MediaKind,
    long BytesWritten,
    TimeSpan Elapsed);

public interface IOpticalDiscBurner
{
    Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default);

    Task<OpticalBurnResult> BurnAsync(
        OpticalBurnRequest request,
        IProgress<OpticalBurnProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
