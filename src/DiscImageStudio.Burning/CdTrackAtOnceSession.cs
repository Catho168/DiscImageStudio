using System.Runtime.InteropServices.ComTypes;

namespace DiscImageStudio.Burning;

internal interface ICdTrackAtOnceSession
{
    int NumberOfExistingTracks { get; }

    long FreeSectorsOnMedia { get; }

    int CurrentWriteSpeed { get; }

    bool CurrentRotationTypeIsPureCav { get; }

    void PrepareMedia();

    void SetWriteSpeed(int sectorsPerSecond, bool rotationTypeIsPureCav);

    void AddAudioTrack(IStream content);

    void CancelAddTrack();

    void ReleaseMedia();
}

internal sealed class DynamicCdTrackAtOnceSession(object format) : ICdTrackAtOnceSession
{
    private readonly dynamic _format = format;

    public int NumberOfExistingTracks => Convert.ToInt32(_format.NumberOfExistingTracks);

    public long FreeSectorsOnMedia => Convert.ToInt64(_format.FreeSectorsOnMedia);

    public int CurrentWriteSpeed => Convert.ToInt32(_format.CurrentWriteSpeed);

    public bool CurrentRotationTypeIsPureCav =>
        Convert.ToBoolean(_format.CurrentRotationTypeIsPureCAV);

    public void PrepareMedia() => _format.PrepareMedia();

    public void SetWriteSpeed(int sectorsPerSecond, bool rotationTypeIsPureCav)
        => _format.SetWriteSpeed(sectorsPerSecond, rotationTypeIsPureCav);

    public void AddAudioTrack(IStream content) => _format.AddAudioTrack(content);

    public void CancelAddTrack() => _format.CancelAddTrack();

    public void ReleaseMedia() => _format.ReleaseMedia();
}
