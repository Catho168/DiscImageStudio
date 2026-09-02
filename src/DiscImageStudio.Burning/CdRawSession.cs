using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DiscImageStudio.Burning;

internal interface ICdRawSession
{
    bool MediaPhysicallyBlank { get; }

    long LastPossibleStartOfLeadout { get; }

    IReadOnlyList<int> SupportedSectorTypes { get; }

    int CurrentWriteSpeed { get; }

    bool CurrentRotationTypeIsPureCav { get; }

    void PrepareMedia();

    void SetRequestedSectorType(int sectorType);

    void SetWriteSpeed(int sectorsPerSecond, bool rotationTypeIsPureCav);

    void WriteMedia(IStream content);

    void CancelWrite();

    void ReleaseMedia();
}

internal interface ICdRawImageSession : IDisposable
{
    long StartOfLeadout { get; }

    IStream CreateAudioImage(IStream audioContent);
}

internal sealed class DynamicCdRawSession(object format) : ICdRawSession
{
    private readonly dynamic _format = format;

    public bool MediaPhysicallyBlank => Convert.ToBoolean(_format.MediaPhysicallyBlank);

    public long LastPossibleStartOfLeadout =>
        Convert.ToInt64(_format.LastPossibleStartOfLeadout);

    public IReadOnlyList<int> SupportedSectorTypes
    {
        get
        {
            if (_format.SupportedSectorTypes is not Array values)
            {
                return [];
            }

            return values.Cast<object>().Select(Convert.ToInt32).ToArray();
        }
    }

    public int CurrentWriteSpeed => Convert.ToInt32(_format.CurrentWriteSpeed);

    public bool CurrentRotationTypeIsPureCav =>
        Convert.ToBoolean(_format.CurrentRotationTypeIsPureCAV);

    public void PrepareMedia() => _format.PrepareMedia();

    public void SetRequestedSectorType(int sectorType) =>
        _format.RequestedSectorType = sectorType;

    public void SetWriteSpeed(int sectorsPerSecond, bool rotationTypeIsPureCav) =>
        _format.SetWriteSpeed(sectorsPerSecond, rotationTypeIsPureCav);

    public void WriteMedia(IStream content) => _format.WriteMedia(content);

    public void CancelWrite() => _format.CancelWrite();

    public void ReleaseMedia() => _format.ReleaseMedia();
}

internal sealed class DynamicCdRawImageSession : ICdRawImageSession
{
    private const int AudioSectorType = 0;
    private const int SubcodeIsCooked = 2;

    private readonly dynamic _creator;
    private object? _resultImage;
    private bool _disposed;

    internal DynamicCdRawImageSession()
    {
        Type type = Type.GetTypeFromProgID(
            "IMAPI2.MsftRawCDImageCreator",
            throwOnError: true)!;
        _creator = Activator.CreateInstance(type)!;
        _creator.ResultingImageType = SubcodeIsCooked;
    }

    public long StartOfLeadout => Convert.ToInt64(_creator.StartOfLeadout);

    public IStream CreateAudioImage(IStream audioContent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(audioContent);
        _creator.AddTrack(AudioSectorType, audioContent);
        _resultImage = _creator.CreateResultImage();
        return (IStream)_resultImage;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_resultImage is not null && Marshal.IsComObject(_resultImage))
        {
            Marshal.FinalReleaseComObject(_resultImage);
        }

        if (Marshal.IsComObject(_creator))
        {
            Marshal.FinalReleaseComObject(_creator);
        }
    }
}
