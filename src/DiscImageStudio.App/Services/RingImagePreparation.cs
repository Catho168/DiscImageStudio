using System.IO;
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
    {
        Directory.CreateDirectory(LivePreviewDirectory);
        string temporaryPath = Path.Combine(
            LivePreviewDirectory,
            $"{prefix}-ring-source-{Guid.NewGuid():N}.png");
        try
        {
            RingImageLayoutSummary summary = RingImageProcessor.Render(
                sourcePath,
                temporaryPath,
                options,
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

            return new PreparedImage(temporaryPath, temporaryPath, summary);
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
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
