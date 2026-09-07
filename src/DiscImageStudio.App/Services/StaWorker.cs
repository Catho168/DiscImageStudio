namespace DiscImageStudio.Services;

internal static class StaWorker
{
    /// Runs COM/WIC-touching work on a dedicated STA background thread.
    internal static Task<int> RunAsync(Func<int> action)
    {
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Disc image worker",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
