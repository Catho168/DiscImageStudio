using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;

internal static class CdAudioCompatibilityTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-cd-audio-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, "asymmetric.png");
            WritePatternPng(sourcePath);
            CdDiscParameters parameters = new(
                InnerRadiusMm: 24.5,
                OuterRadiusMm: 24.8,
                Sectors: 8,
                LinearVelocityMmPerSecond: 1200,
                ImageOuterRadiusMm: 58);
            foreach (bool interleave in new[] { false, true })
            {
                TestFileAndStreamingFormats(directory, sourcePath, parameters, interleave);
            }

            TestCommandOutput(directory, sourcePath, parameters);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestFileAndStreamingFormats(
        string directory,
        string sourcePath,
        CdDiscParameters parameters,
        bool interleave)
    {
        string name = interleave ? "interleaved" : "plain";
        string rawPath = Path.Combine(directory, name + ".raw");
        string wavPath = Path.Combine(directory, name + " 音轨.WAV");
        CdGenerationSummary rawSummary = CdTrackGenerator.Generate(
            sourcePath, rawPath, parameters, interleave);
        CdTrackGenerator.Generate(sourcePath, wavPath, parameters, interleave);
        byte[] raw = File.ReadAllBytes(rawPath);
        byte[] wav = File.ReadAllBytes(wavPath);
        Equal(parameters.TotalBytes, raw.LongLength, name + " raw byte length");
        Equal(raw.LongLength, rawSummary.BytesWritten, name + " raw summary length");
        ValidateWaveHeader(wav, raw.Length, name);
        ValidateCueFile(wavPath);
        True(!File.Exists(Path.ChangeExtension(rawPath, ".cue")), name + " legacy raw does not create a cue");
        byte[] pcm = wav[44..];
        AssertSwappedPairs(raw, pcm, name);

        using MemoryStream legacyStream = new();
        CdTrackGenerator.GenerateToStream(sourcePath, legacyStream, parameters, interleave);
        True(raw.SequenceEqual(legacyStream.ToArray()), name + " default stream preserves raw format");

        using MemoryStream pcmStream = new();
        CdGenerationSummary streamSummary;
        using (ForwardOnlyWriteStream output = new(pcmStream))
        {
            streamSummary = CdTrackGenerator.GenerateToStream(
                sourcePath,
                output,
                parameters,
                interleave,
                audioByteOrder: CdAudioByteOrder.LittleEndian);
        }

        Equal(parameters.TotalBytes, pcmStream.Length, name + " forward stream has no WAV header");
        Equal(pcmStream.Length, streamSummary.BytesWritten, name + " forward stream summary length");
        True(pcm.SequenceEqual(pcmStream.ToArray()), name + " forward PCM stream matches WAV data");

        foreach (int byteStep in new[] { 1, 3, 48 })
        {
            string rawPreview = Path.Combine(directory, $"{name}-raw-{byteStep}.png");
            string wavPreview = Path.Combine(directory, $"{name}-wav-{byteStep}.png");
            CdTrackGenerator.PreviewTrack(rawPath, rawPreview, parameters, 256, byteStep);
            CdTrackGenerator.PreviewTrack(wavPath, wavPreview, parameters, 256, byteStep);
            True(
                ReadPngPixels(rawPreview).SequenceEqual(ReadPngPixels(wavPreview)),
                $"{name} WAV preview normalizes PCM byte order at step {byteStep}");
        }

        if (interleave)
        {
            TestWaveChunkParsing(directory, rawPath, wav, parameters);
        }
    }

    private static void TestCommandOutput(
        string directory,
        string sourcePath,
        CdDiscParameters parameters)
    {
        string outputPath = Path.Combine(directory, "command.wav");
        DiscJobResult result = new CdDiscModule().ExecuteAsync(new DiscJobRequest(
            "cd-generate",
            [
                "--input", sourcePath,
                "--output", outputPath,
                "--r0", parameters.InnerRadiusMm.ToString(CultureInfo.InvariantCulture),
                "--r1", parameters.OuterRadiusMm.ToString(CultureInfo.InvariantCulture),
                "--sectors", parameters.Sectors.ToString(CultureInfo.InvariantCulture),
                "--velocity", parameters.LinearVelocityMmPerSecond.ToString(CultureInfo.InvariantCulture),
                "--outer", parameters.ImageOuterRadiusMm.ToString(CultureInfo.InvariantCulture),
                "--interleave", "true",
            ])).GetAwaiter().GetResult();
        True(result.Succeeded, "CD command generates WAV successfully");
        ValidateCueFile(outputPath);
        True(
            File.ReadAllBytes(Path.Combine(directory, "interleaved 音轨.WAV"))
                .SequenceEqual(File.ReadAllBytes(outputPath)),
            "CD command WAV matches file generation API");
    }

    private static void ValidateCueFile(string wavPath)
    {
        string[] lines = File.ReadAllText(Path.ChangeExtension(wavPath, ".cue"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Equal(3, lines.Length, "WAV cue line count");
        Equal($"FILE \"{Path.GetFileName(wavPath)}\" WAVE", lines[0], "WAV cue declares container format");
        Equal("TRACK 01 AUDIO", lines[1], "WAV cue declares audio track");
        Equal("INDEX 01 00:00:00", lines[2], "WAV cue track begins at the data start");
    }

    private static void TestWaveChunkParsing(
        string directory,
        string rawPath,
        byte[] wav,
        CdDiscParameters parameters)
    {
        using MemoryStream decorated = new();
        decorated.Write(wav.AsSpan(0, 12));
        WriteWaveChunk(decorated, "JUNK", [0x01, 0x02, 0x03]);
        decorated.Write(wav.AsSpan(12));
        WriteWaveChunk(decorated, "LIST", Enumerable.Repeat((byte)0x10, 2048).ToArray());
        byte[] decoratedBytes = decorated.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            decoratedBytes.AsSpan(4, 4), checked((uint)(decoratedBytes.Length - 8)));
        string decoratedPath = Path.Combine(directory, "metadata.wav");
        File.WriteAllBytes(decoratedPath, decoratedBytes);

        // The larger geometry would expose trailing metadata if the reader failed
        // to honor the data chunk's declared size.
        CdDiscParameters largerGeometry = parameters with { Sectors = parameters.Sectors + 1 };
        string rawPreview = Path.Combine(directory, "raw-larger-geometry.png");
        string decoratedPreview = Path.Combine(directory, "metadata-preview.png");
        CdTrackGenerator.PreviewTrack(rawPath, rawPreview, largerGeometry, 256, 1);
        CdTrackGenerator.PreviewTrack(decoratedPath, decoratedPreview, largerGeometry, 256, 1);
        True(
            ReadPngPixels(rawPreview).SequenceEqual(ReadPngPixels(decoratedPreview)),
            "WAV reader skips odd-sized unknown chunks and excludes trailing metadata");

        foreach ((int offset, uint value, bool shortValue, string name) in new[]
        {
            (20, 3U, true, "floating-point format"),
            (22, 1U, true, "mono audio"),
            (24, 48000U, false, "non-CD sample rate"),
            (34, 8U, true, "8-bit samples"),
        })
        {
            byte[] invalid = (byte[])wav.Clone();
            if (shortValue)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(invalid.AsSpan(offset, 2), (ushort)value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(offset, 4), value);
            }

            string invalidPath = Path.Combine(directory, "unsupported.wav");
            File.WriteAllBytes(invalidPath, invalid);
            Throws<InvalidDataException>(
                () => CdTrackGenerator.PreviewTrack(
                    invalidPath, Path.Combine(directory, "unsupported-preview.png"), parameters, 256, 1),
                "WAV preview rejects " + name);
        }
    }

    private static void WriteWaveChunk(Stream output, string id, byte[] payload)
    {
        output.Write(Encoding.ASCII.GetBytes(id));
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)payload.Length));
        output.Write(length);
        output.Write(payload);
        if ((payload.Length & 1) != 0)
        {
            output.WriteByte(0);
        }
    }

    private static void ValidateWaveHeader(byte[] wav, int dataBytes, string name)
    {
        Equal(dataBytes + 44, wav.Length, name + " WAV has a 44-byte PCM header");
        Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4), name + " RIFF signature");
        Equal((uint)(wav.Length - 8), UInt32(wav, 4), name + " RIFF size");
        Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4), name + " WAVE signature");
        Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4), name + " format chunk");
        Equal(16U, UInt32(wav, 16), name + " PCM format size");
        Equal((ushort)1, UInt16(wav, 20), name + " integer PCM format");
        Equal((ushort)2, UInt16(wav, 22), name + " stereo channels");
        Equal(44100U, UInt32(wav, 24), name + " sample rate");
        Equal(176400U, UInt32(wav, 28), name + " byte rate");
        Equal((ushort)4, UInt16(wav, 32), name + " sample frame size");
        Equal((ushort)16, UInt16(wav, 34), name + " sample bit depth");
        Equal("data", Encoding.ASCII.GetString(wav, 36, 4), name + " data chunk");
        Equal((uint)dataBytes, UInt32(wav, 40), name + " PCM data size");
    }

    private static void AssertSwappedPairs(byte[] raw, byte[] pcm, string name)
    {
        Equal(raw.Length, pcm.Length, name + " payload length is unchanged");
        int asymmetricPairs = 0;
        for (int offset = 0; offset < raw.Length; offset += 2)
        {
            if (raw[offset] != raw[offset + 1])
            {
                asymmetricPairs++;
            }

            if (raw[offset] != pcm[offset + 1] || raw[offset + 1] != pcm[offset])
            {
                throw new InvalidOperationException(
                    $"{name} PCM byte order: pair at byte {offset} was not swapped exactly once.");
            }
        }

        True(asymmetricPairs > 10, name + " input exposes swaps using unequal byte pairs");
    }

    private static uint UInt32(byte[] bytes, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

    private static ushort UInt16(byte[] bytes, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));

    private static void WritePatternPng(string path)
    {
        const int size = 256;
        const int stride = size * 4;
        byte[] pixels = new byte[size * stride];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                byte level = ((x * 17) + (y * 31) + ((x * y) % 23)) % 53 < 21
                    ? (byte)0
                    : (byte)255;
                int offset = (y * stride) + (x * 4);
                pixels[offset] = level;
                pixels[offset + 1] = level;
                pixels[offset + 2] = level;
                pixels[offset + 3] = 255;
            }
        }

        BitmapSource bitmap = BitmapSource.Create(
            size, size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }

    private static byte[] ReadPngPixels(string path)
    {
        using FileStream input = File.OpenRead(path);
        BitmapFrame frame = BitmapFrame.Create(
            input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        FormatConvertedBitmap converted = new(frame, PixelFormats.Bgra32, null, 0);
        int stride = checked(converted.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void Equal<T>(T expected, T actual, string name)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}.");
        }
    }

    private static void True(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{name}: condition was false.");
        }
    }

    private static void Throws<TException>(Action action, string name)
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
}
