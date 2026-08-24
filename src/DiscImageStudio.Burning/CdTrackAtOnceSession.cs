using System.Runtime.InteropServices.ComTypes;

namespace DiscImageStudio.Burning;

internal interface ICdTrackAtOnceSession
{
    bool DoNotFinalizeMedia { set; }

    int NumberOfExistingTracks { get; }

    long FreeSectorsOnMedia { get; }

    void PrepareMedia();

    void AddAudioTrack(IStream content);

    void CancelAddTrack();

    void ReleaseMedia();
}

internal sealed class DynamicCdTrackAtOnceSession(object format) : ICdTrackAtOnceSession
{
    private readonly dynamic _format = format;

    public bool DoNotFinalizeMedia
    {
        set => _format.DoNotFinalizeMedia = value;
    }

    public int NumberOfExistingTracks => Convert.ToInt32(_format.NumberOfExistingTracks);

    public long FreeSectorsOnMedia => Convert.ToInt64(_format.FreeSectorsOnMedia);

    public void PrepareMedia() => _format.PrepareMedia();

    public void AddAudioTrack(IStream content) => _format.AddAudioTrack(content);

    public void CancelAddTrack() => _format.CancelAddTrack();

    public void ReleaseMedia() => _format.ReleaseMedia();
}
