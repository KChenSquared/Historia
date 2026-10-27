namespace Historia.Tests;

/// <summary>
/// Background git work is capped at two processes app-wide and runs at below-normal priority;
/// foreground work, network work and writes never wait for a slot. The processes are git aliases
/// that announce themselves with a marker file and then hold until the test writes a release file,
/// so the assertions depend on what is running, never on how long anything takes. The class is in
/// the serial collection: the cap is shared by every <see cref="GitCliService"/> in the process.
/// </summary>
[Trait("tier", "git")]
[Collection("serial")]
public sealed class GitCliPriorityTests : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1.5);

    private readonly string _dir = Directory.CreateTempSubdirectory("historia-priority-").FullName;
    private readonly ConcurrentDictionary<string, ProcessPriorityClass> _priorities = new();
    private readonly List<(string Id, Task<GitCliResult> Task)> _calls = [];
    private GitCliService? _cli;
    private bool _gitFound;

    public async Task InitializeAsync()
    {
        var git = await new GitExecutableLocator().ResolveAsync(null);
        _gitFound = git.Found;
        _cli = new GitCliService(new FixedLocator(git))
        {
            ProcessStarted = process =>
            {
                var id = process.StartInfo.ArgumentList[^1];
                try { _priorities[id] = process.PriorityClass; }
                catch { }
            },
        };
    }

    public async Task DisposeAsync()
    {
        foreach (var (id, _) in _calls)
            Release(id);
        try { await Task.WhenAll(_calls.Select(c => c.Task)).WaitAsync(Patience); }
        catch { }
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task A_third_background_call_waits_until_one_of_two_finishes()
    {
        if (!_gitFound)
            return;

        Begin("b1", GitCliPriority.Background);
        Begin("b2", GitCliPriority.Background);
        await WaitStartedAsync("b1", "b2");

        var third = Hold("b3", GitCliPriority.Background);
        await Task.Delay(Settle);
        Assert.False(Started("b3"), "a third background process started while two were running");
        Assert.False(third.IsCompleted);

        Release("b1");
        await WaitStartedAsync("b3");

        var results = await ReleaseAllAsync();
        Assert.All(results, r => Assert.True(r.IsSuccess, r.StandardError));
    }

    [Fact]
    public async Task Calls_outside_the_cap_start_while_two_background_calls_run()
    {
        if (!_gitFound)
            return;

        Begin("b1", GitCliPriority.Background);
        Begin("b2", GitCliPriority.Background);
        await WaitStartedAsync("b1", "b2");

        Begin("interactive", GitCliPriority.Interactive);
        Begin("normal", GitCliPriority.Normal);
        Begin("network", GitCliPriority.BackgroundNetwork);
        using (GitWriteScope.Enter())
        {
            Begin("write", GitCliPriority.Normal);
            Begin("background-write", GitCliPriority.Background);
        }

        await WaitStartedAsync("interactive", "normal", "network", "write", "background-write");

        var results = await ReleaseAllAsync();
        Assert.All(results, r => Assert.True(r.IsSuccess, r.StandardError));
    }

    [Fact]
    public async Task Background_and_network_processes_run_below_normal()
    {
        if (!_gitFound)
            return;

        Begin("background", GitCliPriority.Background);
        Begin("network", GitCliPriority.BackgroundNetwork);
        Begin("normal", GitCliPriority.Normal);
        Begin("interactive", GitCliPriority.Interactive);
        await WaitStartedAsync("background", "network", "normal", "interactive");

        var results = await ReleaseAllAsync();
        Assert.All(results, r => Assert.True(r.IsSuccess, r.StandardError));
        Assert.Equal(ProcessPriorityClass.BelowNormal, _priorities["background"]);
        Assert.Equal(ProcessPriorityClass.BelowNormal, _priorities["network"]);
        Assert.NotEqual(ProcessPriorityClass.BelowNormal, _priorities["normal"]);
        Assert.NotEqual(ProcessPriorityClass.BelowNormal, _priorities["interactive"]);
    }

    [Fact]
    public async Task A_call_cancelled_while_waiting_for_a_slot_never_starts_git()
    {
        if (!_gitFound)
            return;

        Begin("b1", GitCliPriority.Background);
        Begin("b2", GitCliPriority.Background);
        await WaitStartedAsync("b1", "b2");

        using var cts = new CancellationTokenSource();
        var waiting = Hold("b3", GitCliPriority.Background, cts.Token);
        await Task.Delay(Settle);
        cts.Cancel();
        var result = await waiting.WaitAsync(Patience);

        Assert.Equal(GitErrorKind.Cancelled, result.ErrorKind);
        Assert.False(Started("b3"));

        // The cancelled waiter did not take a slot: a new background call starts once one frees.
        Begin("b4", GitCliPriority.Background);
        Release("b1");
        await WaitStartedAsync("b4");
        await ReleaseAllAsync();
    }

    [Fact]
    public void Lower_priority_is_the_background_priority()
    {
        var lowered = new GitCliRequest { Arguments = ["version"], WorkingDirectory = _dir, LowerPriority = true };
        var network = new GitCliRequest { Arguments = ["version"], WorkingDirectory = _dir, Priority = GitCliPriority.BackgroundNetwork };
        var interactive = new GitCliRequest { Arguments = ["version"], WorkingDirectory = _dir, Priority = GitCliPriority.Interactive, LowerPriority = true };
        var plain = new GitCliRequest { Arguments = ["version"], WorkingDirectory = _dir };

        Assert.Equal(GitCliPriority.Background, lowered.Priority);
        Assert.True(lowered.LowerPriority);
        Assert.True(network.LowerPriority);
        Assert.Equal(GitCliPriority.BackgroundNetwork, network.Priority);
        Assert.Equal(GitCliPriority.Interactive, interactive.Priority);
        Assert.Equal(GitCliPriority.Normal, plain.Priority);
        Assert.False(plain.LowerPriority);
    }

    private void Begin(string id, GitCliPriority priority) => _ = Hold(id, priority);

    private Task<GitCliResult> Hold(string id, GitCliPriority priority, CancellationToken ct = default)
    {
        var call = _cli!.RunAsync(new GitCliRequest
        {
            Arguments =
            [
                "-c", "alias.hold=!f() { touch \"started-$1\"; while [ ! -f \"release-$1\" ]; do sleep 0.1; done; }; f",
                "hold", id,
            ],
            WorkingDirectory = _dir,
            Timeout = TimeSpan.FromMinutes(5),
            Priority = priority,
        }, ct);
        _calls.Add((id, call));
        return call;
    }

    private bool Started(string id) => File.Exists(Path.Combine(_dir, $"started-{id}"));

    private void Release(string id) => File.WriteAllText(Path.Combine(_dir, $"release-{id}"), string.Empty);

    private async Task WaitStartedAsync(params string[] ids)
    {
        var deadline = Stopwatch.StartNew();
        while (!ids.All(Started))
        {
            Assert.True(deadline.Elapsed < Patience,
                $"not started within {Patience}: {string.Join(", ", ids.Where(i => !Started(i)))}");
            await Task.Delay(50);
        }
    }

    private async Task<GitCliResult[]> ReleaseAllAsync()
    {
        foreach (var (id, _) in _calls)
            Release(id);
        return await Task.WhenAll(_calls.Select(c => c.Task)).WaitAsync(Patience);
    }

    private sealed class FixedLocator(GitExecutableInfo info) : IGitExecutableLocator
    {
        public GitExecutableInfo Current => info;

        public Task<GitExecutableInfo> ResolveAsync(string? userOverridePath, CancellationToken ct = default)
            => Task.FromResult(info);

        public Task<GitExecutableInfo> WhenResolvedAsync(CancellationToken ct = default)
            => Task.FromResult(info);
    }
}
