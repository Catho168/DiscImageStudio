using System.Collections.Concurrent;
using System.IO;
using DiscImageStudio.Burning;

internal static class OpticalBurnOperationGateTests
{
    internal static void Run()
    {
        TestEntireOperationIsSerialized().GetAwaiter().GetResult();
        TestQueuedCancellation().GetAwaiter().GetResult();
        TestRunningCancellationDoesNotReleaseEarly().GetAwaiter().GetResult();
        TestFailureAndInstanceIsolation().GetAwaiter().GetResult();
        Console.WriteLine("optical-operation-gate: passed (queries/burn serialized, queued cancellation, complete task lifetime; no devices)");
    }

    private static async Task TestEntireOperationIsSerialized()
    {
        OpticalBurnOperationGate gate = new();
        TaskCompletionSource queryRelease = NewSignal(), burnStarted = NewSignal(), burnRelease = NewSignal();
        ConcurrentQueue<string> events = new();
        int active = 0;
        Task<int> query = gate.RunAsync(async () =>
        {
            Require(Interlocked.Increment(ref active) == 1, "first query owns the operation");
            events.Enqueue("query-start");
            await queryRelease.Task.ConfigureAwait(false);
            events.Enqueue("query-end");
            Interlocked.Decrement(ref active);
            return 1;
        }, CancellationToken.None);
        Task<int> burn = gate.RunAsync(async () =>
        {
            Require(Interlocked.Increment(ref active) == 1, "burn cannot overlap a pending query task");
            events.Enqueue("burn-start");
            burnStarted.SetResult();
            await burnRelease.Task.ConfigureAwait(false);
            events.Enqueue("burn-end");
            Interlocked.Decrement(ref active);
            return 2;
        }, CancellationToken.None);
        Require(events.SequenceEqual(new[] { "query-start" }), "burn delegate is not invoked while the query is in flight");
        queryRelease.SetResult();
        await burnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Task<int> nextQuery = gate.RunAsync(() =>
        {
            Require(Interlocked.Increment(ref active) == 1, "later query cannot overlap the burn task");
            events.Enqueue("next-query");
            Interlocked.Decrement(ref active);
            return Task.FromResult(3);
        }, CancellationToken.None);
        Require(!nextQuery.IsCompleted && !events.Contains("next-query"), "query remains queued throughout the native-task lifetime");
        burnRelease.SetResult();
        int[] values = await Task.WhenAll(query, burn, nextQuery).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Require(values.SequenceEqual(new[] { 1, 2, 3 }) && active == 0, "all operation results pass through");
        Require(events.SequenceEqual(new[] { "query-start", "query-end", "burn-start", "burn-end", "next-query" }),
            "the entire query, burn and later query execute in order");
    }

    private static async Task TestQueuedCancellation()
    {
        OpticalBurnOperationGate gate = new();
        TaskCompletionSource<int> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> owner = gate.RunAsync(() => release.Task, CancellationToken.None);
        using CancellationTokenSource queuedToken = new();
        int calls = 0;
        Task<int> queued = gate.RunAsync(() => { calls++; return Task.FromResult(2); }, queuedToken.Token);
        queuedToken.Cancel();
        OperationCanceledException cancelled = await ExpectAsync<OperationCanceledException>(queued).ConfigureAwait(false);
        Require(cancelled.CancellationToken == queuedToken.Token && calls == 0,
            "cancelled queued operation never invokes its device delegate");
        using CancellationTokenSource alreadyCancelled = new();
        alreadyCancelled.Cancel();
        await ExpectAsync<OperationCanceledException>(gate.RunAsync(() =>
        {
            calls++;
            return Task.FromResult(3);
        }, alreadyCancelled.Token)).ConfigureAwait(false);
        Require(calls == 0 && !owner.IsCompleted, "pre-cancelled callers cannot disturb active ownership");
        release.SetResult(1);
        Require(await owner.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false) == 1, "active operation completes normally");
        Require(await gate.RunAsync(() => Task.FromResult(4), CancellationToken.None).ConfigureAwait(false) == 4,
            "cancelled waiters do not leak the gate");
    }

    private static async Task TestRunningCancellationDoesNotReleaseEarly()
    {
        OpticalBurnOperationGate gate = new();
        using CancellationTokenSource runningToken = new();
        TaskCompletionSource<int> nativeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> running = gate.RunAsync(() => nativeCompletion.Task, runningToken.Token);
        runningToken.Cancel();
        bool laterStarted = false;
        Task<int> later = gate.RunAsync(() => { laterStarted = true; return Task.FromResult(2); }, CancellationToken.None);
        Require(!running.IsCompleted && !laterStarted,
            "cancelling an active token cannot release a native operation that has not returned");
        nativeCompletion.SetResult(1);
        await Task.WhenAll(running, later).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Require(laterStarted, "next operation starts after native completion");
    }

    private static async Task TestFailureAndInstanceIsolation()
    {
        OpticalBurnOperationGate first = new(), second = new();
        TaskCompletionSource<int> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> held = first.RunAsync(() => release.Task, CancellationToken.None);
        Require(await second.RunAsync(() => Task.FromResult(7), CancellationToken.None).ConfigureAwait(false) == 7,
            "operation gates belong to one burner instance");
        IOException expected = new("Simulated native operation failure.");
        release.SetException(expected);
        IOException actual = await ExpectAsync<IOException>(held).ConfigureAwait(false);
        Require(ReferenceEquals(expected, actual), "asynchronous operation failure is preserved");
        IOException synchronous = new("Simulated delegate-start failure.");
        actual = await ExpectAsync<IOException>(first.RunAsync<int>(() => throw synchronous, CancellationToken.None)).ConfigureAwait(false);
        Require(ReferenceEquals(synchronous, actual), "synchronous delegate failure is preserved");
        Require(await first.RunAsync(() => Task.FromResult(8), CancellationToken.None).ConfigureAwait(false) == 8,
            "failure always releases operation ownership");
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<T> ExpectAsync<T>(Task task) where T : Exception
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Optical operation gate: " + message);
    }
}
