using System.IO;
using System.Text.Json;
using DiscImageStudio.Core;

namespace DiscImageStudio.Services;

/// <summary>One recently opened project: its display name, the file it lives in and the
/// thumbnail rendered at last save.</summary>
public sealed record RecentProjectEntry(
    string Name,
    string DiscFamily,
    string ProjectPath,
    string? ThumbnailPngBase64,
    DateTimeOffset LastOpenedUtc)
{
    public OpticalDiscFamily Family => DiscFamily switch
    {
        ProjectDocument.DvdFamily => OpticalDiscFamily.Dvd,
        ProjectDocument.BluRayFamily => OpticalDiscFamily.BluRay,
        _ => OpticalDiscFamily.CompactDisc,
    };

    public bool IsDvd => Family == OpticalDiscFamily.Dvd;

    public string FamilyLabel => Family switch
    {
        OpticalDiscFamily.Dvd => "DVD",
        OpticalDiscFamily.BluRay => "BD",
        _ => "CD",
    };
}

/// <summary>
/// Persists the start page's recent-project list to
/// %LOCALAPPDATA%\DiscImageStudio\recent-projects.json.
/// Convenience state, not user data: an unreadable file degrades to an empty list and a
/// failed write is swallowed, matching <see cref="RecentJobStore"/>.
/// </summary>
public sealed class RecentProjectStore
{
    public const int MaxEntries = 12;

    private const string FileName = "recent-projects.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _filePath;

    public RecentProjectStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiscImageStudio",
            FileName))
    {
    }

    public RecentProjectStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <summary>Newest first, deduplicated by project path, capped.</summary>
    public IReadOnlyList<RecentProjectEntry> Load()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            using FileStream stream = new(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            List<RecentProjectEntry>? entries = JsonSerializer.Deserialize<List<RecentProjectEntry>>(
                stream,
                JsonOptions);
            return entries is null ? [] : Sanitize(entries);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or JsonException
                                              or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>Moves the project to the top of the list, replacing an earlier entry for the
    /// same file.</summary>
    public void Record(RecentProjectEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        List<RecentProjectEntry> entries = [entry];
        entries.AddRange(Load().Where(existing => !IsSameProject(existing, entry)));
        Save(entries);
    }

    public void Remove(string projectPath)
    {
        List<RecentProjectEntry> entries = Load()
            .Where(existing => !IsSameProjectPath(existing.ProjectPath, projectPath))
            .ToList();
        Save(entries);
    }

    public void Clear() => Save([]);

    private void Save(IReadOnlyList<RecentProjectEntry> entries)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using FileStream stream = new(_filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, entries.Take(MaxEntries).ToList(), JsonOptions);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or NotSupportedException)
        {
            // History is a convenience: losing it must not surface as a project failure.
        }
    }

    private static List<RecentProjectEntry> Sanitize(IEnumerable<RecentProjectEntry> entries)
    {
        List<RecentProjectEntry> result = [];
        foreach (RecentProjectEntry entry in entries.OrderByDescending(value => value.LastOpenedUtc))
        {
            if (entry is null
                || !ProjectJsonStore.IsKnownFamily(entry.DiscFamily)
                || string.IsNullOrWhiteSpace(entry.Name)
                || string.IsNullOrWhiteSpace(entry.ProjectPath)
                || result.Any(existing => IsSameProject(existing, entry)))
            {
                continue;
            }

            result.Add(entry);
            if (result.Count == MaxEntries)
            {
                break;
            }
        }

        return result;
    }

    private static bool IsSameProject(RecentProjectEntry left, RecentProjectEntry right)
        => IsSameProjectPath(left.ProjectPath, right.ProjectPath);

    private static bool IsSameProjectPath(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
