using DiscImageStudio.Iso;

namespace DvdImageSolver;

internal sealed record HybridIsoWriteSummary(
    string Mode,
    uint VolumeSectors,
    int LogicalSectorBytes,
    string SourceDirectory,
    int FileCount,
    int DirectoryCount,
    long FileBytes,
    uint DataEndLbaExclusive,
    bool SparseFile,
    bool Iso9660,
    bool Joliet);

/// <summary>Compatibility facade for the media-independent ISO9660/Joliet writer.</summary>
public static class HybridIsoImageWriter
{
    public sealed class LayoutPlan
    {
        internal LayoutPlan(Iso9660ImageWriter.LayoutPlan layout) => Layout = layout;
        internal Iso9660ImageWriter.LayoutPlan Layout { get; }
        public string SourceDirectory => Layout.SourceDirectory;
        public uint VolumeSectors => Layout.VolumeSectors;
        public int FileCount => Layout.FileCount;
        public int DirectoryCount => Layout.DirectoryCount;
        public long FileBytes => Layout.FileBytes;
        public uint DataEndLbaExclusive => Layout.DataEndLbaExclusive;
    }

    public static LayoutPlan Plan(string sourceDirectory, uint volumeSectors, string volumeLabel)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectory);
        return new(Iso9660ImageWriter.Plan(sourceDirectory, volumeSectors, volumeLabel));
    }

    public static void WriteSequentialPrefix(LayoutPlan plan, Stream output, uint endLbaExclusive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Iso9660ImageWriter.WriteSequentialPrefix(plan.Layout, output, endLbaExclusive, cancellationToken);
    }

    internal static HybridIsoWriteSummary Create(string sourceDirectory, string outputPath,
        uint volumeSectors, string volumeLabel)
    {
        string sourceFullPath = Path.GetFullPath(sourceDirectory);
        string outputFullPath = Path.GetFullPath(outputPath);
        if (Iso9660ImageWriter.IsWithinDirectory(outputFullPath, sourceFullPath))
            throw new ArgumentException("--iso-output must be outside --data-dir so the generated image cannot include itself.");
        LayoutPlan plan = Plan(sourceFullPath, volumeSectors, volumeLabel);
        string outputDirectory = Path.GetDirectoryName(outputFullPath)
            ?? throw new ArgumentException("ISO output path has no parent directory.", nameof(outputPath));
        Directory.CreateDirectory(outputDirectory);
        string temporaryPath = Path.Combine(outputDirectory, $".{Path.GetFileName(outputFullPath)}.{Guid.NewGuid():N}.tmp");
        bool sparse = false;
        try
        {
            using (FileStream output = new(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, bufferSize: 128 * 1024, FileOptions.SequentialScan))
            {
                sparse = IsoImageWriter.TryMarkSparse(output.SafeFileHandle);
                output.SetLength(checked((long)volumeSectors * IsoImageWriter.LogicalSectorBytes));
                WriteSequentialPrefix(plan, output, plan.DataEndLbaExclusive);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, outputFullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        return new("hybrid-iso9660-joliet", volumeSectors, IsoImageWriter.LogicalSectorBytes,
            sourceFullPath, plan.FileCount, plan.DirectoryCount, plan.FileBytes,
            plan.DataEndLbaExclusive, sparse, Iso9660: true, Joliet: true);
    }
}
