using System.IO;
using System.Text;
using DiscImageStudio.BluRay;

internal static class BluRayIsoTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bd-iso-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestEmptyAndStreaming(directory);
            TestInnerFiles(directory);
            TestFailures(directory);
            Console.WriteLine("blu-ray-iso: passed (UDF, inner files, full-disc positions, sequential equivalence, atomic failures)");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void TestEmptyAndStreaming(string directory)
    {
        BluRayDiscParameters disc = new(TotalSectors: 1024);
        BluRayIsoPlan plan = BluRayIsoGenerator.Prepare(disc);
        Require(plan.TotalSectors == 1024 && plan.DrawingStartSector >= 288 && plan.DrawingStartSector % 32 == 0 && plan.FileCount == 0,
            "empty ISO reserves one aligned filesystem cluster");
        Require(plan.DrawingStartRadiusMm == new BluRayTrackGeometry(disc).AtSector(plan.DrawingStartSector).RadiusMm,
            "drawing boundary uses complete custom disc geometry");
        AddressMapping mapping = new();
        List<BluRayStreamingProgress> updates = [];
        using ForwardOnlyStream streamed = new();
        BluRayStreamingSummary result = BluRayIsoGenerator.GenerateToStream(streamed, plan, mapping,
            new InlineProgress<BluRayStreamingProgress>(updates.Add));
        Require(result.StartSector == 0 && result.SectorCount == 1024 && result.BytesWritten == 1024 * 2048,
            "ISO summary covers exactly the selected disc capacity");
        Require(mapping.FirstSector == plan.DrawingStartSector && mapping.LastSector == plan.DrawingEndSectorExclusive - 1,
            "drawing mapping keeps full-disc addresses beyond the metadata prefix");
        string path = Path.Combine(directory, "empty.iso");
        BluRayIsoGenerator.GenerateFile(path, plan, new AddressMapping());
        byte[] iso = File.ReadAllBytes(path);
        Require(streamed.Bytes.SequenceEqual(iso), "file and non-seekable stream use the identical ISO bytes");
        Require(UdfFilesystemTests.Inspect(iso, 1024, "BD_IMAGE", plan.DrawingStartSector, plan.DrawingEndSectorExclusive).Count == 0,
            "empty UDF image mounts as a valid empty root");
        Require(updates[0].CompletedBytes == 0 && updates[^1].CompletedBytes == plan.ContentLength
            && updates.All(p => p.TotalBytes == plan.ContentLength), "progress includes metadata, files and drawing");
        for (int i = 1; i < updates.Count; i++)
            Require(updates[i].CompletedBytes >= updates[i - 1].CompletedBytes, "ISO progress is monotonic");
        using MemoryStream raw = new();
        BluRayStreamingGenerator.GenerateToStream(raw, new(disc, plan.DrawingStartSector, plan.DrawingEndSectorExclusive - plan.DrawingStartSector), new AddressMapping());
        Require(raw.ToArray().SequenceEqual(iso.AsSpan((int)plan.DrawingStartSector * 2048, (int)(plan.DrawingEndSectorExclusive - plan.DrawingStartSector) * 2048).ToArray()),
            "filesystem prefix does not shift or rebase drawing scrambling seeds");
        Throws<ArgumentException>(() => BluRayIsoGenerator.Prepare(new(TotalSectors: 32)));

        DateTimeOffset timestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using MemoryStream reproducible = new();
        using MemoryStream restored = new();
        BluRayIsoGenerator.GenerateToStream(reproducible, BluRayIsoGenerator.Prepare(disc, timestamp: timestamp), new AddressMapping());
        BluRayIsoGenerator.GenerateToStream(restored, BluRayIsoGenerator.Prepare(disc, timestamp: timestamp), new AddressMapping());
        Require(reproducible.ToArray().SequenceEqual(restored.ToArray()), "explicit timestamp reproduces the entire ISO across fresh plans");
        UdfFilesystemTests.Inspect(restored.ToArray(), 1024, "BD_IMAGE");
        using CancellationTokenSource suffixCancel = new();
        string preserved = Path.Combine(directory, "tail-cancel.iso");
        File.WriteAllBytes(preserved, [1, 2, 3]);
        Throws<OperationCanceledException>(() => BluRayIsoGenerator.GenerateFile(preserved, plan, new AddressMapping(),
            new InlineProgress<BluRayStreamingProgress>(p =>
            {
                if (p.CompletedBytes >= (long)plan.DrawingEndSectorExclusive * 2048) suffixCancel.Cancel();
            }), suffixCancel.Token));
        Require(File.ReadAllBytes(preserved).SequenceEqual(new byte[] { 1, 2, 3 }),
            "cancellation at the UDF tail preserves the existing output");
    }

    private static void TestInnerFiles(string directory)
    {
        string source = Path.Combine(directory, "inner");
        Directory.CreateDirectory(Path.Combine(source, "资料"));
        byte[] content = Enumerable.Range(0, 80001).Select(i => (byte)(i * 17)).ToArray();
        File.WriteAllBytes(Path.Combine(source, "large.bin"), content);
        byte[] unicodeContent = Encoding.UTF8.GetBytes("蓝光内圈文件\n");
        File.WriteAllBytes(Path.Combine(source, "资料", "说明.txt"), unicodeContent);
        File.WriteAllBytes(Path.Combine(source, "empty.txt"), []);
        BluRayIsoPlan plan = BluRayIsoGenerator.Prepare(new(TotalSectors: 1024), source, "蓝光_TEST");
        Require(plan.FileCount == 3 && plan.DirectoryCount == 2 && plan.FileBytes == content.Length + unicodeContent.Length,
            "plan snapshots inner-file sizes and nested directories");
        Require(plan.DrawingStartSector > BluRayIsoGenerator.Prepare(plan.Disc).DrawingStartSector && plan.DrawingStartRadiusMm > plan.Disc.InnerRadiusMm,
            "files extend inner area and drawing starts on a cluster boundary");
        using MemoryStream output = new();
        BluRayIsoGenerator.GenerateToStream(output, plan, new AddressMapping());
        byte[] iso = output.ToArray();
        Dictionary<string, byte[]> files = UdfFilesystemTests.Inspect(iso, 1024, "蓝光_TEST", plan.DrawingStartSector, plan.DrawingEndSectorExclusive);
        Require(files.Count == 3 && files["large.bin"].SequenceEqual(content)
            && files["资料/说明.txt"].SequenceEqual(unicodeContent) && files["empty.txt"].Length == 0,
            "independent UDF traversal recovers nested Unicode names and exact file data");
        BluRayImagePayloadMapping image = new(plan.Disc, new BluRayGray8Raster(1, 1, [0]), new BluRayRunLengthMapping());
        Require(image.GetPosition(plan.DrawingStartSector, 0).TrackPoint.RadiusMm > plan.Disc.InnerRadiusMm + 10,
            "image sampling preserves complete-disc geometry instead of stretching the outer area");
        string path = Path.Combine(directory, "files.iso");
        BluRayIsoGenerator.GenerateFile(path, plan, new AddressMapping());
        Require(File.ReadAllBytes(path).SequenceEqual(iso), "file and stream match with inner files");
        Throws<ArgumentException>(() => BluRayIsoGenerator.GenerateFile(Path.Combine(source, "large.bin"), plan, new AddressMapping()));
        Require(File.ReadAllBytes(Path.Combine(source, "large.bin")).SequenceEqual(content), "input-directory outputs cannot overwrite input files");

        BluRayDiscModule module = new((_, _) => new(1, 1, [255]));
        string cliPath = Path.Combine(directory, "cli.iso");
        Require(module.ExecuteAsync(new("bd-generate", ["--input", "source.png", "--output", cliPath,
            "--total-sectors", "1024", "--data-dir", source, "--volume-label", "INNER_TEST"]))
            .GetAwaiter().GetResult().Succeeded, "CLI accepts inner directory and label");
        UdfFilesystemTests.Inspect(File.ReadAllBytes(cliPath), 1024, "INNER_TEST");
    }

    private static void TestFailures(string directory)
    {
        string source = Path.Combine(directory, "changing");
        Directory.CreateDirectory(source);
        string input = Path.Combine(source, "input.bin");
        File.WriteAllBytes(input, [1, 2, 3]);
        BluRayIsoPlan plan = BluRayIsoGenerator.Prepare(new(TotalSectors: 1024), source);
        string output = Path.Combine(directory, "preserved.iso");
        byte[] original = [7, 8, 9];
        File.WriteAllBytes(output, original);
        File.WriteAllBytes(input, [1, 2, 3, 4]);
        Throws<IOException>(() => BluRayIsoGenerator.GenerateFile(output, plan, new AddressMapping()));
        Require(File.ReadAllBytes(output).SequenceEqual(original), "changed source failure preserves existing ISO");
        plan = BluRayIsoGenerator.Prepare(new(TotalSectors: 1024), source);
        using CancellationTokenSource cancel = new();
        Throws<OperationCanceledException>(() => BluRayIsoGenerator.GenerateFile(output, plan, new AddressMapping(),
            new InlineProgress<BluRayStreamingProgress>(p => { if (p.CompletedBytes > 0) cancel.Cancel(); }), cancel.Token));
        Require(File.ReadAllBytes(output).SequenceEqual(original), "cancellation during filesystem prefix preserves existing ISO");
        Throws<IOException>(() => BluRayIsoGenerator.GenerateFile(output, plan, new ThrowingMapping()));
        Require(File.ReadAllBytes(output).SequenceEqual(original), "drawing failure preserves existing ISO");
        Require(!Directory.EnumerateFiles(directory).Any(p => p.EndsWith(".partial", StringComparison.Ordinal)), "all temporary ISO files are cleaned");
        File.WriteAllBytes(input, new byte[1024 * 2048]);
        Throws<ArgumentException>(() => BluRayIsoGenerator.Prepare(new(TotalSectors: 1024), source));
        using MemoryStream untouched = new();
        Throws<OperationCanceledException>(() => BluRayIsoGenerator.GenerateToStream(untouched, plan,
            new AddressMapping(), cancellationToken: cancel.Token));
        Require(untouched.Length == 0, "pre-cancel does not touch destination");
    }

    private sealed class AddressMapping : IBluRayPayloadMapping
    {
        public uint FirstSector { get; private set; } = uint.MaxValue;
        public uint LastSector { get; private set; }
        public void FillScrambledPayload(BluRaySectorContext context, Span<byte> destination)
        {
            FirstSector = Math.Min(FirstSector, context.Sector);
            LastSector = context.Sector;
            destination.Fill((byte)context.Sector);
        }
    }
    private sealed class ThrowingMapping : IBluRayPayloadMapping
    { public void FillScrambledPayload(BluRaySectorContext context, Span<byte> destination) => throw new IOException("test mapping failure"); }
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    { public void Report(T value) => action(value); }
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
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Blu-ray ISO: " + message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
}
