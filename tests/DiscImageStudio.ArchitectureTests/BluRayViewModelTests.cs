using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.ViewModels;
using DiscImageStudio.BluRay;

internal static class BluRayViewModelTests
{
    internal static void Run()
    {
        var shell = (ShellViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ShellViewModel));
        BluRayViewModel model = new(shell);
        Require(model.Presets.Count == BluRayDiscPreset.BuiltIn.Count + 1 && model.Presets.Count(preset => preset.IsCustom) == 1
            && model.SelectedPreset?.LayerCount == 1,
            "the UI starts with the supported single-layer preset and an unsaved manual entry without loading user files");
        Require(model.ReadParameters().TotalSectors == BluRayDiscParameters.SectorsPerLayer,
            "the preset supplies the initial capacity");
        double originalPitch = new BluRayTrackGeometry(model.ReadParameters()).EffectiveTrackPitchMicrometers;
        model.TotalSectors = "6119680";
        BluRayDiscParameters edited = model.ReadParameters();
        Require(edited.TotalSectors == 6_119_680 && edited.ContentLength == 6_119_680L * 2048
            && new BluRayTrackGeometry(edited).EffectiveTrackPitchMicrometers > originalPitch,
            "manual capacity controls output size and track geometry together");
        Require(new BluRayTrackGeometry(edited).AtSector(edited.TotalSectors).RadiusMm == edited.OuterRadiusMm,
            "edited capacity reaches the configured outer radius at its last sector");
        model.TotalSectors = "33";
        Require(model.PreviewClip.IsEmpty(), "unaligned capacity invalidates the geometry preview");
        model.TotalSectors = "999999999999999999";
        Require(model.PreviewClip.IsEmpty(), "overflowing edits report invalid geometry without breaking bindings");
        model.TotalSectors = BluRayDiscParameters.SectorsPerLayer.ToString();
        Require(!model.PreviewClip.IsEmpty(), "valid capacity restores geometry after partial edits");
        Require(model.GenerateImageCommand.CanExecute(null) && model.CanChangeMapping && !model.InvertMapping,
            "the built-in 3T/6T mapping enables image generation by default");
        Require(!model.CancelImageCommand.CanExecute(null), "cancellation is unavailable before generation starts");
        Require(model.MappingHeading.Contains("3T / 6T", StringComparison.Ordinal)
            && model.MappingStatus.Contains("暗部（0–127）偏 3T", StringComparison.Ordinal)
            && model.MappingStatus.Contains("亮部（128–255）偏 6T", StringComparison.Ordinal),
            "the UI describes the default threshold and polarity");

        IBluRayLuminanceMapping originalMapping = GetMappingSnapshot(model);
        Require(originalMapping.Map(127, default) == BluRayRunLengthMapping.ThreeTByte
            && originalMapping.Map(128, default) == BluRayRunLengthMapping.SixTByte,
            "the default UI selection supplies the configured 3T/6T threshold mapping");
        List<string?> changedProperties = [];
        model.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);
        model.InvertMapping = true;
        IBluRayLuminanceMapping invertedMapping = GetMappingSnapshot(model);
        Require(changedProperties.Contains(nameof(model.MappingStatus))
            && model.MappingStatus.Contains("暗部（0–127）偏 6T", StringComparison.Ordinal)
            && model.MappingStatus.Contains("亮部（128–255）偏 3T", StringComparison.Ordinal),
            "reversal updates the visible polarity description");
        Require(invertedMapping.Map(127, default) == BluRayRunLengthMapping.SixTByte
            && invertedMapping.Map(128, default) == BluRayRunLengthMapping.ThreeTByte
            && originalMapping.Map(127, default) == BluRayRunLengthMapping.ThreeTByte,
            "reversal changes the next job without mutating an already captured mapping");
        model.InvertMapping = false;

        TestMapping customMapping = new();
        BluRayViewModel injected = new(shell, customMapping);
        injected.InvertMapping = true;
        Require(injected.GenerateImageCommand.CanExecute(null) && !injected.CanChangeMapping
            && !injected.UsesRunLengthMapping && ReferenceEquals(GetMappingSnapshot(injected), customMapping),
            "an injected custom mapping remains enabled and is not changed by the built-in polarity control");
        BluRayViewModel invertedPreset = new(shell, new BluRayRunLengthMapping(invert: true));
        Require(invertedPreset.InvertMapping && invertedPreset.CanChangeMapping,
            "an injected built-in mapping retains its initial polarity");

        int generationCommandChanges = 0;
        model.GenerateImageCommand.CanExecuteChanged += (_, _) => generationCommandChanges++;
        SetShellBusy(shell, true);
        Require(!model.GenerateImageCommand.CanExecute(null) && !injected.GenerateImageCommand.CanExecute(null)
            && !model.GenerateProbeCommand.CanExecute(null) && !model.ExportParametersCommand.CanExecute(null)
            && !model.BrowseImageCommand.CanExecute(null) && !model.BrowseImageOutputCommand.CanExecute(null)
            && !model.BrowseProbeOutputCommand.CanExecute(null) && !model.CanChangeMapping
            && !model.BrowseDataDirectoryCommand.CanExecute(null) && !model.CheckLayoutCommand.CanExecute(null)
            && !model.OpenBurnCommand.CanExecute(null) && !model.OpenCalibrationCommand.CanExecute(null)
            && !model.SavePresetCommand.CanExecute(null) && !model.ImportPresetCommand.CanExecute(null)
            && !model.ReloadPresetsCommand.CanExecute(null) && !model.CanEditPresets,
            "a busy shell blocks generation, probe, browse and polarity changes");
        SetShellBusy(shell, false);
        Require(model.GenerateImageCommand.CanExecute(null) && model.CanChangeMapping
            && generationCommandChanges == 2 && changedProperties.Contains(nameof(model.CanChangeMapping)),
            "busy transitions notify bindings and restore default generation");
        Require(model.PreviewClip.FillContains(new Point(160, 90))
            && !model.PreviewClip.FillContains(new Point(90, 90))
            && !model.PreviewClip.FillContains(new Point(179, 179)),
            "source preview is restricted to the annular drawing area");

        model.InnerRadius = "59";
        Require(model.PreviewClip.IsEmpty(), "invalid radii clear the preview mask without throwing a binding error");
        model.InnerRadius = "24";
        model.OuterRadius = "NaN";
        Require(model.PreviewClip.IsEmpty(), "nonfinite radii cannot produce a usable preview");
        model.OuterRadius = "58";
        Require(!model.PreviewClip.IsEmpty(), "valid geometry recovers after a partial edit");

        string directory = Path.Combine(Path.GetTempPath(), $"disc-bd-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "source.png");
            BitmapSource bitmap = BitmapSource.Create(4, 2, 96, 96, PixelFormats.Gray8, null,
                new byte[] { 0, 85, 170, 255, 255, 170, 85, 0 }, 4);
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(source)) encoder.Save(stream);
            model.ImagePath = source;
            Require(model.SourcePreview is { IsFrozen: true }, "valid source pictures can be previewed independently of a mapping");
            // Loading must release the file handle so the source may be edited externally.
            using (FileStream unlocked = File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Require(unlocked.Length > 0, "preview loading releases the source file");
            model.ImageOutputPath = Path.Combine(directory, "future.iso");
            model.TargetByte = "255";
            Require(model.GenerateImageCommand.CanExecute(null)
                && GetMappingSnapshot(model).Map(0, default) == BluRayRunLengthMapping.ThreeTByte,
                "editing a byte probe does not change the image-generation mapping");

            string files = Path.Combine(directory, "files");
            Directory.CreateDirectory(files);
            File.WriteAllText(Path.Combine(files, "inside.txt"), "Blu-ray inner files");
            model.TotalSectors = "1024";
            model.DataDirectory = files;
            model.VolumeLabel = "BD_TEST";
            model.CheckLayoutCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Require(model.LayoutStatus.Contains("内圈 1 个文件", StringComparison.Ordinal)
                && model.LayoutStatus.Contains("1,024", StringComparison.Ordinal),
                "layout planning combines manual capacity and the selected inner folder");
            model.ClearDataDirectoryCommand.Execute(null);
            Require(model.DataDirectory == string.Empty && model.LayoutStatus.StartsWith("未包含文件夹", StringComparison.Ordinal),
                "clearing inner files invalidates the previous plan summary");
            model.TotalSectors = BluRayDiscParameters.SectorsPerLayer.ToString();

            string corrupt = Path.Combine(directory, "corrupt.png");
            File.WriteAllText(corrupt, "not an image");
            model.ImagePath = corrupt;
            Require(model.SourcePreview is null && model.PreviewStatus.StartsWith("无法预览", StringComparison.Ordinal),
                "invalid images clear stale previews and show a useful status");
            model.ImagePath = string.Empty;
            Require(model.SourcePreview is null, "clearing the path clears the source preview");
            Require(!File.Exists(model.ImageOutputPath), "editing inputs does not automatically generate output");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Console.WriteLine("blu-ray-ui: passed (default 3T/6T mapping, polarity snapshots, busy gating, annulus validation, source previews)");
    }

    private static IBluRayLuminanceMapping GetMappingSnapshot(BluRayViewModel model)
        => model.CreateLuminanceMapping();

    private static void SetShellBusy(ShellViewModel shell, bool busy)
    {
        // The shell is deliberately uninitialized to avoid application startup and preset I/O.
        typeof(ShellViewModel).GetField("_isBusy", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(shell, busy);
        ((Action?)typeof(ShellViewModel).GetField("BusyChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(shell))?.Invoke();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Blu-ray UI: " + message);
    }

    private sealed class TestMapping : IBluRayLuminanceMapping
    {
        public byte Map(byte luminance, BluRayPayloadBytePosition position) => (byte)(luminance ^ 0x5a);
    }
}
