using System.IO;
using System.Text;
using DiscImageStudio.BluRay;

internal static class BluRayDrawingModeTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"bd-drawing-mode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            TestFullZoneStreaming();
            TestDirectoryMode(directory);
            TestRawFilesAndCancellation(directory);
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("blu-ray-drawing-mode: passed (raw first/last sectors, full streaming parity, optional UDF directories, lengths, cancellation and atomic publication; no optical device)");
    }

    private static void TestFullZoneStreaming()
    {
        foreach (uint sectors in new uint[] { 32, 64, 1024 })
        {
            BluRayDiscParameters disc = new(sectors, InnerRadiusMm: 23.9885, OuterRadiusMm: 58.022875);
            using MemoryStream reference = new();
            BluRayStreamingGenerator.GenerateToStream(reference, new(disc, 0, sectors), new AddressMapping());
            byte[] expected = reference.ToArray();
            foreach (string? source in new string?[] { null, "", " \t\r\n" })
            {
                BluRayIsoPlan plan = BluRayIsoGenerator.PrepareDrawing(disc, source);
                Require(!plan.HasFileSystem && plan.DataDirectory is null && plan.FileCount == 0
                    && plan.DirectoryCount == 0 && plan.FileBytes == 0,
                    "an absent or blank directory produces a plan without a filesystem");
                Require(plan.DrawingStartSector == 0 && plan.DrawingEndSectorExclusive == sectors
                    && plan.DrawingStartRadiusMm == disc.InnerRadiusMm && plan.DrawingEndRadiusMm == disc.OuterRadiusMm,
                    "raw drawing spans the original inner and outer data-zone boundaries");
                Require(plan.TotalSectors == sectors && plan.ContentLength == (long)sectors * 2048,
                    "raw plan retains the complete requested capacity, including one-cluster discs");

                AddressMapping mapping = new();
                List<BluRayStreamingProgress> progress = [];
                using ForwardOnlyStream output = new();
                BluRayStreamingSummary summary = BluRayIsoGenerator.GenerateToStream(output, plan, mapping,
                    new InlineProgress<BluRayStreamingProgress>(progress.Add));
                byte[] actual = output.Bytes;
                Require(summary.StartSector == 0 && summary.SectorCount == sectors
                    && summary.BytesWritten == plan.ContentLength && actual.LongLength == plan.ContentLength,
                    "forward-only raw output and its summary have exactly the planned full length");
                Require(mapping.FirstSector == 0 && mapping.LastSector == sectors - 1 && mapping.Calls == sectors,
                    "the drawing mapping receives every sector, including both ends, exactly once");
                Require(actual.AsSpan(0, 2048).SequenceEqual(expected.AsSpan(0, 2048))
                    && actual.AsSpan(actual.Length - 2048).SequenceEqual(expected.AsSpan(expected.Length - 2048)),
                    "neither first nor last sector is replaced by filesystem metadata");
                Require(actual.AsSpan().SequenceEqual(expected),
                    "raw drawing is byte-for-byte identical to direct full-disc streaming");
                CheckProgress(progress, plan.ContentLength);
            }
        }
    }

    private static void TestDirectoryMode(string directory)
    {
        BluRayDiscParameters disc = new(TotalSectors: 1024);
        DateTimeOffset timestamp = new(2026, 10, 25, 3, 4, 5, TimeSpan.Zero);
        BluRayIsoPlan legacy = BluRayIsoGenerator.Prepare(disc, timestamp: timestamp);
        Require(legacy.HasFileSystem && legacy.DrawingStartSector > 0
            && legacy.DrawingEndSectorExclusive < disc.TotalSectors,
            "explicit Prepare remains UDF-compatible when its directory is omitted");

        string emptyDirectory = Path.Combine(directory, "empty-input");
        string dataDirectory = Path.Combine(directory, "input");
        Directory.CreateDirectory(emptyDirectory);
        Directory.CreateDirectory(Path.Combine(dataDirectory, "资料"));
        byte[] contents = Encoding.UTF8.GetBytes("蓝光文件与绘图共同保存\n");
        File.WriteAllBytes(Path.Combine(dataDirectory, "资料", "说明.txt"), contents);
        // Pin directory metadata as well as the volume timestamp. NTFS enumeration
        // can briefly return a stale parent entry after creating a child file.
        Directory.SetLastWriteTimeUtc(Path.Combine(dataDirectory, "资料"), timestamp.UtcDateTime);
        Directory.SetLastWriteTimeUtc(dataDirectory, timestamp.UtcDateTime);
        Directory.SetLastWriteTimeUtc(emptyDirectory, timestamp.UtcDateTime);
        foreach (string source in new[] { emptyDirectory, dataDirectory })
        {
            BluRayIsoPlan plan = BluRayIsoGenerator.PrepareDrawing(disc, source, "DRAWING_TEST", timestamp);
            Require(plan.HasFileSystem && plan.DataDirectory == Path.GetFullPath(source)
                && plan.DrawingStartSector > 0 && plan.DrawingEndSectorExclusive < disc.TotalSectors,
                "selecting an existing directory, even an empty one, retains UDF prefix and tail");
            using ForwardOnlyStream output = new();
            AddressMapping mapping = new();
            BluRayStreamingSummary summary = BluRayIsoGenerator.GenerateToStream(output, plan, mapping);
            byte[] actual = output.Bytes;
            Require(summary.BytesWritten == plan.ContentLength && actual.LongLength == plan.ContentLength
                && mapping.FirstSector == plan.DrawingStartSector
                && mapping.LastSector == plan.DrawingEndSectorExclusive - 1,
                "directory mode keeps the full output length and original drawing-sector addresses");
            Dictionary<string, byte[]> files = UdfFilesystemTests.Inspect(actual, disc.TotalSectors,
                "DRAWING_TEST", plan.DrawingStartSector, plan.DrawingEndSectorExclusive);
            if (source == emptyDirectory)
                Require(files.Count == 0 && plan.FileCount == 0, "an empty selected folder is a valid empty UDF root");
            else
                Require(files.Count == 1 && files["资料/说明.txt"].AsSpan().SequenceEqual(contents)
                    && plan.FileCount == 1 && plan.FileBytes == contents.Length,
                    "independent UDF traversal recovers the selected nested file without changing its bytes");

            using MemoryStream explicitUdf = new();
            BluRayIsoGenerator.GenerateToStream(explicitUdf,
                BluRayIsoGenerator.Prepare(disc, source, "DRAWING_TEST", timestamp), new AddressMapping());
            Require(actual.AsSpan().SequenceEqual(explicitUdf.ToArray()),
                "directory drawing mode preserves the complete existing UDF output format");
        }
    }

    private static void TestRawFilesAndCancellation(string directory)
    {
        BluRayDiscParameters disc = new(TotalSectors: 64);
        BluRayIsoPlan plan = BluRayIsoGenerator.PrepareDrawing(disc);
        using MemoryStream reference = new();
        BluRayStreamingGenerator.GenerateToStream(reference, new(disc, 0, disc.TotalSectors), new AddressMapping());
        byte[] expected = reference.ToArray();
        string path = Path.Combine(directory, "raw.bin");
        BluRayStreamingSummary result = BluRayIsoGenerator.GenerateFile(path, plan, new AddressMapping());
        Require(result.BytesWritten == plan.ContentLength && File.ReadAllBytes(path).AsSpan().SequenceEqual(expected),
            "raw file generation publishes the same complete bytes as direct streaming");

        using CancellationTokenSource preCancel = new();
        preCancel.Cancel();
        AddressMapping untouchedMapping = new();
        List<BluRayStreamingProgress> untouchedProgress = [];
        using MemoryStream untouched = new();
        Throws<OperationCanceledException>(() => BluRayIsoGenerator.GenerateToStream(untouched, plan,
            untouchedMapping, new InlineProgress<BluRayStreamingProgress>(untouchedProgress.Add), preCancel.Token));
        Require(untouched.Length == 0 && untouchedMapping.Calls == 0 && untouchedProgress.Count == 0,
            "pre-cancellation emits no bytes and does not start mapping or progress");

        using CancellationTokenSource midCancel = new();
        AddressMapping partialMapping = new();
        List<BluRayStreamingProgress> partialProgress = [];
        using ForwardOnlyStream partial = new();
        Throws<OperationCanceledException>(() => BluRayIsoGenerator.GenerateToStream(partial, plan, partialMapping,
            new InlineProgress<BluRayStreamingProgress>(value =>
            {
                partialProgress.Add(value);
                if (value.CompletedBytes > 0) midCancel.Cancel();
            }), midCancel.Token));
        byte[] prefix = partial.Bytes;
        Require(prefix.Length > 0 && prefix.LongLength < plan.ContentLength
            && prefix.Length % BluRayDiscParameters.PayloadBytesPerCluster == 0
            && prefix.AsSpan().SequenceEqual(expected.AsSpan(0, prefix.Length)),
            "mid-stream cancellation stops at a complete cluster with the exact full-zone byte prefix");
        Require(partialProgress[^1].CompletedBytes == prefix.Length
            && partialProgress.All(value => value.TotalBytes == plan.ContentLength)
            && partialMapping.Calls == (uint)prefix.Length / 2048,
            "cancelled progress matches only bytes actually written and no later sectors are mapped");

        byte[] original = [7, 8, 9];
        File.WriteAllBytes(path, original);
        Throws<OperationCanceledException>(() => BluRayIsoGenerator.GenerateFile(path, plan,
            new AddressMapping(), cancellationToken: preCancel.Token));
        Require(File.ReadAllBytes(path).AsSpan().SequenceEqual(original), "pre-cancellation preserves the existing raw file");
        using CancellationTokenSource fileCancel = new();
        Throws<OperationCanceledException>(() => BluRayIsoGenerator.GenerateFile(path, plan, new AddressMapping(),
            new InlineProgress<BluRayStreamingProgress>(value =>
            {
                if (value.CompletedBytes > 0) fileCancel.Cancel();
            }), fileCancel.Token));
        Require(File.ReadAllBytes(path).AsSpan().SequenceEqual(original), "mid-file cancellation preserves the existing raw file");
        Throws<IOException>(() => BluRayIsoGenerator.GenerateFile(path, plan, new FailingMapping()));
        Require(File.ReadAllBytes(path).AsSpan().SequenceEqual(original), "a mapping failure after partial output preserves the existing raw file");
        Require(!Directory.EnumerateFiles(directory).Any(file => file.EndsWith(".partial", StringComparison.Ordinal)),
            "raw cancellation and failure clean all unpublished temporary files");
    }

    private static void CheckProgress(List<BluRayStreamingProgress> updates, long length)
    {
        Require(updates.Count >= 2 && updates[0].CompletedBytes == 0 && updates[^1].CompletedBytes == length
            && updates.All(value => value.TotalBytes == length), "raw progress spans the complete planned capacity");
        for (int index = 1; index < updates.Count; index++)
            Require(updates[index].CompletedBytes >= updates[index - 1].CompletedBytes,
                "raw progress remains monotonic");
    }

    private sealed class AddressMapping : IBluRayPayloadMapping
    {
        internal uint FirstSector { get; private set; } = uint.MaxValue;
        internal uint LastSector { get; private set; }
        internal uint Calls { get; private set; }
        public void FillScrambledPayload(BluRaySectorContext context, Span<byte> destination)
        {
            if (Calls != 0) Require(context.Sector == LastSector + 1, "mapping addresses remain sequential");
            FirstSector = Math.Min(FirstSector, context.Sector);
            LastSector = context.Sector;
            Calls++;
            for (int index = 0; index < destination.Length; index++)
                destination[index] = unchecked((byte)(context.Sector * 37 + (context.Sector >> 8) + (uint)index * 13));
        }
    }

    private sealed class FailingMapping : IBluRayPayloadMapping
    {
        private readonly AddressMapping _mapping = new();
        public void FillScrambledPayload(BluRaySectorContext context, Span<byte> destination)
        {
            if (context.Sector >= 32) throw new IOException("Intentional failure after the first cluster.");
            _mapping.FillScrambledPayload(context, destination);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    { public void Report(T value) => report(value); }

    private sealed class ForwardOnlyStream : Stream
    {
        private readonly MemoryStream _bytes = new();
        internal byte[] Bytes => _bytes.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _bytes.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _bytes.Write(buffer);
        protected override void Dispose(bool disposing)
        {
            if (disposing) _bytes.Dispose();
            base.Dispose(disposing);
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Blu-ray drawing mode: " + message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
}
