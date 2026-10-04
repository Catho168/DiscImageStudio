using System.Globalization;
using System.IO;
using System.Reflection;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Dvd;
using DiscImageStudio.ViewModels;

internal static class AppCubicTests
{
    internal static void Run()
    {
        DvdDiscPreset preset = new("test-cubic", "三次轨距", "测试", 2_295_104, 24, 58,
            PitchLinear: 0.12, PitchQuadratic: -0.08, PitchCubic: 0.04);
        DiscParametersState state = new();
        state.ApplyDvdDiscPreset(preset);
        Require(state.DvdPitchLinear == "0.12" && state.DvdPitchQuadratic == "-0.08"
            && state.DvdPitchCubic == "0.04", "preset applies the three coefficients");
        Require(state.DvdPitchSummary.Contains("x²") && state.DvdPitchSummary.Contains("x³ μm")
            && state.DvdPitchSummary.Contains("a₀"), "generation displays the full dimensional polynomial and constrained constant");
        List<string?> changed = [];
        state.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        state.DvdPitchCubic = "0.06";
        Require(state.SelectedDvdPreset.IsCustom && changed.Contains(nameof(state.DvdPitchSummary)),
            "editing pitch detaches the preset and refreshes the polynomial");
        state.DvdActualPitchLinear = "0.3";
        Require(changed.Contains(nameof(state.DvdActualPitchSummary)) && state.DvdPitchLinear == "0.12",
            "actual preview shape is independent of generation shape");
        string actual = state.DvdActualPitchSummary;
        state.DvdTotalSectors = "1147552";
        Require(state.DvdActualPitchSummary != actual, "sector length recomputes the actual polynomial scale");
        state.DvdPitchLinear = "-3";
        Require(state.DvdPitchSummary.Contains("无效"), "nonpositive interior pitch is reported without a binding exception");

        string directory = Path.Combine(Path.GetTempPath(), $"disc-cubic-app-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "presets.json");
            DvdDiscPresetDefinition definition = new(preset.Id, preset.DisplayName, preset.Description,
                preset.TotalSectors, preset.InnerRadiusMm, preset.OuterRadiusMm,
                PitchLinear: preset.PitchLinear, PitchQuadratic: preset.PitchQuadratic, PitchCubic: preset.PitchCubic);
            DiscPresetJsonStore.Save(path, new(DiscPresetJsonStore.CurrentSchemaVersion, [], [definition]));
            Require(DiscPresetJsonStore.Load(path).DvdPresets.Single() == definition,
                "preset JSON round-trips all cubic coefficients");
            string legacy = File.ReadAllText(path);
            foreach (string property in new[] { "pitchLinear", "pitchQuadratic", "pitchCubic" })
                legacy = System.Text.RegularExpressions.Regex.Replace(legacy,
                    $",?\\s*\"{property}\"\\s*:\\s*[-0-9.]+", string.Empty);
            File.WriteAllText(path, legacy);
            DvdDiscPresetDefinition old = DiscPresetJsonStore.Load(path).DvdPresets.Single();
            Require(old.PitchLinear == 0 && old.PitchQuadratic == 0 && old.PitchCubic == 0,
                "legacy presets default to constant pitch");

            CalibrationParameters parameters = new(CalibrationDiscKind.Dvd, 24, 58, 2_295_104, 133.3,
                preset.PitchLinear, preset.PitchQuadratic, preset.PitchCubic);
            CalibrationTarget target = CalibrationTarget.Create(parameters);
            MethodInfo factory = typeof(CalibrationViewModel).GetMethod("DvdOptions", BindingFlags.NonPublic | BindingFlags.Static)!;
            DvdStreamingOptions options = (DvdStreamingOptions)factory.Invoke(null, [target])!;
            Require(options.PitchLinear == parameters.PitchLinear && options.PitchQuadratic == parameters.PitchQuadratic
                && options.PitchCubic == parameters.PitchCubic, "calibration output retains its captured pitch coefficients");
            state.DvdPitchLinear = "0";
            Require(options.PitchLinear == preset.PitchLinear, "subsequent state edits preserve captured calibration options");
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
        Console.WriteLine("app-cubic: passed (presets, UI invalidation, complete polynomial, calibration snapshots)");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"app-cubic: {message}");
    }
}
