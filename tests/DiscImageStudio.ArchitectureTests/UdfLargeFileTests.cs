using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DiscImageStudio.BluRay;
using Microsoft.Win32.SafeHandles;

/// <summary>Exercises a real source larger than 4 GiB without creating an ISO on disk.
/// The source is marked sparse before its length is extended; the destination only
/// checks bytes and counts them. Memory use and physical disk use remain bounded.</summary>
internal static class UdfLargeFileTests
{
    private const int SectorSize = 2048;
    private const long SourceLength = (long)uint.MaxValue + 131_074;
    private const int CaptureLimit = 1024 * 1024;
    private const uint MaximumRecordedExtent = 0x3ffff800;

    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bd-udf-large-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "larger-than-4-GiB.bin");
        try
        {
            List<Sentinel> sentinels = CreateSentinels();
            CreateSparseSource(source, sentinels);

            // Planning with the ordinary capacity does not generate any bytes. The
            // actual test leaves just one drawing cluster plus UDF's reserved tail.
            BluRayIsoPlan preliminary = BluRayIsoGenerator.Prepare(new(), directory, "LARGE_FILE_TEST");
            uint totalSectors = checked((preliminary.DrawingStartSector + 32 + 257 + 31) / 32 * 32);
            BluRayIsoPlan plan = BluRayIsoGenerator.Prepare(new(TotalSectors: totalSectors), directory, "LARGE_FILE_TEST");
            Require(plan.FileBytes == SourceLength && plan.FileCount == 1,
                "the plan retains a source size above UInt32.MaxValue");
            Require(plan.DrawingEndSectorExclusive - plan.DrawingStartSector == 32,
                "the large-source test encodes only one drawing cluster");

            using CancellationTokenSource preCancelled = new();
            preCancelled.Cancel();
            using CountingVerifier untouched = new(0, 0, []);
            Throws<OperationCanceledException>(() => BluRayIsoGenerator.GenerateToStream(untouched, plan,
                new AddressMapping(), cancellationToken: preCancelled.Token));
            Require(untouched.BytesWritten == 0, "pre-cancellation does not write to the destination");

            using CancellationTokenSource cancel = new();
            using PrefixCapture prefix = new(cancel);
            Throws<OperationCanceledException>(() => BluRayIsoGenerator.GenerateToStream(prefix, plan,
                new AddressMapping(), cancellationToken: cancel.Token));
            Require(prefix.BytesWritten >= CaptureLimit && prefix.BytesWritten < CaptureLimit * 2L,
                "mid-file cancellation stops promptly without retaining or writing the large payload");

            FileEntry file = ReadFile(prefix.Bytes, "larger-than-4-GiB.bin");
            Require(file.InformationLength == SourceLength, "UDF file entry stores the complete 64-bit information length");
            Require(file.Extents.Count == 5 && file.Extents.Sum(e => (long)e.Length) == SourceLength,
                "the source is represented by multiple recorded allocation descriptors");
            Require(file.Extents.All(e => e.Length > 0 && e.Length <= MaximumRecordedExtent),
                "each recorded allocation extent fits the UDF 30-bit length field");
            Require(file.Extents[^1].Length == SourceLength % MaximumRecordedExtent
                && file.Extents[^1].Length % SectorSize != 0,
                "the final allocation descriptor retains the unaligned last fragment without truncation or padding");
            for (int i = 0; i < file.Extents.Count - 1; i++)
            {
                Extent current = file.Extents[i];
                Extent next = file.Extents[i + 1];
                Require(current.Length % SectorSize == 0 && next.Offset == current.Offset + current.Length,
                    "all intermediate allocation extents are aligned and contiguous");
            }
            Require(file.RecordedBlocks == (ulong)((SourceLength + SectorSize - 1) / SectorSize),
                "logical blocks recorded uses the complete file length");

            using CountingVerifier output = new(file.Extents[0].Offset, SourceLength, sentinels);
            AddressMapping mapping = new();
            BluRayStreamingSummary summary = BluRayIsoGenerator.GenerateToStream(output, plan, mapping);
            Require(output.BytesWritten == plan.ContentLength && summary.BytesWritten == plan.ContentLength,
                "the forward-only producer writes the complete declared logical volume");
            Require(output.VerifiedPayloadBytes == SourceLength,
                "every source byte, including sparse holes and sentinels across 1 GiB and 4 GiB, survives streaming");
            Require(mapping.FirstSector == plan.DrawingStartSector && mapping.LastSector + 1 == plan.DrawingEndSectorExclusive,
                "drawing covers only the planned range before UDF backup metadata");
            Require(GetAllocatedBytes(source) < CaptureLimit,
                "the large-file regression leaves less than 1 MiB physically allocated for its sparse source");
            Require(Directory.EnumerateFiles(directory).Count() == 1,
                "the large-file regression creates no ISO output file");
            Console.WriteLine("blu-ray-udf-large-file: passed (>4 GiB sparse source; exact sequential payload; 1 GiB allocation splits; bounded memory; no ISO file)");
        }
        finally
        {
            File.Delete(source);
            Directory.Delete(directory);
        }
    }

    private static List<Sentinel> CreateSentinels()
    {
        long[] positions = [0, MaximumRecordedExtent - 32L, (1L << 30) - 32, (1L << 32) - 32, SourceLength - 64];
        return positions.Select((position, index) => new Sentinel(position,
            Enumerable.Range(0, 64).Select(i => (byte)(1 + (i + index * 41) % 255)).ToArray())).ToList();
    }

    private static void CreateSparseSource(string path, List<Sentinel> sentinels)
    {
        using FileStream source = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        // FSCTL_SET_SPARSE must succeed before SetLength: a failed sparse operation
        // must never turn this test into a real four-gigabyte allocation.
        if (!DeviceIoControl(source.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0,
            IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The large-file test requires sparse-file support; file length was not extended.");
        Require((File.GetAttributes(path) & FileAttributes.SparseFile) != 0,
            "the source must report the sparse attribute before its length is extended");
        source.SetLength(SourceLength);
        source.Flush(flushToDisk: true);
        Require(GetAllocatedBytes(path) < CaptureLimit, "extending the sparse source does not allocate its logical length");
        foreach (Sentinel sentinel in sentinels)
        {
            source.Position = sentinel.Offset;
            source.Write(sentinel.Bytes);
        }
        source.Flush(flushToDisk: true);
        Require(GetAllocatedBytes(path) < CaptureLimit, "sentinels consume only a small number of sparse allocation units");
    }

    // Read the standard UDF structures independently of the layout planner. Only
    // the first MiB was captured; source data is never loaded into a byte array.
    private static FileEntry ReadFile(byte[] prefix, string filename)
    {
        ReadOnlySpan<byte> anchor = Sector(prefix, 256);
        Require(U16(anchor, 0) == 2, "the sector-256 anchor is present");
        uint sequenceStart = U32(anchor, 20);
        uint sequenceSectors = (U32(anchor, 16) + SectorSize - 1) / SectorSize;
        uint partitionStart = uint.MaxValue;
        uint fileSetBlock = uint.MaxValue;
        for (uint i = 0; i < sequenceSectors; i++)
        {
            ReadOnlySpan<byte> descriptor = Sector(prefix, sequenceStart + i);
            if (U16(descriptor, 0) == 5) partitionStart = U32(descriptor, 188);
            if (U16(descriptor, 0) == 6)
            {
                Require(U32(descriptor, 212) == SectorSize, "the UDF logical block size is 2048 bytes");
                fileSetBlock = U32(descriptor, 252);
            }
        }
        Require(partitionStart != uint.MaxValue && fileSetBlock != uint.MaxValue,
            "the anchor sequence identifies the partition and file-set descriptor");
        ReadOnlySpan<byte> fileSet = Sector(prefix, partitionStart + fileSetBlock);
        Require(U16(fileSet, 0) == 256, "the file-set descriptor is present");
        FileEntry root = ReadEntry(prefix, partitionStart, U32(fileSet, 404));
        Require(root.Extents.Count == 1, "the small root directory has one recorded extent");
        Extent directory = root.Extents[0];
        int cursor = checked((int)directory.Offset);
        int end = checked(cursor + (int)root.InformationLength);
        while (cursor < end)
        {
            ReadOnlySpan<byte> fid = prefix.AsSpan(cursor);
            Require(U16(fid, 0) == 257, "the root contains standard file identifier descriptors");
            int nameLength = fid[19];
            int implementationLength = U16(fid, 36);
            if ((fid[18] & 8) == 0 && nameLength > 0)
            {
                ReadOnlySpan<byte> encodedName = fid.Slice(38 + implementationLength, nameLength);
                string name = encodedName[0] switch
                {
                    8 => Encoding.Latin1.GetString(encodedName[1..]),
                    16 => Encoding.BigEndianUnicode.GetString(encodedName[1..]),
                    _ => throw new InvalidOperationException("Unexpected UDF filename compression identifier.")
                };
                if (name == filename) return ReadEntry(prefix, partitionStart, U32(fid, 24));
            }
            cursor += (38 + implementationLength + nameLength + 3) / 4 * 4;
        }
        throw new InvalidOperationException("Large UDF source was not found in the root directory.");
    }

    private static FileEntry ReadEntry(byte[] prefix, uint partitionStart, uint relativeSector)
    {
        ReadOnlySpan<byte> entry = Sector(prefix, checked(partitionStart + relativeSector));
        Require(U16(entry, 0) == 261 && (U16(entry, 34) & 7) == 0,
            "a standard file entry uses short allocation descriptors");
        long informationLength = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(56, 8)));
        ulong recordedBlocks = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(64, 8));
        int attributesLength = checked((int)U32(entry, 168));
        int descriptorsLength = checked((int)U32(entry, 172));
        Require(descriptorsLength % 8 == 0, "short allocation descriptor table has the correct element size");
        List<Extent> extents = [];
        for (int offset = 176 + attributesLength; offset < 176 + attributesLength + descriptorsLength; offset += 8)
        {
            uint length = U32(entry, offset);
            Require((length & 0xc0000000) == 0, "file allocations are recorded and allocated");
            long absoluteOffset = checked(((long)partitionStart + U32(entry, offset + 4)) * SectorSize);
            extents.Add(new(absoluteOffset, length));
        }
        return new(informationLength, recordedBlocks, extents);
    }

    private static ReadOnlySpan<byte> Sector(byte[] bytes, uint sector) => bytes.AsSpan(checked((int)sector * SectorSize), SectorSize);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));
    private sealed record Sentinel(long Offset, byte[] Bytes);
    private sealed record Extent(long Offset, uint Length);
    private sealed record FileEntry(long InformationLength, ulong RecordedBlocks, List<Extent> Extents);

    private abstract class WriteOnlyStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public abstract override void Write(ReadOnlySpan<byte> buffer);
    }

    private sealed class PrefixCapture(CancellationTokenSource cancellation) : WriteOnlyStream
    {
        internal byte[] Bytes { get; } = new byte[CaptureLimit];
        internal long BytesWritten { get; private set; }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            int remaining = CaptureLimit - checked((int)BytesWritten);
            buffer[..Math.Min(remaining, buffer.Length)].CopyTo(Bytes.AsSpan((int)BytesWritten));
            BytesWritten += buffer.Length;
            if (BytesWritten >= CaptureLimit) cancellation.Cancel();
        }
    }

    private sealed class CountingVerifier(long fileOffset, long fileLength, List<Sentinel> sentinels) : WriteOnlyStream
    {
        private int _sentinelIndex;
        internal long BytesWritten { get; private set; }
        internal long VerifiedPayloadBytes { get; private set; }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            long start = Math.Max(BytesWritten, fileOffset);
            long end = Math.Min(BytesWritten + buffer.Length, fileOffset + fileLength);
            if (start < end)
            {
                long sourcePosition = start - fileOffset;
                ReadOnlySpan<byte> data = buffer.Slice(checked((int)(start - BytesWritten)), checked((int)(end - start)));
                while (!data.IsEmpty)
                {
                    while (_sentinelIndex < sentinels.Count
                        && sourcePosition >= sentinels[_sentinelIndex].Offset + sentinels[_sentinelIndex].Bytes.Length)
                        _sentinelIndex++;
                    Sentinel? sentinel = _sentinelIndex < sentinels.Count ? sentinels[_sentinelIndex] : null;
                    int count;
                    if (sentinel is not null && sourcePosition >= sentinel.Offset)
                    {
                        int markerOffset = checked((int)(sourcePosition - sentinel.Offset));
                        count = Math.Min(data.Length, sentinel.Bytes.Length - markerOffset);
                        Require(data[..count].SequenceEqual(sentinel.Bytes.AsSpan(markerOffset, count)),
                            $"the large file sentinel is preserved at source byte {sourcePosition}");
                    }
                    else
                    {
                        count = checked((int)Math.Min(data.Length, (sentinel?.Offset ?? fileLength) - sourcePosition));
                        Require(data[..count].IndexOfAnyExcept((byte)0) < 0,
                            $"the sparse zero region is preserved at source byte {sourcePosition}");
                    }
                    sourcePosition += count;
                    VerifiedPayloadBytes += count;
                    data = data[count..];
                }
            }
            BytesWritten += buffer.Length;
        }
    }

    private sealed class AddressMapping : IBluRayPayloadMapping
    {
        internal uint FirstSector { get; private set; } = uint.MaxValue;
        internal uint LastSector { get; private set; }
        public void FillScrambledPayload(BluRaySectorContext context, Span<byte> destination)
        {
            FirstSector = Math.Min(FirstSector, context.Sector);
            LastSector = context.Sector;
            destination.Fill(0xAA);
        }
    }

    private static long GetAllocatedBytes(string path)
    {
        Marshal.SetLastPInvokeError(0);
        uint low = GetCompressedFileSize(path, out uint high);
        int error = Marshal.GetLastWin32Error();
        if (low == uint.MaxValue && error != 0) throw new Win32Exception(error);
        return checked((long)(((ulong)high << 32) | low));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        IntPtr inputBuffer, uint inputBufferSize, IntPtr outputBuffer, uint outputBufferSize,
        out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSize(string filename, out uint high);

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Large UDF file: " + message); }

    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
}
