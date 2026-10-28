namespace Historia.Services.Git;

/// <summary>Moves the rest of an async method off a UI thread before it does blocking work.</summary>
internal static class ThreadHop
{
    /// <summary>Leaves a UI synchronization context for the thread pool; a caller already off the
    /// UI thread continues inline. Task.Yield would post straight back to the UI context's queue;
    /// ForceYielding schedules the continuation on the thread pool. Await it with
    /// <c>ConfigureAwait(false)</c> so the caller does not resume on the context it just left.</summary>
    internal static async ValueTask LeaveUiThreadAsync()
    {
        if (SynchronizationContext.Current is not null)
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
    }
}
