using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DiscImageStudio.Cd;

public sealed record CdProgress(long CompletedBytes, long TotalBytes, TimeSpan Elapsed)
{
    public double Fraction => TotalBytes == 0 ? 0 : (double)CompletedBytes / TotalBytes;
}

public sealed record CdGenerationSummary(
    string SourceImage,
    string OutputTrack,
    long Sectors,
    long BytesWritten,
    bool Interleaved,
    TimeSpan Elapsed)
{
    public string? CueSheetPath { get; init; }
}

public static class CdTrackGenerator
{
    private const byte BlackPaletteValue = 0x10;
    private const byte WhitePaletteValue = 0xAA;

    public static CdGenerationSummary Generate(
        string imagePath,
        string outputPath,
        CdDiscParameters parameters,
        bool interleave,
        bool writeCue = true,
        Action<CdProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        parameters.Validate();
        string sourceFullPath = Path.GetFullPath(imagePath);
        string outputFullPath = Path.GetFullPath(outputPath);
        string extension = Path.GetExtension(outputFullPath);
        bool wave = extension.Equals(".wav", StringComparison.OrdinalIgnoreCase);
        if (!wave && !extension.Equals(".raw", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("CD output must be .wav (PCM audio + CUE) or .raw (big-endian cdrecord audio).", nameof(outputPath));
        }

        if (wave)
        {
            CdWaveFile.ValidateLength(parameters.TotalBytes);
        }

        if (sourceFullPath.Equals(outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The output must not overwrite the source image.", nameof(outputPath));
        }

        EnsureParentDirectory(outputFullPath);
        using FileStream file = new(
            outputFullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        if (wave)
        {
            CdWaveFile.WriteHeader(file, parameters.TotalBytes);
        }

        CdGenerationSummary summary = GenerateToStream(
            sourceFullPath,
            file,
            parameters,
            interleave,
            progress,
            cancellationToken,
            outputFullPath,
            wave ? CdAudioByteOrder.LittleEndian : CdAudioByteOrder.BigEndian);
        file.Dispose();
        CdTrackMetadata.Save(
            outputFullPath,
            new CdTrackMetadata(
                sourceFullPath,
                outputFullPath,
                parameters.InnerRadiusMm,
                parameters.OuterRadiusMm,
                parameters.Sectors,
                parameters.LinearVelocityMmPerSecond,
                parameters.StartAngleRadians * 180.0 / Math.PI,
                parameters.ImageOuterRadiusMm,
                interleave));
        return wave && writeCue
            ? summary with { CueSheetPath = CdWaveFile.WriteCue(outputFullPath) }
            : summary;
    }

    public static CdGenerationSummary GenerateToStream(
        string imagePath,
        Stream output,
        CdDiscParameters parameters,
        bool interleave,
        Action<CdProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string outputDescription = "direct-burn-stream",
        CdAudioByteOrder audioByteOrder = CdAudioByteOrder.BigEndian)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("CD output stream must be writable.", nameof(output));
        }

        CdAudioSamples.Validate(audioByteOrder);
        parameters.Validate();
        string sourceFullPath = Path.GetFullPath(imagePath);
        RasterImage source = RasterImage.Load(sourceFullPath);
        long totalBytes = parameters.TotalBytes;
        CddaInterleaver? cddaInterleaver = interleave ? new CddaInterleaver(audioByteOrder) : null;
        byte[]? plainSector = interleave ? null : new byte[CdDiscParameters.BytesPerSector];
        Stopwatch stopwatch = Stopwatch.StartNew();
        long progressInterval = Math.Max(CdDiscParameters.BytesPerSector, totalBytes / 200);
        long nextProgress = progressInterval;

        for (long globalByte = 0; globalByte < totalBytes; globalByte++)
        {
            if ((globalByte & 0xFFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            byte gray = Sample(source, parameters, globalByte);
            byte value = gray < 128 ? BlackPaletteValue : WhitePaletteValue;
            if (cddaInterleaver is null)
            {
                int sectorOffset = (int)(globalByte % CdDiscParameters.BytesPerSector);
                plainSector![sectorOffset] = value;
                if (sectorOffset == plainSector.Length - 1)
                {
                    CdAudioSamples.WriteSector(output, plainSector, audioByteOrder);
                }
            }
            else
            {
                cddaInterleaver.Add(value, output);
            }

            long completed = globalByte + 1;
            if (completed >= nextProgress)
            {
                progress?.Invoke(new CdProgress(completed, totalBytes, stopwatch.Elapsed));
                nextProgress = checked(completed + progressInterval);
            }
        }

        cddaInterleaver?.Flush(output);
        output.Flush();
        stopwatch.Stop();
        progress?.Invoke(new CdProgress(totalBytes, totalBytes, stopwatch.Elapsed));
        return new CdGenerationSummary(
            sourceFullPath,
            outputDescription,
            parameters.Sectors,
            totalBytes,
            interleave,
            stopwatch.Elapsed);
    }

    public static void PreviewWarp(
        string imagePath,
        string outputPath,
        CdDiscParameters parameters,
        int outputSize,
        int samplesPerSector,
        CancellationToken cancellationToken = default)
    {
        parameters.Validate();
        ValidatePreview(outputSize, samplesPerSector);
        RasterImage source = RasterImage.Load(Path.GetFullPath(imagePath));
        byte[] pixels = CreateWhitePixels(outputSize);
        int pixelCount = checked(outputSize * outputSize);
        int[] levelSums = new int[pixelCount];
        int[] sampleCounts = new int[pixelCount];
        long step = Math.Max(1, CdDiscParameters.BytesPerSector / samplesPerSector);
        long totalBytes = parameters.TotalBytes;
        for (long globalByte = 0; globalByte < totalBytes; globalByte += step)
        {
            if ((globalByte & 0xFFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            byte gray = Sample(source, parameters, globalByte);
            byte level = gray < 128 ? (byte)0 : (byte)255;
            (double pixelX, double pixelY) = parameters.ImagePointFromByte(globalByte, outputSize);
            int x = (int)Math.Round(pixelX);
            int y = (int)Math.Round(pixelY);
            if ((uint)x >= (uint)outputSize || (uint)y >= (uint)outputSize)
            {
                continue;
            }

            int pixelIndex = checked((y * outputSize) + x);
            levelSums[pixelIndex] += level;
            sampleCounts[pixelIndex]++;
        }

        for (int pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            int count = sampleCounts[pixelIndex];
            if (count == 0)
            {
                continue;
            }

            byte level = (byte)((levelSums[pixelIndex] + (count / 2)) / count);
            int offset = checked(pixelIndex * 4);
            pixels[offset] = level;
            pixels[offset + 1] = level;
            pixels[offset + 2] = level;
        }

        WritePng(Path.GetFullPath(outputPath), outputSize, pixels);
    }

    /// <summary>
    /// Renders a generated track as the disc would look. The generator's delay interleave moves
    /// each logical byte forward in the file, so <paramref name="deinterleave"/> must match the
    /// interleave setting the track was generated with: with identical geometry the read-back
    /// then reproduces the source image exactly. Without it the raw file order is rendered, which
    /// is only correct for a track generated with the interleave switched off.
    /// </summary>
    public static void PreviewTrack(
        string trackPath,
        string outputPath,
        CdDiscParameters actual,
        int outputSize,
        int byteStep,
        bool deinterleave,
        CancellationToken cancellationToken = default)
    {
        actual.Validate();
        if (outputSize is < 64 or > 8192)
        {
            throw new ArgumentOutOfRangeException(nameof(outputSize), "Preview size must be 64..8192.");
        }

        if (byteStep <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteStep), "Byte step must be positive.");
        }

        byte[] pixels = CreateWhitePixels(outputSize);
        using FileStream input = new(
            Path.GetFullPath(trackPath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        Span<byte> signature = stackalloc byte[12];
        int signatureLength = input.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false);
        bool wave = Path.GetExtension(trackPath).Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || (signatureLength == 12 && signature[..4].SequenceEqual("RIFF"u8)
                && signature[8..].SequenceEqual("WAVE"u8));
        input.Position = 0;
        long audioOffset = 0;
        long audioLength = input.Length;
        if (wave)
        {
            (audioOffset, audioLength) = CdWaveFile.ReadAudioRange(input);
        }

        long maximumBytes = Math.Min(actual.TotalBytes, audioLength);
        if (deinterleave)
        {
            SplatDeinterleaved(
                input,
                audioOffset,
                audioLength,
                maximumBytes,
                wave,
                actual,
                outputSize,
                byteStep,
                pixels,
                cancellationToken);
        }
        else
        {
            SplatFileOrder(
                input,
                audioOffset,
                maximumBytes,
                wave,
                actual,
                outputSize,
                byteStep,
                pixels,
                cancellationToken);
        }

        WritePng(Path.GetFullPath(outputPath), outputSize, pixels);
    }

    /// <summary>Renders the file's bytes in file order; correct for a track without interleave.</summary>
    private static void SplatFileOrder(
        FileStream input,
        long audioOffset,
        long maximumBytes,
        bool wave,
        CdDiscParameters actual,
        int outputSize,
        int byteStep,
        byte[] pixels,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024 * 1024];
        long globalOffset = 0;
        input.Position = audioOffset;
        while (globalOffset < maximumBytes)
        {
            int requested = (int)Math.Min(buffer.Length, maximumBytes - globalOffset);
            int bytesRead = input.ReadAtLeast(buffer.AsSpan(0, requested), requested, throwOnEndOfStream: false);
            if (bytesRead == 0)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (wave)
            {
                CdAudioSamples.SwapByteOrder(buffer.AsSpan(0, bytesRead));
            }

            long remainder = globalOffset % byteStep;
            int first = remainder == 0 ? 0 : checked((int)(byteStep - remainder));
            for (int index = first; index < bytesRead; index += byteStep)
            {
                long globalByte = globalOffset + index;
                Splat(actual, outputSize, pixels, globalByte, PaletteToGray(buffer[index]));
            }

            globalOffset += bytesRead;
        }
    }

    /// <summary>
    /// Undoes the generator's delay interleave: the logical byte at index i sits at file index
    /// i + CddaInterleaveTable.FileOffsetFor(i % 24), which needs a bounded look-ahead window.
    /// </summary>
    private static void SplatDeinterleaved(
        FileStream input,
        long audioOffset,
        long audioLength,
        long maximumBytes,
        bool wave,
        CdDiscParameters actual,
        int outputSize,
        int byteStep,
        byte[] pixels,
        CancellationToken cancellationToken)
    {
        const int WindowBytes = 1024 * 1024;
        int lookAhead = CddaInterleaveTable.MaximumFileOffset;
        byte[] buffer = new byte[WindowBytes + lookAhead];
        for (long windowStart = 0; windowStart < maximumBytes; windowStart += WindowBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int logicalCount = (int)Math.Min(WindowBytes, maximumBytes - windowStart);
            long remaining = audioLength - windowStart;
            int wanted = (int)Math.Min(buffer.Length, Math.Max(0, remaining));
            if (wanted == 0)
            {
                break;
            }

            input.Position = audioOffset + windowStart;
            int bytesRead = input.ReadAtLeast(buffer.AsSpan(0, wanted), wanted, throwOnEndOfStream: false);
            bytesRead &= ~1;
            if (wave)
            {
                CdAudioSamples.SwapByteOrder(buffer.AsSpan(0, bytesRead));
            }

            long first = ((windowStart + byteStep - 1) / byteStep) * byteStep;
            for (long logical = first; logical < windowStart + logicalCount; logical += byteStep)
            {
                long local = logical
                    + CddaInterleaveTable.FileOffsetFor((int)(logical % CddaInterleaveTable.BytesPerFrame))
                    - windowStart;
                if (local >= bytesRead)
                {
                    // The delay line loses the last frames of the track; nothing to recover.
                    continue;
                }

                Splat(actual, outputSize, pixels, logical, PaletteToGray(buffer[local]));
            }
        }
    }

    private static void Splat(
        CdDiscParameters actual,
        int outputSize,
        byte[] pixels,
        long globalByte,
        byte level)
    {
        (double pixelX, double pixelY) = actual.ImagePointFromByte(globalByte, outputSize);
        SetGrayPixel(pixels, outputSize, (int)Math.Round(pixelX), (int)Math.Round(pixelY), level);
    }

    private static byte Sample(
        RasterImage source,
        CdDiscParameters parameters,
        long globalByte)
    {
        (double pixelX, double pixelY) = parameters.ImagePointFromByte(globalByte, source.Width);
        return source.GrayAt(pixelX, pixelY);
    }

    private static byte PaletteToGray(byte value) => value switch
    {
        0x10 => 32,
        0x21 => 96,
        0x28 => 160,
        0xAA => 224,
        _ => value,
    };

    private static byte[] CreateWhitePixels(int size)
    {
        byte[] pixels = new byte[checked(size * size * 4)];
        Array.Fill(pixels, (byte)255);
        return pixels;
    }

    private static void SetGrayPixel(byte[] pixels, int size, int x, int y, byte level)
    {
        if ((uint)x >= (uint)size || (uint)y >= (uint)size)
        {
            return;
        }

        int offset = checked(((y * size) + x) * 4);
        pixels[offset] = level;
        pixels[offset + 1] = level;
        pixels[offset + 2] = level;
        pixels[offset + 3] = 255;
    }

    private static void WritePng(string outputPath, int size, byte[] pixels)
    {
        EnsureParentDirectory(outputPath);
        int stride = checked(size * 4);
        BitmapSource bitmap = BitmapSource.Create(
            size,
            size,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(output);
    }

    private static void ValidatePreview(int outputSize, int samplesPerSector)
    {
        if (outputSize is < 64 or > 8192)
        {
            throw new ArgumentOutOfRangeException(nameof(outputSize), "Preview size must be 64..8192.");
        }

        if (samplesPerSector is < 1 or > CdDiscParameters.BytesPerSector)
        {
            throw new ArgumentOutOfRangeException(
                nameof(samplesPerSector),
                $"Samples per sector must be 1..{CdDiscParameters.BytesPerSector}.");
        }
    }

    private static void EnsureParentDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
