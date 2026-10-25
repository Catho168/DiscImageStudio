using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Threading;
using DiscImageStudio.BluRay;
using DiscImageStudio.ViewModels;

internal static class BluRayLivePreviewTests
{
    internal static void Run()
    {
        // Wire only the preview and BD page to avoid creating or loading the user's preset
        // file. The window stays unloaded, so parameter edits never start rendering timers.
        ShellViewModel shell = (ShellViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ShellViewModel));
        typeof(ShellViewModel).GetField("_log", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(shell, new StringBuilder());
        DiscParametersState state = new();
        LivePreviewViewModel preview = new(Dispatcher.CurrentDispatcher, shell, state);
        SetShellProperty(shell, nameof(ShellViewModel.State), state);
        SetShellProperty(shell, nameof(ShellViewModel.LivePreview), preview);
        BluRayViewModel bluRay = new(shell);
        SetShellProperty(shell, nameof(ShellViewModel.BluRay), bluRay);
        typeof(LivePreviewViewModel).GetMethod("InitializeBluRay", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(preview, null);

        Require(preview.SelectedDiscTypeIndex == 0 && preview.IsCdSelected
            && !preview.IsDvdSelected && !preview.IsBluRaySelected,
            "the default remains CD and only one disc panel is selected");
        Require(preview.BluRayActualInnerRadius == "24" && preview.BluRayActualOuterRadius == "58"
            && preview.BluRayActualStartAngle == "0", "BD actual geometry starts from its selected preset");
        Require(ReferenceEquals(preview.BluRay, bluRay)
            && ReferenceEquals(preview.BluRay.BrowseImageCommand, bluRay.BrowseImageCommand),
            "preview and production share the BD source model and its browsing command");

        List<string?> changed = [];
        preview.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        preview.HasResultImage = true;
        preview.ResultPreviewPath = "previous-preview.png";
        preview.SelectedDiscTypeIndex = 2;
        Require(preview.IsBluRaySelected && !preview.IsCdSelected && !preview.IsDvdSelected
            && changed.Contains(nameof(preview.IsCdSelected)) && changed.Contains(nameof(preview.IsDvdSelected))
            && changed.Contains(nameof(preview.IsBluRaySelected)),
            "switching to BD updates all mutually exclusive panel bindings");
        Require(preview.ResultPreviewTitle == "实时预览 - BD"
            && preview.ResultPreviewPlaceholderText.Contains("BD 源图片", StringComparison.Ordinal)
            && !preview.HasResultImage && preview.ResultPreviewPath.Length == 0,
            "switching to BD clears the previous disc result and identifies the requested input");
        preview.IsReadbackPreview = true;
        Require(!preview.IsCalibrationPreview
            && preview.ResultPreviewPlaceholderText.Contains("BD ISO", StringComparison.Ordinal),
            "BD readback mode asks for a generated ISO rather than a source image");

        preview.BluRayActualInnerRadius = "25.1";
        preview.BluRayActualOuterRadius = "57.2";
        preview.BluRayActualStartAngle = "12";
        bluRay.InnerRadius = "25";
        bluRay.OuterRadius = "57";
        bluRay.SelectedPreset = null;
        Require(preview.BluRayActualInnerRadius == "25.1" && preview.BluRayActualStartAngle == "12",
            "clearing a preset does not replace manually measured values");
        bluRay.SelectedPreset = bluRay.Presets[0];
        Require(preview.BluRayActualInnerRadius == "24" && preview.BluRayActualOuterRadius == "58"
            && preview.BluRayActualStartAngle == "0" && bluRay.InnerRadius == "24" && bluRay.OuterRadius == "58",
            "selecting a BD preset synchronizes generation and actual geometry");
        preview.BluRayActualInnerRadius = "24.2";
        preview.BluRayActualOuterRadius = "57.8";
        preview.BluRayActualStartAngle = "7.5";
        bluRay.InvertMapping = true;
        preview.BluRayPreviewSize = "512";
        bluRay.InnerRadius = "24.1";
        Require(preview.BluRayActualInnerRadius == "24.2" && preview.BluRayActualOuterRadius == "57.8"
            && preview.BluRayActualStartAngle == "7.5",
            "manual actual geometry remains editable after synchronization and survives unrelated edits");

        string directory = Path.Combine(Path.GetTempPath(), $"disc-bd-live-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "source.png");
            string iso = Path.Combine(directory, "generated.iso");
            using (FileStream stream = File.Create(iso))
                stream.SetLength(1024L * BluRayDiscParameters.BytesPerSector);
            bluRay.ImagePath = " " + source + " ";
            preview.BluRayIsoPath = " " + iso + " ";
            Require(preview.BluRay.ImagePath == bluRay.ImagePath && bluRay.TotalSectors == "1024",
                "source edits are shared and importing a complete ISO adopts its sector capacity");
            Require(preview.BluRayActualInnerRadius == "24.2" && preview.BluRayActualStartAngle == "7.5",
                "importing ISO capacity preserves the measured geometry");
            Require(GetInputPath(preview) == iso, "BD readback uses the ISO path with surrounding whitespace removed");
            preview.IsCalibrationPreview = true;
            Require(GetInputPath(preview) == source, "BD projection uses the source image independently of the ISO path");

            string partial = Path.Combine(directory, "partial.iso");
            using (FileStream stream = File.Create(partial))
                stream.SetLength(33L * BluRayDiscParameters.BytesPerSector);
            preview.BluRayIsoPath = partial;
            Require(bluRay.TotalSectors == "1024", "a partial cluster cannot replace the current sector capacity");
            string unaligned = Path.Combine(directory, "unaligned.iso");
            using (FileStream stream = File.Create(unaligned))
                stream.SetLength(64L * BluRayDiscParameters.BytesPerSector + 1);
            preview.BluRayIsoPath = unaligned;
            Require(bluRay.TotalSectors == "1024", "a partial sector cannot replace the current sector capacity");
            preview.BluRayIsoPath = Path.Combine(directory, "missing.iso");
            Require(bluRay.TotalSectors == "1024", "editing an incomplete or missing ISO path preserves valid geometry");

            state.CdImagePath = Path.Combine(directory, "cd.png");
            state.DvdImagePath = Path.Combine(directory, "dvd.png");
            preview.IsDvdSelected = true;
            Require(preview.SelectedDiscTypeIndex == 1 && GetInputPath(preview) == state.DvdImagePath,
                "the existing DVD selector still selects DVD after leaving BD");
            preview.IsDvdSelected = false;
            Require(preview.IsCdSelected && GetInputPath(preview) == state.CdImagePath,
                "the existing CD selector still selects CD without using the BD source");

            preview.IsReadbackPreview = true;
            bluRay.OpenLivePreviewCommand.Execute(null);
            Require(shell.CurrentPageIndex == ShellViewModel.PreviewTabIndex && preview.IsBluRaySelected
                && preview.IsCalibrationPreview && GetInputPath(preview) == source,
                "the production-page command opens BD source projection regardless of the previous preview mode");

            int browseNotifications = 0;
            int openNotifications = 0;
            preview.BrowseBluRayIsoCommand.CanExecuteChanged += (_, _) => browseNotifications++;
            bluRay.OpenLivePreviewCommand.CanExecuteChanged += (_, _) => openNotifications++;
            SetShellBusy(shell, true);
            Require(!preview.BrowseBluRayIsoCommand.CanExecute(null)
                && !bluRay.OpenLivePreviewCommand.CanExecute(null) && !preview.BluRay.BrowseImageCommand.CanExecute(null),
                "busy jobs disable BD input browsing and the production-page preview action");
            SetShellBusy(shell, false);
            Require(preview.BrowseBluRayIsoCommand.CanExecute(null) && bluRay.OpenLivePreviewCommand.CanExecute(null)
                && preview.BluRay.BrowseImageCommand.CanExecute(null)
                && browseNotifications == 2 && openNotifications == 2,
                "busy transitions notify both preview actions and restore availability");

            // Readback only exposes actual geometry. Incomplete edits left in the hidden
            // source-projection controls must not prevent a valid ISO from rendering.
            preview.BluRayIsoPath = iso;
            preview.IsReadbackPreview = true;
            preview.BluRayPreviewSize = "256";
            bluRay.InnerRadius = string.Empty;
            bluRay.OuterRadius = "invalid";
            RefreshBluRay(preview);
            Require(preview.HasResultImage && preview.ResultImage is not null && IsPreviewReady(preview),
                "ISO readback ignores invalid hidden generation radii: " + preview.StatusText);
            Require(preview.DownloadPreviewCommand.CanExecute(null), "the first valid BD readback enables export");

            int downloadNotifications = 0;
            preview.DownloadPreviewCommand.CanExecuteChanged += (_, _) => downloadNotifications++;
            preview.BluRayActualStartAngle = "9.5";
            Require(preview.HasResultImage && !preview.DownloadPreviewCommand.CanExecute(null)
                && downloadNotifications > 0,
                "editing actual geometry keeps the current canvas but disables its stale export");
            int notificationsBeforeRefresh = downloadNotifications;
            RefreshBluRay(preview);
            Require(preview.HasResultImage && IsPreviewReady(preview)
                && preview.DownloadPreviewCommand.CanExecute(null)
                && downloadNotifications > notificationsBeforeRefresh,
                "replacing an already visible BD preview re-enables export and notifies its command binding");

            preview.BluRayPreviewSize = "1024";
            RefreshBluRay(preview);
            Require(preview.ResultImage is { PixelWidth: 1024, PixelHeight: 1024 }
                && IsPreviewReady(preview),
                "BD readback renders the configured resolution above the former 900-pixel cap: " + preview.StatusText);
            foreach (string size in new[] { "0", "63", "8193" })
            {
                preview.BluRayPreviewSize = size;
                RefreshBluRay(preview);
                Require(!preview.HasResultImage && !preview.DownloadPreviewCommand.CanExecute(null)
                    && !string.IsNullOrWhiteSpace(preview.ResultPreviewPlaceholderText),
                    $"BD readback rejects invalid preview size {size} instead of silently clamping it");
            }
        }
        finally
        {
            typeof(LivePreviewViewModel).GetMethod("OnWindowClosed", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(preview, null);
            foreach (string path in Directory.EnumerateFiles(directory)) File.Delete(path);
            Directory.Delete(directory);
        }
        Console.WriteLine("blu-ray-live-preview: passed (disc selection, shared inputs, presets, ISO capacity, navigation, busy gating, readback, export refresh)");
    }

    private static string GetInputPath(LivePreviewViewModel preview)
        => (string)typeof(LivePreviewViewModel).GetMethod("PreviewInputPath", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(preview, null)!;

    private static bool IsPreviewReady(LivePreviewViewModel preview)
        => (bool)typeof(LivePreviewViewModel).GetProperty("IsLivePreviewReady", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(preview)!;

    private static void RefreshBluRay(LivePreviewViewModel preview)
    {
        int revision = (int)typeof(LivePreviewViewModel).GetField("_livePreviewRevision", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(preview)!;
        Task refresh = (Task)typeof(LivePreviewViewModel).GetMethod("RefreshBluRayAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(preview, [revision])!;
        refresh.GetAwaiter().GetResult();
    }

    private static void SetShellProperty(ShellViewModel shell, string name, object value)
        => typeof(ShellViewModel).GetField($"<{name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(shell, value);

    private static void SetShellBusy(ShellViewModel shell, bool busy)
    {
        typeof(ShellViewModel).GetField("_isBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, busy);
        ((Action?)typeof(ShellViewModel).GetField("BusyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(shell))?.Invoke();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("BD live preview: " + message);
    }
}
