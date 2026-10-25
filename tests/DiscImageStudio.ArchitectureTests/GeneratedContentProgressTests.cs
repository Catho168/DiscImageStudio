using System.Diagnostics;
using System.Runtime.InteropServices;
using DiscImageStudio.Burning;

internal static class GeneratedContentProgressTests
{
    internal static void Run()
    {
        TestMonotonicThrottleAndExactBytes();
        TestSingleReadAndEof();
        TestObserverFailureAndMissingObserver();
        TestCancellationStillStopsReads();
        Console.WriteLine("generated-content-progress: passed (250 ms source throttling, first/final reads, exact bytes, callback isolation; no device access)");
    }

    private static void TestMonotonicThrottleAndExactBytes()
    {
        byte[] expected = Enumerable.Range(0, 32).Select(index => (byte)(index * 7)).ToArray();
        byte[] actual = new byte[expected.Length];
        List<(long Completed, long Total, int Thread)> updates = [];
        long timestamp = 0;
        int readerThread = Environment.CurrentManagedThreadId;
        using GeneratedContentComStream stream = new(expected.Length,
            (output, _) => output.Write(expected),
            (completed, total) => updates.Add((completed, total, Environment.CurrentManagedThreadId)),
            CancellationToken.None, timestampProvider: () => timestamp);
        stream.WaitUntilPrebuffered(CancellationToken.None);
        Require(updates.Count == 0, "prebuffering does not report source consumption");
        IntPtr countPointer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            int[] milliseconds = [0, 0, 249, 250, 499, 500, 501, 501];
            for (int index = 0; index < milliseconds.Length; index++)
            {
                timestamp = milliseconds[index] * Stopwatch.Frequency / 1000;
                byte[] buffer = new byte[4];
                stream.Read(buffer, buffer.Length, countPointer);
                Require(Marshal.ReadInt32(countPointer) == buffer.Length, "each read still returns its complete payload");
                buffer.CopyTo(actual, index * buffer.Length);
            }

            Require(actual.SequenceEqual(expected), "throttled progress preserves every byte in order");
            Require(updates.Select(update => update.Completed).SequenceEqual(new long[] { 4, 16, 24, 32 }),
                "first read, 250 ms boundaries and final read are reported; intermediate reads are suppressed");
            Require(updates.All(update => update.Total == expected.Length && update.Thread == readerThread),
                "callbacks keep the declared length and execute synchronously on the reader thread");

            timestamp = Stopwatch.Frequency * 2;
            stream.Read(new byte[4], 4, countPointer);
            Require(Marshal.ReadInt32(countPointer) == 0 && updates.Count == 4,
                "a later EOF read returns zero without reporting completion again");
        }
        finally { Marshal.FreeHGlobal(countPointer); }
    }

    private static void TestSingleReadAndEof()
    {
        // Cross two full producer chunks and a short tail in a single IStream read.
        byte[] expected = new byte[GeneratedContentComStream.ChunkBytes * 2 + 97];
        for (int index = 0; index < expected.Length; index++) expected[index] = (byte)(index % 251);
        List<long> updates = [];
        using GeneratedContentComStream stream = new(expected.Length,
            (output, _) => output.Write(expected), (completed, _) => updates.Add(completed),
            CancellationToken.None, timestampProvider: () => 0);
        stream.WaitUntilPrebuffered(CancellationToken.None);
        byte[] actual = new byte[expected.Length];
        stream.Read(actual, actual.Length, IntPtr.Zero);
        stream.Read(new byte[1], 1, IntPtr.Zero);
        Require(actual.SequenceEqual(expected), "chunk boundaries and the short tail preserve exact bytes");
        Require(updates.SequenceEqual(new long[] { expected.Length }),
            "a read that is both first and final reports completion exactly once");
    }

    private static void TestObserverFailureAndMissingObserver()
    {
        byte[] expected = [3, 5, 7];
        int calls = 0;
        using (GeneratedContentComStream stream = new(expected.Length,
            (output, _) => output.Write(expected), (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("Simulated progress observer failure.");
            }, CancellationToken.None, timestampProvider: () => 0))
        {
            stream.WaitUntilPrebuffered(CancellationToken.None);
            for (int index = 0; index < expected.Length; index++)
            {
                byte[] actual = new byte[1];
                stream.Read(actual, 1, IntPtr.Zero);
                Require(actual[0] == expected[index], "an observer failure cannot interrupt or corrupt the next byte");
            }
            Require(calls == 2, "failing observers still receive first/final reads and remain throttled");
        }

        using GeneratedContentComStream unobserved = new(expected.Length,
            (output, _) => output.Write(expected), progress: null, CancellationToken.None,
            timestampProvider: () => throw new InvalidOperationException("No clock is needed without a progress observer."));
        unobserved.WaitUntilPrebuffered(CancellationToken.None);
        byte[] result = new byte[expected.Length];
        unobserved.Read(result, result.Length, IntPtr.Zero);
        Require(result.SequenceEqual(expected), "unobserved reads skip progress bookkeeping and preserve bytes");
    }

    private static void TestCancellationStillStopsReads()
    {
        using CancellationTokenSource cancellation = new();
        int callbacks = 0;
        using GeneratedContentComStream stream = new(2, (output, _) => output.Write(new byte[] { 13, 17 }),
            (_, _) => { callbacks++; cancellation.Cancel(); }, cancellation.Token, timestampProvider: () => 0);
        stream.WaitUntilPrebuffered(CancellationToken.None);
        byte[] actual = new byte[1];
        stream.Read(actual, 1, IntPtr.Zero);
        Require(actual[0] == 13 && callbacks == 1, "first callback follows the completed read");
        bool cancelled = false;
        try { stream.Read(actual, 1, IntPtr.Zero); }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled && callbacks == 1 && actual[0] == 13,
            "cancellation stops the next read without reporting or copying later bytes");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Generated content progress: " + message);
    }
}
