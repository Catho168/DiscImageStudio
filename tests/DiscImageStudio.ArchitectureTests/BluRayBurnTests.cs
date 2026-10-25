using DiscImageStudio.Burning;

internal static class BluRayBurnTests
{
    internal static void Run()
    {
        TestSectorValidation();
        TestWriteSpeedUnits();
        TestMediaClassification();
        TestExactMediaFamilies();
        TestBlankMediaAndCapacity();
        TestUnknownMediaRejectedBeforeDeviceAccess();
        BluRayBurnWorkflowTests.Run();
        Console.WriteLine("blu-ray-burn: passed (no recorder access)");
    }

    private static void TestSectorValidation()
    {
        bool produced = false;
        OpticalBurnRequest request = new(
            "test-recorder",
            OpticalBurnMediaKind.BluRayData,
            2048,
            (_, _) => produced = true);
        request.Validate();
        (request with { ContentLength = 12_219_392L * 2048 }).Validate();
        Require(!produced, "validation does not generate content");

        foreach (long length in new long[] { -2048, 0, 1, 2047, 2049, 2352 })
        {
            Throws<ArgumentOutOfRangeException>(
                () => (request with { ContentLength = length }).Validate(),
                $"Blu-ray rejects incomplete 2048-byte sectors ({length})");
        }

        (request with { MediaKind = OpticalBurnMediaKind.DvdData }).Validate();
        (request with { MediaKind = OpticalBurnMediaKind.CdAudio, ContentLength = 2352 }).Validate();
        Throws<ArgumentOutOfRangeException>(
            () => (request with { MediaKind = OpticalBurnMediaKind.CdAudio }).Validate(),
            "CD still requires 2352-byte audio sectors");
        Throws<ArgumentOutOfRangeException>(
            () => (request with { WriteSpeed = new OpticalWriteSpeed(0, false) }).Validate(),
            "Blu-ray rejects zero write speed");
    }

    private static void TestWriteSpeedUnits()
    {
        OpticalWriteSpeed speed = new(9000, false);
        Near(4.096, speed.GetMultiplier(OpticalBurnMediaKind.BluRayData), "Blu-ray 1x is 4.5 MB/s");
        Near(18.432, speed.GetMegabytesPerSecond(OpticalBurnMediaKind.BluRayData), "Blu-ray uses 2048-byte sectors");
        Near(18_432_000.0 / 1_385_000, speed.GetMultiplier(OpticalBurnMediaKind.DvdData), "DVD multiplier is unchanged");
        Near(18.432, speed.GetMegabytesPerSecond(OpticalBurnMediaKind.DvdData), "DVD data rate is unchanged");
        Near(120, speed.GetMultiplier(OpticalBurnMediaKind.CdAudio), "CD multiplier is unchanged");
        Near(21.168, speed.GetMegabytesPerSecond(OpticalBurnMediaKind.CdAudio), "CD audio rate is unchanged");

        Near(
            int.MaxValue * 2048.0 / 1_000_000.0,
            new OpticalWriteSpeed(int.MaxValue, false).GetMegabytesPerSecond(OpticalBurnMediaKind.BluRayData),
            "speed conversion does not overflow an integer");
    }

    private static void TestMediaClassification()
    {
        Require(WindowsImapiBurner.IsWritableBluRayMedia(0x12, [0x41]), "BD-R sequential is accepted");
        Require(!WindowsImapiBurner.IsWritableBluRayMedia(0x12, [0x42]), "BD-R random recording cannot preserve the drawing address model");
        Require(!WindowsImapiBurner.IsWritableBluRayMedia(0x13, [0x43]), "BD-RE is outside the unformatted BD-R path");
        Require(WindowsImapiBurner.IsWritableBluRayMedia(0x12, [0x40, 0x41]), "BD read capability does not hide a writable profile");

        // Includes empty/unknown media, every CD/DVD/HD-DVD physical type and BD-ROM.
        // Even a misleading supported-profile list must not bypass the physical check.
        for (int mediaType = 0; mediaType <= 0x11; mediaType++)
        {
            Require(
                !WindowsImapiBurner.IsWritableBluRayMedia(mediaType, [0x41, 0x42, 0x43]),
                $"non-writable-Blu-ray physical type 0x{mediaType:X2} is rejected");
        }

        foreach (int[] profiles in new int[][] { [], [0x40], [0x09], [0x11], [0x1B], [0x50], [0xFFFF] })
        {
            Require(!WindowsImapiBurner.IsWritableBluRayMedia(0x12, profiles), "BD-R needs a current writable BD-R profile");
            Require(!WindowsImapiBurner.IsWritableBluRayMedia(0x13, profiles), "BD-RE needs a current writable BD-RE profile");
        }

        Require(!WindowsImapiBurner.IsWritableBluRayMedia(0x12, [0x43]), "BD-R and BD-RE identities must agree");
        Require(!WindowsImapiBurner.IsWritableBluRayMedia(0x13, [0x41, 0x42]), "BD-RE and BD-R identities must agree");
        Require(!WindowsImapiBurner.IsWritableBluRayMedia(0x10000, [0x41]), "unknown physical type is rejected");
    }

    private static void TestBlankMediaAndCapacity()
    {
        const long sectors = 12_219_392;
        OpticalBurnRequest request = new(
            "test-recorder",
            OpticalBurnMediaKind.BluRayData,
            sectors * 2048,
            (_, _) => throw new InvalidOperationException("Validation must not produce content."));
        WindowsImapiBurner.ValidateDataMedia(request, 0x02, sectors);
        WindowsImapiBurner.ValidateDataMedia(request, 0x03, sectors + 1);
        Throws<InvalidOperationException>(
            () => WindowsImapiBurner.ValidateDataMedia(request, 0x02, sectors - 1),
            "Blu-ray capacity boundary uses 2048-byte sectors and 64-bit byte counts");
        Throws<InvalidOperationException>(
            () => WindowsImapiBurner.ValidateDataMedia(request, 0x04, sectors),
            "Blu-ray rejects previously written media instead of overwriting it");

        OpticalBurnRequest dvdRequest = request with { MediaKind = OpticalBurnMediaKind.DvdData };
        WindowsImapiBurner.ValidateDataMedia(dvdRequest, 0x02, sectors);
        Throws<InvalidOperationException>(
            () => WindowsImapiBurner.ValidateDataMedia(dvdRequest, 0x04, sectors),
            "DVD blank-media requirement is preserved");
    }

    private static void TestExactMediaFamilies()
    {
        (int Type, int Profile, OpticalBurnMediaKind Kind)[] media =
        [
            (0x2, 0x9, OpticalBurnMediaKind.CdAudio), (0x3, 0xA, OpticalBurnMediaKind.CdAudio),
            (0x5, 0x12, OpticalBurnMediaKind.DvdData), (0x6, 0x1B, OpticalBurnMediaKind.DvdData),
            (0x7, 0x1A, OpticalBurnMediaKind.DvdData), (0x8, 0x2B, OpticalBurnMediaKind.DvdData),
            (0x9, 0x11, OpticalBurnMediaKind.DvdData), (0xA, 0x13, OpticalBurnMediaKind.DvdData),
            (0xA, 0x14, OpticalBurnMediaKind.DvdData), (0xB, 0x15, OpticalBurnMediaKind.DvdData),
            (0xB, 0x16, OpticalBurnMediaKind.DvdData), (0xD, 0x2A, OpticalBurnMediaKind.DvdData),
            (0x12, 0x41, OpticalBurnMediaKind.BluRayData),
        ];
        foreach (OpticalBurnMediaKind selected in Enum.GetValues<OpticalBurnMediaKind>())
        {
            foreach ((int type, int profile, OpticalBurnMediaKind actual) in media)
            {
                Require(WindowsImapiBurner.IsWritableMedia(selected, type, [profile]) == (selected == actual),
                    $"{selected} accepts only its own physical family, for type 0x{type:X2}");
                Require(!WindowsImapiBurner.IsWritableMedia(selected, type, []),
                    "a matching physical medium also requires its current writable profile");
            }
            foreach (int type in new[] { 0, 1, 4, 0xC, 0xE, 0xF, 0x10, 0x11, 0xFFFF })
                Require(!WindowsImapiBurner.IsWritableMedia(selected, type, media.Select(m => m.Profile).ToArray()),
                    "ROM, HD-DVD, unknown and nonoptical types cannot masquerade as a selected medium");
        }
        Throws<ArgumentOutOfRangeException>(
            () => WindowsImapiBurner.IsWritableMedia((OpticalBurnMediaKind)999, 0x12, [0x41]),
            "unknown selected media family is rejected");
    }

    private static void TestUnknownMediaRejectedBeforeDeviceAccess()
    {
        OpticalBurnMediaKind unknown = (OpticalBurnMediaKind)999;
        OpticalBurnRequest request = new("test-recorder", unknown, 2048, (_, _) => { });
        Throws<ArgumentOutOfRangeException>(request.Validate, "unknown request kind is rejected");
        OpticalWriteSpeed speed = new(9000, false);
        Throws<ArgumentOutOfRangeException>(() => speed.GetMultiplier(unknown), "unknown multiplier kind is rejected");
        Throws<ArgumentOutOfRangeException>(() => speed.GetMegabytesPerSecond(unknown), "unknown data-rate kind is rejected");

        WindowsImapiBurner burner = new();
        Throws<ArgumentOutOfRangeException>(
            () => burner.GetSupportedWriteSpeedsAsync("test-recorder", unknown),
            "speed enumeration rejects unknown kind before starting a COM thread");
        Throws<ArgumentOutOfRangeException>(
            () => burner.BurnAsync(request),
            "burn entry point rejects unknown kind before starting a COM thread");
    }

    private static void Near(double expected, double actual, string name)
        => Require(Math.Abs(expected - actual) <= 1e-10, name);

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Blu-ray burning: {name}.");
        }
    }

    private static void Throws<TException>(Action action, string name)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Blu-ray burning: {name} did not throw {typeof(TException).Name}.");
    }
}
