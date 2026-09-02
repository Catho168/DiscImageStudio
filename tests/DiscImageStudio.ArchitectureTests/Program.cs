using DiscImageStudio.Burning;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Media;
using System.Windows.Media.Imaging;

DiscModuleCatalog catalog = new(
[
    new CdDiscModule(),
    new DvdDiscModule(),
    new FutureBluRayModule(),
]);

Equal(3, catalog.Modules.Count, "module count");
Equal(OpticalDiscFamily.CompactDisc, catalog.Resolve("cd-generate").Descriptor.Family, "CD routing");
Equal(OpticalDiscFamily.Dvd, catalog.Resolve("solve").Descriptor.Family, "DVD routing");
Equal(OpticalDiscFamily.BluRay, catalog.Resolve("bd-generate").Descriptor.Family, "future Blu-ray routing");
True(
    catalog.Resolve("cd-generate").Descriptor.SupportsCancellation,
    "CD module cancellation metadata");
True(
    !catalog.Resolve("solve").Descriptor.SupportsCancellation,
    "DVD module cancellation metadata");

DiscJobResult bluRayResult = await catalog.ExecuteAsync(
    new DiscJobRequest("bd-generate", ["--output", "future.iso"]));
True(bluRayResult.Succeeded, "future module execution");

Throws<ArgumentException>(
    () => new DiscModuleCatalog([new FutureBluRayModule(), new ConflictingModule()]),
    "duplicate command rejection");
Throws<ArgumentException>(() => catalog.Resolve("unknown-command"), "unknown command rejection");
TestRingImageLayout();
TestDiscPresets();

Console.WriteLine("architecture-selftest: all checks passed");
return;

static void Equal<T>(T expected, T actual, string name)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"{name}: expected {expected}, actual {actual}.");
    }
}

static void True(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException($"{name}: condition was false.");
    }
}

static void Throws<TException>(Action action, string name)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"{name}: expected {typeof(TException).Name}.");
}

static void TestRingImageLayout()
{
    Equal(8192, RingImageQuality.GenerationSize, "formal ring image resolution");
    Equal(4096, RingImageQuality.SavedPreviewSize, "saved ring preview resolution");
    Equal(2048, RingImageQuality.LivePreviewSize, "live ring preview resolution");
    new RingImageLayoutOptions(58, 26, 56, RingImageQuality.GenerationSize).Validate();

    string directory = Path.Combine(Path.GetTempPath(), $"disc-ring-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        string sourcePath = Path.Combine(directory, "source.png");
        string outputPath = Path.Combine(directory, "ring.png");
        WriteSolidPng(sourcePath, 80, 40, 0);
        RingImageLayoutSummary summary = RingImageProcessor.Render(
            sourcePath,
            outputPath,
            new RingImageLayoutOptions(
                CanvasOuterRadiusMm: 58,
                ContentInnerRadiusMm: 26,
                ContentOuterRadiusMm: 56,
                OutputSize: 512));
        True(summary.CopyCount >= 3, "ring image copies source multiple times");
        True(
            summary.CopyWidthPixels > 0 && summary.CopyHeightPixels > 0,
            "ring layout reports per-copy pixel resolution");
        True(
            Math.Abs((summary.CopyWidthMm / summary.CopyHeightMm) - 2.0) < 1e-10,
            "ring copies preserve source aspect ratio");
        double innerEdgeRadiusMm =
            summary.CopyCentreRadiusMm - (summary.CopyHeightMm / 2.0);
        double outerCornerRadiusMm = Math.Sqrt(
            Math.Pow(summary.CopyCentreRadiusMm + (summary.CopyHeightMm / 2.0), 2)
            + Math.Pow(summary.CopyWidthMm / 2.0, 2));
        True(
            innerEdgeRadiusMm >= summary.ContentInnerRadiusMm,
            "ring copy stays outside inner boundary");
        True(
            outerCornerRadiusMm <= summary.ContentOuterRadiusMm,
            "ring copy corners stay inside outer boundary");
        double copyAngularSpan = 2.0 * Math.Atan(
            (summary.CopyWidthMm / 2.0) / innerEdgeRadiusMm);
        double availableAngularSpan =
            ((2.0 * Math.PI) / summary.CopyCount)
            - (summary.AngularGapDegrees * Math.PI / 180.0);
        True(
            copyAngularSpan <= availableAngularSpan + 1e-10,
            "ring copies do not overlap");
        True(File.Exists(outputPath), "ring image output exists");
        ValidateRingSafetyMargins(outputPath, 58, 25.5, 56.5);
        TestStreamingGeneration(sourcePath);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void TestDiscPresets()
{
    Equal(5, CdDiscPreset.All.Count, "CD preset count including custom");
    Equal(3, DvdDiscPreset.All.Count, "DVD preset count including custom");
    Equal(
        CdDiscPreset.All.Count,
        CdDiscPreset.All.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count(),
        "CD preset IDs are unique");
    Equal(
        DvdDiscPreset.All.Count,
        DvdDiscPreset.All.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count(),
        "DVD preset IDs are unique");

    foreach (CdDiscPreset preset in CdDiscPreset.All.Where(value => !value.IsCustom))
    {
        True(
            preset.LinearVelocityMmPerSecond is >= 1_200 and <= 1_400,
            $"CD preset '{preset.Id}' uses the ECMA-130 scanning velocity range");
        new CdDiscParameters(
            preset.InnerRadiusMm,
            preset.OuterRadiusMm,
            preset.Sectors,
            preset.LinearVelocityMmPerSecond,
            ImageOuterRadiusMm: preset.ImageOuterRadiusMm).Validate();
    }

    foreach (DvdDiscPreset preset in DvdDiscPreset.All.Where(value => !value.IsCustom))
    {
        new DvdStreamingOptions(
            preset.TotalSectors,
            preset.InnerRadiusMm,
            preset.OuterRadiusMm,
            preset.ChannelBitLengthNm,
            StartAngleDegrees: 0).Validate();
    }

    CdDiscPreset cd80 = CdDiscPreset.All.Single(value => value.Id == "cd-80");
    Equal(359_849L, cd80.Sectors, "80-minute CD preset sectors");
    CdDiscPreset ritek = CdDiscPreset.All.Single(value => value.Id == "ritek-medical-aqua");
    Equal(359_845L, ritek.Sectors, "RITEK medical aqua preset sectors");
    Equal(24.911275, ritek.InnerRadiusMm, "RITEK medical aqua inner radius");
    Equal(57.931155, ritek.OuterRadiusMm, "RITEK medical aqua outer radius");
    True(
        ritek.ImageOuterRadiusMm >= ritek.OuterRadiusMm,
        "RITEK medical aqua image canvas covers the outer radius");
    CdDiscPreset verbatim = CdDiscPreset.All.Single(value => value.Id == "verbatim-azo-43438");
    Equal(359_848L, verbatim.Sectors, "Verbatim AZO 43438 preset sectors");
    Equal(24.837775, verbatim.InnerRadiusMm, "Verbatim AZO 43438 inner radius");
    Equal(58.020875, verbatim.OuterRadiusMm, "Verbatim AZO 43438 outer radius");
    True(
        verbatim.ImageOuterRadiusMm >= verbatim.OuterRadiusMm,
        "Verbatim AZO 43438 image canvas covers the outer radius");
    DvdDiscPreset dvd120 = DvdDiscPreset.All.Single(value => value.Id == "dvd-5-120mm");
    Equal(2_295_104U, dvd120.TotalSectors, "120 mm DVD preset sectors");
    DvdDiscPreset dvd80 = DvdDiscPreset.All.Single(value => value.Id == "dvd-5-80mm");
    Equal(714_544U, dvd80.TotalSectors, "80 mm DVD preset sectors");
    Equal(38.0, dvd80.OuterRadiusMm, "80 mm DVD preset outer radius");
}

static void TestStreamingGeneration(string sourcePath)
{
    CdDiscParameters cdParameters = new(
        InnerRadiusMm: 24.5,
        OuterRadiusMm: 24.6,
        Sectors: 2,
        LinearVelocityMmPerSecond: 1200,
        ImageOuterRadiusMm: 58);
    using MemoryStream cdStream = new();
    CdGenerationSummary cdSummary = CdTrackGenerator.GenerateToStream(
        sourcePath,
        cdStream,
        cdParameters,
        interleave: true);
    Equal(2L * CdDiscParameters.BytesPerSector, cdStream.Length, "CD streaming byte length");
    Equal(cdStream.Length, cdSummary.BytesWritten, "CD streaming summary length");

    DvdStreamingOptions dvdOptions = new(
        TotalSectors: 16,
        InnerRadiusMm: 24,
        OuterRadiusMm: 58,
        ChannelBitLengthNm: 133.33,
        StartAngleDegrees: 0,
        FastParallelism: 1);
    using MemoryStream dvdStream = new();
    DvdStreamingSummary dvdSummary = DvdStreamingGenerator.Generate(
        sourcePath,
        dvdStream,
        dvdOptions);
    Equal(dvdOptions.ContentLength, dvdStream.Length, "DVD streaming byte length");
    Equal(dvdStream.Length, dvdSummary.BytesWritten, "DVD streaming summary length");
    True(dvdStream.ToArray().Distinct().Count() > 1, "DVD streaming content is generated");

    string hybridDirectory = Path.Combine(
        Path.GetDirectoryName(sourcePath)!,
        "hybrid-stream-data");
    string nestedDirectory = Path.Combine(hybridDirectory, "资料");
    Directory.CreateDirectory(nestedDirectory);
    byte[] hybridFile = Enumerable.Range(0, 3001)
        .Select(index => (byte)((index * 73 + 19) % 251))
        .ToArray();
    File.WriteAllBytes(Path.Combine(nestedDirectory, "stream-test.bin"), hybridFile);
    DvdStreamingOptions hybridOptions = dvdOptions with { TotalSectors = 64 };
    DvdHybridStreamingPlan hybridPlan = DvdStreamingGenerator.PrepareHybrid(
        hybridDirectory,
        hybridOptions,
        "STREAM_TEST");
    True(
        hybridPlan.DrawingStartLba >= hybridPlan.FilesystemEndLbaExclusive,
        "hybrid drawing starts after filesystem data");
    Equal(
        0U,
        (hybridPlan.DrawingStartLba + 0x30000U) & 0xFU,
        "hybrid drawing starts on an ECC Block boundary");
    using MemoryStream hybridBacking = new();
    using (ForwardOnlyWriteStream hybridStream = new(hybridBacking))
    {
        DvdStreamingSummary hybridSummary = DvdStreamingGenerator.GenerateHybrid(
            sourcePath,
            hybridStream,
            hybridOptions,
            hybridPlan);
        Equal(hybridOptions.ContentLength, hybridSummary.BytesWritten, "hybrid streaming summary length");
    }

    byte[] hybridBytes = hybridBacking.ToArray();
    Equal(hybridOptions.ContentLength, hybridBytes.LongLength, "hybrid streaming byte length");
    True(
        hybridBytes.AsSpan((16 * 2048) + 1, 5).SequenceEqual("CD001"u8),
        "hybrid stream contains an ISO9660 primary volume descriptor");
    int prefixBytes = checked((int)(hybridPlan.DrawingStartLba * 2048));
    True(
        hybridBytes.AsSpan(0, prefixBytes).IndexOf(hybridFile) >= 0,
        "hybrid stream contains the planned source file before drawing data");
    True(
        hybridBytes.AsSpan(prefixBytes).ToArray().Distinct().Count() > 1,
        "hybrid stream switches to generated drawing data at the planned LBA");

    new OpticalBurnRequest(
        "test-recorder",
        OpticalBurnMediaKind.CdAudio,
        cdStream.Length,
        (_, _) => { }).Validate();
    new OpticalBurnRequest(
        "test-recorder",
        OpticalBurnMediaKind.DvdData,
        dvdStream.Length,
        (_, _) => { }).Validate();
    Throws<ArgumentOutOfRangeException>(
        () => new OpticalBurnRequest(
            "test-recorder",
            OpticalBurnMediaKind.DvdData,
            dvdStream.Length - 1,
            (_, _) => { }).Validate(),
        "DVD streaming rejects partial sectors");

    byte[] expected = Enumerable.Range(0, CdDiscParameters.BytesPerSector * 2)
        .Select(index => (byte)(index % 251))
        .ToArray();
    using GeneratedContentComStream generated = new(
        expected.Length,
        (output, _) => output.Write(expected),
        progress: null,
        CancellationToken.None);
    generated.WaitUntilPrebuffered(CancellationToken.None);
    generated.Stat(out STATSTG stat, 0);
    Equal((long)expected.Length, stat.cbSize, "COM stream reports declared length");
    IntPtr countPointer = Marshal.AllocHGlobal(sizeof(long));
    try
    {
        generated.Seek(0, 2, countPointer);
        Equal((long)expected.Length, Marshal.ReadInt64(countPointer), "COM stream seek-to-end length");
        generated.Seek(0, 0, countPointer);
        byte[] actual = new byte[expected.Length];
        int offset = 0;
        while (offset < actual.Length)
        {
            byte[] chunk = new byte[Math.Min(777, actual.Length - offset)];
            generated.Read(chunk, chunk.Length, countPointer);
            int read = Marshal.ReadInt32(countPointer);
            Buffer.BlockCopy(chunk, 0, actual, offset, read);
            offset += read;
        }

        True(expected.SequenceEqual(actual), "buffered COM stream preserves generated bytes");
    }
    finally
    {
        Marshal.FreeHGlobal(countPointer);
    }

    TestCdTrackAtOnceCallOrder();
}

static void TestCdTrackAtOnceCallOrder()
{
    byte[] sector = new byte[2352];
    using GeneratedContentComStream content = new(
        sector.Length,
        (output, _) => output.Write(sector),
        progress: null,
        CancellationToken.None);
    content.WaitUntilPrebuffered(CancellationToken.None);
    OpticalBurnRequest request = new(
        "test-recorder",
        OpticalBurnMediaKind.CdAudio,
        sector.Length,
        (_, _) => { });
    RecordingCdTrackAtOnceSession session = new();

    WindowsImapiBurner.BurnCdAudio(
        session,
        content,
        request,
        CancellationToken.None);

    Equal(
        "DoNotFinalizeMedia,PrepareMedia,NumberOfExistingTracks,"
        + "FreeSectorsOnMedia,AddAudioTrack,ReleaseMedia",
        string.Join(',', session.Calls),
        "CD Track-At-Once preparation order");

    RecordingCdTrackAtOnceSession failingSession = new()
    {
        AddAudioTrackError = new COMException(
            "original write failure",
            unchecked((int)0xC0AA050D)),
        ReleaseMediaError = new COMException(
            "cleanup says not prepared",
            unchecked((int)0xC0AA0502)),
    };
    try
    {
        WindowsImapiBurner.BurnCdAudio(
            failingSession,
            content,
            request,
            CancellationToken.None);
        throw new InvalidOperationException("CD write failure preservation: expected COMException.");
    }
    catch (COMException exception)
    {
        Equal(
            unchecked((int)0xC0AA050D),
            exception.HResult,
            "CD write failure is not masked by cleanup");
    }
}

static void WriteSolidPng(string path, int width, int height, byte level)
{
    int stride = checked(width * 4);
    byte[] pixels = new byte[checked(stride * height)];
    for (int offset = 0; offset < pixels.Length; offset += 4)
    {
        pixels[offset] = level;
        pixels[offset + 1] = level;
        pixels[offset + 2] = level;
        pixels[offset + 3] = 255;
    }

    BitmapSource bitmap = BitmapSource.Create(
        width,
        height,
        96,
        96,
        PixelFormats.Bgra32,
        null,
        pixels,
        stride);
    PngBitmapEncoder encoder = new();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using FileStream output = File.Create(path);
    encoder.Save(output);
}

static void ValidateRingSafetyMargins(
    string path,
    double canvasOuterRadiusMm,
    double safeInnerLimitMm,
    double safeOuterLimitMm)
{
    using FileStream stream = File.OpenRead(path);
    BitmapFrame frame = BitmapFrame.Create(
        stream,
        BitmapCreateOptions.PreservePixelFormat,
        BitmapCacheOption.OnLoad);
    FormatConvertedBitmap converted = new(frame, PixelFormats.Bgra32, null, 0);
    int stride = checked(converted.PixelWidth * 4);
    byte[] pixels = new byte[checked(stride * converted.PixelHeight)];
    converted.CopyPixels(pixels, stride, 0);
    double centre = converted.PixelWidth / 2.0;
    int darkRingPixels = 0;
    for (int y = 0; y < converted.PixelHeight; y++)
    {
        for (int x = 0; x < converted.PixelWidth; x++)
        {
            int offset = (y * stride) + (x * 4);
            byte level = pixels[offset];
            double dx = x - centre;
            double dy = y - centre;
            double radiusMm = Math.Sqrt((dx * dx) + (dy * dy))
                * canvasOuterRadiusMm / centre;
            if (radiusMm < safeInnerLimitMm || radiusMm > safeOuterLimitMm)
            {
                True(level >= 250, "ring safety margins stay blank");
            }
            else if (level < 32)
            {
                darkRingPixels++;
            }
        }
    }

    True(darkRingPixels > 100, "ring layout contains copied image pixels");
}

sealed class ForwardOnlyWriteStream(Stream destination) : Stream
{
    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => destination.Flush();

    public override void Write(byte[] buffer, int offset, int count)
        => destination.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer)
        => destination.Write(buffer);

    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override void SetLength(long value)
        => throw new NotSupportedException();
}

sealed class FutureBluRayModule : IOpticalDiscModule
{
    public DiscModuleDescriptor Descriptor { get; } = new(
        "bluray-future-test",
        "Future Blu-ray module",
        OpticalDiscFamily.BluRay,
        "Proves that a Blu-ray implementation can join the catalog without changing CD or DVD.",
        SupportsCancellation: true,
        Commands:
        [
            new DiscCommandDescriptor(
                "bd-generate",
                "Generate Blu-ray image",
                "Test-only future command.",
                DiscModuleCapabilities.DataImageGeneration,
                [new("output", "Output image", DiscOptionValueType.OutputFile, Required: true)]),
        ]);

    public Task<DiscJobResult> ExecuteAsync(
        DiscJobRequest request,
        IProgress<DiscJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DiscJobResult(
            Descriptor.Id,
            request.Command,
            0,
            "Future Blu-ray module test completed.",
            "future.iso"));
    }
}

sealed class RecordingCdTrackAtOnceSession : ICdTrackAtOnceSession
{
    internal List<string> Calls { get; } = [];

    internal Exception? AddAudioTrackError { get; init; }

    internal Exception? ReleaseMediaError { get; init; }

    public bool DoNotFinalizeMedia
    {
        set => Calls.Add(nameof(DoNotFinalizeMedia));
    }

    public int NumberOfExistingTracks
    {
        get
        {
            Calls.Add(nameof(NumberOfExistingTracks));
            return 0;
        }
    }

    public long FreeSectorsOnMedia
    {
        get
        {
            Calls.Add(nameof(FreeSectorsOnMedia));
            return 1;
        }
    }

    public void PrepareMedia() => Calls.Add(nameof(PrepareMedia));

    public void AddAudioTrack(IStream content)
    {
        Calls.Add(nameof(AddAudioTrack));
        if (AddAudioTrackError is not null)
        {
            throw AddAudioTrackError;
        }
    }

    public void CancelAddTrack() => Calls.Add(nameof(CancelAddTrack));

    public void ReleaseMedia()
    {
        Calls.Add(nameof(ReleaseMedia));
        if (ReleaseMediaError is not null)
        {
            throw ReleaseMediaError;
        }
    }
}

sealed class ConflictingModule : IOpticalDiscModule
{
    public DiscModuleDescriptor Descriptor { get; } = new(
        "conflict-test",
        "Conflicting module",
        OpticalDiscFamily.BluRay,
        "Test-only duplicate command.",
        SupportsCancellation: false,
        Commands:
        [
            new DiscCommandDescriptor(
                "bd-generate",
                "Conflict",
                "Duplicate command.",
                DiscModuleCapabilities.None,
                []),
        ]);

    public Task<DiscJobResult> ExecuteAsync(
        DiscJobRequest request,
        IProgress<DiscJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
