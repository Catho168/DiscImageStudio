using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using TextEncoding = System.Text.Encoding;

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

internal static class HybridIsoImageWriter
{
    private const uint PrimaryVolumeDescriptorLba = 16;
    private const uint SupplementaryVolumeDescriptorLba = 17;
    private const uint TerminatorLba = 18;
    private const uint FirstMetadataLba = 19;
    private const int MaximumPrimaryIdentifierCharacters = 31;
    private const int MaximumJolietIdentifierCharacters = 60;

    internal static HybridIsoWriteSummary Create(
        string sourceDirectory,
        string outputPath,
        uint volumeSectors,
        string volumeLabel)
    {
        if (volumeSectors <= FirstMetadataLba)
        {
            throw new ArgumentOutOfRangeException(
                nameof(volumeSectors),
                "Hybrid ISO volume is too small for filesystem metadata.");
        }

        string sourceFullPath = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(sourceFullPath))
        {
            throw new DirectoryNotFoundException($"Data directory does not exist: {sourceFullPath}");
        }

        string outputFullPath = Path.GetFullPath(outputPath);
        if (IsWithinDirectory(outputFullPath, sourceFullPath))
        {
            throw new ArgumentException(
                "--iso-output must be outside --data-dir so the generated image cannot include itself.");
        }

        IsoNode root = ReadTree(sourceFullPath);
        List<IsoNode> directories = AssignDirectoryNumbers(root);
        AssignIdentifiers(directories);
        Layout layout = BuildLayout(directories, volumeSectors);
        string normalizedPrimaryLabel = NormalizePrimaryIdentifier(volumeLabel, 32, "DVD_IMAGE");
        string normalizedJolietLabel = NormalizeJolietIdentifier(volumeLabel, 16, "DVD_IMAGE");

        string? outputDirectory = Path.GetDirectoryName(outputFullPath);
        if (outputDirectory is null)
        {
            throw new ArgumentException("ISO output path has no parent directory.", nameof(outputPath));
        }

        Directory.CreateDirectory(outputDirectory);
        string temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputFullPath)}.{Guid.NewGuid():N}.tmp");
        bool sparse = false;
        try
        {
            using (FileStream output = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.RandomAccess))
            {
                sparse = IsoImageWriter.TryMarkSparse(output.SafeFileHandle);
                output.SetLength(checked((long)volumeSectors * IsoImageWriter.LogicalSectorBytes));
                DateTimeOffset timestamp = DateTimeOffset.UtcNow;

                WriteSector(output, PrimaryVolumeDescriptorLba, BuildVolumeDescriptor(
                    type: 1,
                    volumeSectors,
                    normalizedPrimaryLabel,
                    layout.PrimaryPathTableBytes,
                    layout.PrimaryLittlePathTableLba,
                    layout.PrimaryBigPathTableLba,
                    root.PrimaryDirectoryLba,
                    root.PrimaryDirectoryBytes,
                    joliet: false,
                    timestamp));
                WriteSector(output, SupplementaryVolumeDescriptorLba, BuildVolumeDescriptor(
                    type: 2,
                    volumeSectors,
                    normalizedJolietLabel,
                    layout.JolietPathTableBytes,
                    layout.JolietLittlePathTableLba,
                    layout.JolietBigPathTableLba,
                    root.JolietDirectoryLba,
                    root.JolietDirectoryBytes,
                    joliet: true,
                    timestamp));
                WriteSector(output, TerminatorLba, BuildTerminator());

                WriteExtent(
                    output,
                    layout.PrimaryLittlePathTableLba,
                    BuildPathTable(directories, littleEndian: true, joliet: false));
                WriteExtent(
                    output,
                    layout.PrimaryBigPathTableLba,
                    BuildPathTable(directories, littleEndian: false, joliet: false));
                WriteExtent(
                    output,
                    layout.JolietLittlePathTableLba,
                    BuildPathTable(directories, littleEndian: true, joliet: true));
                WriteExtent(
                    output,
                    layout.JolietBigPathTableLba,
                    BuildPathTable(directories, littleEndian: false, joliet: true));

                foreach (IsoNode directory in directories)
                {
                    WriteExtent(
                        output,
                        directory.PrimaryDirectoryLba,
                        BuildDirectoryExtent(directory, joliet: false));
                    WriteExtent(
                        output,
                        directory.JolietDirectoryLba,
                        BuildDirectoryExtent(directory, joliet: true));
                }

                foreach (IsoNode file in EnumerateFiles(root))
                {
                    WriteFile(output, file);
                }

                output.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, outputFullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        List<IsoNode> files = EnumerateFiles(root).ToList();
        return new HybridIsoWriteSummary(
            "hybrid-iso9660-joliet",
            volumeSectors,
            IsoImageWriter.LogicalSectorBytes,
            sourceFullPath,
            files.Count,
            directories.Count,
            files.Sum(file => file.DataLength),
            layout.DataEndLbaExclusive,
            sparse,
            Iso9660: true,
            Joliet: true);
    }

    private static IsoNode ReadTree(string sourceFullPath)
    {
        IsoNode root = new(sourceFullPath, string.Empty, isDirectory: true, parent: null)
        {
            Timestamp = Directory.GetLastWriteTimeUtc(sourceFullPath),
        };
        PopulateDirectory(root);
        return root;
    }

    private static void PopulateDirectory(IsoNode directory)
    {
        DirectoryInfo source = new(directory.SourcePath);
        foreach (FileSystemInfo entry in source.EnumerateFileSystemInfos()
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Name, StringComparer.Ordinal))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    $"Reparse points are not supported in --data-dir: {entry.FullName}");
            }

            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                IsoNode child = new(entry.FullName, entry.Name, isDirectory: true, directory)
                {
                    Timestamp = entry.LastWriteTimeUtc,
                };
                directory.Children.Add(child);
                PopulateDirectory(child);
            }
            else
            {
                FileInfo file = (FileInfo)entry;
                if ((ulong)file.Length > uint.MaxValue)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(directory),
                        $"ISO9660 files larger than 4 GiB are not supported: {file.FullName}");
                }

                directory.Children.Add(new IsoNode(
                    file.FullName,
                    file.Name,
                    isDirectory: false,
                    directory)
                {
                    DataLength = file.Length,
                    Timestamp = file.LastWriteTimeUtc,
                });
            }
        }
    }

    private static List<IsoNode> AssignDirectoryNumbers(IsoNode root)
    {
        List<IsoNode> directories = [];
        Queue<IsoNode> pending = new();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            IsoNode directory = pending.Dequeue();
            if (directories.Count == ushort.MaxValue)
            {
                throw new ArgumentException("ISO contains too many directories.");
            }

            directory.DirectoryNumber = checked((ushort)(directories.Count + 1));
            directories.Add(directory);
            foreach (IsoNode child in directory.Children.Where(child => child.IsDirectory))
            {
                pending.Enqueue(child);
            }
        }

        return directories;
    }

    private static void AssignIdentifiers(IEnumerable<IsoNode> directories)
    {
        foreach (IsoNode directory in directories)
        {
            HashSet<string> primaryUsed = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> jolietUsed = new(StringComparer.OrdinalIgnoreCase);
            foreach (IsoNode child in directory.Children)
            {
                string primaryBase = NormalizePrimaryIdentifier(
                    child.Name,
                    child.IsDirectory
                        ? MaximumPrimaryIdentifierCharacters
                        : MaximumPrimaryIdentifierCharacters - 2,
                    child.IsDirectory ? "DIRECTORY" : "FILE");
                child.PrimaryIdentifier = MakeUnique(
                    primaryBase,
                    primaryUsed,
                    child.IsDirectory ? string.Empty : ";1",
                    MaximumPrimaryIdentifierCharacters);

                string jolietBase = NormalizeJolietIdentifier(
                    child.Name,
                    child.IsDirectory
                        ? MaximumJolietIdentifierCharacters
                        : MaximumJolietIdentifierCharacters - 2,
                    child.IsDirectory ? "Directory" : "File");
                child.JolietIdentifier = MakeUnique(
                    jolietBase,
                    jolietUsed,
                    child.IsDirectory ? string.Empty : ";1",
                    MaximumJolietIdentifierCharacters);
            }
        }
    }

    private static Layout BuildLayout(List<IsoNode> directories, uint volumeSectors)
    {
        int primaryPathTableBytes = PathTableLength(directories, joliet: false);
        int jolietPathTableBytes = PathTableLength(directories, joliet: true);
        uint cursor = FirstMetadataLba;
        uint primaryLittlePathTableLba = Allocate(ref cursor, primaryPathTableBytes);
        uint primaryBigPathTableLba = Allocate(ref cursor, primaryPathTableBytes);
        uint jolietLittlePathTableLba = Allocate(ref cursor, jolietPathTableBytes);
        uint jolietBigPathTableLba = Allocate(ref cursor, jolietPathTableBytes);

        foreach (IsoNode directory in directories)
        {
            directory.PrimaryDirectoryBytes = DirectoryExtentLength(directory, joliet: false);
            directory.PrimaryDirectoryLba = Allocate(ref cursor, directory.PrimaryDirectoryBytes);
        }

        foreach (IsoNode directory in directories)
        {
            directory.JolietDirectoryBytes = DirectoryExtentLength(directory, joliet: true);
            directory.JolietDirectoryLba = Allocate(ref cursor, directory.JolietDirectoryBytes);
        }

        foreach (IsoNode file in EnumerateFiles(directories[0]))
        {
            file.DataLba = cursor;
            cursor = checked(cursor + SectorCount(file.DataLength));
        }

        if (cursor > volumeSectors)
        {
            throw new ArgumentException(
                $"Data directory needs {cursor} sectors including filesystem metadata, "
                + $"but the disc has only {volumeSectors} sectors.");
        }

        return new Layout(
            primaryLittlePathTableLba,
            primaryBigPathTableLba,
            jolietLittlePathTableLba,
            jolietBigPathTableLba,
            primaryPathTableBytes,
            jolietPathTableBytes,
            cursor);
    }

    private static uint Allocate(ref uint cursor, long byteCount)
    {
        uint lba = cursor;
        cursor = checked(cursor + SectorCount(byteCount));
        return lba;
    }

    private static uint SectorCount(long byteCount)
        => checked((uint)((byteCount + IsoImageWriter.LogicalSectorBytes - 1)
            / IsoImageWriter.LogicalSectorBytes));

    private static int PathTableLength(IEnumerable<IsoNode> directories, bool joliet)
    {
        int length = 0;
        foreach (IsoNode directory in directories)
        {
            int identifierBytes = directory.Parent is null
                ? 1
                : IdentifierBytes(directory, joliet).Length;
            length = checked(length + 8 + identifierBytes + (identifierBytes & 1));
        }

        return length;
    }

    private static int DirectoryExtentLength(IsoNode directory, bool joliet)
    {
        List<int> recordLengths =
        [
            DirectoryRecordLength(1),
            DirectoryRecordLength(1),
        ];
        recordLengths.AddRange(directory.Children.Select(child =>
            DirectoryRecordLength(IdentifierBytes(child, joliet).Length)));
        return PackedRecordLength(recordLengths);
    }

    private static int PackedRecordLength(IEnumerable<int> recordLengths)
    {
        int offset = 0;
        foreach (int recordLength in recordLengths)
        {
            int sectorOffset = offset % IsoImageWriter.LogicalSectorBytes;
            if (sectorOffset + recordLength > IsoImageWriter.LogicalSectorBytes)
            {
                offset = checked(offset + IsoImageWriter.LogicalSectorBytes - sectorOffset);
            }

            offset = checked(offset + recordLength);
        }

        return checked((int)(SectorCount(offset) * IsoImageWriter.LogicalSectorBytes));
    }

    private static byte[] BuildPathTable(
        IEnumerable<IsoNode> directories,
        bool littleEndian,
        bool joliet)
    {
        using MemoryStream table = new();
        byte[] numeric = new byte[6];
        foreach (IsoNode directory in directories)
        {
            byte[] identifier = directory.Parent is null ? [0] : IdentifierBytes(directory, joliet);
            table.WriteByte(checked((byte)identifier.Length));
            table.WriteByte(0);
            uint extentLba = joliet ? directory.JolietDirectoryLba : directory.PrimaryDirectoryLba;
            ushort parentNumber = directory.Parent?.DirectoryNumber ?? 1;
            if (littleEndian)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(numeric.AsSpan(0, 4), extentLba);
                BinaryPrimitives.WriteUInt16LittleEndian(numeric.AsSpan(4, 2), parentNumber);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(numeric.AsSpan(0, 4), extentLba);
                BinaryPrimitives.WriteUInt16BigEndian(numeric.AsSpan(4, 2), parentNumber);
            }

            table.Write(numeric);
            table.Write(identifier);
            if ((identifier.Length & 1) != 0)
            {
                table.WriteByte(0);
            }
        }

        return table.ToArray();
    }

    private static byte[] BuildDirectoryExtent(IsoNode directory, bool joliet)
    {
        int extentBytes = joliet ? directory.JolietDirectoryBytes : directory.PrimaryDirectoryBytes;
        byte[] extent = new byte[extentBytes];
        int offset = 0;
        uint currentLba = joliet ? directory.JolietDirectoryLba : directory.PrimaryDirectoryLba;
        IsoNode parent = directory.Parent ?? directory;
        uint parentLba = joliet ? parent.JolietDirectoryLba : parent.PrimaryDirectoryLba;
        int parentBytes = joliet ? parent.JolietDirectoryBytes : parent.PrimaryDirectoryBytes;
        AddDirectoryRecord(extent, ref offset, BuildDirectoryRecord(
            currentLba,
            extentBytes,
            isDirectory: true,
            [0],
            directory.Timestamp));
        AddDirectoryRecord(extent, ref offset, BuildDirectoryRecord(
            parentLba,
            parentBytes,
            isDirectory: true,
            [1],
            parent.Timestamp));
        foreach (IsoNode child in directory.Children)
        {
            uint childLba = child.IsDirectory
                ? joliet ? child.JolietDirectoryLba : child.PrimaryDirectoryLba
                : child.DataLba;
            long childBytes = child.IsDirectory
                ? joliet ? child.JolietDirectoryBytes : child.PrimaryDirectoryBytes
                : child.DataLength;
            AddDirectoryRecord(extent, ref offset, BuildDirectoryRecord(
                childLba,
                childBytes,
                child.IsDirectory,
                IdentifierBytes(child, joliet),
                child.Timestamp));
        }

        return extent;
    }

    private static void AddDirectoryRecord(byte[] extent, ref int offset, byte[] record)
    {
        int sectorOffset = offset % IsoImageWriter.LogicalSectorBytes;
        if (sectorOffset + record.Length > IsoImageWriter.LogicalSectorBytes)
        {
            offset += IsoImageWriter.LogicalSectorBytes - sectorOffset;
        }

        record.CopyTo(extent, offset);
        offset += record.Length;
    }

    private static byte[] BuildVolumeDescriptor(
        byte type,
        uint volumeSectors,
        string volumeLabel,
        int pathTableBytes,
        uint littlePathTableLba,
        uint bigPathTableLba,
        uint rootDirectoryLba,
        int rootDirectoryBytes,
        bool joliet,
        DateTimeOffset timestamp)
    {
        byte[] sector = new byte[IsoImageWriter.LogicalSectorBytes];
        sector[0] = type;
        WriteAscii(sector, 1, "CD001");
        sector[6] = 1;
        if (joliet)
        {
            WritePaddedJoliet(sector, 8, 32, "DVDIMAGE");
            WritePaddedJoliet(sector, 40, 32, volumeLabel);
            sector[88] = (byte)'%';
            sector[89] = (byte)'/';
            sector[90] = (byte)'E';
        }
        else
        {
            WritePaddedAscii(sector, 8, 32, "DVDIMAGE");
            WritePaddedAscii(sector, 40, 32, volumeLabel);
        }

        WriteBothEndian32(sector, 80, volumeSectors);
        WriteBothEndian16(sector, 120, 1);
        WriteBothEndian16(sector, 124, 1);
        WriteBothEndian16(sector, 128, IsoImageWriter.LogicalSectorBytes);
        WriteBothEndian32(sector, 132, checked((uint)pathTableBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(140, 4), littlePathTableLba);
        BinaryPrimitives.WriteUInt32BigEndian(sector.AsSpan(148, 4), bigPathTableLba);
        BuildDirectoryRecord(
            rootDirectoryLba,
            rootDirectoryBytes,
            isDirectory: true,
            [0],
            timestamp).CopyTo(sector, 156);
        WriteVolumeDate(sector.AsSpan(813, 17), timestamp);
        WriteVolumeDate(sector.AsSpan(830, 17), timestamp);
        WriteUnspecifiedVolumeDate(sector.AsSpan(847, 17));
        WriteUnspecifiedVolumeDate(sector.AsSpan(864, 17));
        sector[881] = 1;
        return sector;
    }

    private static byte[] BuildTerminator()
    {
        byte[] sector = new byte[IsoImageWriter.LogicalSectorBytes];
        sector[0] = 255;
        WriteAscii(sector, 1, "CD001");
        sector[6] = 1;
        return sector;
    }

    private static byte[] BuildDirectoryRecord(
        uint extentLba,
        long dataLength,
        bool isDirectory,
        ReadOnlySpan<byte> identifier,
        DateTimeOffset timestamp)
    {
        int length = DirectoryRecordLength(identifier.Length);
        byte[] record = new byte[length];
        record[0] = checked((byte)length);
        WriteBothEndian32(record, 2, extentLba);
        WriteBothEndian32(record, 10, checked((uint)dataLength));
        WriteDirectoryDate(record.AsSpan(18, 7), timestamp);
        record[25] = isDirectory ? (byte)0x02 : (byte)0x00;
        WriteBothEndian16(record, 28, 1);
        record[32] = checked((byte)identifier.Length);
        identifier.CopyTo(record.AsSpan(33));
        return record;
    }

    private static int DirectoryRecordLength(int identifierBytes)
        => checked(33 + identifierBytes + (identifierBytes % 2 == 0 ? 1 : 0));

    private static byte[] IdentifierBytes(IsoNode node, bool joliet)
        => joliet
            ? TextEncoding.BigEndianUnicode.GetBytes(node.JolietIdentifier)
            : TextEncoding.ASCII.GetBytes(node.PrimaryIdentifier);

    private static IEnumerable<IsoNode> EnumerateFiles(IsoNode root)
    {
        foreach (IsoNode child in root.Children)
        {
            if (child.IsDirectory)
            {
                foreach (IsoNode nested in EnumerateFiles(child))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return child;
            }
        }
    }

    private static void WriteFile(FileStream output, IsoNode file)
    {
        using FileStream input = new(
            file.SourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        if (input.Length != file.DataLength)
        {
            throw new IOException($"Source file size changed while building ISO: {file.SourcePath}");
        }

        output.Position = checked((long)file.DataLba * IsoImageWriter.LogicalSectorBytes);
        input.CopyTo(output);
    }

    private static void WriteExtent(FileStream output, uint lba, ReadOnlySpan<byte> bytes)
    {
        output.Position = checked((long)lba * IsoImageWriter.LogicalSectorBytes);
        output.Write(bytes);
    }

    private static void WriteSector(FileStream output, uint lba, ReadOnlySpan<byte> sector)
    {
        if (sector.Length != IsoImageWriter.LogicalSectorBytes)
        {
            throw new ArgumentException("ISO logical sector must contain 2048 bytes.", nameof(sector));
        }

        WriteExtent(output, lba, sector);
    }

    private static string MakeUnique(
        string baseName,
        HashSet<string> used,
        string suffix,
        int maximumCharacters)
    {
        string candidate = Truncate(baseName, maximumCharacters - suffix.Length) + suffix;
        if (used.Add(candidate))
        {
            return candidate;
        }

        for (int index = 2; index < 1_000_000; index++)
        {
            string discriminator = $"~{index}";
            candidate = Truncate(
                baseName,
                maximumCharacters - suffix.Length - discriminator.Length)
                + discriminator
                + suffix;
            if (used.Add(candidate))
            {
                return candidate;
            }
        }

        throw new ArgumentException($"Too many colliding ISO identifiers derived from '{baseName}'.");
    }

    private static string NormalizePrimaryIdentifier(
        string value,
        int maximumLength,
        string fallback)
    {
        StringBuilder result = new(maximumLength);
        foreach (char character in value.Normalize(NormalizationForm.FormKC).ToUpperInvariant())
        {
            char normalized = character is >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '_'
                or '.'
                ? character
                : '_';
            result.Append(normalized);
            if (result.Length == maximumLength)
            {
                break;
            }
        }

        string normalizedValue = result.ToString().Trim('.');
        return normalizedValue.Length == 0 ? fallback : normalizedValue;
    }

    private static string NormalizeJolietIdentifier(
        string value,
        int maximumLength,
        string fallback)
    {
        StringBuilder result = new(maximumLength);
        foreach (char character in value.Normalize(NormalizationForm.FormC))
        {
            char normalized = character is '\0' or '/' or '\\' or ':' or ';'
                ? '_'
                : char.IsSurrogate(character)
                    ? '_'
                    : character;
            result.Append(normalized);
            if (result.Length == maximumLength)
            {
                break;
            }
        }

        return result.Length == 0 ? fallback : result.ToString();
    }

    private static string Truncate(string value, int maximumCharacters)
        => value.Length <= maximumCharacters ? value : value[..maximumCharacters];

    private static bool IsWithinDirectory(string path, string directory)
    {
        string relative = Path.GetRelativePath(directory, path);
        return relative == "."
            || (!Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static void WriteBothEndian16(Span<byte> destination, int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(offset, 2), value);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(offset + 2, 2), value);
    }

    private static void WriteBothEndian32(Span<byte> destination, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(offset, 4), value);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(offset + 4, 4), value);
    }

    private static void WriteDirectoryDate(Span<byte> destination, DateTimeOffset timestamp)
    {
        DateTime utc = timestamp.UtcDateTime;
        int year = Math.Clamp(utc.Year, 1900, 2155);
        destination[0] = checked((byte)(year - 1900));
        destination[1] = checked((byte)utc.Month);
        destination[2] = checked((byte)utc.Day);
        destination[3] = checked((byte)utc.Hour);
        destination[4] = checked((byte)utc.Minute);
        destination[5] = checked((byte)utc.Second);
        destination[6] = 0;
    }

    private static void WriteVolumeDate(Span<byte> destination, DateTimeOffset timestamp)
    {
        string digits = timestamp.UtcDateTime.ToString("yyyyMMddHHmmssff", CultureInfo.InvariantCulture);
        TextEncoding.ASCII.GetBytes(digits, destination[..16]);
        destination[16] = 0;
    }

    private static void WriteUnspecifiedVolumeDate(Span<byte> destination)
    {
        destination[..16].Fill((byte)'0');
        destination[16] = 0;
    }

    private static void WriteAscii(Span<byte> destination, int offset, string value)
        => TextEncoding.ASCII.GetBytes(value, destination[offset..]);

    private static void WritePaddedAscii(Span<byte> destination, int offset, int length, string value)
    {
        Span<byte> field = destination.Slice(offset, length);
        field.Fill((byte)' ');
        TextEncoding.ASCII.GetBytes(value, field);
    }

    private static void WritePaddedJoliet(Span<byte> destination, int offset, int length, string value)
    {
        Span<byte> field = destination.Slice(offset, length);
        for (int index = 0; index < field.Length; index += 2)
        {
            field[index] = 0;
            field[index + 1] = (byte)' ';
        }

        TextEncoding.BigEndianUnicode.GetBytes(value, field);
    }

    private sealed class IsoNode(
        string sourcePath,
        string name,
        bool isDirectory,
        IsoNode? parent)
    {
        internal string SourcePath { get; } = sourcePath;
        internal string Name { get; } = name;
        internal bool IsDirectory { get; } = isDirectory;
        internal IsoNode? Parent { get; } = parent;
        internal List<IsoNode> Children { get; } = [];
        internal string PrimaryIdentifier { get; set; } = string.Empty;
        internal string JolietIdentifier { get; set; } = string.Empty;
        internal ushort DirectoryNumber { get; set; }
        internal uint PrimaryDirectoryLba { get; set; }
        internal int PrimaryDirectoryBytes { get; set; }
        internal uint JolietDirectoryLba { get; set; }
        internal int JolietDirectoryBytes { get; set; }
        internal uint DataLba { get; set; }
        internal long DataLength { get; set; }
        internal DateTimeOffset Timestamp { get; set; }
    }

    private sealed record Layout(
        uint PrimaryLittlePathTableLba,
        uint PrimaryBigPathTableLba,
        uint JolietLittlePathTableLba,
        uint JolietBigPathTableLba,
        int PrimaryPathTableBytes,
        int JolietPathTableBytes,
        uint DataEndLbaExclusive);
}
