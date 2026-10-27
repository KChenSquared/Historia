namespace Historia.Tests;

/// <summary>
/// A burst of tree kills is drained by several threads at once rather than one after another.
/// Serial: the test floods the process-wide <see cref="ProcessKiller"/> queue, which would delay
/// every other test's kills if it ran alongside them.
/// </summary>
[Trait("tier", "git")]
[Collection("serial")]
public sealed class ProcessKillerTests
{
    private const int Trees = 24;

    [WindowsFact(Timeout = 120000)]
    public async Task A_burst_of_tree_kills_finishes_well_under_the_one_at_a_time_time()
    {
        // Reference: the same kill, inline and one tree at a time.
        var reference = StartTrees(8);
        var serial = Stopwatch.StartNew();
        foreach (var tree in reference)
            tree.Kill(entireProcessTree: true);
        serial.Stop();
        DisposeAll(reference);
        var perTree = serial.Elapsed / reference.Count;

        var trees = StartTrees(Trees);
        try
        {
            var pooled = Stopwatch.StartNew();
            await Task.WhenAll(trees.Select(ProcessKiller.KillAsync)).WaitAsync(TimeSpan.FromSeconds(60));
            pooled.Stop();

            // One killer thread would need Trees x perTree; four threads need about a quarter of
            // that. The bound sits well between the two.
            var oneAtATime = perTree * Trees;
            Assert.True(pooled.Elapsed < oneAtATime * 0.7,
                $"{Trees} kills took {pooled.Elapsed}; one at a time would take about {oneAtATime}");
            Assert.All(trees, t => Assert.True(t.WaitForExit(10_000)));
        }
        finally
        {
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
