using System.IO;
using System.Reflection;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.ViewModels;

internal static class PreviewPresetSyncTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-preview-presets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "presets.json");
            CdDiscPresetDefinition cd = new("preview-cd", "Preview CD", "Test", 350_000, 25.1, 57.2);
            DvdDiscPresetDefinition dvd = new("preview-dvd", "Preview DVD", "Test", 2_295_104, 24.1, 57.8,
                PitchLinear: 0.12, PitchQuadratic: -0.08, PitchCubic: 0.04);
            DiscPresetJsonStore.Save(path, new(DiscPresetJsonStore.CurrentSchemaVersion, [cd], [dvd]));
            DiscParametersState state = new();
            // Exercise startup and reload against a temporary catalog, without modifying the user's presets.
            typeof(DiscParametersState).GetField("_discPresetJsonPath", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(state, path);
            state.LoadPresets();
            state.SelectDefaultPresets();
            RequireActualMatchesGeneration(state, "startup defaults");

            state.SelectedCdPreset = state.CdPresets.Single(preset => preset.Id == cd.Id);
            state.SelectedDvdPreset = state.DvdPresets.Single(preset => preset.Id == dvd.Id);
            RequireActualMatchesGeneration(state, "selecting presets");
            Require(state.CdActualInnerRadius == "25.1" && state.DvdActualPitchCubic == "0.04",
                "selection uses the chosen catalog values");

            state.CdActualInnerRadius = "25.2";
            state.DvdActualPitchCubic = "0.05";
            state.CdPreviewSize = "1024";
            state.DvdImagePath = "new-source.png";
            Require(state.CdInnerRadius == "25.1" && state.DvdPitchCubic == "0.04"
                && state.CdActualInnerRadius == "25.2" && state.DvdActualPitchCubic == "0.05"
                && state.SelectedCdPreset.Id == cd.Id && state.SelectedDvdPreset.Id == dvd.Id,
                "actual adjustments persist independently of generation and unrelated preview edits");

            List<string?> changed = [];
            state.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
            cd = cd with { InnerRadiusMm = 25.3, OuterRadiusMm = 57.4 };
            dvd = dvd with { InnerRadiusMm = 24.4, OuterRadiusMm = 57.6,
                PitchLinear = 0.15, PitchQuadratic = -0.06, PitchCubic = 0.02 };
            DiscPresetJsonStore.Save(path, new(DiscPresetJsonStore.CurrentSchemaVersion, [cd], [dvd]));
            state.ReloadPresets();
            RequireActualMatchesGeneration(state, "reloading selected presets");
            Require(state.SelectedCdPreset.Id == cd.Id && state.SelectedDvdPreset.Id == dvd.Id
                && state.CdActualInnerRadius == "25.3" && state.DvdActualPitchCubic == "0.02",
                "reload keeps the selected IDs and adopts their new actual geometry");
            Require(changed.Contains(nameof(state.CdActualInnerRadius))
                && changed.Contains(nameof(state.CdActualOuterRadius))
                && changed.Contains(nameof(state.DvdActualInnerRadius))
                && changed.Contains(nameof(state.DvdActualOuterRadius))
                && changed.Contains(nameof(state.DvdActualPitchLinear))
                && changed.Contains(nameof(state.DvdActualPitchQuadratic))
                && changed.Contains(nameof(state.DvdActualPitchCubic))
                && changed.Contains(nameof(state.DvdActualPitchSummary)),
                "preset synchronization notifies actual-parameter bindings and pitch summary");

            state.CdActualInnerRadius = "25.6";
            state.DvdActualPitchCubic = "0.09";
            state.SelectedCdPreset = CdDiscPreset.Manual;
            state.SelectedDvdPreset = DvdDiscPreset.Manual;
            state.ReloadPresets();
            Require(state.CdActualInnerRadius == "25.6" && state.DvdActualPitchCubic == "0.09",
                "switching to manual and reloading preserves actual adjustments");

            state.ApplyCdDiscPreset(state.CdPresets.Single(preset => preset.Id == cd.Id));
            state.ApplyDvdDiscPreset(state.DvdPresets.Single(preset => preset.Id == dvd.Id));
            state.CdActualInnerRadius = "25.8";
            state.DvdActualPitchCubic = "0.11";
            state.CdInnerRadius = "25.4";
            state.DvdPitchLinear = "0.13";
            Require(state.SelectedCdPreset.IsCustom && state.SelectedDvdPreset.IsCustom
                && state.CdActualInnerRadius == "25.8" && state.DvdActualPitchCubic == "0.11",
                "manual generation edits detach presets without overwriting actual adjustments");
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
        Console.WriteLine("preview-preset-sync: passed (startup, selection, reload, manual adjustments, notifications)");
    }

    private static void RequireActualMatchesGeneration(DiscParametersState state, string scenario)
    {
        Require(state.CdActualInnerRadius == state.CdInnerRadius && state.CdActualOuterRadius == state.CdOuterRadius,
            scenario + " synchronizes CD actual radii");
        Require(state.DvdActualInnerRadius == state.DvdInnerRadius && state.DvdActualOuterRadius == state.DvdOuterRadius
            && state.DvdActualPitchLinear == state.DvdPitchLinear
            && state.DvdActualPitchQuadratic == state.DvdPitchQuadratic
            && state.DvdActualPitchCubic == state.DvdPitchCubic,
            scenario + " synchronizes DVD actual radii and every pitch coefficient");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Preview preset sync: " + message);
    }
}
