using System.Diagnostics;
using DvdImageSolver.Encoding;

namespace DvdImageSolver.Solver;

public sealed record FastDvdStreamProgress(
    int CompletedBlocks,
    int TotalBlocks,
    uint BlockLba,
    long BytesWritten,
    long TotalBytes,
    TimeSpan Elapsed)
{
    public double Fraction => TotalBytes == 0 ? 0 : (double)BytesWritten / TotalBytes;
}

public sealed record FastDvdStreamSummary(
    int CompletedBlocks,
    uint SectorsWritten,
    long BytesWritten,
    long ControlledWords,
    TimeSpan Elapsed);

internal sealed record FastDispersionWriteResult(
    int CompletedBlocks,
    uint FilledSectors,
    long ControlledWords,
    TimeSpan Elapsed,
    bool SparseIso,
    uint IsoSectors);

internal sealed record FastDispersionProgress(
    int CompletedBlocks,
    int TotalBlocks,
    uint BlockLba,
    long ControlledWords,
    TimeSpan Elapsed);

public static class FastDispersionImageWriter
{
    private static readonly int[] PayloadChannelCenters = BuildPayloadChannelCenters();

    internal static FastDispersionWriteResult Write(
        string imagePath,
        string isoOutputPath,
        MultiBlockSolveOptions options,
        Action<FastDispersionProgress>? progress)
    {
        Validate(options);
        RasterImage image = RasterImage.Load(imagePath);
        PayloadImageSampler sampler = new(image, options.ImageMapping);
        int totalBlocks = checked((int)(options.FillSectors / DvdEccBlockEncoder.SectorCount));
        int parallelism = options.FastParallelism == 0
            ? Environment.ProcessorCount
            : options.FastParallelism;
        uint isoSectors = options.PreserveExistingIso
            ? options.ImageMapping.TotalSectors
            : checked(options.StartLba + options.FillSectors);
        long controlledWords = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        using RawIsoImageWriter iso = new(
            isoOutputPath,
            isoSectors,
            options.PreserveExistingIso);
        ParallelOptions parallelOptions = new()
        {
            MaxDegreeOfParallelism = parallelism,
        };
        int batchCapacity = Math.Max(1, checked(parallelism * 2));
        for (int batchStart = 0; batchStart < totalBlocks; batchStart += batchCapacity)
        {
            int batchCount = Math.Min(batchCapacity, totalBlocks - batchStart);
            GeneratedBlock[] generated = new GeneratedBlock[batchCount];
            Parallel.For(0, batchCount, parallelOptions, batchOffset =>
            {
                int blockIndex = batchStart + batchOffset;
                generated[batchOffset] = GenerateBlock(blockIndex, sampler, options);
            });

            for (int batchOffset = 0; batchOffset < generated.Length; batchOffset++)
            {
                GeneratedBlock block = generated[batchOffset];
                iso.WriteBlock(block.Lba, block.Payloads);
                controlledWords += block.ControlledWords;
                int completedBlocks = batchStart + batchOffset + 1;
                progress?.Invoke(new FastDispersionProgress(
                    completedBlocks,
                    totalBlocks,
                    block.Lba,
                    controlledWords,
                    stopwatch.Elapsed));
            }
        }

        bool sparseIso = iso.SparseFile;
        iso.Dispose();
        stopwatch.Stop();
        return new FastDispersionWriteResult(
            totalBlocks,
            options.FillSectors,
            controlledWords,
            stopwatch.Elapsed,
            sparseIso,
            isoSectors);
    }

    public static FastDvdStreamSummary WriteStream(
        string imagePath,
        Stream output,
        MultiBlockSolveOptions options,
        Action<FastDvdStreamProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("DVD output stream must be writable.", nameof(output));
        }

        Validate(options);
        if (options.PreserveExistingIso)
        {
            throw new ArgumentException(
                "Direct DVD streaming cannot preserve or seek within an existing ISO.",
                nameof(options));
        }

        RasterImage image = RasterImage.Load(imagePath);
        PayloadImageSampler sampler = new(image, options.ImageMapping);
        int totalBlocks = checked((int)(options.FillSectors / DvdEccBlockEncoder.SectorCount));
        int parallelism = options.FastParallelism == 0
            ? Environment.ProcessorCount
            : options.FastParallelism;
        ParallelOptions parallelOptions = new()
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = cancellationToken,
        };
        int batchCapacity = Math.Max(1, checked(parallelism * 2));
        long totalBytes = checked((long)options.FillSectors * DvdEccBlockEncoder.PayloadBytesPerSector);
        long bytesWritten = 0;
        long controlledWords = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int batchStart = 0; batchStart < totalBlocks; batchStart += batchCapacity)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int batchCount = Math.Min(batchCapacity, totalBlocks - batchStart);
            GeneratedBlock[] generated = new GeneratedBlock[batchCount];
            Parallel.For(0, batchCount, parallelOptions, batchOffset =>
            {
                int blockIndex = batchStart + batchOffset;
                generated[batchOffset] = GenerateBlock(blockIndex, sampler, options);
            });

            for (int batchOffset = 0; batchOffset < generated.Length; batchOffset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GeneratedBlock block = generated[batchOffset];
                output.Write(block.Payloads);
                bytesWritten = checked(bytesWritten + block.Payloads.Length);
                controlledWords += block.ControlledWords;
                int completedBlocks = batchStart + batchOffset + 1;
                progress?.Invoke(new FastDvdStreamProgress(
                    completedBlocks,
                    totalBlocks,
                    block.Lba,
                    bytesWritten,
                    totalBytes,
                    stopwatch.Elapsed));
            }
        }

        output.Flush();
        stopwatch.Stop();
        return new FastDvdStreamSummary(
            totalBlocks,
            options.FillSectors,
            bytesWritten,
            controlledWords,
            stopwatch.Elapsed);
    }

    private static GeneratedBlock GenerateBlock(
        int blockIndex,
        PayloadImageSampler sampler,
        MultiBlockSolveOptions options)
    {
        uint lba = checked(
            options.StartLba + ((uint)blockIndex * DvdEccBlockEncoder.SectorCount));
        byte[] payloads = new byte[DvdEccBlockEncoder.PayloadBytesPerBlock];
        if (!options.ZeroInitialPayload)
        {
            int blockSeed = unchecked(options.RandomSeed + (blockIndex * 1_000_003));
            new Random(blockSeed).NextBytes(payloads);
        }

        int controlledWords = 0;
        ulong blockFirstGlobalBit = (ulong)lba * DvdEccBlockEncoder.ChannelBitsPerSector;
        uint firstPsn = checked(lba + options.PsnOffset);
        for (int sector = 0; sector < DvdEccBlockEncoder.SectorCount; sector++)
        {
            int payloadOffset = sector * DvdEccBlockEncoder.PayloadBytesPerSector;
            Span<byte> sectorPayload = payloads.AsSpan(
                payloadOffset,
                DvdEccBlockEncoder.PayloadBytesPerSector);
            ReadOnlySpan<byte> scrambler = DvdDataFrameBuilder.PayloadScramblerMask(
                firstPsn + checked((uint)sector));
            for (int byteIndex = 0; byteIndex < sectorPayload.Length; byteIndex++)
            {
                int payloadIndex = payloadOffset + byteIndex;
                PixelSample sample = sampler.SampleGlobalChannelBit(
                    blockFirstGlobalBit + checked((ulong)PayloadChannelCenters[payloadIndex]));
                if (sample.Alpha < options.ImageMapping.AlphaThreshold)
                {
                    continue;
                }

                byte scrambledByte = sample.Luminance < options.ImageMapping.LuminanceThreshold
                    ? DispersionPoolSolver.BlackScrambledByte
                    : DispersionPoolSolver.WhiteScrambledByte;
                sectorPayload[byteIndex] = (byte)(scrambledByte ^ scrambler[byteIndex]);
                controlledWords++;
            }
        }

        return new GeneratedBlock(lba, payloads, controlledWords);
    }

    private static int[] BuildPayloadChannelCenters()
    {
        int[] centers = new int[DvdEccBlockEncoder.PayloadBytesPerBlock];
        for (int payloadIndex = 0; payloadIndex < centers.Length; payloadIndex++)
        {
            centers[payloadIndex] = checked(
                DvdEccBlockEncoder.PayloadDirectChannelOffset(payloadIndex) + 8);
        }

        return centers;
    }

    private static void Validate(MultiBlockSolveOptions options)
    {
        if (options.Algorithm != SolverAlgorithm.Dispersion)
        {
            throw new ArgumentException("Fast output requires --algorithm dispersion.");
        }

        if (options.FastParallelism is < 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Fast parallelism must be 0 (automatic) or 1..64.");
        }

        if (options.FillSectors < DvdEccBlockEncoder.SectorCount
            || options.FillSectors % DvdEccBlockEncoder.SectorCount != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Fast output sector count must be a positive multiple of 16.");
        }

        if ((ulong)options.StartLba + options.FillSectors > options.ImageMapping.TotalSectors)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Filled range exceeds the disc size.");
        }

        uint firstPsn = checked(options.StartLba + options.PsnOffset);
        uint finalPsn = checked(firstPsn + options.FillSectors - 1);
        if ((firstPsn & 0xF) != 0)
        {
            throw new ArgumentException("The first PSN must be aligned to a 16-sector ECC Block.");
        }

        if (finalPsn > 0x00FF_FFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The output exceeds the 24-bit PSN range.");
        }
    }

    private readonly record struct GeneratedBlock(
        uint Lba,
        byte[] Payloads,
        int ControlledWords);

    private sealed class PayloadImageSampler
    {
        private readonly RasterImage _image;
        private readonly ImageMappingOptions _options;
        private readonly ArchimedeanSpiral _spiral;
        private readonly double _bitLengthMm;
        private readonly double _startAngleRadians;
        private readonly double _direction;

        internal PayloadImageSampler(RasterImage image, ImageMappingOptions options)
        {
            _image = image;
            _options = options;
            _bitLengthMm = options.ChannelBitLengthNm * 1e-6;
            double trackLengthMm = options.TotalSectors
                * (double)DvdEccBlockEncoder.ChannelBitsPerSector
                * _bitLengthMm;
            _spiral = ArchimedeanSpiral.Create(
                options.InnerRadiusMm,
                options.OuterRadiusMm,
                trackLengthMm);
            _startAngleRadians = options.StartAngleDegrees * Math.PI / 180.0;
            _direction = options.Clockwise ? -1.0 : 1.0;
        }

        internal PixelSample SampleGlobalChannelBit(ulong globalChannelBit)
        {
            double arcLengthMm = (globalChannelBit + 0.0) * _bitLengthMm;
            // One Newton correction is sufficient at DVD pitch/radius ratios and avoids
            // four redundant inverse-spiral iterations for every payload code word.
            double radiusMm = _spiral.RadiusAtArcLengthFast(arcLengthMm);
            double trackAngle = (radiusMm - _options.InnerRadiusMm)
                / _spiral.RadialGrowthPerRadianMm;
            double polarAngle = _startAngleRadians + (_direction * trackAngle);
            return _image.SampleDisc(
                radiusMm * Math.Cos(polarAngle),
                radiusMm * Math.Sin(polarAngle),
                _options.OuterRadiusMm);
        }
    }
}
