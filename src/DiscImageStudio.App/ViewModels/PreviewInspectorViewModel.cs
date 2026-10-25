using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiscImageStudio.ViewModels;

/// <summary>
/// The preview workspace's parameter panel, projected over whichever disc family the family
/// switch has selected. CD and DVD keep separate parameter sets in
/// <see cref="DiscParametersState"/> — they are different physical models — but the panel is one
/// set of rows that re-points when the family changes, instead of a second copy of itself. The
/// projections are read/write where the preview owns the value (measured geometry, preview
/// precision), so an edit lands on the family that is currently on screen.
/// </summary>
public class PreviewInspectorViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly DiscParametersState _state;
    private bool _isDvd;

    public PreviewInspectorViewModel(ShellViewModel shell, DiscParametersState state)
    {
        _shell = shell;
        _state = state;
        _state.PropertyChanged += State_PropertyChanged;
    }

    /// <summary>The shared parameters, for the rows the panel edits directly (interleave).</summary>
    public DiscParametersState State => _state;

    public bool IsDvd => _isDvd;

    public bool IsCd => !_isDvd;

    /// <summary>Re-points every row at the given family. Called by the preview view model when
    /// its family switch changes.</summary>
    internal void SetFamily(bool dvd)
    {
        if (_isDvd == dvd)
        {
            return;
        }

        _isDvd = dvd;
        foreach (string name in ProjectedProperties)
        {
            OnPropertyChanged(name);
        }
    }

    // ---- Read-back mode: the generated artifact is the input ----

    public string ArtifactLabel => _isDvd ? "生成的 DVD ISO" : "生成的 CD 音轨";

    public string ArtifactHint => _isDvd
        ? "可直接粘贴或浏览已生成的 ISO 镜像。"
        : "可直接粘贴或浏览已生成的音轨。";

    public ICommand BrowseArtifactCommand =>
        _isDvd ? _shell.BrowseDvdIsoCommand : _shell.BrowseCdTrackCommand;

    public string ArtifactPath
    {
        get => _isDvd ? _state.DvdIsoPath : _state.CdTrackPath;
        set
        {
            if (_isDvd)
            {
                _state.DvdIsoPath = value;
            }
            else
            {
                _state.CdTrackPath = value;
            }
        }
    }

    // ---- Generation geometry: owned by the project settings dialog ----

    public ICommand EditGeometryCommand => _shell.EditProjectCommand;

    // ---- Measured geometry: the preview's own calibration values ----

    public string MeasuredInnerRadius
    {
        get => _isDvd ? _state.DvdActualInnerRadius : _state.CdActualInnerRadius;
        set
        {
            if (_isDvd)
            {
                _state.DvdActualInnerRadius = value;
            }
            else
            {
                _state.CdActualInnerRadius = value;
            }
        }
    }

    public string MeasuredOuterRadius
    {
        get => _isDvd ? _state.DvdActualOuterRadius : _state.CdActualOuterRadius;
        set
        {
            if (_isDvd)
            {
                _state.DvdActualOuterRadius = value;
            }
            else
            {
                _state.CdActualOuterRadius = value;
            }
        }
    }

    public string MeasuredPitchLinear
    {
        get => _state.DvdActualPitchLinear;
        set => _state.DvdActualPitchLinear = value;
    }

    public string MeasuredPitchQuadratic
    {
        get => _state.DvdActualPitchQuadratic;
        set => _state.DvdActualPitchQuadratic = value;
    }

    public string MeasuredPitchCubic
    {
        get => _state.DvdActualPitchCubic;
        set => _state.DvdActualPitchCubic = value;
    }

    public string MeasuredPitchSummary => _state.DvdActualPitchSummary;

    // ---- Preview precision: inspection behaviour, not a project parameter ----

    public string PreviewSize
    {
        get => _isDvd ? _state.DvdPreviewSize : _state.CdPreviewSize;
        set
        {
            if (_isDvd)
            {
                _state.DvdPreviewSize = value;
            }
            else
            {
                _state.CdPreviewSize = value;
            }
        }
    }

    public string SamplesPerSector
    {
        get => _isDvd ? _state.DvdSamplesPerSector : _state.CdSamplesPerSector;
        set
        {
            if (_isDvd)
            {
                _state.DvdSamplesPerSector = value;
            }
            else
            {
                _state.CdSamplesPerSector = value;
            }
        }
    }

    /// <summary>Every property whose value depends on the selected family. Kept as one list so
    /// the family switch and the state forwarder cannot disagree about what to refresh.</summary>
    private static readonly string[] ProjectedProperties =
    [
        nameof(IsDvd),
        nameof(IsCd),
        nameof(ArtifactLabel),
        nameof(ArtifactHint),
        nameof(BrowseArtifactCommand),
        nameof(ArtifactPath),
        nameof(MeasuredInnerRadius),
        nameof(MeasuredOuterRadius),
        nameof(MeasuredPitchLinear),
        nameof(MeasuredPitchQuadratic),
        nameof(MeasuredPitchCubic),
        nameof(MeasuredPitchSummary),
        nameof(PreviewSize),
        nameof(SamplesPerSector),
    ];

    /// <summary>Forwards the shared state's changes onto the projected rows. Grouped names
    /// (e.g. <c>CdGeometrySummary</c>) refresh every row that composes them.</summary>
    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        void Raise(string name) => OnPropertyChanged(name);

        switch (e.PropertyName)
        {
            case nameof(DiscParametersState.CdTrackPath):
            case nameof(DiscParametersState.DvdIsoPath):
                Raise(nameof(ArtifactPath));
                break;
            case nameof(DiscParametersState.CdActualInnerRadius):
            case nameof(DiscParametersState.DvdActualInnerRadius):
                Raise(nameof(MeasuredInnerRadius));
                break;
            case nameof(DiscParametersState.CdActualOuterRadius):
            case nameof(DiscParametersState.DvdActualOuterRadius):
                Raise(nameof(MeasuredOuterRadius));
                break;
            case nameof(DiscParametersState.DvdActualPitchLinear):
                Raise(nameof(MeasuredPitchLinear));
                break;
            case nameof(DiscParametersState.DvdActualPitchQuadratic):
                Raise(nameof(MeasuredPitchQuadratic));
                break;
            case nameof(DiscParametersState.DvdActualPitchCubic):
                Raise(nameof(MeasuredPitchCubic));
                break;
            case nameof(DiscParametersState.DvdActualPitchSummary):
                Raise(nameof(MeasuredPitchSummary));
                break;
            case nameof(DiscParametersState.CdPreviewSize):
            case nameof(DiscParametersState.DvdPreviewSize):
                Raise(nameof(PreviewSize));
                break;
            case nameof(DiscParametersState.CdSamplesPerSector):
            case nameof(DiscParametersState.DvdSamplesPerSector):
                Raise(nameof(SamplesPerSector));
                break;
        }
    }
}
