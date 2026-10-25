namespace DiscImageStudio.Burning;

/// <summary>Serializes one burner's complete device operations, including their
/// STA task lifetime. Cancellation can remove a queued operation, but must not
/// release an active operation's ownership before its native work has returned.</summary>
internal sealed class OpticalBurnOperationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    internal async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Do not use WaitAsync(cancellationToken) on the operation itself:
            // a synchronous device command may still be using the recorder.
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
