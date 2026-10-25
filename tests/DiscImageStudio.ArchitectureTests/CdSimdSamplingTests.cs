using System.IO;
using System.Runtime.Intrinsics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Cd;

internal static class CdSimdSamplingTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-cd-simd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (bool colour in new[] { false, true })
            {
                const int width = 259, height = 197;
                byte[] pixels = CreatePixels(width, height, colour);
                string path = Path.Combine(directory, colour ? "colour.png" : "binary.png");
                WritePng(path, width, height, pixels);
                CdDiscParameters[] geometries =
                [
                    new(24.5, 57.5, 64),
                    new(24.5, 57.5, 64, StartAngleRadians: -Math.PI / 3),
                    new(24.5, 57.5, 64, StartAngleRadians: Math.PI),
                    // The short fixture spans angles comparable to a full CD.
                    new(24.5, 57.5, 64, LinearVelocityMmPerSecond: 6_000_000),
                    new(24.5, 57.5, 8, StartAngleRadians: 1e12),
                    new(24.5, 57.5, 8, ImageOuterRadiusMm: 20),
                ];
                foreach (CdDiscParameters parameters in geometries)
                foreach (bool interleave in new[] { false, true })
                foreach (CdAudioByteOrder byteOrder in Enum.GetValues<CdAudioByteOrder>())
                    Compare(path, pixels, width, height, parameters, interleave, byteOrder);
            }

            // At the first byte these geometries deliberately hit x/y = 63.5.
            // Values immediately around that tie exercise the scalar guard.
            byte[] boundaryPixels = CreatePixels(64, 64, colour: false);
            string boundaryPath = Path.Combine(directory, "boundary.png");
            WritePng(boundaryPath, 64, 64, boundaryPixels);
            foreach (double angle in new[] { 0d, Math.PI / 2, Math.PI, -Math.PI / 2 })
            foreach (double offset in new[] { -1e-12, 0d, 1e-12 })
            {
                CdDiscParameters parameters = new(24, 25, 4,
                    StartAngleRadians: angle, ImageOuterRadiusMm: (32d * 24 / 31.5) + offset);
                Compare(boundaryPath, boundaryPixels, 64, 64, parameters, false, CdAudioByteOrder.BigEndian);
            }

            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            using MemoryStream cancelledOutput = new();
            try
            {
                CdTrackGenerator.GenerateToStream(boundaryPath, cancelledOutput, new(24, 57, 8), false,
                    cancellationToken: cancellation.Token);
                throw new InvalidOperationException("CD SIMD ignored cancellation.");
            }
            catch (OperationCanceledException)
            {
                Require(cancelledOutput.Length == 0, "pre-cancel writes no audio");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        Console.WriteLine(Vector256.IsHardwareAccelerated
            ? "cd-simd-sampling: passed (scalar/SIMD byte parity, independent pixel reference, full-radius/angle range, colour/alpha, non-square, half-pixel boundaries, interleave, byte order, cancellation)"
            : "cd-simd-sampling: passed (scalar/automatic fallback byte parity; hardware SIMD unavailable)");
    }

    private static void Compare(string path, byte[] pixels, int width, int height,
        CdDiscParameters parameters, bool interleave, CdAudioByteOrder byteOrder)
    {
        using MemoryStream scalar = new(), simd = new();
        CdTrackGenerator.GenerateToStream(path, scalar, parameters, interleave,
            audioByteOrder: byteOrder, useSimdSampling: false);
        CdTrackGenerator.GenerateToStream(path, simd, parameters, interleave,
            audioByteOrder: byteOrder, useSimdSampling: true);
        byte[] expected = scalar.ToArray();
        byte[] actual = simd.ToArray();
        Require(expected.LongLength == parameters.TotalBytes && actual.AsSpan().SequenceEqual(expected),
            $"scalar/SIMD output: {parameters}, interleave={interleave}, order={byteOrder}");
        if (!interleave)
        {
            byte[] reference = new byte[expected.Length];
            for (int index = 0; index < reference.Length; index++)
            {
                (double x, double y) = parameters.ImagePointFromByte(index, width);
                int pixelX = (int)Math.Round(x), pixelY = (int)Math.Round(y);
                int gray = 255;
                if ((uint)pixelX < width && (uint)pixelY < height)
                {
                    int offset = ((pixelY * width) + pixelX) * 4;
                    gray = ((pixels[offset + 2] * 299) + (pixels[offset + 1] * 587)
                        + (pixels[offset] * 114)) / 1000;
                }
                reference[index] = gray < 128 ? (byte)0x0D : (byte)0xAA;
            }
            if (byteOrder == CdAudioByteOrder.LittleEndian)
                for (int index = 0; index < reference.Length; index += 2)
                    (reference[index], reference[index + 1]) = (reference[index + 1], reference[index]);
            Require(actual.AsSpan().SequenceEqual(reference), "independent scalar pixel reference");
        }
    }

    private static byte[] CreatePixels(int width, int height, bool colour)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        Random random = new(57241);
        for (int index = 0; index < pixels.Length; index += 4)
        {
            byte value = random.Next(2) == 0 ? (byte)0 : (byte)255;
            pixels[index] = colour ? (byte)random.Next(256) : value;
            pixels[index + 1] = colour ? (byte)random.Next(256) : value;
            pixels[index + 2] = colour ? (byte)random.Next(256) : value;
            pixels[index + 3] = colour ? (byte)random.Next(256) : (byte)255;
        }
        return pixels;
    }

    private static void WritePng(string path, int width, int height, byte[] pixels)
    {
        BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
            null, pixels, width * 4);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"CD SIMD sampling: {message}.");
    }
}
