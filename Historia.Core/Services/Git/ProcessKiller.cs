namespace Historia.Services.Git;

/// <summary>
/// Kills process trees on one dedicated background thread. <c>Process.Kill(entireProcessTree: true)</c>
/// walks and terminates every descendant and takes around a hundred milliseconds, so it must never
/// run inline on the thread that cancelled — a UI thread closing a tab, a pool thread running a
/// cancellation callback. Kills are queued and run one after another.
/// </summary>
internal static class ProcessKiller
{
    private static readonly BlockingCollection<(Process Process, TaskCompletionSource Done)> Queue = new();

    static ProcessKiller()
    {
        new Thread(KillLoop, maxStackSize: 256 * 1024) { IsBackground = true, Name = "git-process-killer" }.Start();
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
