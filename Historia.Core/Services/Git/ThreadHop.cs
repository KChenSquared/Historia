namespace Historia.Services.Git;

/// <summary>Moves the rest of an async method off a UI thread before it does blocking work.</summary>
internal static class ThreadHop
{
    /// <summary>Leaves a UI synchronization context for the thread pool; a caller already off the
    /// UI thread continues inline. Task.Yield would post straight back to the UI context's queue;
    /// ForceYielding schedules the awaiting method's continuation on the thread pool, and neither
    /// option resumes on the captured context.
    /// <para>Trap: this used to be an <c>async</c> method that awaited the yield itself. Its body
    /// after the yield was empty, so the pool thread could complete the returned task before the
    /// caller's await looked at it, and the caller then carried on inline on the UI thread. The
    /// awaitable is returned as is, so the caller's own continuation is what moves.</para></summary>
    internal static ConfiguredTaskAwaitable LeaveUiThreadAsync()
        => Task.CompletedTask.ConfigureAwait(SynchronizationContext.Current is null
            ? ConfigureAwaitOptions.None
            : ConfigureAwaitOptions.ForceYielding);
}
