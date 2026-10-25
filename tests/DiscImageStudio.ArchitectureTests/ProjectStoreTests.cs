using System.IO;
using DiscImageStudio.Core;

internal static class ProjectStoreTests
{
    public static void Run()
    {
        TestRoundtrip();
        TestOverwritePreservesSingleFile();
        TestLegacyImageProcessingSection();
        TestValidation();
        Require(true, "completed");
    }

    private static ProjectDocument CreateDocument(string name = "测试工程")
    {
        List<ProjectLayer> layers =
        [
            new("layer-bottom", "底层图", @"C:\pics\bottom.png", true, -1.5, 2.25, 90, 0.8),
            new("layer-top", "顶层图", @"C:\pics\top.png", false, 0, 0, 0, 1),
        ];
        return new ProjectDocument(
            ProjectJsonStore.CurrentSchemaVersion,
            name,
            ProjectDocument.DvdFamily,
            new ProjectGeometry(24.0, 58.0, 2_295_104, 0.001, -0.0002, 0.00001),
            layers,
            new ProjectOutputs(@"C:\out\测试工程.iso", WriteCue: false, @"C:\data", "DISC_IMAGE", Interleave: false),
            CalibrationSessionPath: @"C:\calib\x.calibration.json",
            ThumbnailPngBase64: Convert.ToBase64String([1, 2, 3, 4]),
            CreatedUtc: new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            ModifiedUtc: new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero));
    }

    private static void TestRoundtrip()
    {
        ProjectDocument original = CreateDocument();
        string path = Path.Combine(CreateTempDirectory(), "roundtrip.discproj");
        ProjectJsonStore.Save(path, original);
        Require(File.Exists(path), "save creates the file");
        ProjectDocument loaded = ProjectJsonStore.Load(path);

        Require(loaded.SchemaVersion == original.SchemaVersion, "schemaVersion");
        Require(loaded.Name == original.Name, "name");
        Require(loaded.IsDvd, "family");
        Require(loaded.Geometry.InnerRadiusMm == 24.0, "geometry.inner");
        Require(loaded.Geometry.OuterRadiusMm == 58.0, "geometry.outer");
        Require(loaded.Geometry.Sectors == 2_295_104, "geometry.sectors");
        Require(Math.Abs(loaded.Geometry.PitchLinear - 0.001) < 1e-12, "geometry.pitchLinear");
        Require(loaded.Layers.Count == 2, "layer count");
        Require(loaded.Layers[0].Id == "layer-bottom", "bottom-first order");
        Require(loaded.Layers[1].Visible == false, "layer visibility");
        Require(loaded.Layers[0].RotationDeg == 90, "layer rotation");
        Require(loaded.Outputs.OutputPath == original.Outputs.OutputPath, "outputs.path");
        Require(loaded.Outputs.DataDirectory == @"C:\data", "outputs.dataDirectory");
        Require(loaded.Outputs.VolumeLabel == "DISC_IMAGE", "outputs.volumeLabel");
        Require(loaded.Outputs.Interleave == false, "outputs.interleave");
        Require(loaded.CalibrationSessionPath == original.CalibrationSessionPath, "calibrationSessionPath");
        Require(loaded.ThumbnailPngBase64 == original.ThumbnailPngBase64, "thumbnail");
        Require(loaded.CreatedUtc == original.CreatedUtc, "createdUtc");
        Require(loaded.ModifiedUtc == original.ModifiedUtc, "modifiedUtc");

        // UTF-8 project names survive the file round-trip (Encoding check via a second save).
        ProjectDocument unicode = CreateDocument("碟片 工程-№2");
        string unicodePath = Path.Combine(CreateTempDirectory(), "unicode.discproj");
        ProjectJsonStore.Save(unicodePath, unicode);
        Require(ProjectJsonStore.Load(unicodePath).Name == "碟片 工程-№2", "unicode name roundtrip");

        // Non-DVD families and defaults stay consistent.
        ProjectDocument cd = CreateDocument() with
        {
            DiscFamily = ProjectDocument.CdFamily,
            Geometry = new ProjectGeometry(24.5, 56.8, 359_849),
        };
        string cdPath = Path.Combine(CreateTempDirectory(), "cd.discproj");
        ProjectJsonStore.Save(cdPath, cd);
        Require(!ProjectJsonStore.Load(cdPath).IsDvd, "cd family");
    }

    private static void TestOverwritePreservesSingleFile()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "overwrite.discproj");
        ProjectJsonStore.Save(path, CreateDocument("第一版"));
        ProjectJsonStore.Save(path, CreateDocument("第二版"));
        Require(ProjectJsonStore.Load(path).Name == "第二版", "overwrite keeps newest document");
        // The atomic-write temp file must be gone; only the project file remains.
        Require(Directory.GetFiles(directory, "*.tmp").Length == 0, "temp files cleaned up");
    }

    private static void TestLegacyImageProcessingSection()
    {
        // Files written before the imageProcessing section was removed must still open; the
        // legacy section is dropped on the next save, while any other unknown member stays
        // a hard error.
        string directory = CreateTempDirectory();
        string legacyPath = Path.Combine(directory, "legacy.discproj");
        ProjectJsonStore.Save(legacyPath, CreateDocument());
        string savedJson = File.ReadAllText(legacyPath);
        int rootBrace = savedJson.IndexOf('{');
        string legacyJson = string.Concat(
            savedJson.AsSpan(0, rootBrace + 1),
            "\"imageProcessing\":{\"mode\":\"ringCopy\",\"ringInnerMarginMm\":1.5,\"ringOuterMarginMm\":2.5},",
            savedJson.AsSpan(rootBrace + 1));
        File.WriteAllText(legacyPath, legacyJson);

        ProjectDocument loaded = ProjectJsonStore.Load(legacyPath);
        Require(loaded.Name == "测试工程", "legacy file with imageProcessing loads");

        string migratedPath = Path.Combine(directory, "migrated.discproj");
        ProjectJsonStore.Save(migratedPath, loaded);
        string migratedJson = File.ReadAllText(migratedPath);
        Require(!migratedJson.Contains("imageProcessing", StringComparison.Ordinal),
            "saving a legacy file drops the imageProcessing section");

        string unknownPath = Path.Combine(directory, "unknown.discproj");
        string unknownJson = legacyJson.Replace(
            "\"imageProcessing\"",
            "\"bogusField\"",
            StringComparison.Ordinal);
        File.WriteAllText(unknownPath, unknownJson);
        try
        {
            ProjectJsonStore.Load(unknownPath);
            throw new InvalidOperationException("ProjectStore: unknown member passed but should fail.");
        }
        catch (InvalidDataException)
        {
        }
    }

    private static void TestValidation()
    {
        ThrowsInvalidData("wrong schema", static document => document with { SchemaVersion = 99 });
        ThrowsInvalidData("empty name", static document => document with { Name = " " });
        ThrowsInvalidData("unknown family", static document => document with { DiscFamily = "bd" });
        ThrowsInvalidData("inverted radii", static document => document with
        {
            Geometry = document.Geometry with { InnerRadiusMm = 60, OuterRadiusMm = 58 },
        });
        ThrowsInvalidData("non-multiple DVD sectors", static document => document with
        {
            Geometry = document.Geometry with { Sectors = 2_295_103 },
        });
        ThrowsInvalidData("zero sectors", static document => document with
        {
            Geometry = document.Geometry with { Sectors = 0 },
        });
        ThrowsInvalidData("diverging pitch", static document => document with
        {
            Geometry = document.Geometry with { PitchQuadratic = double.NaN },
        });
        ThrowsInvalidData("null layers", static document => document with { Layers = null! });
        ThrowsInvalidData("duplicate layer id", static document => document with
        {
            Layers =
            [
                new("dup", "a", @"C:\a.png", true, 0, 0, 0, 1),
                new("dup", "b", @"C:\b.png", true, 0, 0, 0, 1),
            ],
        });
        ThrowsInvalidData("empty layer name", static document => document with
        {
            Layers = [new("id", " ", @"C:\a.png", true, 0, 0, 0, 1)],
        });
        ThrowsInvalidData("empty layer path", static document => document with
        {
            Layers = [new("id", "a", " ", true, 0, 0, 0, 1)],
        });
        ThrowsInvalidData("non-positive scale", static document => document with
        {
            Layers = [new("id", "a", @"C:\a.png", true, 0, 0, 0, 0)],
        });
        ThrowsInvalidData("non-finite translation", static document => document with
        {
            Layers = [new("id", "a", @"C:\a.png", true, double.PositiveInfinity, 0, 0, 1)],
        });
        ThrowsInvalidData("empty output path", static document => document with
        {
            Outputs = document.Outputs with { OutputPath = "" },
        });
        ThrowsInvalidData("broken thumbnail base64", static document => document with
        {
            ThumbnailPngBase64 = "not-base64!!",
        });

        static void ThrowsInvalidData(string name, Func<ProjectDocument, ProjectDocument> mutate)
        {
            ProjectDocument mutated = mutate(CreateDocument());
            try
            {
                ProjectJsonStore.Validate(mutated);
            }
            catch (InvalidDataException)
            {
                return;
            }

            throw new InvalidOperationException($"ProjectStore validation '{name}' passed but should fail.");
        }
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"disc-project-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("ProjectStore: " + message);
        }
    }
}
