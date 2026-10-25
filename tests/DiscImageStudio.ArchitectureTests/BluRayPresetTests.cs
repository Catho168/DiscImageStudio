using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;
using DiscImageStudio.BluRay;
using DiscImageStudio.Core;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

internal static class BluRayPresetTests
{
    private const string RitekPresetId = "bd-r-ritek-10x";
    private const double RitekInnerRadiusMm = 23.98822105616562;
    private const double RitekOuterRadiusMm = 58.023234731100764;

    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "disc-bd-presets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestPersistence(directory);
            TestSelectionAndCli(directory);
            TestPreviewSynchronization(directory);
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("blu-ray-presets: passed (independent persistence, legacy export import, manual selection, CLI base, preview sync, invalid import preservation)");
    }

    private static void TestPersistence(string directory)
    {
        string catalogPath = Path.Combine(directory, "catalog.json");
        string otherPath = Path.Combine(directory, "disc-presets.json");
        File.WriteAllText(otherPath, "CD/DVD sentinel");
        BluRayPresetStore store = new(catalogPath);
        Require(store.Load().Count == 0 && !File.Exists(catalogPath), "reading a missing BD catalog does not create files");
        BluRayDiscParameters parameters = new(6_119_680, 1, 24.123456789, 57.876543219);
        BluRayDiscPreset saved = store.Save("标定蓝光", parameters, true);
        BluRayDiscPreset loaded = new BluRayPresetStore(catalogPath).Load().Single();
        Require(loaded == saved && loaded.CreateParameters() == parameters && loaded.InvertMapping
            && loaded.IsUserPreset && !loaded.IsCustom, "names, exact geometry and polarity survive a new store instance");
        Require(File.ReadAllText(otherPath) == "CD/DVD sentinel", "BD persistence never changes the CD/DVD catalog");
        BluRayPresetStore importedStore = new(Path.Combine(directory, "copied-catalog.json"));
        BluRayDiscPreset copied = importedStore.Import(catalogPath).Single();
        Require(copied.Id != saved.Id && copied.CreateParameters() == parameters && copied.InvertMapping,
            "catalog imports allocate independent identifiers while retaining values");

        string legacy = Path.Combine(directory, "old-bd-info.json");
        BluRayDiscModule module = new();
        module.ExecuteAsync(new DiscJobRequest("bd-info",
            ["--preset", "bd-r-sl-25gb", "--total-sectors", "6119680", "--r0", "24.123456789",
                "--r1", "57.876543219", "--invert", "true", "--output", legacy])).GetAwaiter().GetResult();
        BluRayDiscPreset fromLegacy = store.Import(legacy).Single();
        Require(fromLegacy.DisplayName == "old-bd-info" && fromLegacy.CreateParameters() == parameters
            && fromLegacy.InvertMapping, "the existing bd-info JSON export imports its PascalCase geometry and inversion exactly");

        byte[] goodCatalog = File.ReadAllBytes(catalogPath);
        string rejected = Path.Combine(directory, "unsupported-angle.json");
        File.WriteAllText(rejected, JsonSerializer.Serialize(new
        {
            parameters = parameters with { StartAngleDegrees = 12.5 }, luminanceMapping = new { invert = true },
        }));
        Exception angle = Reject(() => store.Import(rejected));
        Require(angle.Message.Contains("0°", StringComparison.Ordinal), "nonzero exported angles have an explicit unsupported-angle error");
        Require(File.ReadAllBytes(catalogPath).SequenceEqual(goodCatalog), "rejected angle import leaves the catalog intact");
        Reject(() => store.Save("bad", parameters with { TotalSectors = 33 }, false));
        Reject(() => store.Save(" ", parameters, false));
        Require(File.ReadAllBytes(catalogPath).SequenceEqual(goodCatalog), "invalid named saves leave the catalog intact");
        File.WriteAllText(rejected, "{\"parameters\":null}");
        Reject(() => store.Import(rejected));
        File.WriteAllText(rejected, "{\"schemaVersion\":1,\"presets\":[null]}");
        Reject(() => store.Import(rejected));
        Require(File.ReadAllBytes(catalogPath).SequenceEqual(goodCatalog), "null imports do not partially write the catalog");

        string damagedPath = Path.Combine(directory, "damaged.json");
        File.WriteAllText(damagedPath, "{ invalid");
        BluRayPresetStore damaged = new(damagedPath);
        Reject(() => damaged.Save("valid", parameters, false));
        Require(File.ReadAllText(damagedPath) == "{ invalid", "saving never overwrites a damaged original catalog");
        BluRayViewModel brokenModel = Model(damagedPath);
        Require(Invoke<string?>(brokenModel, "TryLoadPresets") is not null && brokenModel.Presets.Count == BluRayDiscPreset.BuiltIn.Count + 1,
            "startup reports a damaged BD catalog while retaining builtin and manual options");
        File.WriteAllText(damagedPath, "{\"schemaVersion\":2,\"presets\":[]}");
        Require(Invoke<string?>(brokenModel, "TryLoadPresets") is not null && brokenModel.Presets.Count == BluRayDiscPreset.BuiltIn.Count + 1
            && File.ReadAllText(damagedPath) == "{\"schemaVersion\":2,\"presets\":[]}",
            "a syntactically valid unsupported schema also keeps startup usable and preserves its file");
        Require(BluRayPresetStore.DefaultPath == Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscImageStudio", BluRayPresetStore.FileName),
            "the default store is stable across build and release directories");
    }

    private static void TestSelectionAndCli(string directory)
    {
        string catalogPath = Path.Combine(directory, "model-presets.json");
        BluRayViewModel model = Model(catalogPath);
        BluRayDiscPreset builtin = model.Presets[0];
        model.InnerRadius = "24.123456789";
        model.OuterRadius = "57.876543219";
        model.TotalSectors = "6119680";
        model.InvertMapping = true;
        Require(model.SelectedPreset == BluRayDiscPreset.Manual, "manual geometry and polarity detach from the builtin name");
        BluRayDiscParameters geometry = model.ReadParameters();
        BluRayDiscPreset saved = Invoke<BluRayDiscPreset>(model, "SaveCurrentPreset", "我的 6 GB 测试");
        Require(model.SelectedPreset == saved && model.Presets.Contains(saved) && model.InvertMapping,
            "saving adds and selects an independently named geometry snapshot");
        model.OuterRadius = "57.75";
        Require(model.SelectedPreset == BluRayDiscPreset.Manual && saved.CreateParameters() == geometry,
            "editing a saved preset detaches without changing its immutable snapshot");
        Invoke<int>(model, "LoadPresets");
        Require(model.SelectedPreset == BluRayDiscPreset.Manual && model.OuterRadius == "57.75",
            "reload preserves unsaved geometry and the manual selection");
        model.SelectedPreset = model.Presets.Single(preset => preset.Id == saved.Id);
        Require(model.ReadParameters() == geometry && model.InvertMapping, "reselecting the saved preset restores its complete snapshot");
        model.InvertMapping = false;
        Require(model.SelectedPreset == BluRayDiscPreset.Manual, "polarity alone marks the values as unsaved");
        model.SelectedPreset = builtin;
        Require(model.ReadParameters() == new BluRayDiscParameters() && !model.InvertMapping,
            "reselecting the builtin restores default geometry and polarity");
        model.VolumeLabel = "SOURCE_CHANGE";
        Require(model.SelectedPreset == builtin, "a job source option is not a geometry preset edit");

        BluRayViewModel restarted = Model(catalogPath);
        Require(restarted.Presets.Count == BluRayDiscPreset.BuiltIn.Count + 1, "construction does not read an existing catalog implicitly");
        Require(Invoke<int>(restarted, "LoadPresets") == 1, "explicit startup load finds the saved preset after restart");
        restarted.SelectedPreset = restarted.Presets.Single(preset => preset.Id == saved.Id);
        List<string> arguments = Invoke<List<string>>(restarted, "CreateArguments", "bd-info", restarted.ReadParameters());
        Require(arguments[2] == "bd-r-sl-25gb" && !arguments.Contains(saved.Id), "custom IDs never reach the builtin-only CLI selector");
        string export = Path.Combine(directory, "custom-cli-export.json");
        arguments.AddRange(["--output", export, "--invert", restarted.InvertMapping.ToString()]);
        new BluRayDiscModule().ExecuteAsync(new DiscJobRequest(arguments[0], arguments.Skip(1))).GetAwaiter().GetResult();
        BluRayDiscPreset reimported = new BluRayPresetStore(Path.Combine(directory, "cli-roundtrip.json")).Import(export).Single();
        Require(reimported.CreateParameters() == geometry && reimported.InvertMapping,
            "the real CLI accepts saved custom geometry and exports values that can be reimported exactly");
    }

    private static void TestPreviewSynchronization(string directory)
    {
        ShellViewModel shell = (ShellViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ShellViewModel));
        DiscParametersState state = new();
        LivePreviewViewModel preview = new(Dispatcher.CurrentDispatcher, shell, state);
        BluRayViewModel model = new(shell, presetJsonPath: Path.Combine(directory, "preview-presets.json"));
        SetShellProperty(shell, nameof(ShellViewModel.State), state);
        SetShellProperty(shell, nameof(ShellViewModel.LivePreview), preview);
        SetShellProperty(shell, nameof(ShellViewModel.BluRay), model);
        Invoke<object?>(preview, "InitializeBluRay");
        preview.BluRayActualInnerRadius = "24.111";
        preview.BluRayActualOuterRadius = "57.999";
        model.InnerRadius = "25.123456789";
        Require(model.SelectedPreset?.IsCustom == true && preview.BluRayActualInnerRadius == "24.111"
            && preview.BluRayActualOuterRadius == "57.999", "switching to Manual preserves measured preview geometry");
        BluRayDiscPreset saved = Invoke<BluRayDiscPreset>(model, "SaveCurrentPreset", "测量预设");
        Require(preview.BluRayActualInnerRadius == "25.123456789", "selecting a named snapshot synchronizes actual preview geometry without rounding");
        preview.BluRayActualInnerRadius = "25.2";
        model.TotalSectors = "1024";
        Require(preview.BluRayActualInnerRadius == "25.2", "later manual capacity edits do not reset measured geometry");
        model.SelectedPreset = saved;
        Require(preview.BluRayActualInnerRadius == "25.123456789" && preview.BluRayActualStartAngle == "0",
            "reselecting the saved geometry restores preview values and the supported zero angle");

        BluRayDiscPreset ritek = model.Presets.Single(preset => preset.Id == RitekPresetId);
        Require(ritek.DisplayName == "铼德 BD-R 10x" && !ritek.IsCustom && !ritek.IsUserPreset,
            "the calibrated Ritek preset is a named builtin rather than a user entry");
        model.SelectedPreset = ritek;
        BluRayDiscParameters expected = new(12_219_392, 1, RitekInnerRadiusMm, RitekOuterRadiusMm, 0);
        Require(model.ReadParameters() == expected && !model.InvertMapping
            && preview.BluRayActualInnerRadius == "23.98822105616562"
            && preview.BluRayActualOuterRadius == "58.023234731100764"
            && preview.BluRayActualStartAngle == "0",
            "selecting Ritek loads the exported geometry at full precision in production and actual preview");
        Require(ReferenceEquals(preview.BluRay.Presets, model.Presets)
            && preview.BluRay.SelectedPreset == ritek,
            "the BD page and live preview use one preset list and selection");
        List<string> ritekArguments = Invoke<List<string>>(model, "CreateArguments", "bd-info", model.ReadParameters());
        Require(ritekArguments[2] == RitekPresetId
            && ritekArguments[ritekArguments.IndexOf("--r0") + 1] == "23.98822105616562"
            && ritekArguments[ritekArguments.IndexOf("--r1") + 1] == "58.023234731100764",
            "exporting a known calibrated builtin preserves its ID and explicit full-precision geometry");
        model.InnerRadius = "24.25";
        model.InvertMapping = true;
        Require(model.SelectedPreset == BluRayDiscPreset.Manual
            && preview.BluRayActualInnerRadius == "23.98822105616562"
            && ritek.CreateParameters() == expected && !ritek.InvertMapping,
            "editing the calibrated builtin preserves its snapshot and the measured preview coordinates");
        model.SelectedPreset = ritek;
        Require(model.ReadParameters() == expected && !model.InvertMapping,
            "reselecting the calibrated builtin restores exact geometry and normal mapping");
        Invoke<int>(model, "LoadPresets");
        Require(model.SelectedPreset?.Id == RitekPresetId && model.ReadParameters() == expected
            && model.Presets.Any(preset => preset.Id == saved.Id),
            "reloading keeps both the calibrated builtin selection and previously saved user entries");
        BluRayDiscPreset copiedRitek = Invoke<BluRayDiscPreset>(model, "SaveCurrentPreset", "铼德参数副本");
        Require(copiedRitek.IsUserPreset && copiedRitek.Id != RitekPresetId && copiedRitek.CreateParameters() == expected,
            "saving the calibrated builtin as a new named entry produces an independent user snapshot");
        model.SelectedPreset = model.Presets[0];
        Require(model.ReadParameters() == new BluRayDiscParameters() && !model.InvertMapping,
            "selecting the generic preset after Ritek restores the original 24–58 mm defaults");
    }

    private static BluRayViewModel Model(string path)
        => new((ShellViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ShellViewModel)), presetJsonPath: path);
    private static T Invoke<T>(object target, string name, params object?[] arguments)
    {
        try { return (T)target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments)!; }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
    }
    private static void SetShellProperty(ShellViewModel shell, string property, object value)
        => typeof(ShellViewModel).GetField($"<{property}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, value);
    private static Exception Reject(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or JsonException) { return exception; }
        throw new InvalidOperationException("Blu-ray presets: expected invalid input to be rejected.");
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Blu-ray presets: " + message); }
}
