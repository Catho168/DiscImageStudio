using System.Collections.ObjectModel;
using System.IO;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Services;
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
    [ObservableProperty] private string _cdInnerRadius = "24.5";
    [ObservableProperty] private string _cdOuterRadius = "56.8";
    [ObservableProperty] private string _cdSectors = "359849";
    [ObservableProperty] private string _cdPreviewSize = "1600";
    [ObservableProperty] private string _cdSamplesPerSector = "16";
    [ObservableProperty] private bool _cdInterleave = true;

    // Second parameter set of the live preview page's calibration mode: the disc the reader
    // follows, as opposed to the generated geometry above. It is preview-only and never
    // feeds a generation job, so it is not a preset field either.
    [ObservableProperty] private string _cdActualInnerRadius = "24.3";
    [ObservableProperty] private string _cdActualOuterRadius = "56.6";

    // DVD page / live preview shared fields. Channel-bit length (133.3 nm) and start
    // angle (0°) are fixed physical constants, same as the CD constants above. The live
    // preview reads back the generated ISO through DvdIsoPath.
    [ObservableProperty] private string _dvdImagePath = string.Empty;
    [ObservableProperty] private string _dvdIsoPath = string.Empty;
    [ObservableProperty] private string _dvdOutputPath = string.Empty;
    [ObservableProperty] private string _dvdDataDirectory = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DvdPitchSummary), nameof(DvdActualPitchSummary))] private string _dvdTotalSectors = "2295104";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DvdPitchSummary))] private string _dvdInnerRadius = "24.0";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DvdPitchSummary))] private string _dvdOuterRadius = "58.0";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DvdPitchSummary))] private string _dvdPitchLinear = "0";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DvdPitchSummary))] private string _dvdPitchQuadratic = "0";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DvdPitchSummary))] private string _dvdPitchCubic = "0";
    [ObservableProperty] private string _dvdVolumeLabel = DefaultDvdVolumeLabel;
    [ObservableProperty] private string _dvdPreviewSize = "1200";
    [ObservableProperty] private string _dvdSamplesPerSector = "4";

    /// <summary>Second parameter set of the DVD calibration preview, same role as the CD one.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DvdActualPitchSummary))] private string _dvdActualInnerRadius = "24.0";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DvdActualPitchSummary))] private string _dvdActualOuterRadius = "58.0";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DvdActualPitchSummary))] private string _dvdActualPitchLinear = "0";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DvdActualPitchSummary))] private string _dvdActualPitchQuadratic = "0";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DvdActualPitchSummary))] private string _dvdActualPitchCubic = "0";

    [ObservableProperty] private string _cdPresetHint = string.Empty;
    [ObservableProperty] private string _dvdPresetHint = string.Empty;
    [ObservableProperty] private CdDiscPreset _selectedCdPreset = CdDiscPreset.Manual;
    [ObservableProperty] private DvdDiscPreset _selectedDvdPreset = DvdDiscPreset.Manual;

    // Tone mapping shared by both families. New projects open at 75% dither: enough to keep
    // midtones at viewing distance without the grain full strength adds. A project file
    // written before tone mapping existed applies its absent section as 0, so old projects
    // keep their original hard-threshold output until the user moves the slider.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToneDitherText), nameof(ToneSummary))]
    private double _toneDitherStrength = 0.75;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFourLevelSelected), nameof(ToneSummary))]
    private int _toneLevels = ToneProfile.TwoLevels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToneBrightnessText), nameof(ToneSummary))]
    private double _toneBrightness;

    /// <summary>Two-way flag of the 2/4-level segmented control (the right option is 4 levels).</summary>
    public bool IsFourLevelSelected
    {
        get => ToneLevels == ToneProfile.FourLevels;
        set => ToneLevels = value ? ToneProfile.FourLevels : ToneProfile.TwoLevels;
    }

    /// <summary>Snapshot of the UI fields for the generators; clamped so a
    /// mid-edit slider value can never reach the engines.</summary>
    public ToneProfile Tone => new ToneProfile(
        Math.Clamp(ToneDitherStrength, 0, 1),
        ToneLevels == ToneProfile.FourLevels ? ToneProfile.FourLevels : ToneProfile.TwoLevels,
        Math.Clamp(ToneBrightness, ToneProfile.MinimumBrightness, ToneProfile.MaximumBrightness));

    /// <summary>DVD's dispersion pool has only the black and white textures; a CD project's
    /// 4-level setting must not leak into a DVD job.</summary>
    public ToneProfile DvdTone => Tone with { Levels = ToneProfile.TwoLevels };

    public string ToneDitherText => $"{ToneDitherStrength * 100:0}%";

    public string ToneBrightnessText => ToneBrightness > 0
        ? $"+{ToneBrightness:0}"
        : $"{ToneBrightness:0}";

    public string ToneSummary => $"{ToneLevels} 级 · {ToneDitherText} 抖动";

    /// <summary>A CUE sheet only accompanies PCM WAV tracks, so the CD page offers its
    /// toggle only while the chosen output is a .wav file.</summary>
    public bool IsCdWaveOutput => Path.GetExtension(CdOutputPath.Trim())
        .Equals(".wav", StringComparison.OrdinalIgnoreCase);

    public string DvdPitchSummary => PitchSummary(actual: false);
    public string DvdActualPitchSummary => PitchSummary(actual: true);

    private string PitchSummary(bool actual)
    {
        try
        {
            return DvdPitchDisplay.Format(new(CalibrationDiscKind.Dvd,
                ParameterParser.ParsePositiveDouble(actual ? DvdActualInnerRadius : DvdInnerRadius, "内半径"),
                ParameterParser.ParsePositiveDouble(actual ? DvdActualOuterRadius : DvdOuterRadius, "外半径"),
                ParameterParser.ParsePositiveLong(DvdTotalSectors, "总扇区数"),
                DvdStreamingOptions.StandardChannelBitLengthNm,
                ParameterParser.ParseDouble(actual ? DvdActualPitchLinear : DvdPitchLinear, "相对一次项"),
                ParameterParser.ParseDouble(actual ? DvdActualPitchQuadratic : DvdPitchQuadratic, "相对二次项"),
                ParameterParser.ParseDouble(actual ? DvdActualPitchCubic : DvdPitchCubic, "相对三次项")));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            return "轨距模型无效：请检查半径、扇区数及系数，整个刻录区的轨距必须为正。";
        }
    }

    public IReadOnlyList<CdDiscPreset> CdPresets => _cdPresets;

    public IReadOnlyList<DvdDiscPreset> DvdPresets => _dvdPresets;

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

    public void EnsurePresetsJsonExists()
    {
        if (!File.Exists(_discPresetJsonPath))
        {
            DiscPresetJsonStore.Save(
                _discPresetJsonPath,
                DiscPresetJsonStore.CreateInitialUserDocument());
        }
    }

    /// <summary>
    /// Appends one parameter set to the user preset JSON and refreshes the catalog. The
    /// display name and note come from the save dialog the user confirmed; the id carries a
    /// timestamp plus a random suffix so two saves within the same second stay distinct.
    /// Refreshing uses <see cref="LoadPresets"/> on purpose: it swaps the lists without
    /// re-applying the selected preset, so the open project's live values are never
    /// overwritten. Throws when the name is blank or the JSON cannot be read or written;
    /// the caller surfaces the message.
    /// </summary>
    public string AddUserPreset(
        bool isDvd,
        string displayName,
        string description,
        double innerRadiusMm,
        double outerRadiusMm,
        long sectors,
        double pitchLinear = 0,
        double pitchQuadratic = 0,
        double pitchCubic = 0)
    {
        string name = displayName.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("请填写预设名称。", nameof(displayName));
        }

        EnsurePresetsJsonExists();
        DiscPresetJsonDocument user = DiscPresetJsonStore.Load(_discPresetJsonPath);
        DateTime now = DateTime.Now;
        string id = $"user-{(isDvd ? "dvd" : "cd")}-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        string note = description.Trim();
        DiscPresetJsonDocument updated = isDvd
            ? user with
            {
                SchemaVersion = DiscPresetJsonStore.CurrentSchemaVersion,
                DvdPresets =
                [
                    .. user.DvdPresets,
                    new DvdDiscPresetDefinition(
                        id,
                        name,
                        note,
                        checked((uint)sectors),
                        innerRadiusMm,
                        outerRadiusMm,
                        PitchLinear: pitchLinear,
                        PitchQuadratic: pitchQuadratic,
                        PitchCubic: pitchCubic),
                ],
            }
            : user with
            {
                SchemaVersion = DiscPresetJsonStore.CurrentSchemaVersion,
                CdPresets =
                [
                    .. user.CdPresets,
                    new CdDiscPresetDefinition(
                        id,
                        name,
                        note,
                        sectors,
                        innerRadiusMm,
                        outerRadiusMm),
                ],
            };

        DiscPresetJsonStore.Save(_discPresetJsonPath, updated);
        LoadPresets();
        return displayName;
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
                value.DescriptionResourceKey,
                PitchLinear: value.PitchLinear,
                PitchQuadratic: value.PitchQuadratic,
                PitchCubic: value.PitchCubic))
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

    partial void OnDvdPitchLinearChanged(string value) => OnDvdParameterEdited(presetField: true);
    partial void OnDvdPitchQuadraticChanged(string value) => OnDvdParameterEdited(presetField: true);
    partial void OnDvdPitchCubicChanged(string value) => OnDvdParameterEdited(presetField: true);

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
                DvdPitchLinear = FormatPresetNumber(preset.PitchLinear);
                DvdPitchQuadratic = FormatPresetNumber(preset.PitchQuadratic);
                DvdPitchCubic = FormatPresetNumber(preset.PitchCubic);
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
