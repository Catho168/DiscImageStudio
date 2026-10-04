using System.IO;
using System.Text.Json;
using DiscImageStudio.Core;

namespace DiscImageStudio.Dvd;

/// <summary>
/// Geometry recorded next to a generated ISO as <c>&lt;iso&gt;.json</c> by the fast dispersion
/// writer. The live preview reads it back so an ISO made by an earlier run, or imported from
/// another machine, is simulated with the disc size and radii it was generated with; the
/// drawing range itself is resolved by the simulate command from the same file.
/// </summary>
public sealed record DvdImageMetadata(
    uint TotalSectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    double PitchLinear = 0,
    double PitchQuadratic = 0,
    double PitchCubic = 0)
{
    /// <summary>Reads the sidecar of an ISO, or null when it is absent or unreadable.</summary>
    public static DvdImageMetadata? TryLoad(string isoPath)
    {
        string path = isoPath + ".json";
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("imageMapping", out JsonElement mapping))
            {
                return null;
            }

            uint totalSectors = GetUInt32(mapping, "totalSectors");
            double innerRadius = GetDouble(mapping, "innerRadiusMm");
            double outerRadius = GetDouble(mapping, "outerRadiusMm");
            double linear = GetPitchCoefficient(mapping, "pitchLinear");
            double quadratic = GetPitchCoefficient(mapping, "pitchQuadratic");
            double cubic = GetPitchCoefficient(mapping, "pitchCubic");
            DvdTrackGeometry.ValidatePitchCoefficients(linear, quadratic, cubic);
            return totalSectors > 0 && double.IsFinite(innerRadius) && double.IsFinite(outerRadius)
                && innerRadius > 0 && outerRadius > innerRadius
                ? new DvdImageMetadata(totalSectors, innerRadius, outerRadius, linear, quadratic, cubic)
                : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private static uint GetUInt32(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.TryGetUInt32(out uint result)
            ? result
            : 0u;

    private static double GetDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double result)
            ? result
            : 0;

    private static double GetPitchCoefficient(JsonElement element, string name)
        => !element.TryGetProperty(name, out JsonElement value) ? 0
            : value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double result) ? result : double.NaN;
}
