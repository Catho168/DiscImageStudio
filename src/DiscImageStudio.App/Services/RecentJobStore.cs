using System.IO;
using System.Text.Json;

namespace DiscImageStudio.Services;

/// <summary>One finished generation job: the picture it was made from, where the result
/// went, and when it ran. Previews are not recorded, so every entry is reopenable.</summary>
public sealed record RecentJobEntry(
    string DiscFamily,
    string SourceImagePath,
    string OutputPath,
    DateTimeOffset LastUsedUtc)
{
    public const string DvdFamily = "dvd";
    public const string CdFamily = "cd";

    public bool IsDvd => string.Equals(DiscFamily, DvdFamily, StringComparison.Ordinal);

    public static bool IsKnownFamily(string? family)
        => string.Equals(family, DvdFamily, StringComparison.OrdinalIgnoreCase)
           || string.Equals(family, CdFamily, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Persists the start page's recent list to
/// %LOCALAPPDATA%\DiscImageStudio\recent-jobs.json.
/// This is convenience state, not user data: an unreadable or half-written file degrades to
/// an empty list and a failed write is swallowed, so a locked profile or a second running
/// instance can never fail an otherwise successful generation job.
/// </summary>
public sealed class RecentJobStore
{
    public const int MaxEntries = 12;

    private const string FileName = "recent-jobs.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _filePath;

    public RecentJobStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiscImageStudio",
            FileName))
    {
    }

    public RecentJobStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <summary>Newest first, deduplicated, capped.</summary>
    public IReadOnlyList<RecentJobEntry> Load()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            using FileStream stream = new(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            List<RecentJobEntry>? entries = JsonSerializer.Deserialize<List<RecentJobEntry>>(
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

    /// <summary>Moves the job to the top of the list, replacing an earlier identical run.</summary>
    public void Record(RecentJobEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        List<RecentJobEntry> entries = [entry];
        entries.AddRange(Load().Where(existing => !IsSameJob(existing, entry)));
        Save(entries);
    }

    public void Clear() => Save([]);

    private void Save(IReadOnlyList<RecentJobEntry> entries)
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
            // History is a convenience: losing it must not surface as a job failure.
        }
    }

    private static List<RecentJobEntry> Sanitize(IEnumerable<RecentJobEntry> entries)
    {
        List<RecentJobEntry> result = [];
        foreach (RecentJobEntry entry in entries.OrderByDescending(value => value.LastUsedUtc))
        {
            if (entry is null
                || !RecentJobEntry.IsKnownFamily(entry.DiscFamily)
                || string.IsNullOrWhiteSpace(entry.SourceImagePath)
                || string.IsNullOrWhiteSpace(entry.OutputPath)
                || result.Any(existing => IsSameJob(existing, entry)))
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

    private static bool IsSameJob(RecentJobEntry left, RecentJobEntry right)
        => string.Equals(left.DiscFamily, right.DiscFamily, StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.SourceImagePath, right.SourceImagePath, StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.OutputPath, right.OutputPath, StringComparison.OrdinalIgnoreCase);
}
