using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

/// <summary>What the shell asks of the project dialog: create a new project (optionally
/// preselecting a family and template preset) or edit the open project's settings.</summary>
public sealed record ProjectDialogRequest(
    bool EditMode,
    OpticalDiscFamily? PreselectFamily = null,
    string? PresetId = null);

/// <summary>The dialog's outcome. Null (from the handler) means the user cancelled.</summary>
public sealed record ProjectDialogResult(
    string Name,
    OpticalDiscFamily Family,
    double InnerRadiusMm,
    double OuterRadiusMm,
    long Sectors,
    double PitchLinear,
    double PitchQuadratic,
    double PitchCubic,
    string SavePath,
    bool BluRayInvertMapping = false);

/// <summary>The name and optional note the user gave a preset in the save dialog. Null (from
/// the handler) means the user cancelled.</summary>
public sealed record PresetSaveResult(string Name, string Comment);

/// <summary>Backing state for ProjectDialogWindow. One instance per dialog; the window binds
/// straight to these properties and calls <see cref="ConfirmCommand"/> / cancel.</summary>
public partial class ProjectDialogViewModel : ObservableObject
{
    private readonly DiscParametersState _state;
    private readonly bool _editMode;

    public ProjectDialogViewModel(DiscParametersState state, ProjectDialogRequest request)
    {
        _state = state;
        _editMode = request.EditMode;
        // Commands first: the prefill below writes observable properties whose partial
        // changed handlers notify those commands.
        ConfirmCommand = new RelayCommand(Confirm, CanConfirm);
        BrowseSavePathCommand = new RelayCommand(BrowseSavePath);
        SaveAsPresetCommand = new RelayCommand(SaveAsPreset);
        FamilyIndex = (int)(request.PreselectFamily ?? OpticalDiscFamily.CompactDisc);
        BuildTemplates();
        if (_editMode)
        {
            Name = "工程设置";
        }
        else if (request.PresetId is not null)
        {
            ProjectTemplateTile? tile = Templates.FirstOrDefault(t => t.PresetId == request.PresetId);
            if (tile is not null)
            {
                Family = tile.Family;
                SelectedTemplate = tile;
            }
        }
        else
        {
            SelectedTemplate = Templates.FirstOrDefault();
        }

        SaveDirectory = ProjectJsonStore.GetDefaultProjectsDirectory();
    }

    public bool EditMode => _editMode;

    public string DialogTitle => _editMode ? "工程设置" : "新建工程";

    public string ConfirmLabel => _editMode ? "确定" : "创建工程";

    public System.Windows.Visibility ShowTemplatesVisibility => _editMode
        ? System.Windows.Visibility.Collapsed
        : System.Windows.Visibility.Visible;

    public System.Windows.Visibility IsDvdFieldsVisibility => IsDvd
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    /// <summary>Live values for the annulus diagram; unparsable fields fall back to the
    /// blank-project defaults so the diagram never blanks out mid-edit.</summary>
    public double InnerRadiusValue => TryParseMm(InnerRadiusText)
        ?? (IsBluRay ? 24.0 : IsDvd ? 24.0 : 24.5);

    public double OuterRadiusValue => TryParseMm(OuterRadiusText)
        ?? (IsBluRay ? 58.0 : IsDvd ? 58.0 : 56.8);

    public string SaveFileNameHint
    {
        get
        {
            string name = string.IsNullOrWhiteSpace(Name) ? "未命名" : Name.Trim();
            return $"将保存为：{SanitizeFileName(name)}{ProjectJsonStore.FileExtension}";
        }
    }

    /// <summary>Template picking only exists on the create path; the open project's family is
    /// fixed once created.</summary>
    public bool FamilySwitchEnabled => !_editMode;

    public bool ShowTemplates => !_editMode;

    public System.Windows.Visibility EditModeVisibility => _editMode
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    // ---- Preset catalog: the open project's family is fixed once created, so the dialog
    //      no longer offers preset picking; the edit mode instead saves the current fields
    //      into the user catalog (SaveAsPresetCommand). ----

    [ObservableProperty]
    private string _savedPresetMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCd), nameof(IsDvd), nameof(IsBluRay), nameof(FamilyIndex),
        nameof(FamilySwitchEnabled), nameof(ShowTemplates), nameof(IsCdFieldsVisible),
        nameof(IsDvdFieldsVisible), nameof(IsDvdFieldsVisibility), nameof(IsBluRayFieldsVisibility),
        nameof(InnerRadiusValue), nameof(OuterRadiusValue))]
    private OpticalDiscFamily _family = OpticalDiscFamily.CompactDisc;

    public bool IsCd => Family == OpticalDiscFamily.CompactDisc;

    public bool IsDvd => Family == OpticalDiscFamily.Dvd;

    public bool IsBluRay => Family == OpticalDiscFamily.BluRay;

    /// <summary>Three-option segmented selector binding.</summary>
    public int FamilyIndex
    {
        get => (int)Family;
        set => Family = (OpticalDiscFamily)Math.Clamp(value, 0, 2);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveFileNameHint))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InnerRadiusValue))]
    private string _innerRadiusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OuterRadiusValue))]
    private string _outerRadiusText = string.Empty;

    [ObservableProperty]
    private string _sectorsText = string.Empty;

    [ObservableProperty]
    private string _pitchLinearText = "0";

    [ObservableProperty]
    private string _pitchQuadraticText = "0";

    [ObservableProperty]
    private string _pitchCubicText = "0";

    [ObservableProperty]
    private string _saveDirectory;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SaveFileNameHint))]
    private ProjectTemplateTile? _selectedTemplate;

    public bool HasSelection => SelectedTemplate is not null;

    public bool IsCdFieldsVisible => IsCd;

    public bool IsDvdFieldsVisible => IsDvd;

    public System.Windows.Visibility IsBluRayFieldsVisibility => IsBluRay
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    public IReadOnlyList<ProjectTemplateTile> Templates { get; private set; } = [];

    public RelayCommand ConfirmCommand { get; }

    public RelayCommand BrowseSavePathCommand { get; }

    /// <summary>Edit mode only: writes the fields' current geometry into the user preset
    /// catalog, so a tuned project's parameters can seed future projects.</summary>
    public RelayCommand SaveAsPresetCommand { get; }

    /// <summary>Set by the window: asks for the preset's name and note before a save lands
    /// in the catalog. Null result means the user cancelled.</summary>
    internal Func<PresetSaveResult?>? ShowPresetSaveDialog { get; set; }

    /// <summary>Set by the window: folder picker for the save location.</summary>
    internal Func<string, string?>? PickSaveDirectory { get; set; }

    /// <summary>Set by the window: closes the dialog with a positive result.</summary>
    internal Action? CloseWithConfirm { get; set; }

    /// <summary>The result, valid after ConfirmCommand succeeded and the dialog closed.</summary>
    public ProjectDialogResult? Result { get; private set; }

    private bool _templateInvertMapping;
    private bool _suppressFamilyRebuild;

    partial void OnSelectedTemplateChanged(ProjectTemplateTile? value)
    {
        if (value is not null && value.Family != Family)
        {
            // Selecting another family's template switches the form too, without letting
            // the family handler rebuild the list and drop the selection being applied.
            _suppressFamilyRebuild = true;
            try
            {
                Family = value.Family;
            }
            finally
            {
                _suppressFamilyRebuild = false;
            }
        }

        ApplyTemplate();
    }

    partial void OnFamilyChanged(OpticalDiscFamily value)
    {
        if (_suppressFamilyRebuild)
        {
            return;
        }

        BuildTemplates();
        if (!_editMode)
        {
            // Family switch snaps the selection to that family's blank template; the fields
            // then take the blank or preset geometry.
            SelectedTemplate = Templates.FirstOrDefault(
                t => t.PresetId is null && t.Family == value);
        }

        ApplyTemplate();
    }

    partial void OnNameChanged(string value) => ConfirmCommand.NotifyCanExecuteChanged();

    private void BuildTemplates()
    {
        // Every catalog preset is listed, user-saved ones included: this scrollable list is
        // where a preset saved from the project settings gets picked again.
        List<ProjectTemplateTile> templates =
        [
            new(null, "空白工程", "从默认参数开始", Family),
        ];
        foreach (DvdDiscPreset preset in _state.DvdPresets.Where(p => !p.IsCustom))
        {
            templates.Add(new(
                preset.Id,
                preset.DisplayName,
                $"{preset.TotalSectors:N0} 扇区 · {preset.InnerRadiusMm:0.#}–{preset.OuterRadiusMm:0.#} mm",
                OpticalDiscFamily.Dvd));
        }

        foreach (CdDiscPreset preset in _state.CdPresets.Where(p => !p.IsCustom))
        {
            templates.Add(new(
                preset.Id,
                preset.DisplayName,
                $"{preset.Sectors:N0} 扇区 · {preset.InnerRadiusMm:0.#}–{preset.OuterRadiusMm:0.#} mm",
                OpticalDiscFamily.CompactDisc));
        }

        foreach (BluRayDiscPreset preset in _state.BluRayPresets.Where(p => !p.IsCustom))
        {
            BluRayDiscParameters geometry = preset.CreateParameters();
            templates.Add(new(
                preset.Id,
                preset.DisplayName,
                $"{geometry.TotalSectors:N0} 扇区 · {geometry.InnerRadiusMm:0.#}–{geometry.OuterRadiusMm:0.#} mm"
                + (preset.InvertMapping ? " · 反转 3T/6T" : string.Empty),
                OpticalDiscFamily.BluRay));
        }

        Templates = templates;
        OnPropertyChanged(nameof(Templates));
    }

    /// <summary>Template or family switches write the preset's geometry into the fields;
    /// keeping them as text lets the user adjust before confirming.</summary>
    private void ApplyTemplate()
    {
        if (_editMode)
        {
            return;
        }

        ProjectTemplateTile? tile = SelectedTemplate;
        _templateInvertMapping = Family == OpticalDiscFamily.BluRay && _state.BdInvertMapping;
            if (tile?.PresetId is null)
            {
                switch (Family)
                {
                    case OpticalDiscFamily.Dvd:
                        InnerRadiusText = "24.0";
                        OuterRadiusText = "58.0";
                        SectorsText = "2295104";
                        PitchLinearText = "0";
                        PitchQuadraticText = "0";
                        PitchCubicText = "0";
                        break;
                    case OpticalDiscFamily.BluRay:
                        InnerRadiusText = "24.0";
                        OuterRadiusText = "58.0";
                        SectorsText = "12219392";
                        PitchLinearText = "0";
                        PitchQuadraticText = "0";
                        PitchCubicText = "0";
                        _templateInvertMapping = false;
                        break;
                    default:
                        InnerRadiusText = "24.5";
                        OuterRadiusText = "56.8";
                        SectorsText = "359849";
                        break;
                }
            }
            else if (Family == OpticalDiscFamily.Dvd)
            {
                DvdDiscPreset preset = _state.DvdPresets.FirstOrDefault(p => p.Id == tile.PresetId)
                    ?? DvdDiscPreset.Manual;
                if (!preset.IsCustom)
                {
                    InnerRadiusText = preset.InnerRadiusMm.ToString("R", CultureInfo.InvariantCulture);
                    OuterRadiusText = preset.OuterRadiusMm.ToString("R", CultureInfo.InvariantCulture);
                    SectorsText = preset.TotalSectors.ToString(CultureInfo.InvariantCulture);
                    PitchLinearText = preset.PitchLinear.ToString("R", CultureInfo.InvariantCulture);
                    PitchQuadraticText = preset.PitchQuadratic.ToString("R", CultureInfo.InvariantCulture);
                    PitchCubicText = preset.PitchCubic.ToString("R", CultureInfo.InvariantCulture);
                }
            }
            else if (Family == OpticalDiscFamily.BluRay)
            {
                BluRayDiscPreset preset = _state.BluRayPresets.FirstOrDefault(p => p.Id == tile.PresetId)
                    ?? BluRayDiscPreset.Manual;
                if (!preset.IsCustom)
                {
                    BluRayDiscParameters geometry = preset.CreateParameters();
                    InnerRadiusText = geometry.InnerRadiusMm.ToString("R", CultureInfo.InvariantCulture);
                    OuterRadiusText = geometry.OuterRadiusMm.ToString("R", CultureInfo.InvariantCulture);
                    SectorsText = geometry.TotalSectors.ToString(CultureInfo.InvariantCulture);
                    PitchLinearText = "0";
                    PitchQuadraticText = "0";
                    PitchCubicText = "0";
                    _templateInvertMapping = preset.InvertMapping;
                }
            }
            else
            {
                CdDiscPreset preset = _state.CdPresets.FirstOrDefault(p => p.Id == tile.PresetId)
                    ?? CdDiscPreset.Manual;
                if (!preset.IsCustom)
                {
                    InnerRadiusText = preset.InnerRadiusMm.ToString("R", CultureInfo.InvariantCulture);
                    OuterRadiusText = preset.OuterRadiusMm.ToString("R", CultureInfo.InvariantCulture);
                    SectorsText = preset.Sectors.ToString(CultureInfo.InvariantCulture);
                }
            }
    }

    private bool CanConfirm() => !string.IsNullOrWhiteSpace(Name);

    private void BrowseSavePath()
    {
        string? picked = PickSaveDirectory?.Invoke(SaveDirectory);
        if (picked is not null)
        {
            SaveDirectory = picked;
        }
    }

    private void Confirm()
    {
        try
        {
            Result = BuildResult();
            ErrorMessage = string.Empty;
            CloseWithConfirm?.Invoke();
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or FormatException
                                              or OverflowException
                                              or ArgumentOutOfRangeException)
        {
            ErrorMessage = exception.Message;
        }
    }

    private ProjectDialogResult BuildResult()
    {
        string name = Name.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("请填写工程名。");
        }

        (double inner, double outer, long sectors, double pitchLinear, double pitchQuadratic, double pitchCubic)
            = ParseGeometry();
        string directory = SaveDirectory.Trim();
        if (directory.Length == 0)
        {
            throw new ArgumentException("请选择保存位置。");
        }

        string fileName = $"{SanitizeFileName(name)}{ProjectJsonStore.FileExtension}";
        string savePath = Path.Combine(directory, fileName);
        return new ProjectDialogResult(
            name,
            Family,
            inner,
            outer,
            sectors,
            pitchLinear,
            pitchQuadratic,
            pitchCubic,
            savePath,
            BluRayInvertMapping: Family == OpticalDiscFamily.BluRay
                && (_editMode ? _state.BdInvertMapping : _templateInvertMapping));
    }

    /// <summary>Shared by confirming the dialog and saving a preset: the geometry fields with
    /// the same rules both paths enforce.</summary>
    private (double Inner, double Outer, long Sectors, double PitchLinear, double PitchQuadratic, double PitchCubic)
        ParseGeometry()
    {
        double inner = ParseField(InnerRadiusText, "内半径");
        double outer = ParseField(OuterRadiusText, "外半径");
        long sectors = long.Parse(SectorsText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (sectors <= 0)
        {
            throw new ArgumentException("扇区数必须是正整数。");
        }

        if (IsDvd && sectors % 16 != 0)
        {
            throw new ArgumentException("DVD 的扇区数必须是 16 的倍数。");
        }

        if (IsBluRay)
        {
            if (sectors % ProjectDocument.BluRaySectorsPerCluster != 0)
            {
                throw new ArgumentException("蓝光的扇区数必须是 32 的倍数。");
            }

            if (sectors > ProjectDocument.BluRaySectorsPerLayer)
            {
                throw new ArgumentException(
                    $"蓝光扇区数不能超过单层容量 {ProjectDocument.BluRaySectorsPerLayer:N0}。");
            }

            if (outer > 60)
            {
                throw new ArgumentException("蓝光外半径不能超过 60 mm。");
            }
        }

        if (outer <= inner)
        {
            throw new ArgumentException("外半径必须大于内半径。");
        }

        double pitchLinear = 0, pitchQuadratic = 0, pitchCubic = 0;
        if (IsDvd)
        {
            // Pitch coefficients are deltas, so zero is the normal default; only finiteness
            // is required here, unlike the strictly positive radii.
            pitchLinear = ParseNumber(PitchLinearText, "相对一次项");
            pitchQuadratic = ParseNumber(PitchQuadraticText, "相对二次项");
            pitchCubic = ParseNumber(PitchCubicText, "相对三次项");
            DvdTrackGeometry.ValidatePitchCoefficients(pitchLinear, pitchQuadratic, pitchCubic);
        }

        return (inner, outer, sectors, pitchLinear, pitchQuadratic, pitchCubic);
    }

    private void SaveAsPreset()
    {
        try
        {
            // Geometry validates first: an invalid set reports here instead of after the
            // user has already typed a name into the save dialog.
            (double inner, double outer, long sectors, double pitchLinear, double pitchQuadratic, double pitchCubic)
                = ParseGeometry();
            PresetSaveResult? confirmed = ShowPresetSaveDialog?.Invoke();
            if (confirmed is null)
            {
                return;
            }

            string displayName = _state.AddUserPreset(
                Family,
                confirmed.Name,
                confirmed.Comment,
                inner,
                outer,
                sectors,
                pitchLinear,
                pitchQuadratic,
                pitchCubic,
                invertMapping: Family == OpticalDiscFamily.BluRay && _state.BdInvertMapping);
            ErrorMessage = string.Empty;
            SavedPresetMessage = $"已保存为预设：{displayName}";
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or FormatException
                                              or OverflowException
                                              or ArgumentOutOfRangeException
                                              or InvalidOperationException
                                              or InvalidDataException)
        {
            SavedPresetMessage = string.Empty;
            ErrorMessage = $"无法保存预设：{exception.Message}";
        }
    }

    private static double? TryParseMm(string raw)
    {
        if (double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            if (double.IsFinite(value) && value > 0)
            {
                return value;
            }
        }

        return null;
    }

    private static double ParseField(string raw, string field)
        => TryParseMm(raw) ?? throw new FormatException($"{field}不是有效数字。");

    private static double ParseNumber(string raw, string field)
    {
        if (double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            if (double.IsFinite(value))
            {
                return value;
            }
        }

        throw new FormatException($"{field}不是有效数字。");
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return cleaned.Length == 0 ? "工程" : cleaned;
    }
}

/// <summary>One template row in the dialog's create mode: the blank project (PresetId null)
/// or a preset from the editable catalog.</summary>
public sealed record ProjectTemplateTile(
    string? PresetId,
    string Title,
    string Detail,
    OpticalDiscFamily Family)
{
    public string FamilyLabel => Family switch
    {
        OpticalDiscFamily.Dvd => "DVD",
        OpticalDiscFamily.BluRay => "BD",
        _ => "CD",
    };
}
