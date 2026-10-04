using DiscImageStudio.Controls;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;

internal static class CalibrationOutputPublisherTests
{
    internal static void Run()
    {
        TestCompleteReplacement();
        TestLockedOldFileRestoresPreviousSet();
        TestLockedStagedFileRollsBackPublishedFiles();
        TestPathConflictsBeforeModification();
        Console.WriteLine("calibration-publisher: complete replacement, occupied-file rollback and path guards passed");
    }

    private static void TestCompleteReplacement()
    {
        using var fixture = new Fixture();
        fixture.Stage("track.wav", "new-wave");
        fixture.Stage("track.cue", "new-cue");
        fixture.Stage("track.calibration.json", "new-session");
        fixture.Old("track.wav", "old-wave");
        fixture.Old("track.cue", "old-cue");
        fixture.Old("track.calibration.json", "old-session");
        fixture.Old("unrelated.txt", "keep");
        Publish(fixture.Staging, fixture.Destination);
        fixture.RequireOutput("track.wav", "new-wave");
        fixture.RequireOutput("track.cue", "new-cue");
        fixture.RequireOutput("track.calibration.json", "new-session");
        fixture.RequireOutput("unrelated.txt", "keep");
        Require(Directory.GetFiles(fixture.Staging).Length == 0, "complete staging set moved");
        Require(fixture.Backups().Length == 0, "success clears backups");
    }

    private static void TestLockedOldFileRestoresPreviousSet()
    {
        using var fixture = new Fixture();
        foreach (string name in new[] { "a-track.raw", "b-track.cue", "z-track.calibration.json" })
        {
            fixture.Stage(name, "new-" + name);
            fixture.Old(name, "old-" + name);
        }
        using (var occupied = new FileStream(Path.Combine(fixture.Destination, "z-track.calibration.json"),
            FileMode.Open, FileAccess.Read, FileShare.None))
            Throws<IOException>(() => Publish(fixture.Staging, fixture.Destination), "occupied old metadata fails publication");
        foreach (string name in new[] { "a-track.raw", "b-track.cue", "z-track.calibration.json" })
        {
            fixture.RequireOutput(name, "old-" + name);
            Require(File.ReadAllText(Path.Combine(fixture.Staging, name)) == "new-" + name, "staging preserved after backup failure");
        }
        Require(fixture.Backups().Length == 0, "successful rollback leaves no backup content");
    }

    private static void TestLockedStagedFileRollsBackPublishedFiles()
    {
        using var fixture = new Fixture();
        foreach (string name in new[] { "a-track.calibration.json", "b-track.cue", "z-track.wav" })
            fixture.Stage(name, "new-" + name);
        fixture.Old("a-track.calibration.json", "old-session");
        fixture.Old("z-track.wav", "old-wave");
        using (var occupied = new FileStream(Path.Combine(fixture.Staging, "z-track.wav"),
            FileMode.Open, FileAccess.Read, FileShare.None))
            Throws<IOException>(() => Publish(fixture.Staging, fixture.Destination), "occupied source fails after earlier new files moved");
        fixture.RequireOutput("a-track.calibration.json", "old-session");
        fixture.RequireOutput("z-track.wav", "old-wave");
        Require(!File.Exists(Path.Combine(fixture.Destination, "b-track.cue")), "new-only output removed during rollback");
        foreach (string name in new[] { "a-track.calibration.json", "b-track.cue", "z-track.wav" })
            Require(File.ReadAllText(Path.Combine(fixture.Staging, name)) == "new-" + name, "published source restored for retry");
        Require(fixture.Backups().Length == 0, "replacement rollback restored all backups");
    }

    private static void TestPathConflictsBeforeModification()
    {
        using var fixture = new Fixture();
        fixture.Stage("track.raw", "new-wave");
        fixture.Stage("track.calibration.json", "new-session");
        fixture.Old("track.raw", "old-wave");
        Directory.CreateDirectory(Path.Combine(fixture.Destination, "track.calibration.json"));
        Throws<IOException>(() => Publish(fixture.Staging, fixture.Destination), "directory with same output name rejected");
        fixture.RequireOutput("track.raw", "old-wave");
        Require(fixture.Backups().Length == 0, "path validation precedes backups");
        Throws<ArgumentException>(() => Publish(fixture.Staging, fixture.Staging), "identical directories rejected");
        Throws<ArgumentException>(() => Publish(fixture.Staging, Path.Combine(fixture.Staging, "nested-output")),
            "destination inside staging rejected");
        Require(File.ReadAllText(Path.Combine(fixture.Staging, "track.raw")) == "new-wave", "path guards preserve staged data");
    }

    private static void Publish(string staging, string destination)
    {
        MethodInfo method = typeof(CalibrationPhotoCanvas).Assembly.GetType("DiscImageStudio.Services.CalibrationOutputPublisher", throwOnError: true)!
            .GetMethod("Publish", BindingFlags.Static | BindingFlags.NonPublic)!;
        try { method.Invoke(null, [staging, destination]); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
        }
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("calibration-publisher: " + name);
    }

    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("calibration-publisher: expected " + typeof(T).Name + "; " + name);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        internal string Destination { get; }
        internal string Staging { get; }

        internal Fixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "DiscImageStudio-publisher-test-" + Guid.NewGuid().ToString("N"));
            Destination = Path.Combine(_root, "output");
            Staging = Path.Combine(Destination, ".calibration-staging");
            Directory.CreateDirectory(Staging);
        }

        internal void Stage(string name, string content) => File.WriteAllText(Path.Combine(Staging, name), content);
        internal void Old(string name, string content) => File.WriteAllText(Path.Combine(Destination, name), content);
        internal void RequireOutput(string name, string content) => Require(File.ReadAllText(Path.Combine(Destination, name)) == content, name);
        internal string[] Backups() => Directory.GetDirectories(Destination, ".calibration-backup-*");

        public void Dispose()
        {
            // Only this fixture's freshly created temporary tree may be recursively removed.
            string resolved = Path.GetFullPath(_root);
            string temporary = Path.GetFullPath(Path.GetTempPath());
            Require(resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(resolved).StartsWith("DiscImageStudio-publisher-test-", StringComparison.Ordinal), "temporary cleanup scope");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
