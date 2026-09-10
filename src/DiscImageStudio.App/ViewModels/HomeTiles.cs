using System.IO;
using System.Windows.Media.Imaging;
using DiscImageStudio.Services;
using MahApps.Metro.IconPacks;

namespace DiscImageStudio.ViewModels;

/// <summary>
/// One card under "快速创建": a disc preset from the editable catalog, or the trailing
/// custom-size card (<see cref="PresetId"/> null), which opens the parameter page for the
/// family the action bar has selected and therefore ignores <see cref="IsDvd"/>. The custom
/// card belongs to no family, so it carries its own caption rather than a CD/DVD one.
/// </summary>
public sealed record QuickCreateTile(
    string Title,
    string Caption,
    string Detail,
    PackIconLucideKind Icon,
    bool IsDvd,
    string? PresetId)
{
    /// <summary>The preset's own discriminator (its parenthetical, or its geometry) stays off
    /// the card face — the family is what the row is read for — and shows on hover instead.</summary>
    public string ToolTipText => string.IsNullOrWhiteSpace(Detail)
        ? $"{Title}\n{Caption}"
        : $"{Title}\n{Caption} · {Detail}";
}

/// <summary>
/// One card in the start page's recent list. The source picture is the thumbnail when it is
/// still readable; otherwise the card degrades to the disc glyph and says so.
/// </summary>
public sealed class RecentJobTile
{
    private readonly bool _sourceExists;

    private RecentJobTile(RecentJobEntry entry, BitmapSource? thumbnail, bool sourceExists)
    {
        Entry = entry;
        Thumbnail = thumbnail;
        _sourceExists = sourceExists;
    }

    internal RecentJobEntry Entry { get; }

    public BitmapSource? Thumbnail { get; }

    public bool HasThumbnail => Thumbnail is not null;

    public string FamilyLabel => Entry.IsDvd ? "DVD" : "CD";

    public string Title => Path.GetFileName(Entry.SourceImagePath);

    public string LocationText => _sourceExists
        ? Path.GetDirectoryName(Entry.SourceImagePath) ?? Entry.SourceImagePath
        : "源图片已移动或删除";

    public string ToolTipText
        => $"源图片：{Entry.SourceImagePath}\n输出：{Entry.OutputPath}"
           + $"\n最近使用：{Entry.LastUsedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

    internal static RecentJobTile Create(RecentJobEntry entry, int decodeWidth, long maxThumbnailBytes)
    {
        bool sourceExists = false;
        try
        {
            FileInfo source = new(entry.SourceImagePath);
            sourceExists = source.Exists;
            if (sourceExists && source.Length <= maxThumbnailBytes)
            {
                return new RecentJobTile(entry, LoadThumbnail(entry.SourceImagePath, decodeWidth), true);
            }
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException
                                              or NotSupportedException)
        {
            // A path the file system refuses to describe still gets a card.
        }

        return new RecentJobTile(entry, null, sourceExists);
    }

    /// <summary>
    /// Any decode failure degrades the card to the glyph; a corrupt or unsupported picture
    /// must not take the start page down. WPF reports damaged image data as
    /// <see cref="OutOfMemoryException"/>, so that is caught here on purpose.
    /// </summary>
    private static BitmapSource? LoadThumbnail(string path, int decodeWidth)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            BitmapImage bitmap = new();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
