namespace Historia.Tests;

/// <summary>
/// A burst of tree kills is drained by every killer thread at once rather than one after another.
/// Serial: the test floods the process-wide <see cref="ProcessKiller"/> queue and holds its threads
/// in a process-wide hook, which would delay every other test's kills if it ran alongside them.
/// </summary>
[Trait("tier", "git")]
[Collection("serial")]
public sealed class ProcessKillerTests
{
    private const int Trees = 24;

    [WindowsFact(Timeout = 120000)]
    public async Task A_burst_of_tree_kills_is_drained_by_every_killer_thread_at_once()
    {
        // Each kill holds its killer thread until ThreadCount kills are inside at the same time, so
        // the test passes only if the queue is drained by that many threads in parallel. With fewer
        // threads the first wait gives up, releases every later kill and the peak says how many ran.
        var inside = 0;
        var peak = 0;
        var gaveUp = false;
        var gate = new object();
        using var allInside = new ManualResetEventSlim();
        ProcessKiller.KillStartingForTesting = () =>
        {
            lock (gate)
            {
                inside++;
                peak = Math.Max(peak, inside);
                if (inside >= ProcessKiller.ThreadCount)
                    allInside.Set();
            }
            if (!allInside.Wait(TimeSpan.FromSeconds(10)))
            {
                gaveUp = true;
                allInside.Set();
            }
            lock (gate)
                inside--;
        };

        var trees = StartTrees(Trees);
        try
        {
            await Task.WhenAll(trees.Select(ProcessKiller.KillAsync)).WaitAsync(TimeSpan.FromSeconds(90));

            Assert.False(gaveUp, $"at most {peak} of {ProcessKiller.ThreadCount} killer threads ran a kill at once");
            Assert.Equal(ProcessKiller.ThreadCount, peak);
            Assert.All(trees, t => Assert.True(t.WaitForExit(10_000)));
        }
        finally
        {
            ProcessKiller.KillStartingForTesting = null;
            allInside.Set();
            DisposeAll(trees);
        }
    }

    // cmd.exe running ping: a root with a child, so Kill(entireProcessTree) has a tree to walk.
    private static List<Process> StartTrees(int count)
    {
        var trees = new List<Process>();
        for (var i = 0; i < count; i++)
        {
            trees.Add(Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "ping.exe", "-n", "120", "127.0.0.1"])
            { CreateNoWindow = true, UseShellExecute = false })!);
        }

        // Let each cmd.exe spawn its ping before anything is killed.
        Thread.Sleep(1_500);
        return trees;
    }

    private static void DisposeAll(List<Process> trees)
    {
        foreach (var tree in trees)
        {
            try { if (!tree.HasExited) tree.Kill(entireProcessTree: true); } catch { /* best effort */ }
            tree.Dispose();
        }
    }
}
