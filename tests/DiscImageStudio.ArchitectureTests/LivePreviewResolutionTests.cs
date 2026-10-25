using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using DiscImageStudio.Imaging;
using DiscImageStudio.ViewModels;

internal static class LivePreviewResolutionTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-live-resolution-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            // These entry points validate paths and construct render settings; decoding and
            // rendering belong to the renderer tests, so no large disc artifacts are needed.
            string input = Path.Combine(directory, "input.bin");
            File.WriteAllBytes(input, [0]);
            string output = Path.Combine(directory, "preview.png");
            DiscParametersState state = new()
            {
                CdImagePath = input,
                CdTrackPath = input,
                DvdImagePath = input,
                DvdIsoPath = input,
            };
            ShellViewModel shell = (ShellViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ShellViewModel));
            LivePreviewViewModel preview = new(Dispatcher.CurrentDispatcher, shell, state);

            foreach (int size in new[] { 64, 2048, 4096, 8192 })
            {
                string requested = size.ToString(CultureInfo.InvariantCulture);
                state.CdPreviewSize = state.DvdPreviewSize = requested;
                RequireAccepted(preview, "TryReadCdSettings", output, size);
                RequireAccepted(preview, "TryReadCdWarpSettings", output, size);
                RequireAccepted(preview, "TryBuildDvdCommand", output, size);
                RequireAccepted(preview, "TryBuildDvdCalibrateCommand", output, size);
            }

            state.CdImageProcessingModeIndex = state.DvdImageProcessingModeIndex = 1;
            foreach (int size in new[] { 2048, 4096, 8192 })
            {
                state.CdPreviewSize = state.DvdPreviewSize = size.ToString(CultureInfo.InvariantCulture);
                object?[] cd = RequireAccepted(preview, "TryReadCdWarpSettings", output, size);
                object?[] dvd = RequireAccepted(preview, "TryBuildDvdCalibrateCommand", output, size);
                Require(cd[5] is RingImageLayoutOptions cdRing && cdRing.OutputSize == size,
                    $"CD ring preparation preserves {size} pixels");
                Require(dvd[5] is RingImageLayoutOptions dvdRing && dvdRing.OutputSize == size,
                    $"DVD ring preparation preserves {size} pixels");
            }

            // Dither texture must stay on the same pixel grid used for the final
            // image/burn, independent of the requested display resolution. Merely
            // checking the output preview size would miss this visible mismatch.
            state.CdImageProcessingEnabled = state.DvdImageProcessingEnabled = true;
            foreach (int size in new[] { 64, 2048, 4096, 8192 })
            {
                state.CdPreviewSize = state.DvdPreviewSize = size.ToString(CultureInfo.InvariantCulture);
                object?[] cd = RequireAccepted(preview, "TryReadCdWarpSettings", output, size);
                object?[] dvd = RequireAccepted(preview, "TryBuildDvdCalibrateCommand", output, size);
                Require(cd[5] is RingImageLayoutOptions cdRing && cdRing.OutputSize == RingImageQuality.GenerationSize,
                    $"CD dither source uses production resolution while preview remains {size} pixels");
                Require(dvd[5] is RingImageLayoutOptions dvdRing && dvdRing.OutputSize == RingImageQuality.GenerationSize,
                    $"DVD dither source uses production resolution while preview remains {size} pixels");
            }
            state.CdImageProcessingEnabled = state.DvdImageProcessingEnabled = false;
            foreach (string size in new[] { "0", "63", "8193", "", "not-a-number" })
            {
                state.CdPreviewSize = state.DvdPreviewSize = size;
                foreach (string method in new[]
                {
                    "TryReadCdSettings", "TryReadCdWarpSettings",
                    "TryBuildDvdCommand", "TryBuildDvdCalibrateCommand",
                })
                {
                    (bool succeeded, object?[] arguments) = ReadSettings(preview, method, output);
                    Require(!succeeded && arguments[^1] is string { Length: > 0 },
                        $"{method} rejects invalid size '{size}' with a validation message instead of clamping");
                }
            }

            state.CdPreviewSize = state.DvdPreviewSize = "2048";
            state.CdSamplesPerSector = state.DvdSamplesPerSector = "64";
            RequireAccepted(preview, "TryReadCdSettings", output, 2048, expectedSamples: 64);
            RequireAccepted(preview, "TryReadCdWarpSettings", output, 2048, expectedSamples: 64);
            RequireAccepted(preview, "TryBuildDvdCommand", output, 2048, expectedSamples: 64);
            RequireAccepted(preview, "TryBuildDvdCalibrateCommand", output, 2048, expectedSamples: 64);

            state.CdSamplesPerSector = "2352";
            state.DvdSamplesPerSector = "2048";
            RequireAccepted(preview, "TryReadCdSettings", output, 2048, expectedSamples: 2352);
            RequireAccepted(preview, "TryReadCdWarpSettings", output, 2048, expectedSamples: 2352);
            RequireAccepted(preview, "TryBuildDvdCommand", output, 2048, expectedSamples: 2048);
            RequireAccepted(preview, "TryBuildDvdCalibrateCommand", output, 2048, expectedSamples: 2048);

            foreach (string samples in new[] { "0", "2353" })
            {
                state.CdSamplesPerSector = samples;
                RequireRejected(preview, "TryReadCdSettings", output, "CD samples " + samples);
                RequireRejected(preview, "TryReadCdWarpSettings", output, "CD samples " + samples);
            }
            foreach (string samples in new[] { "0", "2049" })
            {
                state.DvdSamplesPerSector = samples;
                RequireRejected(preview, "TryBuildDvdCommand", output, "DVD samples " + samples);
                RequireRejected(preview, "TryBuildDvdCalibrateCommand", output, "DVD samples " + samples);
            }
            Require(!File.Exists(output), "settings validation does not generate an artifact");
        }
        finally
        {
            foreach (string path in Directory.EnumerateFiles(directory)) File.Delete(path);
            Directory.Delete(directory);
        }
        Console.WriteLine("live-preview-resolution: passed (CD/DVD projection and readback sizes, sampling, command arguments, ring preparation, range validation)");
    }

    private static object?[] RequireAccepted(LivePreviewViewModel preview, string method, string output,
        int size, int expectedSamples = 16)
    {
        (bool succeeded, object?[] arguments) = ReadSettings(preview, method, output);
        Require(succeeded, $"{method} accepts {size} pixels: {arguments[^1]}");
        int sizeIndex = method == "TryReadCdSettings" ? 2 : 3;
        Require(arguments[sizeIndex] is int actual && actual == size,
            $"{method} forwards requested {size} pixels without a hidden resolution cap");
        Require(arguments[sizeIndex + 1] is int actualSamples && actualSamples == expectedSamples,
            $"{method} forwards {expectedSamples} samples per sector without silently reducing precision");
        if (method.StartsWith("TryBuildDvd", StringComparison.Ordinal))
        {
            string[] command = (string[])arguments[2]!;
            int optionIndex = Array.IndexOf(command, "--preview-size");
            Require(optionIndex >= 0 && optionIndex + 1 < command.Length
                && command[optionIndex + 1] == size.ToString(CultureInfo.InvariantCulture),
                $"{method} passes {size} pixels to the DVD renderer command");
            int samplesIndex = Array.IndexOf(command, "--samples-per-sector");
            Require(samplesIndex >= 0 && samplesIndex + 1 < command.Length
                && command[samplesIndex + 1] == expectedSamples.ToString(CultureInfo.InvariantCulture),
                $"{method} passes {expectedSamples} samples per sector to the DVD renderer command");
        }
        return arguments;
    }

    private static void RequireRejected(LivePreviewViewModel preview, string method, string output, string value)
    {
        (bool succeeded, object?[] arguments) = ReadSettings(preview, method, output);
        Require(!succeeded && arguments[^1] is string { Length: > 0 },
            $"{method} rejects invalid {value} with a validation message instead of clamping");
    }

    private static (bool Succeeded, object?[] Arguments) ReadSettings(
        LivePreviewViewModel preview, string methodName, string output)
    {
        MethodInfo method = typeof(LivePreviewViewModel).GetMethod(methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments = new object?[method.GetParameters().Length];
        if (methodName.StartsWith("TryBuildDvd", StringComparison.Ordinal)) arguments[0] = output;
        return ((bool)method.Invoke(preview, arguments)!, arguments);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Live preview resolution: " + message);
    }
}
