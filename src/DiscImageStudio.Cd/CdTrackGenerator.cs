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
    TimeSpan Elapsed);

public static class CdTrackGenerator
{
    private const byte BlackPaletteValue = 0x10;
    private const byte WhitePaletteValue = 0xAA;

    public static CdGenerationSummary Generate(
        string imagePath,
        string outputPath,
        CdDiscParameters parameters,
        bool interleave,
        Action<CdProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        parameters.Validate();
        string sourceFullPath = Path.GetFullPath(imagePath);
        string outputFullPath = Path.GetFullPath(outputPath);
        EnsureParentDirectory(outputFullPath);
        RasterImage source = RasterImage.Load(sourceFullPath);
        long totalBytes = parameters.TotalBytes;
        CddaInterleaver? cddaInterleaver = interleave ? new CddaInterleaver() : null;
        Stopwatch stopwatch = Stopwatch.StartNew();
        long progressInterval = Math.Max(CdDiscParameters.BytesPerSector, totalBytes / 200);
        long nextProgress = progressInterval;

        using FileStream file = new(
            outputFullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        using BufferedStream output = new(file, 1024 * 1024);
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
                output.WriteByte(value);
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
            outputFullPath,
            parameters.Sectors,
            totalBytes,
            interleave,
            stopwatch.Elapsed);
    }

    public static void PreviewWarp(
        string imagePath,
        string outputPath,
        CdDiscParameters generated,
        CdDiscParameters actual,
        int outputSize,
        int samplesPerSector,
        CancellationToken cancellationToken = default)
    {
        generated.Validate();
        actual.Validate();
        ValidatePreview(outputSize, samplesPerSector);
        RasterImage source = RasterImage.Load(Path.GetFullPath(imagePath));
        byte[] pixels = CreateWhitePixels(outputSize);
        int pixelCount = checked(outputSize * outputSize);
        int[] levelSums = new int[pixelCount];
        int[] sampleCounts = new int[pixelCount];
        long step = Math.Max(1, CdDiscParameters.BytesPerSector / samplesPerSector);
        long totalBytes = generated.TotalBytes;
        for (long globalByte = 0; globalByte < totalBytes; globalByte += step)
        {
            if ((globalByte & 0xFFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            byte gray = Sample(source, generated, globalByte);
            byte level = gray < 128 ? (byte)0 : (byte)255;
            (double pixelX, double pixelY) = actual.ImagePointFromByte(globalByte, outputSize);
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

    public static void PreviewTrack(
        string trackPath,
        string outputPath,
        CdDiscParameters actual,
        int outputSize,
        int byteStep,
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
        byte[] buffer = new byte[1024 * 1024];
        long globalOffset = 0;
        long maximumBytes = actual.TotalBytes;
        using FileStream input = new(
            Path.GetFullPath(trackPath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: buffer.Length,
            FileOptions.SequentialScan);
        int bytesRead;
        while (globalOffset < maximumBytes
            && (bytesRead = input.Read(buffer, 0, (int)Math.Min(buffer.Length, maximumBytes - globalOffset))) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long remainder = globalOffset % byteStep;
            int first = remainder == 0 ? 0 : checked((int)(byteStep - remainder));
            for (int index = first; index < bytesRead; index += byteStep)
            {
                long globalByte = globalOffset + index;
                byte level = PaletteToGray(buffer[index]);
                (double pixelX, double pixelY) = actual.ImagePointFromByte(globalByte, outputSize);
                int x = (int)Math.Round(pixelX);
                int y = (int)Math.Round(pixelY);
                SetGrayPixel(pixels, outputSize, x, y, level);
            }

            globalOffset += bytesRead;
        }

        WritePng(Path.GetFullPath(outputPath), outputSize, pixels);
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
