using System.Diagnostics;
using DiscImageStudio.Core;
using DvdImageSolver;
using DvdImageSolver.Encoding;
using DvdImageSolver.Solver;

namespace DiscImageStudio.Dvd;

public sealed record DvdStreamingOptions(
    uint TotalSectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    double ChannelBitLengthNm = 133.3,
    double StartAngleDegrees = 0,
    byte LuminanceThreshold = 128,
    byte AlphaThreshold = 1,
    int RandomSeed = 1,
    int FastParallelism = 0,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0)
{
    /// <summary>Physical channel-bit length of the DVD scheme; the UI no longer exposes it.</summary>
    public const double StandardChannelBitLengthNm = 133.3;

    public long ContentLength => checked(
        (long)TotalSectors * DvdEccBlockEncoder.PayloadBytesPerSector);

    public void Validate()
    {
        DvdTrackGeometry.ValidatePitchCoefficients(PitchLinear, PitchQuadratic, PitchCubic);
        if (TotalSectors == 0 || TotalSectors % DvdEccBlockEncoder.SectorCount != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TotalSectors),
                "DVD streaming sector count must be a positive multiple of 16.");
        }

        if (!double.IsFinite(InnerRadiusMm)
            || !double.IsFinite(OuterRadiusMm)
            || InnerRadiusMm <= 0
            || OuterRadiusMm <= InnerRadiusMm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OuterRadiusMm),
                "DVD outer radius must be greater than its positive inner radius.");
        }

        if (!double.IsFinite(ChannelBitLengthNm) || ChannelBitLengthNm <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ChannelBitLengthNm),
                "DVD channel-bit length must be positive.");
        }

        if (!double.IsFinite(StartAngleDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(StartAngleDegrees));
        }

        if (FastParallelism is < 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(FastParallelism),
                "DVD streaming parallelism must be automatic (0) or 1..64.");
        }

        _ = ContentLength;
        // Resolve the complete curve before a hybrid stream writes its filesystem
        // prefix. Positive coefficients alone do not guarantee numerical resolution.
        double trackLengthMm = TotalSectors * (double)DvdEccBlockEncoder.ChannelBitsPerSector
            * ChannelBitLengthNm * 1e-6;
        _ = DvdTrackGeometry.Create(InnerRadiusMm, OuterRadiusMm, trackLengthMm,
            PitchLinear, PitchQuadratic, PitchCubic);
    }
}

public sealed record DvdStreamingProgress(
    long CompletedBytes,
    long TotalBytes,
    int CompletedBlocks,
    int TotalBlocks,
    TimeSpan Elapsed)
{
    public double Fraction => TotalBytes == 0 ? 0 : (double)CompletedBytes / TotalBytes;
}

public sealed record DvdStreamingSummary(
    string SourceImage,
    uint Sectors,
    long BytesWritten,
    long ControlledWords,
    TimeSpan Elapsed);

public sealed class DvdHybridStreamingPlan
{
    internal DvdHybridStreamingPlan(
        HybridIsoImageWriter.LayoutPlan isoLayout,
        uint drawingStartLba)
    {
        IsoLayout = isoLayout;
        DrawingStartLba = drawingStartLba;
    }

    internal HybridIsoImageWriter.LayoutPlan IsoLayout { get; }

    public string SourceDirectory => IsoLayout.SourceDirectory;

    public uint TotalSectors => IsoLayout.VolumeSectors;

    public uint FilesystemEndLbaExclusive => IsoLayout.DataEndLbaExclusive;

    public uint DrawingStartLba { get; }

    public uint DrawingSectors => checked(TotalSectors - DrawingStartLba);

    public int FileCount => IsoLayout.FileCount;

    public int DirectoryCount => IsoLayout.DirectoryCount;

    public long FileBytes => IsoLayout.FileBytes;
}

public static class DvdStreamingGenerator
{
    private const uint PsnOffset = 0x30000;

    public static DvdHybridStreamingPlan PrepareHybrid(
        string dataDirectory,
        DvdStreamingOptions options,
        string volumeLabel = "DVD_IMAGE")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        HybridIsoImageWriter.LayoutPlan isoLayout = HybridIsoImageWriter.Plan(
            dataDirectory,
            options.TotalSectors,
            volumeLabel);
        uint drawingStartLba = AlignToEccBlock(isoLayout.DataEndLbaExclusive, PsnOffset);
        uint drawingSectors = checked(options.TotalSectors - drawingStartLba);
        if (drawingSectors < DvdEccBlockEncoder.SectorCount)
        {
            throw new ArgumentException(
                "The inner-ring filesystem leaves no complete ECC Block for outer-ring drawing.",
                nameof(dataDirectory));
        }

        return new DvdHybridStreamingPlan(isoLayout, drawingStartLba);
    }

    public static DvdStreamingSummary Generate(
        string imagePath,
        Stream output,
        DvdStreamingOptions options,
        Action<DvdStreamingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        string sourceFullPath = Path.GetFullPath(imagePath);
        MultiBlockSolveOptions solveOptions = CreateSolveOptions(
            options,
            startLba: 0,
            fillSectors: options.TotalSectors);
        FastDvdStreamSummary summary = FastDispersionImageWriter.WriteStream(
            sourceFullPath,
            output,
            solveOptions,
            value => progress?.Invoke(new DvdStreamingProgress(
                value.BytesWritten,
                value.TotalBytes,
                value.CompletedBlocks,
                value.TotalBlocks,
                value.Elapsed)),
            cancellationToken);
        return new DvdStreamingSummary(
            sourceFullPath,
            summary.SectorsWritten,
            summary.BytesWritten,
            summary.ControlledWords,
            summary.Elapsed);
    }

    /// <summary>
    /// Generates calibration payload sectors directly from an analytic grayscale
    /// pattern. Coordinates are millimetres from the disc centre, x right and y down.
    /// The callback must support concurrent calls when parallel generation is enabled.
    /// </summary>
    public static DvdStreamingSummary GeneratePattern(
        Func<double, double, byte> sampleGray,
        Stream output,
        DvdStreamingOptions options,
        Action<DvdStreamingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sampleGray);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        MultiBlockSolveOptions solveOptions = CreateSolveOptions(
            options,
            startLba: 0,
            fillSectors: options.TotalSectors);
        FastDvdStreamSummary summary = FastDispersionImageWriter.WritePatternStream(
            sampleGray,
            output,
            solveOptions,
            value => progress?.Invoke(new DvdStreamingProgress(
                value.BytesWritten,
                value.TotalBytes,
                value.CompletedBlocks,
                value.TotalBlocks,
                value.Elapsed)),
            cancellationToken);
        return new DvdStreamingSummary(
            "calibration",
            summary.SectorsWritten,
            summary.BytesWritten,
            summary.ControlledWords,
            summary.Elapsed);
    }

    public static DvdStreamingSummary GenerateHybrid(
        string imagePath,
        Stream output,
        DvdStreamingOptions options,
        DvdHybridStreamingPlan plan,
        Action<DvdStreamingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(plan);
        options.Validate();
        if (plan.TotalSectors != options.TotalSectors)
        {
            throw new ArgumentException(
                "Hybrid layout and DVD streaming options describe different disc sizes.",
                nameof(plan));
        }

        string sourceFullPath = Path.GetFullPath(imagePath);
        Stopwatch stopwatch = Stopwatch.StartNew();
        HybridIsoImageWriter.WriteSequentialPrefix(
            plan.IsoLayout,
            output,
            plan.DrawingStartLba,
            cancellationToken);
        long prefixBytes = checked(
            (long)plan.DrawingStartLba * DvdEccBlockEncoder.PayloadBytesPerSector);
        int totalBlocks = checked((int)(options.TotalSectors / DvdEccBlockEncoder.SectorCount));
        progress?.Invoke(new DvdStreamingProgress(
            prefixBytes,
            options.ContentLength,
            checked((int)(plan.DrawingStartLba / DvdEccBlockEncoder.SectorCount)),
            totalBlocks,
            stopwatch.Elapsed));

        MultiBlockSolveOptions solveOptions = CreateSolveOptions(
            options,
            plan.DrawingStartLba,
            plan.DrawingSectors);
        FastDvdStreamSummary drawing = FastDispersionImageWriter.WriteStream(
            sourceFullPath,
            output,
            solveOptions,
            value => progress?.Invoke(new DvdStreamingProgress(
                checked(prefixBytes + value.BytesWritten),
                options.ContentLength,
                checked((int)(plan.DrawingStartLba / DvdEccBlockEncoder.SectorCount)
                    + value.CompletedBlocks),
                totalBlocks,
                stopwatch.Elapsed)),
            cancellationToken);
        stopwatch.Stop();
        return new DvdStreamingSummary(
            sourceFullPath,
            options.TotalSectors,
            checked(prefixBytes + drawing.BytesWritten),
            drawing.ControlledWords,
            stopwatch.Elapsed);
    }

    private static MultiBlockSolveOptions CreateSolveOptions(
        DvdStreamingOptions options,
        uint startLba,
        uint fillSectors)
    {
        ImageMappingOptions imageMapping = new(
            options.TotalSectors,
            options.InnerRadiusMm,
            options.OuterRadiusMm,
            options.ChannelBitLengthNm,
            options.StartAngleDegrees,
            Clockwise: true,
            options.LuminanceThreshold,
            options.AlphaThreshold,
            SampleEveryChannelBits: 1,
            PitchLinear: options.PitchLinear,
            PitchQuadratic: options.PitchQuadratic,
            PitchCubic: options.PitchCubic);
        return new MultiBlockSolveOptions(
            startLba,
            fillSectors,
            PsnOffset,
            IterationsPerBlock: 1,
            MutationBytes: 4,
            RandomSeed: options.RandomSeed,
            InitialTemperature: 64.0,
            FinalTemperature: 0.25,
            ZeroInitialPayload: false,
            imageMapping,
            Algorithm: SolverAlgorithm.Dispersion,
            FastParallelism: options.FastParallelism);
    }

    private static uint AlignToEccBlock(uint lba, uint psnOffset)
    {
        uint remainder = (lba + psnOffset) & (DvdEccBlockEncoder.SectorCount - 1);
        return remainder == 0
            ? lba
            : checked(lba + DvdEccBlockEncoder.SectorCount - remainder);
    }
}
