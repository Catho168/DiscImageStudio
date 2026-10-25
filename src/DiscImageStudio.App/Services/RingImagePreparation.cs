using System.IO;
using DiscImageStudio.Core;
using DiscImageStudio.Imaging;

namespace DiscImageStudio.Services;

internal sealed class RingImagePreparation
{
    internal static readonly string LivePreviewDirectory = Path.Combine(
        Path.GetTempPath(),
        "DiscImageStudio");

    internal static void TryDeleteTemporaryFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// Prepares the ring-copy intermediate image (or passes the original through) and
    /// writes an optional log summary. The result must be disposed to delete temp files.
    internal static PreparedImage Prepare(
        string sourcePath,
        string discPrefix,
        Func<int> processingModeIndex,
        Func<RingImageLayoutOptions?> ringLayout,
        int outputSize,
        CancellationToken cancellationToken = default,
        bool writeLog = true,
        Action<string>? appendLog = null)
    {
        if (processingModeIndex() != 1)
        {
            return PreparedImage.Original(sourcePath);
        }

        RingImageLayoutOptions options = ringLayout()
            ?? throw new InvalidOperationException("环形复制布局不可用。");
        return PrepareRingImage(sourcePath, options, discPrefix, cancellationToken, writeLog, appendLog);
    }

    internal static PreparedImage PrepareRingImage(
        string sourcePath,
        RingImageLayoutOptions options,
        string prefix,
        CancellationToken cancellationToken = default,
        bool writeLog = true,
        Action<string>? appendLog = null)
        => PrepareImage(sourcePath, options, prefix, imageProcessingEnabled: false,
            gammaCorrectionEnabled: false, cancellationToken, writeLog, appendLog);

    /// <summary>
    /// Applies the optional layout before a single grayscale/gamma/dither pass. The
    /// original file is never changed; disposing the result removes its intermediate PNG.
    /// </summary>
    internal static PreparedImage PrepareImage(
        string sourcePath,
        RingImageLayoutOptions? ringLayout,
        string prefix,
        bool imageProcessingEnabled,
        bool gammaCorrectionEnabled,
        CancellationToken cancellationToken = default,
        bool writeLog = true,
        Action<string>? appendLog = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ringLayout is null && !imageProcessingEnabled)
        {
            return PreparedImage.Original(sourcePath);
        }

        Directory.CreateDirectory(LivePreviewDirectory);
        string temporaryPath = Path.Combine(
            LivePreviewDirectory,
            $"{prefix}-processed-source-{Guid.NewGuid():N}.png");
        try
        {
            RingImageLayoutSummary? summary = null;
            if (ringLayout is not null)
            {
                // Gamma must see the continuous tones created by resampling. Doing the
                // legacy palette-preserving pass first would discard those tones.
                summary = RingImageProcessor.Render(
                    sourcePath,
                    temporaryPath,
                    ringLayout with
                    {
                        PreserveDitherPalette = imageProcessingEnabled
                            ? ReadOnlyMemory<byte>.Empty
                            : BinaryGrayscale.Levels.ToArray(),
                    },
                    cancellationToken);
                if (writeLog && appendLog is not null)
                {
                    appendLog(
                        $"[{DateTime.Now:HH:mm:ss}] 环形图片处理：自动复制 {summary.CopyCount} 份，"
                        + $"每份等比尺寸 {summary.CopyWidthMm:F1} × {summary.CopyHeightMm:F1} mm，"
                        + $"约 {summary.CopyWidthPixels} × {summary.CopyHeightPixels} px；"
                        + $"中间图 {summary.OutputSize} × {summary.OutputSize} px，"
                        + $"有效半径 {summary.ContentInnerRadiusMm:F1}–{summary.ContentOuterRadiusMm:F1} mm。\n");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (imageProcessingEnabled)
            {
                var source = GrayscaleDither.LoadImage(ringLayout is null ? sourcePath : temporaryPath);
                var processed = GrayscaleDither.Process(source, BinaryGrayscale.Levels,
                    cancellationToken, linearizeSrgb: gammaCorrectionEnabled);
                cancellationToken.ThrowIfCancellationRequested();
                GrayscaleDither.SavePng(processed, temporaryPath, BinaryGrayscale.Levels);
                if (writeLog && appendLog is not null)
                {
                    appendLog($"[{DateTime.Now:HH:mm:ss}] 图像处理：黑白二阶抖动（蛇形 Floyd–Steinberg），"
                        + $"sRGB 伽马校正：{(gammaCorrectionEnabled ? "开" : "关")}；"
                        + $"{processed.PixelWidth} × {processed.PixelHeight} px。\n");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new PreparedImage(temporaryPath, temporaryPath, summary);
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    /// <summary>Immutable UI-thread snapshot consumed by a background preparation worker.</summary>
    internal sealed record Request(
        string SourcePath,
        RingImageLayoutOptions? RingLayout,
        string Prefix,
        bool ImageProcessingEnabled,
        bool GammaCorrectionEnabled)
    {
        internal PreparedImage Prepare(CancellationToken cancellationToken = default,
            bool writeLog = true, Action<string>? appendLog = null)
            => PrepareImage(SourcePath, RingLayout, Prefix, ImageProcessingEnabled,
                GammaCorrectionEnabled, cancellationToken, writeLog, appendLog);
    }

    internal sealed class PreparedImage : IDisposable
    {
        private readonly string? _temporaryPath;

        internal PreparedImage(
            string path,
            string? temporaryPath,
            RingImageLayoutSummary? summary)
        {
            Path = path;
            _temporaryPath = temporaryPath;
            Summary = summary;
        }

        internal string Path { get; }

        internal RingImageLayoutSummary? Summary { get; }

        internal static PreparedImage Original(string path) => new(path, null, null);

        public void Dispose() => TryDeleteTemporaryFile(_temporaryPath);
    }
}
