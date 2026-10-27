using System.IO;
using System.Text.Json;

namespace DiscImageStudio.Services;

/// <summary>
/// Persists the preset ids the new-project dialog was last confirmed with, to
/// %LOCALAPPDATA%\DiscImageStudio\recent-templates.json. Convenience state, not user data:
/// an unreadable file degrades to an empty list and a failed write is swallowed, matching
/// <see cref="RecentProjectStore"/>.
/// </summary>
public sealed class RecentTemplateStore
{
    public const int MaxEntries = 8;

    private const string FileName = "recent-templates.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _filePath;

    public RecentTemplateStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiscImageStudio",
            FileName))
    {
    }

    public RecentTemplateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <summary>Newest first, deduplicated, capped.</summary>
    public IReadOnlyList<string> Load()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            using FileStream stream = new(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            List<string>? entries = JsonSerializer.Deserialize<List<string>>(stream, JsonOptions);
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

    /// <summary>Moves the preset to the top of the list, replacing an earlier entry.</summary>
    public void Record(string presetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetId);
        List<string> entries = [presetId];
        entries.AddRange(Load().Where(existing => !string.Equals(existing, presetId, StringComparison.Ordinal)));
        Save(entries);
    }

    /// <summary>Drops one id so a deleted preset stops occupying a slot in the 最近 tab.</summary>
    public void Remove(string presetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetId);
        List<string> entries = Load()
            .Where(existing => !string.Equals(existing, presetId, StringComparison.Ordinal))
            .ToList();
        Save(entries);
    }

    private void Save(IReadOnlyList<string> entries)
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
            // History is a convenience: losing it must not surface as a dialog failure.
        }
    }

    private static List<string> Sanitize(IEnumerable<string?> entries)
    {
        List<string> result = [];
        foreach (string? entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry)
                || result.Contains(entry, StringComparer.Ordinal))
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
}
