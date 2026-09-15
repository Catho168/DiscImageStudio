using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscImageStudio.Core;

public sealed record CdDiscPresetDefinition(
    string Id,
    string DisplayName,
    string Description,
    long Sectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    // Retained so a user file keeps every field it was written with. The CD engine scans at
    // the ECMA-130 standard speed regardless of this value and the pages no longer expose a
    // form for it, so the field is carried, validated and written back unchanged.
    double LinearVelocityMmPerSecond = 1_200,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DisplayNameResourceKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DescriptionResourceKey = null);

public sealed record DvdDiscPresetDefinition(
    string Id,
    string DisplayName,
    string Description,
    uint TotalSectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    // Same role as the CD linear velocity above: retained for file compatibility, fixed at
    // the DVD standard channel-bit length while generating.
    double ChannelBitLengthNm = 133.33,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DisplayNameResourceKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DescriptionResourceKey = null);

public sealed record DiscPresetJsonDocument(
    int SchemaVersion,
    IReadOnlyList<CdDiscPresetDefinition> CdPresets,
    IReadOnlyList<DvdDiscPresetDefinition> DvdPresets,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? DisabledCdPresetIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? DisabledDvdPresetIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? FallbackLanguage = null);

public static class DiscPresetJsonStore
{
    public const int CurrentSchemaVersion = 2;

    /// <summary>Schema 3 was written by a build that dropped <c>linearVelocityMmPerSecond</c>
    /// and <c>channelBitLengthNm</c> from the preset shape. Those files stay readable so a
    /// version bump cannot strand presets a user already saved.</summary>
    private const int InterimSchemaVersion = 3;

    public const string FileName = "disc-presets.json";

    private const string BuiltInResourceName =
        "DiscImageStudio.Core.BuiltInDiscPresets.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static string GetDefaultPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiscImageStudio",
            FileName);

    public static DiscPresetJsonDocument LoadBuiltIn()
    {
        using Stream stream = typeof(DiscPresetJsonStore).Assembly
            .GetManifestResourceStream(BuiltInResourceName)
            ?? throw new InvalidOperationException("程序缺少内置盘片预设资源。");
        return Deserialize(stream, "内置盘片预设");
    }

    public static DiscPresetJsonDocument CreateInitialUserDocument()
        => new(
            CurrentSchemaVersion,
            [
                new CdDiscPresetDefinition(
                    "user-custom-cd",
                    "自定义 CD 参数",
                    "用户预设，可直接修改或复制后添加更多 CD 参数。",
                    359_849,
                    24.5,
                    56.8,
                    1_200),
            ],
            [
                new DvdDiscPresetDefinition(
                    "user-custom-dvd",
                    "自定义 DVD 参数",
                    "用户预设，可直接修改或复制后添加更多 DVD 参数。",
                    2_295_104,
                    24.0,
                    58.0,
                    133.33),
            ],
            DisabledCdPresetIds: [],
            DisabledDvdPresetIds: []);

    public static DiscPresetJsonDocument LoadOrCreate(
        string path,
        DiscPresetJsonDocument initialUserDocument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(initialUserDocument);

        if (!File.Exists(path))
        {
            Save(path, initialUserDocument);
        }

        return Load(path);
    }

    public static DiscPresetJsonDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(path);
        return Deserialize(stream, Path.GetFileName(path));
    }

    public static DiscPresetJsonDocument Merge(
        DiscPresetJsonDocument builtIn,
        DiscPresetJsonDocument user)
    {
        ArgumentNullException.ThrowIfNull(builtIn);
        ArgumentNullException.ThrowIfNull(user);
        Validate(builtIn);
        Validate(user);

        HashSet<string> disabledCdIds = new(
            user.DisabledCdPresetIds ?? [],
            StringComparer.Ordinal);
        HashSet<string> disabledDvdIds = new(
            user.DisabledDvdPresetIds ?? [],
            StringComparer.Ordinal);
        DiscPresetJsonDocument merged = new(
            CurrentSchemaVersion,
            MergePresets(
                builtIn.CdPresets,
                user.CdPresets,
                disabledCdIds,
                value => value.Id),
            MergePresets(
                builtIn.DvdPresets,
                user.DvdPresets,
                disabledDvdIds,
                value => value.Id),
            FallbackLanguage: builtIn.FallbackLanguage);
        Validate(merged);
        return merged;
    }

    public static void Save(string path, DiscPresetJsonDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        Validate(document);

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("预设 JSON 路径必须包含目录。", nameof(path));
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static void Validate(DiscPresetJsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != CurrentSchemaVersion
            && document.SchemaVersion != InterimSchemaVersion)
        {
            throw new InvalidDataException(
                $"不支持 schemaVersion={document.SchemaVersion}；当前支持 "
                + $"{CurrentSchemaVersion} 与 {InterimSchemaVersion}。请把顶层 schemaVersion 改为受支持的值，"
                + "或删除该文件让程序重新生成分层配置模板。");
        }

        if (document.CdPresets is null)
        {
            throw new InvalidDataException("cdPresets 不能为空。");
        }

        if (document.DvdPresets is null)
        {
            throw new InvalidDataException("dvdPresets 不能为空。");
        }

        ValidateUniqueIds(document.CdPresets.Select(value => value?.Id), "CD");
        ValidateUniqueIds(document.DvdPresets.Select(value => value?.Id), "DVD");
        ValidateUniqueIds(document.DisabledCdPresetIds ?? [], "disabled CD");
        ValidateUniqueIds(document.DisabledDvdPresetIds ?? [], "disabled DVD");
        if (document.FallbackLanguage is not null)
        {
            ValidateText(document.FallbackLanguage, "document", "fallbackLanguage");
        }

        foreach (string id in document.DisabledCdPresetIds ?? [])
        {
            ValidateText(id, "disabled CD", "id");
        }

        foreach (string id in document.DisabledDvdPresetIds ?? [])
        {
            ValidateText(id, "disabled DVD", "id");
        }

        foreach (CdDiscPresetDefinition? preset in document.CdPresets)
        {
            if (preset is null)
            {
                throw new InvalidDataException("CD 预设不能是 null。");
            }

            ValidateText(preset.Id, "CD", "id");
            ValidateText(preset.DisplayName, preset.Id, "displayName");
            ValidateText(preset.Description, preset.Id, "description");
            ValidateOptionalText(
                preset.DisplayNameResourceKey,
                preset.Id,
                "displayNameResourceKey");
            ValidateOptionalText(
                preset.DescriptionResourceKey,
                preset.Id,
                "descriptionResourceKey");
            ValidateReservedId(preset.Id, "CD");
            if (preset.Sectors <= 0)
            {
                throw InvalidValue(preset.Id, "sectors", "必须大于 0");
            }

            ValidateRadiusRange(preset.Id, preset.InnerRadiusMm, preset.OuterRadiusMm);
            ValidatePositiveFinite(
                preset.Id,
                "linearVelocityMmPerSecond",
                preset.LinearVelocityMmPerSecond);
        }

        foreach (DvdDiscPresetDefinition? preset in document.DvdPresets)
        {
            if (preset is null)
            {
                throw new InvalidDataException("DVD 预设不能是 null。");
            }

            ValidateText(preset.Id, "DVD", "id");
            ValidateText(preset.DisplayName, preset.Id, "displayName");
            ValidateText(preset.Description, preset.Id, "description");
            ValidateOptionalText(
                preset.DisplayNameResourceKey,
                preset.Id,
                "displayNameResourceKey");
            ValidateOptionalText(
                preset.DescriptionResourceKey,
                preset.Id,
                "descriptionResourceKey");
            ValidateReservedId(preset.Id, "DVD");
            if (preset.TotalSectors == 0)
            {
                throw InvalidValue(preset.Id, "totalSectors", "必须大于 0");
            }

            if (preset.TotalSectors % 16 != 0)
            {
                throw InvalidValue(preset.Id, "totalSectors", "必须是 16 的倍数");
            }

            ValidateRadiusRange(preset.Id, preset.InnerRadiusMm, preset.OuterRadiusMm);
            ValidatePositiveFinite(preset.Id, "channelBitLengthNm", preset.ChannelBitLengthNm);
        }
    }

    private static DiscPresetJsonDocument Deserialize(Stream stream, string sourceName)
    {
        try
        {
            DiscPresetJsonDocument? document = JsonSerializer.Deserialize<DiscPresetJsonDocument>(
                stream,
                JsonOptions);
            if (document is null)
            {
                throw new InvalidDataException($"{sourceName} 不能为空。");
            }

            Validate(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"{sourceName} 格式有误（第 {exception.LineNumber + 1} 行，第 {exception.BytePositionInLine + 1} 列）：{exception.Message}",
                exception);
        }
    }

    private static IReadOnlyList<TPreset> MergePresets<TPreset>(
        IReadOnlyList<TPreset> builtIn,
        IReadOnlyList<TPreset> user,
        IReadOnlySet<string> disabledIds,
        Func<TPreset, string> getId)
    {
        Dictionary<string, TPreset> userById = user.ToDictionary(getId, StringComparer.Ordinal);
        HashSet<string> builtInIds = new(builtIn.Select(getId), StringComparer.Ordinal);
        List<TPreset> merged = [];

        foreach (TPreset preset in builtIn)
        {
            string id = getId(preset);
            if (!disabledIds.Contains(id))
            {
                merged.Add(userById.TryGetValue(id, out TPreset? replacement) ? replacement : preset);
            }
        }

        foreach (TPreset preset in user)
        {
            string id = getId(preset);
            if (!builtInIds.Contains(id) && !disabledIds.Contains(id))
            {
                merged.Add(preset);
            }
        }

        return merged;
    }

    private static void ValidateUniqueIds(IEnumerable<string?> ids, string family)
    {
        HashSet<string> uniqueIds = new(StringComparer.Ordinal);
        foreach (string? id in ids)
        {
            if (!string.IsNullOrWhiteSpace(id) && !uniqueIds.Add(id))
            {
                throw new InvalidDataException($"{family} 预设 id '{id}' 重复。");
            }
        }
    }

    private static void ValidateReservedId(string id, string family)
    {
        if (string.Equals(id, "__manual__", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{family} 预设 id '__manual__' 由界面的未保存状态保留，请换一个 id。");
        }
    }

    private static void ValidateText(string? value, string presetId, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw InvalidValue(presetId, fieldName, "不能为空");
        }
    }

    private static void ValidateOptionalText(string? value, string presetId, string fieldName)
    {
        if (value is not null)
        {
            ValidateText(value, presetId, fieldName);
        }
    }

    private static void ValidateRadiusRange(
        string presetId,
        double innerRadius,
        double outerRadius)
    {
        ValidatePositiveFinite(presetId, "innerRadiusMm", innerRadius);
        ValidatePositiveFinite(presetId, "outerRadiusMm", outerRadius);
        if (outerRadius <= innerRadius)
        {
            throw InvalidValue(presetId, "outerRadiusMm", "必须大于 innerRadiusMm");
        }
    }

    private static void ValidatePositiveFinite(string presetId, string fieldName, double value)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw InvalidValue(presetId, fieldName, "必须是大于 0 的有限数值");
        }
    }

    private static InvalidDataException InvalidValue(
        string presetId,
        string fieldName,
        string requirement)
        => new($"预设 '{presetId}' 的 {fieldName} {requirement}。");
}
