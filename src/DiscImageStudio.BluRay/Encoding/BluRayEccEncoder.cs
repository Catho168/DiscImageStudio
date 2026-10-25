using System.Buffers.Binary;

namespace DiscImageStudio.BluRay.Encoding;

/// <summary>BD-R user-data cluster through ECC multiplexing (ISO/IEC 30190:2021, 13.2..13.14).</summary>
/// <remarks>The returned bytes precede dc-control, 17PP and RUB framing; they are not a burnable disc image.</remarks>
public static class BluRayEccEncoder
{
    public const int SectorsPerCluster = 32;
    public const int PayloadBytes = SectorsPerCluster * BluRayEdc.UserDataBytes;
    public const int RecordingFrames = 496;
    public const int EccBytesPerFrame = 155;
    public const int EccClusterBytes = RecordingFrames * EccBytesPerFrame;
    public const int LdcClusterBytes = RecordingFrames * 152;
    public const int BisClusterBytes = RecordingFrames * 3;
    public const int UserControlBytes = SectorsPerCluster * 18;

    public static byte[] Encode(ReadOnlySpan<byte> userData, uint firstPhysicalSectorNumber,
        ReadOnlySpan<byte> addressFlags = default, ReadOnlySpan<byte> userControlData = default)
    {
        byte[] ldc = EncodeLdcCluster(userData, firstPhysicalSectorNumber);
        byte[] bis = EncodeBisCluster(firstPhysicalSectorNumber, addressFlags, userControlData);
        byte[] ecc = new byte[EccClusterBytes];
        for (int row = 0; row < RecordingFrames; row++)
        {
            for (int group = 0; group < 4; group++)
            {
                ldc.AsSpan(row * 152 + group * 38, 38).CopyTo(ecc.AsSpan(row * 155 + group * 39));
                if (group < 3) ecc[row * 155 + group * 39 + 38] = bis[row * 3 + group];
            }
        }
        return ecc;
    }

    /// <summary>Returns 496 rows of 152 LDC bytes in recording order.</summary>
    public static byte[] EncodeLdcCluster(ReadOnlySpan<byte> userData, uint firstPhysicalSectorNumber)
    {
        ValidatePsn(firstPhysicalSectorNumber);
        if (userData.Length != PayloadBytes)
            throw new ArgumentException("A BD cluster contains 65536 user-data bytes.", nameof(userData));
        byte[] dataBlock = new byte[32 * BluRayEdc.DataFrameBytes];
        byte[] mask = BluRayScrambler.CreateMask(firstPhysicalSectorNumber);
        for (int sector = 0; sector < SectorsPerCluster; sector++)
        {
            Span<byte> frame = dataBlock.AsSpan(sector * BluRayEdc.DataFrameBytes, BluRayEdc.DataFrameBytes);
            BluRayEdc.WriteDataFrame(userData.Slice(sector * BluRayEdc.UserDataBytes, BluRayEdc.UserDataBytes), frame);
            for (int i = 0; i < frame.Length; i++) frame[i] ^= mask[i];
        }

        byte[] cluster = new byte[LdcClusterBytes];
        Span<byte> codeword = stackalloc byte[248];
        for (int column = 0; column < 304; column++)
        {
            dataBlock.AsSpan(column * 216, 216).CopyTo(codeword);
            BluRayReedSolomon.ComputeParity(codeword[..216], codeword[216..]);
            for (int row = 0; row < 248; row++)
            {
                int outputRow = row * 2 + column % 2;
                int outputColumn = (column / 2 + 152 - (row * 3) % 152) % 152;
                cluster[outputRow * 152 + outputColumn] = codeword[row];
            }
        }
        return cluster;
    }

    /// <summary>Returns 496 rows of 3 BIS bytes. Empty optional spans mean all-zero flags/control.</summary>
    public static byte[] EncodeBisCluster(uint firstPhysicalSectorNumber,
        ReadOnlySpan<byte> addressFlags = default, ReadOnlySpan<byte> userControlData = default)
    {
        ValidatePsn(firstPhysicalSectorNumber);
        if (!addressFlags.IsEmpty && addressFlags.Length != 16)
            throw new ArgumentException("Supply one flag byte for each of the 16 address units.", nameof(addressFlags));
        if (!userControlData.IsEmpty && userControlData.Length != UserControlBytes)
            throw new ArgumentException("Supply 18 control bytes for each of the 32 sectors.", nameof(userControlData));

        // Keep the access block column-major so each shortened RS word is contiguous.
        byte[] access = new byte[24 * 30];
        Span<byte> address = stackalloc byte[9];
        for (int unit = 0; unit < 16; unit++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(address, firstPhysicalSectorNumber + (uint)(unit * 2));
            address[4] = addressFlags.IsEmpty ? (byte)0 : addressFlags[unit];
            BluRayReedSolomon.ComputeParity(address[..5], address[5..]);
            for (int x = 0; x < 9; x++)
            {
                int row = 2 * (x / 3) + unit / 8;
                int column = 3 * ((x / 3 + 16 - unit) % 8) + (x - x / 3) % 3;
                access[column * 30 + row] = address[x];
            }
        }
        for (int i = 0; i < userControlData.Length; i++)
            access[(i / 24) * 30 + 6 + i % 24] = userControlData[i];

        byte[] cluster = new byte[BisClusterBytes];
        Span<byte> codeword = stackalloc byte[62];
        for (int column = 0; column < 24; column++)
        {
            access.AsSpan(column * 30, 30).CopyTo(codeword);
            BluRayReedSolomon.ComputeParity(codeword[..30], codeword[30..]);
            for (int row = 0; row < 62; row++)
            {
                int unit = (row / 2 + 8 - column / 3) % 8 + 8 * (row % 2);
                int unitRow = row / 2;
                int outputColumn = (column + unitRow) % 3;
                cluster[(unit * 31 + unitRow) * 3 + outputColumn] = codeword[row];
            }
        }
        return cluster;
    }

    private static void ValidatePsn(uint firstPhysicalSectorNumber)
    {
        if ((firstPhysicalSectorNumber & 31) != 0 || firstPhysicalSectorNumber > 0x07ffffe0)
            throw new ArgumentOutOfRangeException(nameof(firstPhysicalSectorNumber),
                "A cluster PSN must be a multiple of 32 with reserved bits 31..27 clear.");
    }
}
