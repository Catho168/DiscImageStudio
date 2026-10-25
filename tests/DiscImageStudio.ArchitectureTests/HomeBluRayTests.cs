using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Dvd;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

internal static class HomeBluRayTests
{
    internal static void Run()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try { TestHome(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Console.WriteLine("home-blu-ray: passed (startup order, three families, new/custom/cards, ISO history, reopening, busy gating)");
    }

    private static void TestHome()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-home-bd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        LivePreviewViewModel? preview = null;
        bool previewInitialized = false;
        try
        {
            // Avoid loading user presets, user history, or any burn/device service. The
            // only generated artifact in this test is a 2 MiB ISO inside this temp folder.
            ShellViewModel shell = (ShellViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ShellViewModel));
            SetField(shell, "_dispatcher", Dispatcher.CurrentDispatcher);
            SetField(shell, "_log", new StringBuilder());
            SetShellProperty(shell, nameof(ShellViewModel.Toasts), new ObservableCollection<ToastItem>());
            foreach (string command in new[]
            {
                nameof(ShellViewModel.OpenOutputCommand), nameof(ShellViewModel.BrowseCdImageCommand),
                nameof(ShellViewModel.BrowseDvdImageCommand), nameof(ShellViewModel.BrowseCdTrackCommand),
                nameof(ShellViewModel.BrowseDvdIsoCommand),
            }) SetShellProperty(shell, command, new RelayCommand(() => { }));

            DiscParametersState state = new();
            CdDiscPreset cdPreset = new("home-cd", "测试 CD", "测试", 1000, 24.5, 56.8);
            DvdDiscPreset dvdPreset = new("home-dvd", "测试 DVD", "测试", 1024, 24, 58);
            SetField(state, "_cdPresets", new[] { cdPreset, CdDiscPreset.Manual });
            SetField(state, "_dvdPresets", new[] { dvdPreset, DvdDiscPreset.Manual });
            SetShellProperty(shell, nameof(ShellViewModel.State), state);
            preview = new LivePreviewViewModel(Dispatcher.CurrentDispatcher, shell, state);
            SetShellProperty(shell, nameof(ShellViewModel.LivePreview), preview);
            RecentJobStore store = new(Path.Combine(directory, "recent-jobs.json"));
            HomeViewModel home = new(shell, state, store);
            SetShellProperty(shell, nameof(ShellViewModel.Home), home);
            QuickCreateTile bdTile = home.Tiles.Single(tile => tile.IsBluRay && tile.PresetId == BluRayDiscPreset.BuiltIn[0].Id);
            Require(bdTile.Caption == "BD" && bdTile.PresetId == BluRayDiscPreset.BuiltIn[0].Id,
                "BD cards exist while the BD view model has not yet been constructed");
            BluRayViewModel bluRay = new(shell);
            SetShellProperty(shell, nameof(ShellViewModel.BluRay), bluRay);
            typeof(LivePreviewViewModel).GetMethod("InitializeBluRay", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(preview, null);
            previewInitialized = true;

            List<string?> changed = [];
            home.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
            home.SelectedDiscTypeIndex = 2;
            Require(preview.IsBluRaySelected && home.IsBluRaySelected && !home.IsDvdSelected,
                "the home selector and preview share all three disc families");
            preview.SelectedDiscTypeIndex = 1;
            Require(home.SelectedDiscTypeIndex == 1 && home.IsDvdSelected
                && changed.Contains(nameof(home.SelectedDiscTypeIndex)) && changed.Contains(nameof(home.IsBluRaySelected)),
                "external disc selection updates the home selector bindings");
            home.SelectedDiscTypeIndex = 2;
            bluRay.TotalSectors = "1024";
            bluRay.InnerRadius = "25";
            preview.BluRayActualInnerRadius = "25.2";
            home.NewCommand.Execute(null);
            Require(shell.CurrentPageIndex == ShellViewModel.BluRayTabIndex
                && bluRay.ReadParameters() == BluRayDiscPreset.BuiltIn[0].CreateParameters()
                && preview.BluRayActualInnerRadius == "24",
                "BD New reapplies the selected preset even after manual edits to the same preset");

            bluRay.TotalSectors = "2048";
            bluRay.InnerRadius = "24.7";
            home.CreateTileCommand.Execute(home.Tiles.Single(tile => tile.PresetId is null));
            Require(shell.CurrentPageIndex == ShellViewModel.BluRayTabIndex && bluRay.TotalSectors == "2048"
                && bluRay.InnerRadius == "24.7", "custom BD opens its parameter page without replacing the current geometry");
            home.SelectedDiscTypeIndex = 0;
            home.CreateTileCommand.Execute(bdTile);
            Require(home.IsBluRaySelected && shell.CurrentPageIndex == ShellViewModel.BluRayTabIndex
                && bluRay.ReadParameters() == BluRayDiscPreset.BuiltIn[0].CreateParameters(),
                "the BD preset card selects BD and applies its geometry from any active family");
            QuickCreateTile ritekTile = home.Tiles.Single(tile => tile.IsBluRay && tile.PresetId == "bd-r-ritek-10x");
            home.CreateTileCommand.Execute(ritekTile);
            Require(bluRay.SelectedPreset?.Id == "bd-r-ritek-10x"
                && bluRay.ReadParameters() == new BluRayDiscParameters(12_219_392, 1, 23.98822105616562, 58.023234731100764)
                && preview.BluRayActualInnerRadius == "23.98822105616562"
                && preview.BluRayActualOuterRadius == "58.023234731100764",
                "the calibrated Ritek home card applies its full-precision geometry to production and preview");
            home.CreateTileCommand.Execute(bdTile);
            shell.SelectPage(ShellViewModel.HomeTabIndex);
            home.OpenCommand.Execute(null); // No dialog service means a cancelled picker.
            Require(shell.CurrentPageIndex == ShellViewModel.HomeTabIndex && bluRay.ImagePath.Length == 0,
                "cancelling BD Open keeps the home page and the existing source unchanged");

            home.SelectedDiscTypeIndex = 0;
            home.NewCommand.Execute(null);
            Require(shell.CurrentPageIndex == ShellViewModel.CdTabIndex && state.SelectedCdPreset == cdPreset,
                "CD New retains its recommended-preset behavior");
            home.CreateTileCommand.Execute(home.Tiles.Single(tile => tile.IsDvd));
            Require(shell.CurrentPageIndex == ShellViewModel.DvdTabIndex && state.SelectedDvdPreset == dvdPreset,
                "DVD preset cards still select DVD and apply their own presets");

            string source = Path.Combine(directory, "source.png");
            WriteSource(source);
            string iso = Path.Combine(directory, "bd-result.iso");
            bluRay.ImagePath = source;
            bluRay.ImageOutputPath = iso;
            bluRay.TotalSectors = "1024";
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new InlineTestContext());
                bluRay.GenerateImageCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
            Require(File.Exists(iso) && new FileInfo(iso).Length == 1024L * BluRayDiscParameters.BytesPerSector,
                "a small BD ISO can be generated without a burner or optical drive");
            Require(store.Load().Count == 1 && store.Load()[0].IsBluRay && home.Recents.Single().FamilyLabel == "BD",
                "successful BD generation persists history and immediately displays a BD recent card");
            bluRay.ImagePath = string.Empty;
            bluRay.ImageOutputPath = string.Empty;
            bluRay.TotalSectors = "2048";
            home.SelectedDiscTypeIndex = 0;
            home.OpenRecentCommand.Execute(home.Recents.Single());
            Require(home.IsBluRaySelected && shell.CurrentPageIndex == ShellViewModel.BluRayTabIndex
                && bluRay.ImagePath == source && bluRay.ImageOutputPath == iso
                && preview.BluRayIsoPath == iso && bluRay.TotalSectors == "1024",
                "opening BD history restores source, output and preview ISO capacity in the BD family");

            DateTimeOffset now = DateTimeOffset.UtcNow;
            store.Record(new("BD", source, iso, now.AddSeconds(1)));
            store.Record(new("DVD", source, Path.Combine(directory, "dvd.iso"), now.AddSeconds(2)));
            store.Record(new("cd", source, Path.Combine(directory, "cd.wav"), now.AddSeconds(3)));
            IReadOnlyList<RecentJobEntry> entries = store.Load();
            Require(entries.Count == 3 && entries.Single(entry => entry.IsBluRay).DiscTypeIndex == 2
                && entries.Single(entry => entry.IsDvd).DiscTypeIndex == 1
                && RecentJobEntry.IsKnownFamily("Bd") && !RecentJobEntry.IsKnownFamily("unknown"),
                "history accepts BD case-insensitively, deduplicates it and preserves existing CD/DVD entries");

            int cardNotifications = 0;
            int recentNotifications = 0;
            home.CreateTileCommand.CanExecuteChanged += (_, _) => cardNotifications++;
            home.OpenRecentCommand.CanExecuteChanged += (_, _) => recentNotifications++;
            SetBusy(shell, true);
            Require(!home.NewCommand.CanExecute(null) && !home.OpenCommand.CanExecute(null)
                && !home.CreateTileCommand.CanExecute(bdTile) && !home.OpenRecentCommand.CanExecute(home.Recents[0]),
                "all home entry actions are disabled while a disc job is running");
            SetBusy(shell, false);
            Require(home.NewCommand.CanExecute(null) && home.OpenCommand.CanExecute(null)
                && home.CreateTileCommand.CanExecute(bdTile) && home.OpenRecentCommand.CanExecute(home.Recents[0])
                && cardNotifications == 2 && recentNotifications == 2,
                "home card bindings are notified on busy transitions and become available again");
        }
        finally
        {
            if (preview is not null && previewInitialized)
                typeof(LivePreviewViewModel).GetMethod("OnWindowClosed", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(preview, null);
            foreach (string path in Directory.EnumerateFiles(directory)) File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private static void WriteSource(string path)
    {
        BitmapSource bitmap = BitmapSource.Create(4, 2, 96, 96, PixelFormats.Gray8, null,
            new byte[] { 0, 0, 255, 255, 255, 255, 0, 0 }, 4);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void SetField(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static void SetShellProperty(ShellViewModel shell, string name, object value)
        => SetField(shell, $"<{name}>k__BackingField", value);

    private static void SetBusy(ShellViewModel shell, bool busy)
    {
        SetField(shell, "_isBusy", busy);
        ((Action?)typeof(ShellViewModel).GetField("BusyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(shell))?.Invoke();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Home BD: " + message);
    }

    // Serialize each producer's progress and completion callbacks without requiring a
    // displayed WPF window or a dispatcher pump in this file-only integration test.
    private sealed class InlineTestContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }
}
