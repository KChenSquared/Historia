namespace Historia.Tests;

/// <summary>AF6 — the auto-fetch schedule, over a real <see cref="WorkspaceService"/> with
/// tiny temp repos, a manual delay pump, and a fake host (no real git fetch or graph paint).</summary>
[Trait("tier", "git")]
public sealed class AutoFetchServiceTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];
    private WorkspaceService? _ws;
    private AutoFetchService? _svc;

    private WorkspaceService NewWorkspace()
    {
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var recents = new RecentRepositoryService(_host.Settings);
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        var identity = new RepoIdentityService(cli);
        return _ws = new WorkspaceService(_host.Provider, cli, recents, profiles, identity);
    }

    private TestRepositoryBuilder Repo(string file)
    {
        var b = TestRepositoryBuilder.FromTemplate($"autofetch-repo-{file}", tpl =>
        {
            tpl.WriteFile(file, "x\n").Stage();
            tpl.Commit("c");
        });
        _repos.Add(b);
        return b;
    }

    private static async Task WaitUntil(Func<bool> cond, string what, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return;
            await Task.Delay(20);
        }
        throw new Xunit.Sdk.XunitException($"Timed out waiting for: {what}");
    }

    // ── positional cursor ──

    [Fact(Timeout = 60000)]
    public async Task No_fetch_runs_when_restored_repos_finish_loading()
    {
        var ws = NewWorkspace();
        await ws.OpenAsync(Repo("a.txt").Path);
        await ws.OpenAsync(Repo("b.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);
        _svc.Start();

        await delay.WaitForPending("initial idle");
        Assert.Empty(host.Snapshot());
        Assert.Single(delay.Requested);
        Assert.Equal(AutoFetchService.InitialIdle, delay.Requested[0]);
    }

    [Fact(Timeout = 60000)]
    public async Task Loop_fetches_the_repo_at_each_slot()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);
        var r2 = await ws.OpenAsync(Repo("b.txt").Path);
        var r3 = await ws.OpenAsync(Repo("c.txt").Path); // focused

        var host = new FakeHost();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);
        _svc.Start();

        // Cycle: 5-min idle, then slot 0, 1 min, slot 1, 1 min, slot 2.
        await delay.WaitForPending("initial idle");
        Assert.Equal(AutoFetchService.InitialIdle, delay.Requested[^1]);
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == 1, "slot 0 fetch");
        Assert.Same(r1, host.Snapshot()[0]);

        await delay.WaitForPending("step to slot 1");
        Assert.Equal(AutoFetchService.PerRepoStep, delay.Requested[^1]);
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == 2, "slot 1 fetch");
        Assert.Same(r2, host.Snapshot()[1]);

        await delay.WaitForPending("step to slot 2");
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == 3, "slot 2 fetch");
        Assert.Same(r3, host.Snapshot()[2]);
    }

    [Fact(Timeout = 60000)]
    public async Task Reordering_a_just_fetched_tab_forward_refetches_it_and_skips_its_replacement()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);
        var r2 = await ws.OpenAsync(Repo("b.txt").Path);
        var r3 = await ws.OpenAsync(Repo("c.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);
        _svc.Start();

        await delay.WaitForPending("initial idle");
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == 1, "slot 0");
        Assert.Same(r1, host.Snapshot()[^1]); // slot 0 = r1

        await delay.WaitForPending("step to slot 1");
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == 2, "slot 1");
        Assert.Same(r2, host.Snapshot()[^1]); // slot 1 = r2

        // The user drags the just-fetched r2 into the last slot: order is now r1, r3, r2.
        // Same count -> no restart. The cursor advances to slot 2, which is now r2 again.
        ws.Move(r2, 2);
        await delay.WaitForPending("step to slot 2");
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == 3, "slot 2");
        Assert.Same(r2, host.Snapshot()[^1]); // r2 fetched again

        var thisCycle = host.Snapshot().ToArray();
        Assert.Equal(new[] { r1, r2, r2 }, thisCycle);
        Assert.DoesNotContain(r3, thisCycle); // r3 slid into slot 1, already passed
    }

    // ── add / close restart ──

    [Fact(Timeout = 60000)]
    public async Task Opening_a_tab_restarts_the_in_progress_cycle()
    {
        var ws = NewWorkspace();
        await ws.OpenAsync(Repo("a.txt").Path);
        await ws.OpenAsync(Repo("b.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);
        _svc.Start();

        await delay.WaitForPending("initial idle");
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == 1, "slot 0");
        await delay.WaitForPending("step to slot 1");
        Assert.Equal(1, delay.CountOf(AutoFetchService.InitialIdle));

        // A third tab appears mid-cycle -> restart with a fresh 5-min idle.
        await ws.OpenAsync(Repo("c.txt").Path);
        await WaitUntil(() => delay.CountOf(AutoFetchService.InitialIdle) == 2, "cycle restarts with a fresh idle");
    }

    [Fact(Timeout = 60000)]
    public async Task Closing_a_tab_restarts_the_in_progress_cycle()
    {
        var ws = NewWorkspace();
        await ws.OpenAsync(Repo("a.txt").Path);
        await ws.OpenAsync(Repo("b.txt").Path);
        var r3 = await ws.OpenAsync(Repo("c.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);
        _svc.Start();

        await delay.WaitForPending("initial idle");
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == 1, "slot 0");
        await delay.WaitForPending("step");
        Assert.Equal(1, delay.CountOf(AutoFetchService.InitialIdle));

        await ws.CloseAsync(r3);
        await WaitUntil(() => delay.CountOf(AutoFetchService.InitialIdle) == 2, "cycle restarts on close");
    }

    [Fact(Timeout = 60000)]
    public async Task Opening_a_tab_mid_fetch_lets_the_fetch_finish_then_restarts()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);
        await ws.OpenAsync(Repo("b.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);
        _svc.Start();

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BlockNext = release;
        await delay.WaitForPending("initial idle");
        delay.Fire();
        await host.BlockedFetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // A new tab mid-fetch: the fetch must not be cancelled (cancelling kills git).
        await ws.OpenAsync(Repo("c.txt").Path);
        await Task.Delay(100);
        Assert.False(host.BlockedFetchToken.IsCancellationRequested);
        Assert.Equal(1, delay.CountOf(AutoFetchService.InitialIdle));

        release.SetResult();
        await WaitUntil(() => host.Snapshot().Count == 1, "the interrupted fetch completes");
        Assert.Same(r1, host.Snapshot()[^1]);
        await WaitUntil(() => delay.CountOf(AutoFetchService.InitialIdle) == 2, "then the cycle restarts");
    }

    [Fact(Timeout = 60000)]
    public async Task Stopping_still_cancels_an_in_flight_fetch()
    {
        var ws = NewWorkspace();
        await ws.OpenAsync(Repo("a.txt").Path);

        var host = new FakeHost { BlockNext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);
        _svc.Start();
        await delay.WaitForPending("initial idle");
        delay.Fire();
        await host.BlockedFetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _svc.Stop();
        Assert.True(host.BlockedFetchToken.IsCancellationRequested);
    }

    [Fact(Timeout = 60000)]
    public async Task A_repo_busy_with_a_user_operation_is_skipped_until_a_later_cycle()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);
        var r2 = await ws.OpenAsync(Repo("b.txt").Path); // focused

        var host = new FakeHost();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);
        _svc.Start();

        Assert.True(r1.TryBeginNetworkOperation(new NetworkOperationInfo(NetworkOperationOwner.User, "Pull"), out var userOp));
        await delay.WaitForPending("initial idle");
        delay.Fire();

        // Slot 0 (r1) is skipped: nothing fetched, the loop moves on to the next step.
        await delay.WaitForPending("step to slot 1");
        Assert.Empty(host.Snapshot());
        Assert.Equal(AutoFetchTabState.PendingThisCycle, _svc.GetTabState(r1));

        userOp.Dispose();
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == 1, "slot 1");
        Assert.Same(r2, host.Snapshot()[^1]);

        await delay.WaitForPending("trailing step");
        delay.Fire();
        await delay.WaitForPending("next idle");
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == 2, "r1 fetched next cycle");
        Assert.Same(r1, host.Snapshot()[^1]);
    }

    [Fact(Timeout = 60000)]
    public async Task A_pure_reorder_does_not_restart_the_cycle()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);
        await ws.OpenAsync(Repo("b.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);
        _svc.Start();

        await delay.WaitForPending("initial idle");
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == 1, "slot 0");
        await delay.WaitForPending("step to slot 1");

        ws.Move(r1, 1); // same count
        await Task.Delay(300);
        Assert.Equal(1, delay.CountOf(AutoFetchService.InitialIdle)); // still the same cycle
    }

    // ── Gate released before the refresh ──

    [Fact(Timeout = 60000)]
    public async Task The_network_gate_is_free_while_the_post_fetch_refresh_runs()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);

        var gateFreeDuringRefresh = false;
        var host = new FakeHost { RefreshGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        host.DuringRefresh = ctx =>
        {
            if (ctx.TryBeginNetworkOperation(new NetworkOperationInfo(NetworkOperationOwner.User, "Pull"), out var userOp))
            {
                gateFreeDuringRefresh = true;
                userOp.Dispose();
            }
        };
        var graph = new RecordingGraphLayoutService();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay, graph);
        _svc.Start();

        await delay.WaitForPending("initial idle");
        delay.Fire();

        await host.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(gateFreeDuringRefresh);
        Assert.Same(r1, Assert.Single(host.Snapshot()));
        Assert.Empty(graph.Snapshot()); // the graph refresh waits for the state refresh

        host.RefreshGate!.SetResult();
        await WaitUntil(() => graph.Snapshot().Count == 1, "commit-graph refresh after the state refresh");
    }

    [Fact(Timeout = 60000)]
    public async Task A_tag_conflict_refreshes_after_the_gate_is_released_and_skips_the_graph_refresh()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);

        var gateFreeDuringRefresh = false;
        var host = new FakeHost
        {
            FailWith = new HistoriaGitException(GitErrorKind.TagConflict, "tag moved"),
            RefreshGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        host.DuringRefresh = ctx =>
        {
            if (ctx.TryBeginNetworkOperation(new NetworkOperationInfo(NetworkOperationOwner.User, "Pull"), out var userOp))
            {
                gateFreeDuringRefresh = true;
                userOp.Dispose();
            }
        };
        var graph = new RecordingGraphLayoutService();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay, graph);
        _svc.Start();

        await delay.WaitForPending("initial idle");
        delay.Fire();

        await host.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(gateFreeDuringRefresh);
        Assert.Same(r1, Assert.Single(host.RefreshedSnapshot()));

        host.RefreshGate!.SetResult();
        await delay.WaitForPending("trailing step");
        Assert.Empty(graph.Snapshot());
        Assert.Equal(AutoFetchTabState.PendingThisCycle, _svc.GetTabState(r1));
    }

    [Fact(Timeout = 60000)]
    public async Task The_gate_is_held_for_the_fetch_itself()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new FakeHost { BlockNext = release };
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);
        _svc.Start();

        await delay.WaitForPending("initial idle");
        delay.Fire();
        await host.BlockedFetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(r1.TryBeginNetworkOperation(new NetworkOperationInfo(NetworkOperationOwner.User, "Pull"), out _));
        Assert.Empty(host.RefreshedSnapshot());

        release.SetResult();
        await WaitUntil(() => host.RefreshedSnapshot().Count == 1, "refresh after the fetch");
    }

    [Fact(Timeout = 60000)]
    public async Task A_failed_fetch_skips_the_post_fetch_refresh()
    {
        var ws = NewWorkspace();
        await ws.OpenAsync(Repo("a.txt").Path);
        await ws.OpenAsync(Repo("b.txt").Path);

        var host = new FakeHost { Fail = true };
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);
        _svc.Start();

        await delay.WaitForPending("loop idle");
        delay.Fire();
        await delay.WaitForPending("step to slot 1 after failed slot 0 fetch");
        Assert.Empty(host.RefreshedSnapshot());
    }

    // ── Commit-graph refresh after a successful fetch ──

    [Fact(Timeout = 60000)]
    public async Task A_successful_fetch_triggers_exactly_one_commit_graph_refresh_for_that_repo()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);

        var host = new FakeHost();
        var graph = new RecordingGraphLayoutService();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay, graph);
        _svc.Start();

        await delay.WaitForPending("initial idle");
        delay.Fire();

        await WaitUntil(() => host.Snapshot().Count == 1, "slot 0 fetch");
        await WaitUntil(() => graph.Snapshot().Count == 1, "commit-graph refresh after the fetch");
        Assert.Same(r1, graph.Snapshot()[0]);

        await Task.Delay(200); // give a wrongly-duplicated refresh a chance to also fire
        Assert.Single(graph.Snapshot());
    }

    [Fact(Timeout = 60000)]
    public async Task A_failed_fetch_triggers_no_commit_graph_refresh()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);
        var r2 = await ws.OpenAsync(Repo("b.txt").Path); // r2 focused, r1 inactive

        var host = new FakeHost { Fail = true };
        var graph = new RecordingGraphLayoutService();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay, graph);
        _svc.Start();

        await delay.WaitForPending("loop idle");
        Assert.Empty(host.Snapshot());
        delay.Fire(); // step to slot 0 (r1, inactive)
        await delay.WaitForPending("step to slot 1 after failed slot 0 fetch");
        Assert.Empty(graph.Snapshot());
        Assert.Empty(graph.EnqueuedSnapshot());
    }

    [Fact(Timeout = 60000)]
    public async Task An_auto_fetch_of_an_inactive_tab_queues_headless_disk_caching_for_it()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);
        var r2 = await ws.OpenAsync(Repo("b.txt").Path); // r2 focused, r1 inactive

        var host = new FakeHost();
        var graph = new RecordingGraphLayoutService();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay, graph);
        _svc.Start();

        // Cycle: initial idle -> slot 0 (r1, inactive)
        await delay.WaitForPending("initial idle");
        delay.Fire();

        await WaitUntil(() => graph.EnqueuedSnapshot().Count == 1, "headless caching queued for r1");
        var (context, query) = graph.EnqueuedSnapshot()[0];
        Assert.Same(r1, context);
        Assert.Equal(WorkspaceService.TabGraphQuery, query);
    }

    [Fact(Timeout = 60000)]
    public async Task The_focused_tab_is_never_queued_for_headless_disk_caching()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);
        var r2 = await ws.OpenAsync(Repo("b.txt").Path); // r2 focused, r1 inactive

        var host = new FakeHost();
        var graph = new RecordingGraphLayoutService();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay, graph);
        _svc.Start();

        await delay.WaitForPending("initial idle");
        delay.Fire(); // slot 0 (r1)

        await delay.WaitForPending("step to slot 1");
        delay.Fire(); // slot 1 (r2, focused)

        await WaitUntil(() => host.Snapshot().Count == 2, "slot 1 fetch on r2");
        Assert.Same(r2, host.Snapshot()[1]);

        // r1 was queued, but r2 (focused) is never queued
        var enqueued = graph.EnqueuedSnapshot();
        Assert.DoesNotContain(enqueued, item => item.Context == r2);
    }

    [Fact(Timeout = 60000)]
    public async Task A_throwing_headless_enqueue_never_stops_the_loop()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);
        var r2 = await ws.OpenAsync(Repo("b.txt").Path); // r2 focused, r1 inactive

        var host = new FakeHost();
        var graph = new RecordingGraphLayoutService { ThrowOnEnqueue = true };
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay, graph);
        _svc.Start();

        await delay.WaitForPending("initial idle");
        delay.Fire(); // slot 0 (r1, throws on enqueue)

        // Commit graph refresh for r1 still recorded
        await WaitUntil(() => graph.Snapshot().Count >= 1, "commit-graph refresh for r1");
        Assert.Same(r1, graph.Snapshot()[0]);

        // Loop still reaches slot 1 (r2)
        await delay.WaitForPending("step to slot 1");
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == 2, "slot 1 fetch on r2");
        Assert.Same(r2, host.Snapshot()[1]);
    }

    // ── gating ──

    [Fact(Timeout = 60000)]
    public async Task Disabling_the_setting_mid_loop_stops_further_fetches()
    {
        var ws = NewWorkspace();
        await ws.OpenAsync(Repo("a.txt").Path);
        await ws.OpenAsync(Repo("b.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        var settings = Settings(true);
        _svc = new AutoFetchService(ws, host, settings, delay.Delay);
        _svc.Start();
        await delay.WaitForPending("initial idle");

        await settings.UpdateAsync(s => s with { AutoFetchEnabled = false });
        Assert.False(_svc.IsEnabled);

        var count = host.Snapshot().Count;
        delay.FireAll();
        await Task.Delay(400);
        Assert.Equal(count, host.Snapshot().Count); // no fetches after disable
    }

    // ── start conditions with no open repo ──

    [Fact(Timeout = 60000)]
    public async Task Cold_start_then_open_waits_the_idle_before_the_first_fetch()
    {
        var ws = NewWorkspace();
        var host = new FakeHost();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);
        _svc.Start();

        var r1 = await ws.OpenAsync(Repo("a.txt").Path);

        await delay.WaitForPending("loop idle");
        Assert.Equal(AutoFetchService.InitialIdle, delay.Requested[^1]);
        Assert.Empty(host.Snapshot());

        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == 1, "slot 0 fetch");
        Assert.Same(r1, host.Snapshot()[0]);
    }

    [Fact(Timeout = 60000)]
    public async Task With_open_repos_the_loop_waits_for_every_repo_to_fully_load()
    {
        var ws = NewWorkspace();
        await ws.OpenAsync(Repo("a.txt").Path);
        await ws.OpenAsync(Repo("b.txt").Path);

        var host = new FakeHost { LoadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);
        _svc.Start();

        await Task.Delay(300);
        Assert.Empty(host.Snapshot());            // blocked on the load gate
        Assert.Equal(0, delay.PendingCount);

        host.LoadGate!.SetResult();
        await delay.WaitForPending("loop idle");
        Assert.Empty(host.Snapshot());
    }

    // ── dev visuals: per-tab state ──

    [Fact(Timeout = 60000)]
    public async Task Tab_state_goes_not_started_then_pending_then_fetched_then_resets_on_restart()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);
        var r2 = await ws.OpenAsync(Repo("b.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);

        Assert.Equal(AutoFetchTabState.NotStarted, _svc.GetTabState(r1)); // loop not started

        _svc.Start();
        await delay.WaitForPending("initial idle"); // loop has entered its first cycle
        await WaitUntil(() => _svc.GetTabState(r1) == AutoFetchTabState.PendingThisCycle, "r1 pending");
        Assert.Equal(AutoFetchTabState.PendingThisCycle, _svc.GetTabState(r2));

        delay.Fire(); // → slot 0 (r1)
        await WaitUntil(() => _svc.GetTabState(r1) == AutoFetchTabState.FetchedThisCycle, "r1 fetched");
        Assert.Equal(AutoFetchTabState.PendingThisCycle, _svc.GetTabState(r2));

        await delay.WaitForPending("step to slot 1");
        delay.Fire(); // → slot 1 (r2)
        await WaitUntil(() => _svc.GetTabState(r2) == AutoFetchTabState.FetchedThisCycle, "r2 fetched");

        // A new tab restarts the cycle → everything back to pending, loop still running.
        await ws.OpenAsync(Repo("c.txt").Path);
        await WaitUntil(() => _svc.GetTabState(r1) == AutoFetchTabState.PendingThisCycle, "r1 reset to pending");
        Assert.Equal(AutoFetchTabState.PendingThisCycle, _svc.GetTabState(r2));
    }

    [Fact(Timeout = 60000)]
    public async Task Disabling_returns_tab_state_to_not_started()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        var settings = Settings(true);
        _svc = new AutoFetchService(ws, host, settings, delay.Delay);
        _svc.Start();
        await delay.WaitForPending("initial idle");
        await WaitUntil(() => _svc.GetTabState(r1) == AutoFetchTabState.PendingThisCycle, "r1 pending");

        await settings.UpdateAsync(s => s with { AutoFetchEnabled = false });
        await WaitUntil(() => _svc.GetTabState(r1) == AutoFetchTabState.NotStarted, "r1 back to not-started");
    }

    [Fact(Timeout = 60000)]
    public async Task Trimmer_runs_after_the_idle_and_before_the_first_fetch()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        var trimmer = new FakeTrimmer(host);
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay, logTrimmer: trimmer);
        _svc.Start();

        await delay.WaitForPending("initial idle");
        Assert.Equal(0, trimmer.CallCount);

        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count == 1, "slot 0 fetch");
        Assert.Equal(1, trimmer.CallCount);
        Assert.Equal(0, trimmer.HostFetchCounts[0]);
    }

    [Fact(Timeout = 60000)]
    public async Task Restart_before_the_idle_never_trims_and_each_full_cycle_trims_once()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        var trimmer = new FakeTrimmer(host);
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay, logTrimmer: trimmer);
        _svc.Start();

        // First cycle initial idle is pending; no trim yet
        await delay.WaitForPending("initial idle");
        Assert.Equal(0, trimmer.CallCount);

        // Opening a second repo before the idle fires restarts the cycle; still no trim
        var r2 = await ws.OpenAsync(Repo("b.txt").Path);
        await delay.WaitForPending("initial idle after restart");
        Assert.Equal(0, trimmer.CallCount);

        // Fire idle: trim runs (count 1), then slot 0 fetch
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count >= 1, "slot 0 fetch");
        Assert.Equal(1, trimmer.CallCount);

        // Advance slot 1 fetch
        await delay.WaitForPending("step to slot 1");
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count >= 2, "slot 1 fetch");

        // Trailing step
        await delay.WaitForPending("trailing step");
        delay.Fire();

        // Next cycle begins: initial idle is pending; count is still 1
        await delay.WaitForPending("next cycle initial idle");
        Assert.Equal(1, trimmer.CallCount);

        // Fire next cycle idle: second trim runs
        delay.Fire();
        await WaitUntil(() => trimmer.CallCount == 2, "next cycle trim");
        Assert.Equal(2, trimmer.CallCount);
    }

    [Fact(Timeout = 60000)]
    public async Task Restart_during_the_trim_returns_to_the_idle()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trimmer = new FakeTrimmer(host) { Gate = gate };
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay, logTrimmer: trimmer);
        _svc.Start();

        await delay.WaitForPending("initial idle");
        delay.Fire();

        // Wait until trimmer is called and blocked on gate
        await WaitUntil(() => trimmer.CallCount == 1, "trimmer entered");

        // Open a repo while trim is blocked: cancels cycle token
        var r2 = await ws.OpenAsync(Repo("b.txt").Path);

        // Release trimmer gate
        gate.SetResult();

        // No per-repo fetch should happen; returns to initial idle
        await delay.WaitForPending("restarted initial idle");
        Assert.Empty(host.Snapshot());
    }

    [Fact(Timeout = 60000)]
    public async Task Trimmer_cancellation_unrelated_to_the_run_does_not_stop_the_loop()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        var trimmer = new FakeTrimmer(host) { ThrowCanceled = true };
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay, logTrimmer: trimmer);
        _svc.Start();

        await delay.WaitForPending("initial idle");
        delay.Fire();

        await WaitUntil(() => host.Snapshot().Count >= 1, "slot 0 fetch after canceled trimmer", 10000);
        Assert.Contains(r1, host.Snapshot());
    }

    [Fact(Timeout = 60000)]
    public async Task Throwing_trimmer_does_not_stop_the_loop()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        var trimmer = new FakeTrimmer(host) { ShouldThrow = true };
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay, logTrimmer: trimmer);
        _svc.Start();

        await delay.WaitForPending("initial idle");
        Assert.Equal(0, trimmer.CallCount);

        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count >= 1, "slot 0 fetch after throwing trimmer");
        Assert.Equal(1, trimmer.CallCount);
        Assert.Contains(r1, host.Snapshot());
    }

    [Fact(Timeout = 60000)]
    public async Task Trimming_does_not_occur_while_autofetch_is_disabled()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        var trimmer = new FakeTrimmer();
        _svc = new AutoFetchService(ws, host, Settings(false), delay.Delay, logTrimmer: trimmer);
        _svc.Start();

        await Task.Delay(100);
        Assert.Equal(0, trimmer.CallCount);
        Assert.Equal(0, delay.PendingCount);
    }

    [Fact(Timeout = 60000)]
    public async Task No_trimmer_keeps_existing_behavior()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(Repo("a.txt").Path);

        var host = new FakeHost();
        var delay = new ManualDelayer();
        _svc = new AutoFetchService(ws, host, Settings(true), delay.Delay);
        _svc.Start();

        await delay.WaitForPending("initial idle");
        delay.Fire();
        await WaitUntil(() => host.Snapshot().Count >= 1, "slot 0 fetch with null trimmer");
        Assert.Contains(r1, host.Snapshot());
    }

    private FakeSettingsService Settings(bool enabled) =>
        new(new AppSettings { AutoFetchEnabled = enabled });

    public async ValueTask DisposeAsync()
    {
        if (_svc is not null)
            await _svc.DisposeAsync();
        if (_ws is not null)
            await _ws.DisposeAsync();
        await _host.DisposeAsync();
        foreach (var r in _repos)
            r.Dispose();
    }

    // ── test doubles ──

    private sealed class FakeTrimmer : ILogTrimmer
    {
        private readonly object _g = new();
        private readonly List<(DateTime Time, int HostFetchCount)> _calls = [];
        private readonly FakeHost? _host;
        public int CallCount { get { lock (_g) return _calls.Count; } }
        public bool ShouldThrow { get; set; }
        public bool ThrowCanceled { get; set; }
        public TaskCompletionSource? Gate { get; set; }

        public FakeTrimmer(FakeHost? host = null)
        {
            _host = host;
        }

        public IReadOnlyList<int> HostFetchCounts
        {
            get { lock (_g) return _calls.Select(c => c.HostFetchCount).ToList(); }
        }

        public async Task TrimAsync(CancellationToken ct = default)
        {
            lock (_g) _calls.Add((DateTime.UtcNow, _host?.Snapshot().Count ?? 0));

            if (Gate is not null)
                await Gate.Task;

            if (ThrowCanceled)
                throw new OperationCanceledException();

            if (ShouldThrow)
                throw new InvalidOperationException("simulated trimmer failure");
        }
    }

    private sealed class FakeHost : IAutoFetchHost
    {
        private readonly object _g = new();
        private readonly List<RepositoryContext> _fetched = [];
        private readonly List<RepositoryContext> _refreshed = [];

        public TaskCompletionSource? LoadGate { get; init; }

        /// <summary>When set, every <see cref="FetchAsync"/> call throws instead of
        /// succeeding — used to prove a failed fetch triggers no commit-graph refresh.</summary>
        public bool Fail { get; set; }

        /// <summary>When set, every <see cref="FetchAsync"/> call throws this exception.</summary>
        public Exception? FailWith { get; init; }

        /// <summary>When set, the next <see cref="FetchAsync"/> records its token, signals
        /// <see cref="BlockedFetchStarted"/> and waits for this to complete before succeeding.</summary>
        public TaskCompletionSource? BlockNext { get; set; }

        /// <summary>When set, <see cref="RefreshAfterFetchAsync"/> signals
        /// <see cref="RefreshStarted"/>, runs <see cref="DuringRefresh"/>, then waits for this to
        /// complete.</summary>
        public TaskCompletionSource? RefreshGate { get; init; }

        public TaskCompletionSource RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Runs inside <see cref="RefreshAfterFetchAsync"/>, before it waits on
        /// <see cref="RefreshGate"/>.</summary>
        public Action<RepositoryContext>? DuringRefresh { get; set; }

        public TaskCompletionSource BlockedFetchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken BlockedFetchToken { get; private set; }

        public Task WhenFullyLoadedAsync(RepositoryContext context, CancellationToken ct)
            => LoadGate is null ? Task.CompletedTask : LoadGate.Task.WaitAsync(ct);

        public async Task FetchAsync(RepositoryContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (FailWith is not null)
                throw FailWith;
            if (Fail)
                throw new InvalidOperationException("simulated fetch failure");
            if (BlockNext is { } block)
            {
                BlockNext = null;
                BlockedFetchToken = ct;
                BlockedFetchStarted.TrySetResult();
                await block.Task.WaitAsync(ct);
            }
            lock (_g)
                _fetched.Add(context);
        }

        public async Task RefreshAfterFetchAsync(RepositoryContext context, CancellationToken ct)
        {
            DuringRefresh?.Invoke(context);
            lock (_g)
                _refreshed.Add(context);
            RefreshStarted.TrySetResult();
            if (RefreshGate is not null)
                await RefreshGate.Task.WaitAsync(ct);
        }

        public List<RepositoryContext> Snapshot()
        {
            lock (_g)
                return [.. _fetched];
        }

        public List<RepositoryContext> RefreshedSnapshot()
        {
            lock (_g)
                return [.. _refreshed];
        }
    }

    /// <summary>Records every context <see cref="RefreshCommitGraphAfterFetch"/>
    /// and <see cref="EnqueueInactiveTabCaching"/> was called with — the rest of
    /// <see cref="IGraphLayoutService"/> is unused by <see cref="AutoFetchService"/> and throws if
    /// ever called.</summary>
    private sealed class RecordingGraphLayoutService : IGraphLayoutService
    {
        private readonly object _g = new();
        private readonly List<RepositoryContext> _refreshed = [];
        private readonly List<(RepositoryContext Context, GraphQuery Query)> _enqueued = [];

        public bool ThrowOnEnqueue { get; set; }

        public void RefreshCommitGraphAfterFetch(RepositoryContext context)
        {
            lock (_g)
                _refreshed.Add(context);
        }

        public void SweepDiskCache(IReadOnlyCollection<string> openRepositoryPaths) { }

        public void EnqueueInactiveTabCaching(RepositoryContext context, GraphQuery query)
        {
            if (ThrowOnEnqueue)
                throw new InvalidOperationException("simulated enqueue failure");
            lock (_g)
                _enqueued.Add((context, query));
        }

        public List<RepositoryContext> Snapshot()
        {
            lock (_g)
                return [.. _refreshed];
        }

        public List<(RepositoryContext Context, GraphQuery Query)> EnqueuedSnapshot()
        {
            lock (_g)
                return [.. _enqueued];
        }

        public Task<GraphLayoutResult> ComputeLayoutAsync(RepositoryContext context, GraphQuery query, int maxRows = 10000, CancellationToken ct = default)
            => throw new NotImplementedException();
        public GraphLayoutResult? TryOptimisticSnapshot(RepositoryContext context, GraphQuery query, int maxRows) => throw new NotImplementedException();
        public Task<GraphLayoutResult> ExtendLayoutAsync(RepositoryContext context, GraphLayoutResult existing, int additionalRows, CancellationToken ct = default, bool background = false)
            => throw new NotImplementedException();
        public Task<GraphLayoutResult> RecomputeAsync(RepositoryContext context, GraphQuery query, GraphLayoutResult previous, InvalidationReason reason, int maxRows = 10000, CancellationToken ct = default)
            => throw new NotImplementedException();
        public void Forget(RepositoryContext context) => throw new NotImplementedException();
        public Task Hibernate(RepositoryContext context) => throw new NotImplementedException();
        public int ClearPersistentCache() => throw new NotImplementedException();
        public Task RunHeadlessCacheAsync(RepositoryContext context, GraphQuery query, CancellationToken ct) => throw new NotImplementedException();
        public Task CancelInactiveTabCachingAsync(RepositoryContext context) => throw new NotImplementedException();
        public Task PrecacheClonedRepositoryAsync(RepositoryContext context, GraphQuery query, IProgress<GitProgress>? progress, CancellationToken ct) => throw new NotImplementedException();
    }

    /// <summary>A hand-pumped stand-in for the production <c>Task.Delay</c> seam: records every
    /// requested interval and completes them on command, honouring cancellation.</summary>
    private sealed class ManualDelayer
    {
        private readonly object _g = new();
        private readonly List<TimeSpan> _requested = [];
        private readonly List<(TaskCompletionSource Tcs, CancellationTokenRegistration Reg)> _pending = [];

        public IReadOnlyList<TimeSpan> Requested
        {
            get { lock (_g) return [.. _requested]; }
        }

        public int PendingCount
        {
            get { lock (_g) return _pending.Count(p => !p.Tcs.Task.IsCompleted); }
        }

        public int CountOf(TimeSpan span)
        {
            lock (_g) return _requested.Count(t => t == span);
        }

        public Task Delay(TimeSpan span, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reg = ct.Register(() => tcs.TrySetCanceled());
            lock (_g)
            {
                _requested.Add(span);
                _pending.Add((tcs, reg));
            }
            return tcs.Task;
        }

        public void Fire()
        {
            (TaskCompletionSource Tcs, CancellationTokenRegistration Reg)? item = null;
            lock (_g)
            {
                var idx = _pending.FindIndex(p => !p.Tcs.Task.IsCompleted);
                if (idx >= 0)
                {
                    item = _pending[idx];
                    _pending.RemoveAt(idx);
                }
            }
            if (item is null)
                throw new Xunit.Sdk.XunitException("Fire(): no pending delay");
            item.Value.Reg.Dispose();
            item.Value.Tcs.TrySetResult();
        }

        public void FireAll()
        {
            List<(TaskCompletionSource Tcs, CancellationTokenRegistration Reg)> items;
            lock (_g)
            {
                items = _pending.Where(p => !p.Tcs.Task.IsCompleted).ToList();
                _pending.Clear();
            }
            foreach (var p in items)
            {
                p.Reg.Dispose();
                p.Tcs.TrySetResult();
            }
        }

        public async Task WaitForPending(string what, int timeoutMs = 5000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (PendingCount > 0) return;
                await Task.Delay(20);
            }
            throw new Xunit.Sdk.XunitException($"Timed out waiting for a pending delay: {what}");
        }
    }
}
