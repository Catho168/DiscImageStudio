using System.IO;
using DiscImageStudio.BluRay;

internal static class BluRayPatternRangeTests
{
    internal static void Run()
    {
        BluRayDiscParameters disc = new(128);
        BluRayPatternRangeMapping mapping = new(disc, static (_, _) => 0, 32, 96, invertMapping: false);
        byte[] payload = new byte[BluRayDiscParameters.BytesPerSector];
        mapping.FillScrambledPayload(new BluRaySectorContext(0, disc.GetPhysicalSectorNumber(0), disc), payload);
        Require(payload.AsSpan().IndexOfAnyExcept((byte)0) < 0, "before-range payload stays zero");
        mapping.FillScrambledPayload(new BluRaySectorContext(32, disc.GetPhysicalSectorNumber(32), disc), payload);
        Require(payload.AsSpan().IndexOfAnyExcept(BluRayRunLengthMapping.ThreeTByte) < 0,
            "drawing range carries the dark 3T byte");
        mapping.FillScrambledPayload(new BluRaySectorContext(96, disc.GetPhysicalSectorNumber(96), disc), payload);
        Require(payload.AsSpan().IndexOfAnyExcept((byte)0) < 0, "after-range payload stays zero");

        BluRayPatternRangeMapping invertedMapping = new(disc, static (_, _) => 0, 32, 96, invertMapping: true);
        invertedMapping.FillScrambledPayload(
            new BluRaySectorContext(32, disc.GetPhysicalSectorNumber(32), disc), payload);
        Require(payload.AsSpan().IndexOfAnyExcept(BluRayRunLengthMapping.SixTByte) < 0,
            "polarity swaps the in-range run class");

        Throws<ArgumentOutOfRangeException>(() => new BluRayPatternRangeMapping(
            disc, static (_, _) => 0, 31, 96, invertMapping: false), "unaligned start rejected");
        Throws<ArgumentOutOfRangeException>(() => new BluRayPatternRangeMapping(
            disc, static (_, _) => 0, 32, 160, invertMapping: false), "range past the disc rejected");

        Console.WriteLine("blu-ray-pattern-range: passed (full-disc zeros, drawing range, polarity, alignment)");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Blu-ray pattern range: " + message + ".");
        }
    }

    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException("Blu-ray pattern range: " + message + ".");
    }
}
