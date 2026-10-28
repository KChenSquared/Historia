namespace Historia.Services.Git;

/// <summary>
/// Kills process trees on a small fixed pool of dedicated background threads.
/// <c>Process.Kill(entireProcessTree: true)</c> walks and terminates every descendant and takes
/// tens to a hundred milliseconds, so it must never run inline on the thread that cancelled — a UI
/// thread closing a tab, a pool thread running a cancellation callback. Kills are queued and drained
/// by <see cref="ThreadCount"/> consumers, so a burst of cancels (many tabs closing, a wave of
/// cancelled reads) does not wait for one kill at a time.
/// </summary>
internal static class ProcessKiller
{
    internal const int ThreadCount = 4;

    // A backlog this deep means kills are no longer keeping up; logged so it is visible.
    private const int BacklogWarningDepth = 16;

    private static readonly BlockingCollection<(Process Process, TaskCompletionSource Done)> Queue = new();

    static ProcessKiller()
    {
        for (var i = 0; i < ThreadCount; i++)
            new Thread(KillLoop, maxStackSize: 256 * 1024) { IsBackground = true, Name = $"git-process-killer-{i}" }.Start();
    }

    /// <summary>Queues the kill and returns immediately.</summary>
    public static void Kill(Process process) => _ = KillAsync(process);

    /// <summary>Queues the kill; the task completes once it has been attempted. A caller that is
    /// about to dispose <paramref name="process"/> awaits it first — a disposed process can no
    /// longer be killed.</summary>
    public static Task KillAsync(Process process)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add((process, done));
        var depth = Queue.Count;
        if (depth > BacklogWarningDepth)
            AppLog.Write(AppLogLevel.Warn, "git-cli", $"Process kill queue is {depth} deep.");
        return done.Task;
    }

    private static void KillLoop()
    {
        foreach (var (process, done) in Queue.GetConsumingEnumerable())
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Already gone, already disposed, or a descendant refused to die — nothing to recover.
            }

            done.TrySetResult();
        }
    }
}
