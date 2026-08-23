namespace DvdImageSolver;

internal sealed class RawIsoImageWriter : IDisposable
{
    private readonly FileStream _output;
    private bool _disposed;

    internal RawIsoImageWriter(string path, uint volumeSectors, bool preserveExisting = false)
    {
        if (volumeSectors == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(volumeSectors));
        }

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        if (preserveExisting)
        {
            long expectedLength = checked((long)volumeSectors * IsoImageWriter.LogicalSectorBytes);
            if (!File.Exists(fullPath) || new FileInfo(fullPath).Length != expectedLength)
            {
                throw new ArgumentException(
                    "The existing hybrid ISO size does not match the reported disc size.",
                    nameof(path));
            }
        }

        _output = new FileStream(
            fullPath,
            preserveExisting ? FileMode.Open : FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.RandomAccess);
        SparseFile = IsoImageWriter.TryMarkSparse(_output.SafeFileHandle);
        if (!preserveExisting)
        {
            _output.SetLength(checked((long)volumeSectors * IsoImageWriter.LogicalSectorBytes));
        }
        VolumeSectors = volumeSectors;
    }

    internal uint VolumeSectors { get; }

    internal bool SparseFile { get; }

    internal void WriteBlock(uint lba, ReadOnlySpan<byte> payloads)
    {
        if (payloads.Length == 0 || payloads.Length % IsoImageWriter.LogicalSectorBytes != 0)
        {
            throw new ArgumentException("Raw ISO payload must contain complete 2048-byte sectors.", nameof(payloads));
        }

        ulong sectorCount = (uint)(payloads.Length / IsoImageWriter.LogicalSectorBytes);
        if ((ulong)lba + sectorCount > VolumeSectors)
        {
            throw new ArgumentOutOfRangeException(nameof(lba), "Payload exceeds raw ISO size.");
        }

        _output.Position = checked((long)lba * IsoImageWriter.LogicalSectorBytes);
        _output.Write(payloads);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _output.Flush(flushToDisk: true);
        _output.Dispose();
        _disposed = true;
    }
}
