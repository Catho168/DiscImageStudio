using System.IO;

namespace DiscImageStudio.Services;

/// <summary>Shared temp-file location and best-effort cleanup for artifacts the app stages
/// between jobs (design composites, live previews).</summary>
internal static class TempFiles
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
}
