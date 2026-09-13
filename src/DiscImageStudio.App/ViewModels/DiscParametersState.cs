using System.Collections.ObjectModel;
using System.IO;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiscImageStudio.ViewModels;

/// <summary>
/// Single shared parameter store: the DVD/CD pages and the live preview page bind to the
/// same properties, replacing the previous 27 textbox/2 combobox pairs with one source.
/// </summary>
public partial class DiscParametersState : ObservableObject
{
    private const string DefaultDvdVolumeLabel = "DISC_IMAGE";

    private readonly string _discPresetJsonPath = DiscPresetJsonStore.GetDefaultPath();

    private IReadOnlyList<CdDiscPreset> _cdPresets = [CdDiscPreset.Manual];
    private IReadOnlyList<DvdDiscPreset> _dvdPresets = [DvdDiscPreset.Manual];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCdRingMode))]
    private int _cdImageProcessingModeIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDvdRingMode))]
    private int _dvdImageProcessingModeIndex;

    private bool _isApplyingDiscPreset;

    // CD page / live preview shared fields. Linear velocity, start angle and the image
    // outer radius are physical constants of the scheme (1200 mm/s, 0°, 57.5 mm) and are
    // deliberately not parameters. The live preview reads back the generated track, so
    // CdTrackPath points at the burn artifact while CdImagePath stays generation-only.
    [ObservableProperty] private string _cdImagePath = string.Empty;
    [ObservableProperty] private string _cdTrackPath = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCdWaveOutput))]
    private string _cdOutputPath = string.Empty;
    [ObservableProperty] private bool _cdWriteCue = true;
    [ObservableProperty] private string _cdRingInnerMargin = "2.0";
    [ObservableProperty] private string _cdRingOuterMargin = "2.0";
    [ObservableProperty] private string _cdInnerRadius = "24.5";
    [ObservableProperty] private string _cdOuterRadius = "56.8";
    [ObservableProperty] private string _cdSectors = "359849";
    [ObservableProperty] private string _cdPreviewSize = "1600";
    [ObservableProperty] private string _cdSamplesPerSector = "16";
    [ObservableProperty] private bool _cdInterleave = true;

    // DVD page / live preview shared fields. Channel-bit length (133.33 nm) and start
    // angle (0°) are fixed physical constants, same as the CD constants above. The live
    // preview reads back the generated ISO through DvdIsoPath.
    [ObservableProperty] private string _dvdImagePath = string.Empty;
    [ObservableProperty] private string _dvdIsoPath = string.Empty;
    [ObservableProperty] private string _dvdOutputPath = string.Empty;
    [ObservableProperty] private string _dvdDataDirectory = string.Empty;
    [ObservableProperty] private string _dvdRingInnerMargin = "2.0";
    [ObservableProperty] private string _dvdRingOuterMargin = "2.0";
    [ObservableProperty] private string _dvdTotalSectors = "2295104";
    [ObservableProperty] private string _dvdInnerRadius = "24.0";
    [ObservableProperty] private string _dvdOuterRadius = "58.0";
    [ObservableProperty] private string _dvdVolumeLabel = DefaultDvdVolumeLabel;
    [ObservableProperty] private string _dvdPreviewSize = "1600";
    [ObservableProperty] private string _dvdSamplesPerSector = "16";

    [ObservableProperty] private string _cdPresetHint = string.Empty;
    [ObservableProperty] private string _dvdPresetHint = string.Empty;
    [ObservableProperty] private CdDiscPreset _selectedCdPreset = CdDiscPreset.Manual;
    [ObservableProperty] private DvdDiscPreset _selectedDvdPreset = DvdDiscPreset.Manual;

    public bool IsCdRingMode => CdImageProcessingModeIndex == 1;

    /// <summary>A CUE sheet only accompanies PCM WAV tracks, so the CD page offers its
    /// toggle only while the chosen output is a .wav file.</summary>
    public bool IsCdWaveOutput => Path.GetExtension(CdOutputPath.Trim())
        .Equals(".wav", StringComparison.OrdinalIgnoreCase);

    public bool IsDvdRingMode => DvdImageProcessingModeIndex == 1;

    public IReadOnlyList<CdDiscPreset> CdPresets => _cdPresets;

    public IReadOnlyList<DvdDiscPreset> DvdPresets => _dvdPresets;

    public string DiscPresetJsonPath => _discPresetJsonPath;

    /// <summary>
    /// Loads built-in + user presets (LoadBuiltIn + LoadOrCreate + Merge). Throws when the
    /// JSON cannot be parsed; the caller keeps the previously valid lists in that case.
    /// </summary>
    public void LoadPresets()
    {
        DiscPresetJsonDocument builtIn = DiscPresetJsonStore.LoadBuiltIn();
        DiscPresetJsonDocument user = DiscPresetJsonStore.LoadOrCreate(
            _discPresetJsonPath,
            DiscPresetJsonStore.CreateInitialUserDocument());
        ApplyPresetDocuments(MergePresets(builtIn, user));
    }

    /// <summary>
    /// Startup load with the original fallback semantics: on failure keep the built-in
    /// catalog only and return the warning text for the shell to surface.
    /// </summary>
    public string? TryLoadPresets()
    {
        try
        {
            LoadPresets();
            return null;
        }
        catch (Exception exception)
        {
            DiscPresetJsonDocument builtIn = DiscPresetJsonStore.LoadBuiltIn();
            DiscPresetJsonDocument emptyUser = new(
                DiscPresetJsonStore.CurrentSchemaVersion,
                [],
                [],
                FallbackLanguage: "zh-CN");
            ApplyPresetDocuments(MergePresets(builtIn, emptyUser));
            return $"用户预设 JSON 无法加载，已继续使用程序内置参数。\n\n{exception.Message}\n\n文件位置：\n{_discPresetJsonPath}";
        }
    }

    public (int CdCount, int DvdCount) ReloadPresets()
    {
        DiscPresetJsonDocument builtIn = DiscPresetJsonStore.LoadBuiltIn();
        DiscPresetJsonDocument user = DiscPresetJsonStore.LoadOrCreate(
            _discPresetJsonPath,
            DiscPresetJsonStore.CreateInitialUserDocument());
        (IReadOnlyList<CdDiscPreset> cdPresets, IReadOnlyList<DvdDiscPreset> dvdPresets) =
            MergePresets(builtIn, user);

        // Preserve the current selection by Id across the rebind, exactly like before.
        string? selectedCdId = SelectedCdPreset.Id;
        string? selectedDvdId = SelectedDvdPreset.Id;
        ApplyPresetDocuments((cdPresets, dvdPresets));
        CdDiscPreset selectedCd =
            cdPresets.FirstOrDefault(value => value.Id == selectedCdId) ?? cdPresets[0];
        DvdDiscPreset selectedDvd =
            dvdPresets.FirstOrDefault(value => value.Id == selectedDvdId) ?? dvdPresets[0];
        ApplyCdDiscPreset(selectedCd);
        ApplyDvdDiscPreset(selectedDvd);
        return (cdPresets.Count - 1, dvdPresets.Count - 1);
    }

    public void EnsurePresetsJsonExists()
    {
        if (!File.Exists(_discPresetJsonPath))
        {
            DiscPresetJsonStore.Save(
                _discPresetJsonPath,
                DiscPresetJsonStore.CreateInitialUserDocument());
        }
    }

    private (IReadOnlyList<CdDiscPreset> Cd, IReadOnlyList<DvdDiscPreset> Dvd) MergePresets(
        DiscPresetJsonDocument builtIn,
        DiscPresetJsonDocument user)
    {
        DiscPresetJsonDocument merged = DiscPresetJsonStore.Merge(builtIn, user);
        List<CdDiscPreset> cdPresets = merged.CdPresets
            .Select(value => new CdDiscPreset(
                value.Id,
                value.DisplayName,
                value.Description,
                value.Sectors,
                value.InnerRadiusMm,
                value.OuterRadiusMm,
                value.DisplayNameResourceKey,
                value.DescriptionResourceKey))
            .ToList();
        cdPresets.Add(CdDiscPreset.Manual);

        List<DvdDiscPreset> dvdPresets = merged.DvdPresets
            .Select(value => new DvdDiscPreset(
                value.Id,
                value.DisplayName,
                value.Description,
                value.TotalSectors,
                value.InnerRadiusMm,
                value.OuterRadiusMm,
                value.DisplayNameResourceKey,
                value.DescriptionResourceKey))
            .ToList();
        dvdPresets.Add(DvdDiscPreset.Manual);
        return (cdPresets, dvdPresets);
    }

    private void ApplyPresetDocuments((IReadOnlyList<CdDiscPreset> Cd, IReadOnlyList<DvdDiscPreset> Dvd) presets)
    {
        _cdPresets = presets.Cd;
        _dvdPresets = presets.Dvd;
        OnPropertyChanged(nameof(CdPresets));
        OnPropertyChanged(nameof(DvdPresets));
    }

    /// <summary>Initial selection: the first JSON preset for each disc family.</summary>
    public void SelectDefaultPresets()
    {
        ApplyCdDiscPreset(_cdPresets[0]);
        ApplyDvdDiscPreset(_dvdPresets[0]);
    }

    // Only the four generation fields per disc family switch the preset to "custom"
    // when edited by hand, matching the previous TextChanged subscriptions.
    partial void OnCdSectorsChanged(string value) => OnCdParameterEdited(presetField: true);

    partial void OnCdInnerRadiusChanged(string value) => OnCdParameterEdited(presetField: true);

    partial void OnCdOuterRadiusChanged(string value) => OnCdParameterEdited(presetField: true);

    partial void OnDvdTotalSectorsChanged(string value) => OnDvdParameterEdited(presetField: true);

    partial void OnDvdInnerRadiusChanged(string value) => OnDvdParameterEdited(presetField: true);

    partial void OnDvdOuterRadiusChanged(string value) => OnDvdParameterEdited(presetField: true);

    /// <summary>Writing through the selector applies the preset, mirroring SelectionChanged.</summary>
    partial void OnSelectedCdPresetChanged(CdDiscPreset value)
    {
        if (!_isApplyingDiscPreset)
        {
            ApplyCdDiscPreset(value);
        }
    }

    partial void OnSelectedDvdPresetChanged(DvdDiscPreset value)
    {
        if (!_isApplyingDiscPreset)
        {
            ApplyDvdDiscPreset(value);
        }
    }

    private void OnCdParameterEdited(bool presetField)
    {
        if (_isApplyingDiscPreset || !presetField || SelectedCdPreset.IsCustom)
        {
            return;
        }

        SelectedCdPreset = CdDiscPreset.Manual;
    }

    private void OnDvdParameterEdited(bool presetField)
    {
        if (_isApplyingDiscPreset || !presetField || SelectedDvdPreset.IsCustom)
        {
            return;
        }

        SelectedDvdPreset = DvdDiscPreset.Manual;
    }

    public void ApplyCdDiscPreset(CdDiscPreset preset)
    {
        bool previous = _isApplyingDiscPreset;
        _isApplyingDiscPreset = true;
        try
        {
            SelectedCdPreset = preset;
            CdPresetHint = preset.Description;
            if (!preset.IsCustom)
            {
                CdSectors = preset.Sectors.ToString(System.Globalization.CultureInfo.InvariantCulture);
                CdInnerRadius = FormatPresetNumber(preset.InnerRadiusMm);
                CdOuterRadius = FormatPresetNumber(preset.OuterRadiusMm);
            }
        }
        finally
        {
            _isApplyingDiscPreset = previous;
        }
    }

    public void ApplyDvdDiscPreset(DvdDiscPreset preset)
    {
        bool previous = _isApplyingDiscPreset;
        _isApplyingDiscPreset = true;
        try
        {
            SelectedDvdPreset = preset;
            DvdPresetHint = preset.Description;
            if (!preset.IsCustom)
            {
                DvdTotalSectors = preset.TotalSectors.ToString(System.Globalization.CultureInfo.InvariantCulture);
                DvdInnerRadius = FormatPresetNumber(preset.InnerRadiusMm);
                DvdOuterRadius = FormatPresetNumber(preset.OuterRadiusMm);
            }
        }
        finally
        {
            _isApplyingDiscPreset = previous;
        }
    }

    private static string FormatPresetNumber(double value)
        => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}
