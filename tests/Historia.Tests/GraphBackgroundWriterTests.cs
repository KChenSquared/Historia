namespace Historia.Tests;

/// <summary>
/// <see cref="GraphBackgroundWriter"/> is the one
/// app-wide serial queue for commit-graph writes and the inactive-tab headless pipeline. These
/// tests use <see cref="RecordingGitCliService"/> so no real <c>git.exe</c> invocation is needed
/// — only that the RIGHT arguments would have been sent, in the right order, without ever
/// touching <c>git maintenance</c> or git config.
/// </summary>
[Trait("tier", "git")]
public sealed class GraphBackgroundWriterTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    public GraphBackgroundWriterTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("seed-1", b => b.WriteFile("f.txt", "1\n").Stage().Commit("c1"));
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        foreach (var other in _others)
            other.Dispose();
        await _host.DisposeAsync();
    }

    private readonly List<TestRepositoryBuilder> _others = [];

    /// <summary>A tab on a repository of its own. Inactive jobs are tracked by on-disk store, and
    /// two tabs on one path share a store, so tests of distinct tabs need distinct paths.</summary>
    private RepositoryContext OpenOtherTab()
    {
        var repo = TestRepositoryBuilder.FromTemplate("seed-1", b => b.WriteFile("f.txt", "1\n").Stage().Commit("c1"));
        _others.Add(repo);
        return _host.Open(repo.Path);
    }

    private void WriteStubGraph()
    {
        var info = Path.Combine(_repo.Path, ".git", "objects", "info");
        Directory.CreateDirectory(info);
        File.WriteAllText(Path.Combine(info, "commit-graph"), "stub");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }

    [Fact]
    public async Task A_missing_commit_graph_is_written_plain_reachable_at_below_normal_priority()
    {
        var cli = new RecordingGitCliService();
        var writer = new GraphBackgroundWriter(cli);
        await using var ctx = _host.Open(_repo.Path);

        await writer.EnsureCurrentAsync(ctx);

        var req = Assert.Single(cli.Requests);
        Assert.Equal(["commit-graph", "write", "--reachable"], req.Arguments);
        Assert.True(req.LowerPriority);
        Assert.Equal("1", req.ExtraEnvironment?["GIT_NO_LAZY_FETCH"]);
    }

    [Fact]
    public async Task Never_calls_git_maintenance_or_touches_git_config()
    {
        var cli = new RecordingGitCliService();
        var writer = new GraphBackgroundWriter(cli);
        await using var ctx = _host.Open(_repo.Path);

        await writer.EnsureCurrentAsync(ctx);
        WriteStubGraph();
        File.WriteAllText(Path.Combine(_repo.Path, ".git", "refs", "heads", "other"), (await ctx.RefSnapshots.GetAsync()).HeadTarget + "\n");
        await writer.EnsureCurrentAsync(ctx);

        Assert.Equal(2, cli.Requests.Count);
        Assert.All(cli.Requests, r =>
        {
            Assert.DoesNotContain("maintenance", r.Arguments);
            Assert.DoesNotContain("config", r.Arguments);
        });
    }

    [Fact]
    public async Task An_unusable_existing_graph_is_refreshed_with_split_and_an_expire_time()
    {
        WriteStubGraph();
        var cli = new RecordingGitCliService();
        var writer = new GraphBackgroundWriter(cli);
        await using var ctx = _host.Open(_repo.Path);

        await writer.EnsureCurrentAsync(ctx);

        var req = Assert.Single(cli.Requests);
        Assert.Equal(["commit-graph", "write", "--reachable", "--split"], req.Arguments.Take(4));
        Assert.Contains(req.Arguments, a => a.StartsWith("--expire-time="));
        Assert.True(req.LowerPriority);
        Assert.Equal("1", req.ExtraEnvironment?["GIT_NO_LAZY_FETCH"]);
    }

    [Fact]
    public async Task A_second_check_with_unchanged_refs_does_not_write_again()
    {
        var cli = new RecordingGitCliService();
        var writer = new GraphBackgroundWriter(cli);
        await using var ctx = _host.Open(_repo.Path);

        await writer.EnsureCurrentAsync(ctx);
        // The recording CLI writes nothing, so the graph is still missing: only the recorded ref
        // state keeps the second check from writing.
        await writer.EnsureCurrentAsync(ctx);

        Assert.Single(cli.Requests);
    }

    [Fact]
    public async Task RefreshAfterFetch_with_an_existing_graph_writes_split_with_an_expire_time()
    {
        WriteStubGraph();
        var cli = new RecordingGitCliService();
        var writer = new GraphBackgroundWriter(cli);
        await using var ctx = _host.Open(_repo.Path);

        writer.RefreshAfterFetch(ctx);
        await WaitForAsync(() => cli.Requests.Count > 0);

        var req = Assert.Single(cli.Requests);
        Assert.Contains("--split", req.Arguments);
        Assert.Contains(req.Arguments, a => a.StartsWith("--expire-time="));
    }

    [Fact]
    public async Task RefreshAfterFetch_with_no_graph_writes_plain_reachable_whatever_the_size()
    {
        var cli = new RecordingGitCliService();
        var writer = new GraphBackgroundWriter(cli);
        await using var ctx = _host.Open(_repo.Path);

        writer.RefreshAfterFetch(ctx);
        await WaitForAsync(() => cli.Requests.Count > 0);

        var req = Assert.Single(cli.Requests);
        Assert.Equal(["commit-graph", "write", "--reachable"], req.Arguments);
    }

    [Fact]
    public async Task EnsureCurrent_waits_for_the_gate_before_checking()
    {
        var gate = new BlockingGate();
        var cli = new RecordingGitCliService();
        var writer = new GraphBackgroundWriter(cli, gate);
        await using var ctx = _host.Open(_repo.Path);

        var check = writer.EnsureCurrentAsync(ctx);
        await Task.Delay(200);
        Assert.False(check.IsCompleted);
        Assert.Empty(cli.Requests);

        gate.Open();
        await check.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(cli.Requests);
    }

    [Fact]
    public async Task Commit_graph_writes_run_with_an_uncancellable_token()
    {
        var cli = new TokenRecordingGitCliService();
        var writer = new GraphBackgroundWriter(cli);
        await using var ctx = _host.Open(_repo.Path);

        await writer.EnsureCurrentAsync(ctx);

        var ct = Assert.Single(cli.Tokens);
        Assert.False(ct.CanBeCanceled);
    }

    [Fact]
    public async Task Cancelling_the_tab_lifetime_mid_write_does_not_cancel_the_git_process()
    {
        // Verified against Historia.Core.Services.Git.GitCliService.RunAsync: the request's own
        // CancellationToken is what's registered to TryKill the process (see
        // GitCliProcessSetup.TryKill -> Process.Kill(entireProcessTree: true), i.e. TerminateProcess
        // on Windows). Passing CancellationToken.None for the process run itself means nothing the
        // caller does to its own RepositoryContext.Lifetime can reach that registration.
        var block = new TaskCompletionSource();
        var cli = new TokenRecordingGitCliService(block);
        var writer = new GraphBackgroundWriter(cli);
        var ctx = _host.Open(_repo.Path);

        var check = writer.EnsureCurrentAsync(ctx);
        await WaitForAsync(() => cli.Tokens.Count > 0);
        Assert.Single(cli.Tokens); // the write has started and is now blocked mid-run

        await ctx.DisposeAsync(); // the tab closes while the git process is still "running"
        await Task.Delay(150); // give a wrongly-cancelled write a chance to unwind
        Assert.False(check.IsCompleted);

        block.SetResult(); // let the fake "process" finish, as if the real one ran to completion
        await check.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, cli.Completed);

        // The serializer was released: a later ref change on a new tab writes again.
        await using var ctx2 = _host.Open(_repo.Path);
        File.WriteAllText(Path.Combine(_repo.Path, ".git", "refs", "heads", "other"), (await ctx2.RefSnapshots.GetAsync()).HeadTarget + "\n");
        await writer.EnsureCurrentAsync(ctx2);
        Assert.Equal(2, cli.Tokens.Count);
        Assert.All(cli.Tokens, t => Assert.False(t.CanBeCanceled));
    }

    [Fact]
    public async Task A_commit_graph_write_completes_while_an_inactive_job_is_still_parked()
    {
        var cli = new RecordingGitCliService();
        var writer = new GraphBackgroundWriter(cli);
        await using var ctx = _host.Open(_repo.Path);

        var headlessStarted = new TaskCompletionSource();
        var headlessGate = new TaskCompletionSource();
        writer.EnqueueInactiveTab(ctx, async ct =>
        {
            headlessStarted.SetResult();
            await headlessGate.Task.WaitAsync(ct); // parked indefinitely, holding only _headlessSerial
        });

        await headlessStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // With the headless job still parked, a commit-graph write must still go through — it
        // only ever needs _serial, which the headless job never touches.
        await writer.EnsureCurrentAsync(ctx).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(cli.Requests);
    }

    [Fact]
    public async Task EnqueueInactiveTab_runs_jobs_one_at_a_time_in_enqueue_order()
    {
        var writer = new GraphBackgroundWriter(new RecordingGitCliService());
        // Two distinct stores — EnqueueInactiveTab de-dupes by store, so
        // this ordering test must not enqueue the same store twice.
        await using var ctx1 = _host.Open(_repo.Path);
        await using var ctx2 = OpenOtherTab();

        var order = new List<int>();
        var sync = new object();
        var gate1 = new TaskCompletionSource();

        writer.EnqueueInactiveTab(ctx1, async ct =>
        {
            lock (sync) order.Add(1);
            await gate1.Task.WaitAsync(ct); // job 1 blocks until released
            lock (sync) order.Add(2);
        });
        writer.EnqueueInactiveTab(ctx2, ct =>
        {
            lock (sync) order.Add(3); // must run only after job 1 finishes
            return Task.CompletedTask;
        });

        await Task.Delay(200);
        lock (sync) Assert.Equal([1], order); // job 2 hasn't started — job 1 is still blocked

        gate1.SetResult();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (sync) if (order.Count == 3) break;
            await Task.Delay(20);
        }

        lock (sync) Assert.Equal([1, 2, 3], order);
    }

    [Fact]
    public async Task EnqueueInactiveTab_one_jobs_failure_does_not_stall_the_rest()
    {
        var writer = new GraphBackgroundWriter(new RecordingGitCliService());
        // Two distinct stores — see the ordering test above for why.
        await using var ctx1 = _host.Open(_repo.Path);
        await using var ctx2 = OpenOtherTab();

        var secondRan = new TaskCompletionSource();
        writer.EnqueueInactiveTab(ctx1, _ => throw new InvalidOperationException("boom"));
        writer.EnqueueInactiveTab(ctx2, ct => { secondRan.SetResult(); return Task.CompletedTask; });

        await secondRan.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EnqueueInactiveTab_waits_for_the_gate_before_running()
    {
        var fakeGate = new BlockingGate();
        var writer = new GraphBackgroundWriter(new RecordingGitCliService(), fakeGate);
        await using var ctx = _host.Open(_repo.Path);

        var ran = new TaskCompletionSource();
        writer.EnqueueInactiveTab(ctx, ct => { ran.SetResult(); return Task.CompletedTask; });

        await Task.Delay(200);
        Assert.False(ran.Task.IsCompleted); // gate closed — job must not have started

        fakeGate.Open();
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EnqueueInactiveTab_a_context_already_queued_is_not_queued_again()
    {
        var writer = new GraphBackgroundWriter(new RecordingGitCliService());
        await using var ctxA = _host.Open(_repo.Path);
        await using var ctxB = OpenOtherTab();

        var blockA = new TaskCompletionSource();
        writer.EnqueueInactiveTab(ctxA, async ct => await blockA.Task.WaitAsync(ct));

        await Task.Delay(150); // let A start running and occupy the drain loop

        var bRuns = 0;
        writer.EnqueueInactiveTab(ctxB, ct => { Interlocked.Increment(ref bRuns); return Task.CompletedTask; });
        // ctxB's job is still queued behind A (not yet running) — this second call must be a no-op.
        writer.EnqueueInactiveTab(ctxB, ct => { Interlocked.Increment(ref bRuns); return Task.CompletedTask; });

        blockA.SetResult();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Volatile.Read(ref bRuns) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        await Task.Delay(200); // give a wrongly-duplicated second run a chance to also fire

        Assert.Equal(1, bRuns);
    }

    /// <summary>Local fake — not <see cref="RecordingGitCliService"/> in <c>Fakes.cs</c> — because
    /// it also records the <see cref="CancellationToken"/>
    /// each call receives, and can optionally block mid-call to simulate a long-running write.</summary>
    private sealed class TokenRecordingGitCliService(TaskCompletionSource? blockUntil = null) : IGitCliService
    {
        public List<GitCliRequest> Requests { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public int Completed { get; private set; }

        public bool IsGitAvailable => true;

        public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
            => RunAsync(request, progress: null, onStdoutLine: null, ct);

        public async Task<GitCliResult> RunAsync(
            GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default)
        {
            Requests.Add(request);
            Tokens.Add(ct);
            if (blockUntil is not null)
                await blockUntil.Task;
            Completed++;
            return new GitCliResult(0, "", "", GitErrorKind.None, TimeSpan.Zero);
        }
    }

    private sealed class BlockingGate : IBackgroundWorkGate
    {
        private TaskCompletionSource _tcs = new();
        public bool IsOpen => _tcs.Task.IsCompleted;
        public Task WaitUntilOpenAsync(CancellationToken ct = default) => _tcs.Task.WaitAsync(ct);
        public void ReportGraphSettled(RepositoryContext context) { }
        public void Open() => _tcs.TrySetResult();
    }

    private sealed class FocusTrackingGate : IBackgroundWorkGate
    {
        private readonly TaskCompletionSource _tcs = new();
        public bool IsOpen => _tcs.Task.IsCompleted;
        public RepositoryContext? Focused { get; set; }
        public Task WaitUntilOpenAsync(CancellationToken ct = default) => _tcs.Task.WaitAsync(ct);
        public void ReportGraphSettled(RepositoryContext context) { }
        public void Open() => _tcs.TrySetResult();
    }

    [Fact]
    public async Task EnqueueInactiveTab_skips_a_job_whose_tab_is_focused_when_it_starts()
    {
        var gate = new FocusTrackingGate();
        var writer = new GraphBackgroundWriter(new RecordingGitCliService(), gate);
        await using var ctx = _host.Open(_repo.Path);

        var ran = false;
        writer.EnqueueInactiveTab(ctx, ct => { ran = true; return Task.CompletedTask; });
        gate.Focused = ctx;
        gate.Open();

        // The job is never cancelled here: it leaves the queue because the start-time focus check
        // skips it, and a later cancel for the tab has nothing left to wait for.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (writer.HasInactiveJob(ctx) && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.False(writer.HasInactiveJob(ctx));
        Assert.False(ran);
        await writer.CancelInactiveTabAsync(ctx).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EnqueueInactiveTab_still_runs_for_an_unfocused_tab()
    {
        var gate = new FocusTrackingGate();
        var writer = new GraphBackgroundWriter(new RecordingGitCliService(), gate);
        await using var focused = _host.Open(_repo.Path);
        await using var other = OpenOtherTab();
        gate.Focused = focused;
        gate.Open();

        var ran = new TaskCompletionSource();
        writer.EnqueueInactiveTab(other, ct => { ran.SetResult(); return Task.CompletedTask; });

        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task HasInactiveJob_covers_queued_and_running_then_clears()
    {
        var gate = new BlockingGate();
        var writer = new GraphBackgroundWriter(new RecordingGitCliService(), gate);
        await using var ctx = _host.Open(_repo.Path);
        Assert.False(writer.HasInactiveJob(ctx));

        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        writer.EnqueueInactiveTab(ctx, async ct => { started.SetResult(); await release.Task; });

        Assert.True(writer.HasInactiveJob(ctx)); // queued behind a closed gate

        gate.Open();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(writer.HasInactiveJob(ctx)); // running

        release.SetResult();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (writer.HasInactiveJob(ctx) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.False(writer.HasInactiveJob(ctx));
    }

    [Fact]
    public async Task CancelInactiveTab_drops_a_queued_job_without_waiting_for_the_one_running()
    {
        var writer = new GraphBackgroundWriter(new RecordingGitCliService());
        await using var running = _host.Open(_repo.Path);
        await using var queued = OpenOtherTab();
        await using var after = OpenOtherTab();

        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var runningCancelled = false;
        writer.EnqueueInactiveTab(running, async ct =>
        {
            started.SetResult();
            await release.Task;
            runningCancelled = ct.IsCancellationRequested;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var queuedRan = false;
        var afterRan = new TaskCompletionSource();
        writer.EnqueueInactiveTab(queued, _ => { queuedRan = true; return Task.CompletedTask; });
        writer.EnqueueInactiveTab(after, _ => { afterRan.SetResult(); return Task.CompletedTask; });

        // Completes while the other tab's job still runs, and the tab no longer counts as having one.
        await writer.CancelInactiveTabAsync(queued).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(writer.HasInactiveJob(queued));
        Assert.True(writer.HasInactiveJob(running));

        // A later enqueue for the same tab is not swallowed by the de-dupe.
        writer.EnqueueInactiveTab(queued, _ => { queuedRan = true; return Task.CompletedTask; });
        Assert.True(writer.HasInactiveJob(queued));
        await writer.CancelInactiveTabAsync(queued).WaitAsync(TimeSpan.FromSeconds(1));

        release.SetResult();
        await afterRan.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(queuedRan);
        Assert.False(runningCancelled);
    }
}
