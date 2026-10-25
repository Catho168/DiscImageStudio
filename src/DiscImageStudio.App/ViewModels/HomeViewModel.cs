using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Dvd;
using DiscImageStudio.Services;
using MahApps.Metro.IconPacks;

namespace DiscImageStudio.ViewModels;

/// <summary>
/// Start page: a welcome heading, an action bar (disc family plus New/Open), then quick-create
/// preset tiles and the recent list. The disc-family flag is shared with the live preview page,
/// so the action bar, the tiles and the rest of the shell agree.
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    private const int ThumbnailDecodeWidth = 320;
    private const long ThumbnailMaxBytes = 64L * 1024 * 1024;

    /// <summary>The catalog is user-editable and can grow without bound, while the reference
    /// start screen keeps its quick-create row to a handful of curated cards. Only the first
    /// few presets per family (the recommended ones by catalog order) get a card; every
    /// preset stays selectable on the disc pages.</summary>
    private const int PresetsPerFamily = 2;

    private readonly ShellViewModel _shell;
    private readonly DiscParametersState _state;
    private readonly RecentJobStore _recentStore;

    public HomeViewModel(ShellViewModel shell, DiscParametersState state, RecentJobStore recentStore)
    {
        _shell = shell;
        _state = state;
        _recentStore = recentStore;
        NewCommand = new RelayCommand(NewJob, () => !_shell.IsBusy);
        OpenCommand = new RelayCommand(OpenImage, () => !_shell.IsBusy);
        CreateTileCommand = new RelayCommand<QuickCreateTile>(CreateFromTile, tile => tile is not null && !_shell.IsBusy);
        OpenRecentCommand = new RelayCommand<RecentJobTile>(OpenRecent, tile => tile is not null && !_shell.IsBusy);
        ClearRecentsCommand = new RelayCommand(ClearRecents, () => HasRecents);
        _shell.BusyChanged += NotifyCommands;
        _state.PropertyChanged += State_PropertyChanged;
        _shell.LivePreview.PropertyChanged += LivePreview_PropertyChanged;
        BuildTiles();
        RefreshRecents();
    }

    public ObservableCollection<QuickCreateTile> Tiles { get; } = [];

    public ObservableCollection<RecentJobTile> Recents { get; } = [];

    public bool HasRecents => Recents.Count > 0;

    /// <summary>Disc family of the action bar's New/Open actions; the same selection the live preview
    /// page's disc-type selector owns.</summary>
    public int SelectedDiscTypeIndex
    {
        get => _shell.LivePreview.SelectedDiscTypeIndex;
        set => _shell.LivePreview.SelectedDiscTypeIndex = value;
    }

    public bool IsDvdSelected
    {
        get => _shell.LivePreview.IsDvdSelected;
        set => _shell.LivePreview.IsDvdSelected = value;
    }

    public bool IsBluRaySelected => SelectedDiscTypeIndex == 2;

    public RelayCommand NewCommand { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand<QuickCreateTile> CreateTileCommand { get; }

    public RelayCommand<RecentJobTile> OpenRecentCommand { get; }

    public RelayCommand ClearRecentsCommand { get; }

    /// <summary>Opens the editable disc-preset JSON; owned by the shell so the start page
    /// and the disc pages share one implementation.</summary>
    public RelayCommand OpenPresetsJsonCommand => _shell.OpenPresetsJsonCommand;

    /// <summary>Called by the shell once a generation job has succeeded.</summary>
    internal void RecordJob(string discFamily, string sourceImagePath, string outputPath)
    {
        _recentStore.Record(new RecentJobEntry(
            discFamily,
            sourceImagePath,
            outputPath,
            DateTimeOffset.Now));
        RefreshRecents();
    }

    private void NotifyCommands()
    {
        NewCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
        CreateTileCommand.NotifyCanExecuteChanged();
        OpenRecentCommand.NotifyCanExecuteChanged();
    }

    /// "新建": the recommended preset of the selected family, then its parameter page.
    private void NewJob()
    {
        if (IsBluRaySelected)
        {
            _shell.BluRay.ApplyDiscPreset(BluRayDiscPreset.BuiltIn[0]);
            _shell.NavigateTo(ShellViewModel.BluRayTabIndex);
        }
        else if (IsDvdSelected)
        {
            _state.ApplyDvdDiscPreset(_state.DvdPresets[0]);
            _shell.NavigateTo(ShellViewModel.DvdTabIndex);
        }
        else
        {
            _state.ApplyCdDiscPreset(_state.CdPresets[0]);
            _shell.NavigateTo(ShellViewModel.CdTabIndex);
        }
    }

    /// "打开": pick a source picture for the selected family, then its parameter page.
    /// The shell pickers own the output/preview path prefill, so they are reused as-is.
    private void OpenImage()
    {
        int discType = SelectedDiscTypeIndex;
        bool opened = discType switch
        {
            2 => _shell.BluRay.BrowseImage(),
            1 => _shell.BrowseDvdImage(),
            _ => _shell.BrowseCdImage(),
        };
        if (opened)
        {
            NavigateToFamily(discType);
        }
    }

    private void CreateFromTile(QuickCreateTile? tile)
    {
        if (tile is null)
        {
            return;
        }

        if (tile.PresetId is null)
        {
            // Custom size: keep the family and parameters the user already has, open the page.
            NavigateToFamily(SelectedDiscTypeIndex);
            return;
        }

        SelectedDiscTypeIndex = tile.DiscTypeIndex;
        if (tile.IsBluRay)
        {
            _shell.BluRay.ApplyDiscPreset(
                BluRayDiscPreset.BuiltIn.FirstOrDefault(value => value.Id == tile.PresetId) ?? BluRayDiscPreset.BuiltIn[0]);
        }
        else if (tile.IsDvd)
        {
            _state.ApplyDvdDiscPreset(
                _state.DvdPresets.FirstOrDefault(value => value.Id == tile.PresetId) ?? DvdDiscPreset.Manual);
        }
        else
        {
            _state.ApplyCdDiscPreset(
                _state.CdPresets.FirstOrDefault(value => value.Id == tile.PresetId) ?? CdDiscPreset.Manual);
        }

        NavigateToFamily(tile.DiscTypeIndex);
    }

    private void OpenRecent(RecentJobTile? tile)
    {
        if (tile is null)
        {
            return;
        }

        RecentJobEntry entry = tile.Entry;
        if (!File.Exists(entry.SourceImagePath))
        {
            _shell.ShowToast(
                "无法打开",
                $"源图片已不存在：\n{entry.SourceImagePath}",
                ToastKind.Warning);
            return;
        }

        SelectedDiscTypeIndex = entry.DiscTypeIndex;
        if (entry.IsBluRay)
        {
            _shell.BluRay.ImagePath = entry.SourceImagePath;
            _shell.BluRay.ImageOutputPath = entry.OutputPath;
            _shell.LivePreview.ImportBluRayIso(entry.OutputPath);
        }
        else if (entry.IsDvd)
        {
            _state.DvdImagePath = entry.SourceImagePath;
            _state.DvdOutputPath = entry.OutputPath;
        }
        else
        {
            _state.CdImagePath = entry.SourceImagePath;
            _state.CdOutputPath = entry.OutputPath;
        }

        NavigateToFamily(entry.DiscTypeIndex);
    }

    private void ClearRecents()
    {
        _recentStore.Clear();
        RefreshRecents();
    }

    private void NavigateToFamily(int discTypeIndex)
        => _shell.NavigateTo(discTypeIndex switch
        {
            2 => ShellViewModel.BluRayTabIndex,
            1 => ShellViewModel.DvdTabIndex,
            _ => ShellViewModel.CdTabIndex,
        });

    private void RefreshRecents()
    {
        // Reuse the cards that are already on screen: decoding thumbnails is the only real
        // cost here, and every refresh after a job would otherwise redo the whole list.
        Dictionary<RecentJobEntry, RecentJobTile> loaded = Recents.ToDictionary(tile => tile.Entry);
        Recents.Clear();
        foreach (RecentJobEntry entry in _recentStore.Load())
        {
            Recents.Add(loaded.TryGetValue(entry, out RecentJobTile? tile)
                ? tile
                : RecentJobTile.Create(entry, ThumbnailDecodeWidth, ThumbnailMaxBytes));
        }

        OnPropertyChanged(nameof(HasRecents));
        ClearRecentsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Preset cards follow the reference start screens: the caption is the disc family the card
    /// creates, and the preset's parenthetical becomes the detail behind the hover text ("A4" and
    /// "4.7 GB，推荐" instead of one long label). Presets without a parenthetical fall back to
    /// their geometry.
    /// </summary>
    private void BuildTiles()
    {
        Tiles.Clear();
        foreach (DvdDiscPreset preset in _state.DvdPresets.Where(value => !value.IsCustom).Take(PresetsPerFamily))
        {
            (string title, string detail) = SplitPresetName(
                preset.DisplayName,
                $"{preset.TotalSectors:N0} 扇区 · {preset.InnerRadiusMm:0.#}–{preset.OuterRadiusMm:0.#} mm");
            Tiles.Add(new QuickCreateTile(title, "DVD", detail, PackIconLucideKind.Disc, RecentJobEntry.DvdFamily, preset.Id));
        }

        foreach (CdDiscPreset preset in _state.CdPresets.Where(value => !value.IsCustom).Take(PresetsPerFamily))
        {
            (string title, string detail) = SplitPresetName(
                preset.DisplayName,
                $"{preset.Sectors:N0} 扇区 · {preset.InnerRadiusMm:0.#}–{preset.OuterRadiusMm:0.#} mm");
            Tiles.Add(new QuickCreateTile(title, "CD", detail, PackIconLucideKind.Disc3, RecentJobEntry.CdFamily, preset.Id));
        }

        // Home is constructed before the BD page, so build cards from the static catalog.
        foreach (BluRayDiscPreset preset in BluRayDiscPreset.BuiltIn.Take(PresetsPerFamily))
        {
            BluRayDiscParameters parameters = preset.CreateParameters();
            Tiles.Add(new QuickCreateTile(preset.DisplayName, "BD",
                $"{preset.TotalSectors:N0} 扇区 · {parameters.InnerRadiusMm:0.#}–{parameters.OuterRadiusMm:0.#} mm",
                PackIconLucideKind.Disc, RecentJobEntry.BluRayFamily, preset.Id));
        }

        // No family of its own: the card opens whichever family the action bar has selected.
        Tiles.Add(new QuickCreateTile(
            "自定义大小",
            "设置扇区与半径",
            "打开当前选中的光盘类型",
            PackIconLucideKind.Ruler,
            DiscFamily: string.Empty,
            PresetId: null));
    }

    private static (string Title, string Spec) SplitPresetName(string displayName, string fallbackSpec)
    {
        int open = displayName.LastIndexOf('（');
        if (open > 0 && displayName.EndsWith('）'))
        {
            string detail = displayName[(open + 1)..^1].Trim();
            if (detail.Length > 0)
            {
                return (displayName[..open].Trim(), detail);
            }
        }

        return (displayName, fallbackSpec);
    }

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The user can add or drop presets in the JSON while the app runs and reload them
        // from the disc pages; the quick-create row follows the same catalog.
        if (e.PropertyName is nameof(DiscParametersState.CdPresets) or nameof(DiscParametersState.DvdPresets))
        {
            BuildTiles();
        }
    }

    private void LivePreview_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LivePreviewViewModel.SelectedDiscTypeIndex))
        {
            OnPropertyChanged(nameof(SelectedDiscTypeIndex));
            OnPropertyChanged(nameof(IsDvdSelected));
            OnPropertyChanged(nameof(IsBluRaySelected));
        }
    }
}
