using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Services;
using MahApps.Metro.IconPacks;

namespace DiscImageStudio.ViewModels;

/// <summary>
/// Start page, PR-style: a heading with New/Open, a template row (blank project plus the
/// first presets of each disc family), then the recent-project cards. The page shows the
/// home mode of the shell; all project actions run through the shell.
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    /// <summary>The catalog is user-editable and can grow without bound, while the start
    /// screen keeps its template row to a handful of curated cards. Only the first presets
    /// per family get a card; every preset stays available in the project dialog.</summary>
    private const int PresetsPerFamily = 2;

    private readonly ShellViewModel _shell;
    private readonly DiscParametersState _state;
    private readonly RecentProjectStore _recentStore;

    public HomeViewModel(ShellViewModel shell, DiscParametersState state, RecentProjectStore recentStore)
    {
        _shell = shell;
        _state = state;
        _recentStore = recentStore;
        NewProjectCommand = new AsyncRelayCommand(
            _ => _shell.NewProjectCommand.ExecuteAsync(null),
            () => !_shell.IsBusy);
        OpenProjectCommand = new AsyncRelayCommand(
            _ => _shell.OpenProjectCommand.ExecuteAsync(null),
            () => !_shell.IsBusy);
        ContinueProjectCommand = new RelayCommand(
            () => _shell.NavigateTo(Workspace.Design),
            () => _shell.IsProjectOpen);
        TemplateCommand = new AsyncRelayCommand<HomeTemplateTile>(CreateFromTemplate, _ => !_shell.IsBusy);
        OpenRecentCommand = new AsyncRelayCommand<RecentProjectTile>(OpenRecent, _ => !_shell.IsBusy);
        RemoveRecentCommand = new RelayCommand<RecentProjectTile>(RemoveRecent);
        ClearRecentsCommand = new RelayCommand(ClearRecents, () => HasRecents);
        _shell.BusyChanged += NotifyCommands;
        _shell.PropertyChanged += Shell_PropertyChanged;
        _state.PropertyChanged += State_PropertyChanged;
        BuildTemplates();
        RefreshRecentProjects();
    }

    public ObservableCollection<HomeTemplateTile> Templates { get; } = [];

    public ObservableCollection<RecentProjectTile> Recents { get; } = [];

    public bool HasRecents => Recents.Count > 0;

    /// <summary>True while a project is open and the user came back to the home page; the
    /// page then offers a jump straight back into it.</summary>
    public bool IsProjectOpen => _shell.IsProjectOpen;

    public System.Windows.Visibility IsProjectOpenVisibility => _shell.IsProjectOpen
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    public string CurrentProjectName => _shell.CurrentProject?.Name ?? string.Empty;

    public IAsyncRelayCommand NewProjectCommand { get; }

    public IAsyncRelayCommand OpenProjectCommand { get; }

    public RelayCommand ContinueProjectCommand { get; }

    public IAsyncRelayCommand<HomeTemplateTile> TemplateCommand { get; }

    public IAsyncRelayCommand<RecentProjectTile> OpenRecentCommand { get; }

    public RelayCommand<RecentProjectTile> RemoveRecentCommand { get; }

    public RelayCommand ClearRecentsCommand { get; }

    /// <summary>Called by the shell whenever the home page becomes visible or the recent
    /// list changes underneath it.</summary>
    internal void RefreshRecentProjects()
    {
        Dictionary<RecentProjectEntry, RecentProjectTile> loaded = Recents.ToDictionary(tile => tile.Entry);
        Recents.Clear();
        foreach (RecentProjectEntry entry in _recentStore.Load())
        {
            Recents.Add(loaded.TryGetValue(entry, out RecentProjectTile? tile)
                ? tile
                : RecentProjectTile.Create(entry));
        }

        OnPropertyChanged(nameof(HasRecents));
        OnPropertyChanged(nameof(IsProjectOpen));
        OnPropertyChanged(nameof(IsProjectOpenVisibility));
        OnPropertyChanged(nameof(CurrentProjectName));
        ContinueProjectCommand.NotifyCanExecuteChanged();
        ClearRecentsCommand.NotifyCanExecuteChanged();
    }

    private void NotifyCommands()
    {
        NewProjectCommand.NotifyCanExecuteChanged();
        OpenProjectCommand.NotifyCanExecuteChanged();
        TemplateCommand.NotifyCanExecuteChanged();
        OpenRecentCommand.NotifyCanExecuteChanged();
        ContinueProjectCommand.NotifyCanExecuteChanged();
    }

    private void Shell_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsProjectOpen) or nameof(ShellViewModel.CurrentProject))
        {
            OnPropertyChanged(nameof(IsProjectOpen));
            OnPropertyChanged(nameof(IsProjectOpenVisibility));
            OnPropertyChanged(nameof(CurrentProjectName));
        }
    }

    /// <summary>Preset cards follow the start-screen idiom: the caption is the disc family
    /// and the preset's parenthetical becomes the detail line.</summary>
    private void BuildTemplates()
    {
        Templates.Clear();
        Templates.Add(HomeTemplateTile.Blank);
        foreach (DvdDiscPreset preset in _state.DvdPresets.Where(value => !value.IsCustom).Take(PresetsPerFamily))
        {
            (string title, string detail) = SplitPresetName(
                preset.DisplayName,
                $"{preset.TotalSectors:N0} 扇区 · {preset.InnerRadiusMm:0.#}–{preset.OuterRadiusMm:0.#} mm");
            Templates.Add(new HomeTemplateTile(
                preset.Id, title, detail, PackIconLucideKind.Disc, OpticalDiscFamily.Dvd, IsBlank: false));
        }

        foreach (CdDiscPreset preset in _state.CdPresets.Where(value => !value.IsCustom).Take(PresetsPerFamily))
        {
            (string title, string detail) = SplitPresetName(
                preset.DisplayName,
                $"{preset.Sectors:N0} 扇区 · {preset.InnerRadiusMm:0.#}–{preset.OuterRadiusMm:0.#} mm");
            Templates.Add(new HomeTemplateTile(
                preset.Id, title, detail, PackIconLucideKind.Disc3,
                OpticalDiscFamily.CompactDisc, IsBlank: false));
        }

        foreach (BluRayDiscPreset preset in _state.BluRayPresets.Where(value => !value.IsCustom).Take(PresetsPerFamily))
        {
            BluRayDiscParameters geometry = preset.CreateParameters();
            (string title, string detail) = SplitPresetName(
                preset.DisplayName,
                $"{geometry.TotalSectors:N0} 扇区 · {geometry.InnerRadiusMm:0.#}–{geometry.OuterRadiusMm:0.#} mm");
            Templates.Add(new HomeTemplateTile(
                preset.Id, title, detail, PackIconLucideKind.DiscAlbum,
                OpticalDiscFamily.BluRay, IsBlank: false));
        }
    }

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The user can add or drop presets in the JSON while the app runs and reload them
        // from the preview page; the template row follows the same catalog.
        if (e.PropertyName is nameof(DiscParametersState.CdPresets)
            or nameof(DiscParametersState.DvdPresets)
            or nameof(DiscParametersState.BluRayPresets))
        {
            BuildTemplates();
        }
    }

    private async Task CreateFromTemplate(HomeTemplateTile? tile)
    {
        if (tile is null || tile.IsBlank)
        {
            // The blank card opens the dialog with its default family; the dialog is where
            // family and parameters are chosen either way.
            await _shell.NewProjectCommand.ExecuteAsync(null);
            return;
        }

        await _shell.RequestNewProjectFromTemplateAsync(tile.Family, tile.PresetId);
    }

    private async Task OpenRecent(RecentProjectTile? tile)
    {
        if (tile is null)
        {
            return;
        }

        RecentProjectEntry entry = tile.Entry;
        if (!File.Exists(entry.ProjectPath))
        {
            _shell.ShowToast(
                "无法打开工程",
                $"工程文件已移动或删除：\n{entry.ProjectPath}",
                ToastKind.Warning);
            return;
        }

        await _shell.OpenProjectFromAsync(entry.ProjectPath);
    }

    /// <summary>Right-click removal: drops one entry from the recent list without touching
    /// the project file itself.</summary>
    private void RemoveRecent(RecentProjectTile? tile)
    {
        if (tile is null)
        {
            return;
        }

        _recentStore.Remove(tile.Entry.ProjectPath);
        RefreshRecentProjects();
    }

    private void ClearRecents()
    {
        _recentStore.Clear();
        RefreshRecentProjects();
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
}
