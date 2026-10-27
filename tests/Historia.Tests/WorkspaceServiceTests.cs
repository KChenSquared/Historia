namespace Historia.Tests;

/// <summary>Shared host and helpers for the <see cref="WorkspaceService"/> suites. Split into
/// several classes so xunit runs them as parallel collections instead of one long sequence.</summary>
public abstract class WorkspaceServiceTestBase : IAsyncDisposable
{
    protected readonly MiniHost _host = new();
    protected RecentRepositoryService? _recents;

    protected WorkspaceService NewWorkspace(
        IRepoIdentityService? identity = null,
        IGitCliService? cli = null,
        IServiceProvider? provider = null)
    {
        cli ??= _host.Provider.GetRequiredService<IGitCliService>();
        provider ??= _host.Provider;
        var recents = _recents = new RecentRepositoryService(_host.Settings);
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        identity ??= new RepoIdentityService(cli);
        return new WorkspaceService(provider, cli, recents, profiles, identity);
    }

    protected static string Norm(string p) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)).ToLowerInvariant();

    protected static TestRepositoryBuilder Repo(string file) =>
        TestRepositoryBuilder.FromTemplate($"ws-repo-{file}", b =>
        {
            b.WriteFile(file, "x\n").Stage();
            b.Commit("c");
        });

    /// <summary>Overrides one service type while delegating everything else to a real provider —
    /// so <see cref="WorkspaceService"/>'s <c>IGraphLayoutService</c> can be swapped for a
    /// recording fake without standing up a whole second DI container.</summary>
    protected sealed class OverrideServiceProvider(IServiceProvider inner, Type overriddenType, object overriddenInstance)
        : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == overriddenType ? overriddenInstance : inner.GetService(serviceType);
    }

    /// <summary>A real nested git repo under <paramref name="owner"/>'s working directory —
    /// enough for <c>Repository.Discover</c> to find it, without needing real
    /// <c>git submodule add</c> registration (OpenSubmoduleAsync only cares about the on-disk
    /// working copy, not <c>.gitmodules</c>).</summary>
    protected static SubmoduleInfo MakeNestedSubmodule(RepositoryContext owner, string relativePath, string fileName)
    {
        var fullPath = Path.Combine(owner.RepositoryPath, relativePath);
        Directory.CreateDirectory(fullPath);
        LibGit2Sharp.Repository.Init(fullPath);
        using (var repo = new LibGit2Sharp.Repository(fullPath))
        {
            File.WriteAllText(Path.Combine(fullPath, fileName), "x");
            LibGit2Sharp.Commands.Stage(repo, "*");
            var sig = new LibGit2Sharp.Signature("Test User", "test@example.com", DateTimeOffset.Now);
            repo.Commit("c", sig, sig);
        }
        return new SubmoduleInfo
        {
            Name = relativePath,
            Path = relativePath,
            Url = fullPath,
            Status = SubmoduleStatus.Clean,
        };
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}

[Trait("tier", "git")]
public class WorkspaceServiceTests : WorkspaceServiceTestBase
{
    /// <summary>
    /// Re-opening an already-open repo raises <see cref="IWorkspaceService.FocusedRepositoryChanged"/>
    /// with the lock released, avoiding deadlock with UI thread marshaling.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Reopening_an_open_repo_does_not_raise_focus_event_under_the_lock()
    {
        var ws = NewWorkspace();
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");

        await ws.OpenAsync(r1.Path);
        await ws.OpenAsync(r2.Path);          // focus is now r2

        var otherThreadCouldRead = false;
        ws.FocusedRepositoryChanged += (_, _) =>
        {
            // A different thread reads state guarded by _sync. If the event fires while the
            // caller still holds _sync, this Wait times out — the deadlock, reproduced.
            var read = Task.Run(() => { _ = ws.OpenRepositories; _ = ws.Focused; });
            otherThreadCouldRead = read.Wait(TimeSpan.FromSeconds(5));
        };

        await ws.OpenAsync(r1.Path);          // already open → dup-guard → FocusInternal

        Assert.True(otherThreadCouldRead, "FocusedRepositoryChanged was raised while _sync was held");
        await ws.DisposeAsync();
    }

    // ── Clone URL validation ──

    [Theory]
    [InlineData("--upload-pack=calc.exe")]
    [InlineData("-oProxyCommand=calc.exe")]
    [InlineData("ext::sh -c calc.exe")]
    [InlineData("fd::1")]
    public async Task CloneAsync_rejects_urls_that_are_not_a_repository_address(string maliciousUrl)
    {
        var ws = NewWorkspace();
        var target = TestScratch.NewPath("historia-clone-test-");

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(
            () => ws.CloneAsync(maliciousUrl, target, new Historia.Services.Workspace.CloneOptions(), progress: null));

        Assert.False(Directory.Exists(target), "a rejected clone must never touch the filesystem");
        Assert.NotNull(ex);
        await ws.DisposeAsync();
    }

    /// <summary>ValidateCloneUrl delegates to CloneValidation.DescribeUrlProblem, which
    /// also rejects a URL with no repo-address shape at all (not just the security-motivated
    /// cases above) — confirms that delegation reaches the shape check too.</summary>
    [Fact]
    public async Task CloneAsync_rejects_a_url_with_no_repository_shape()
    {
        var ws = NewWorkspace();
        var target = TestScratch.NewPath("historia-clone-test-");

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(
            () => ws.CloneAsync("just some plain text", target, new Historia.Services.Workspace.CloneOptions(), progress: null));

        Assert.False(Directory.Exists(target));
        Assert.NotNull(ex);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task CloneAsync_still_clones_a_plain_local_path()
    {
        var ws = NewWorkspace();
        using var source = Repo("a.txt");
        var target = TestScratch.NewPath("historia-clone-test-");

        try
        {
            var ctx = await ws.CloneAsync(source.Path, target, new Historia.Services.Workspace.CloneOptions(), progress: null);
            Assert.True(Directory.Exists(Path.Combine(target, ".git")));
            // ws.DisposeAsync() below disposes every still-open context, ctx included — don't
            // dispose it a second time here.
        }
        finally
        {
            await ws.DisposeAsync();
            TestScratch.Delete(target);
        }
    }

    [Fact]
    public async Task CloneAsync_focuses_an_existing_tab_instead_of_opening_a_duplicate()
    {
        var ws = NewWorkspace();
        using var source = Repo("a.txt");
        var target = TestScratch.NewPath("historia-clone-dedup-");

        try
        {
            var first = await ws.CloneAsync(source.Path, target, new Historia.Services.Workspace.CloneOptions(), progress: null);
            Assert.Single(ws.OpenRepositories);
            Assert.Same(first, ws.Focused);

            TestScratch.Delete(target);

            var second = await ws.CloneAsync(source.Path, target, new Historia.Services.Workspace.CloneOptions(), progress: null);
            Assert.Same(first, second);
            Assert.Single(ws.OpenRepositories);
            Assert.Same(first, ws.Focused);
        }
        finally
        {
            await ws.DisposeAsync();
            TestScratch.Delete(target);
        }
    }

    // ── tab drag-and-drop reorder ──

    [Fact(Timeout = 30000)]
    public async Task Move_reorders_open_tabs_and_raises_open_changed()
    {
        var ws = NewWorkspace();
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");
        using var r3 = Repo("c.txt");

        var a = await ws.OpenAsync(r1.Path);
        await ws.OpenAsync(r2.Path);
        await ws.OpenAsync(r3.Path);

        var raised = 0;
        ws.OpenRepositoriesChanged += (_, _) => raised++;
        var focusedBefore = ws.Focused;

        ws.Move(a, 2); // a -> end

        Assert.Equal(1, raised);
        Assert.Equal(
            new[] { Norm(r2.Path), Norm(r3.Path), Norm(r1.Path) },
            ws.OpenRepositories.Select(c => Norm(c.RepositoryPath)));
        Assert.Same(focusedBefore, ws.Focused); // reorder does not change focus
        await ws.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task Move_clamps_the_index_and_ignores_a_closed_context()
    {
        var ws = NewWorkspace();
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");

        var a = await ws.OpenAsync(r1.Path);
        var b = await ws.OpenAsync(r2.Path);

        ws.Move(a, 99); // clamps to the last slot
        Assert.Equal(
            new[] { Norm(r2.Path), Norm(r1.Path) },
            ws.OpenRepositories.Select(c => Norm(c.RepositoryPath)));

        await ws.CloseAsync(b);
        var raised = false;
        ws.OpenRepositoriesChanged += (_, _) => raised = true;
        ws.Move(b, 0); // b is closed → no-op, no event
        Assert.False(raised);
        await ws.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task Move_persists_the_new_order_for_session_restore()
    {
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");

        var ws = NewWorkspace();
        var a = await ws.OpenAsync(r1.Path);
        await ws.OpenAsync(r2.Path);
        ws.Move(a, 1); // order is now r2, r1
        await Task.Delay(500); // let the debounced writer flush
        await ws.DisposeAsync();

        var node = JsonNode.Parse(File.ReadAllText(AppPaths.SessionFile))!;
        Assert.Equal(
            new[] { Norm(r2.Path), Norm(r1.Path) },
            node["paths"]!.AsArray().Select(n => Norm(n!.GetValue<string>())));
    }

    // ── session restore ──

    [Fact(Timeout = 30000)]
    public async Task Session_v1_array_still_loads()
    {
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");
        File.WriteAllText(AppPaths.SessionFile, JsonSerializer.Serialize(new[] { r1.Path, r2.Path }));

        var ws = NewWorkspace();
        await ws.RestoreSessionAsync();

        Assert.Equal(2, ws.OpenRepositories.Count);
        await ws.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task Session_v2_object_loads_and_restores_focus()
    {
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");
        using var r3 = Repo("c.txt");
        var payload = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["paths"] = new JsonArray(r1.Path, r2.Path, r3.Path),
            ["focusedPath"] = r2.Path,
        };
        File.WriteAllText(AppPaths.SessionFile, payload.ToJsonString());

        var ws = NewWorkspace();
        await ws.RestoreSessionAsync();

        Assert.Equal(3, ws.OpenRepositories.Count);
        Assert.Equal(Norm(r2.Path), Norm(ws.Focused!.RepositoryPath)); // focus, not last-opened
        // Prioritizing the focused tab's open must not reorder the tab strip -- it always has
        // to match the saved session order regardless of which tab was focused.
        Assert.Equal(
            new[] { Norm(r1.Path), Norm(r2.Path), Norm(r3.Path) },
            ws.OpenRepositories.Select(c => Norm(c.RepositoryPath)));
        await ws.DisposeAsync();
    }

    [Fact(Timeout = 60000)]
    public async Task Session_restore_opens_the_focused_tab_first_and_never_more_than_two_others_at_once()
    {
        var repos = Enumerable.Range(0, 6).Select(i => Repo($"r{i}.txt")).ToList();
        try
        {
            var focusIndex = 3;
            var focusPath = repos[focusIndex].Path;
            File.WriteAllText(AppPaths.SessionFile, new JsonObject
            {
                ["schemaVersion"] = 2,
                ["paths"] = new JsonArray(repos.Select(r => (JsonNode)r.Path).ToArray()),
                ["focusedPath"] = focusPath,
            }.ToJsonString());

            var ws = NewWorkspace();
            var sync = new object();
            var started = new List<string>();
            var gates = new Dictionary<string, TaskCompletionSource>(StringComparer.OrdinalIgnoreCase);
            var inFlightOthers = 0;
            var maxInFlightOthers = 0;
            foreach (var r in repos)
                gates[r.Path] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            ws.OpenContext = async (path, services, ct) =>
            {
                var isFocus = string.Equals(path, focusPath, StringComparison.OrdinalIgnoreCase);
                lock (sync)
                {
                    started.Add(path);
                    if (!isFocus)
                        maxInFlightOthers = Math.Max(maxInFlightOthers, ++inFlightOthers);
                }

                try
                {
                    await gates[path].Task.WaitAsync(ct);
                    return await RepositoryContext.OpenAsync(path, services, ct);
                }
                finally
                {
                    if (!isFocus)
                        lock (sync) inFlightOthers--;
                }
            };

            List<string> Started() { lock (sync) return [.. started]; }
            async Task WaitForStartedAsync(int count)
            {
                var sw = Stopwatch.StartNew();
                while (Started().Count < count)
                {
                    Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"expected {count} opens to have started");
                    await Task.Delay(5);
                }
            }

            var restore = ws.RestoreSessionAsync();

            // Only the focused tab's open starts; the others wait for it.
            await WaitForStartedAsync(1);
            await Task.Delay(150);
            Assert.Equal([focusPath], Started());

            // Once it finishes, exactly two others start.
            gates[focusPath].SetResult();
            await WaitForStartedAsync(3);
            await Task.Delay(150);
            Assert.Equal(3, Started().Count);

            // Each finished open lets exactly one more start, until all have.
            for (var startedCount = 3; startedCount < repos.Count; startedCount++)
            {
                var running = Started().First(p => p != focusPath && !gates[p].Task.IsCompleted);
                gates[running].SetResult();
                await WaitForStartedAsync(startedCount + 1);
            }

            foreach (var g in gates.Values)
                g.TrySetResult();
            await restore.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(2, maxInFlightOthers);
            Assert.Equal(repos.Count, ws.OpenRepositories.Count);
            Assert.Equal(Norm(focusPath), Norm(ws.Focused!.RepositoryPath));
            Assert.Equal(
                repos.Select(r => Norm(r.Path)),
                ws.OpenRepositories.Select(c => Norm(c.RepositoryPath)));
            await ws.DisposeAsync();
        }
        finally
        {
            foreach (var r in repos)
                r.Dispose();
        }
    }

    /// <summary>
    /// Verifies that restored repositories from session are not touched in the recent list.
    /// Also guards the dispose path: the restored background tab is still queued when the
    /// workspace is disposed, and DrainBackgroundInitQueueAsync must not spin forever on the
    /// closed foreground tab and hang DisposeAsync.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Session_restore_does_not_touch_the_recent_list()
    {
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");

        // Seed the Recent list with r1 only, then hand-write a session that restores both.
        var seed = NewWorkspace();
        await seed.OpenAsync(r1.Path);
        await Task.Delay(500);
        await seed.DisposeAsync();
        // Flush the debounced Recent write so the next workspace loads the same list.
        var seedRecents = _recents!;
        await seedRecents.DisposeAsync();
        var recentsBefore = seedRecents.Items.Select(i => Norm(i.Path)).ToArray();

        File.WriteAllText(AppPaths.SessionFile, new JsonObject
        {
            ["schemaVersion"] = 2,
            ["paths"] = new JsonArray(r1.Path, r2.Path),
            ["focusedPath"] = r2.Path,
        }.ToJsonString());

        var ws = NewWorkspace();
        await ws.RestoreSessionAsync();
        await Task.Delay(500);
        await ws.DisposeAsync();

        // r2 was restored but never explicitly opened, so it must not have joined Recent,
        // and r1's position/timestamp is unchanged.
        Assert.Equal(recentsBefore, _recents!.Items.Select(i => Norm(i.Path)).ToArray());
    }

    [Fact(Timeout = 30000)]
    public async Task Restoring_persists_a_v3_session_file_on_the_next_change()
    {
        using var r1 = Repo("a.txt");

        var ws = NewWorkspace();
        await ws.OpenAsync(r1.Path);
        await Task.Delay(500); // let the debounced writer flush
        await ws.DisposeAsync();

        var node = JsonNode.Parse(File.ReadAllText(AppPaths.SessionFile))!;
        Assert.Equal(3, node["schemaVersion"]!.GetValue<int>());
        Assert.Equal(Norm(r1.Path), Norm(node["focusedPath"]!.GetValue<string>()));
        Assert.Single(node["paths"]!.AsArray());
        Assert.Single(node["linkOwners"]!.AsArray());
        Assert.True(node["linkOwners"]!.AsArray()[0] is null);
    }
}

[Trait("tier", "git")]
public class WorkspaceFocusPriorityTests : WorkspaceServiceTestBase
{
    // ── Focus-priority interrupt-and-promote ──

    [Fact(Timeout = 20000)]
    public async Task Focusing_away_mid_init_leaves_FirstFullLoad_incomplete_until_retried()
    {
        var identity = new SlowIdentityService();
        var ws = NewWorkspace(identity);
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");

        var ctx1 = await ws.OpenAsync(r1.Path);
        ctx1.MarkFirstGraphPaintDone(); // skip the first-paint ceiling wait — no graph control here
        var ctx2 = await ws.OpenAsync(r2.Path); // focus switch: interrupts ctx1's in-flight identity resolution
        ctx2.MarkFirstGraphPaintDone();
        identity.Release(r2.Path); // ctx2 resolves quickly and finishes on its own

        Assert.False(ctx1.FirstFullLoad.IsCompleted, "an interrupted attempt must not be marked done");

        identity.Release(r1.Path); // let the pump's retry of ctx1 proceed
        await ctx1.FirstFullLoad.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(ctx1.FirstFullLoad.IsCompleted);

        await ws.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task Focusing_a_previously_interrupted_tab_is_promoted_ahead_of_the_tab_that_displaced_it()
    {
        var identity = new SlowIdentityService();
        var ws = NewWorkspace(identity);
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");

        var ctx1 = await ws.OpenAsync(r1.Path);
        ctx1.MarkFirstGraphPaintDone();
        var ctx2 = await ws.OpenAsync(r2.Path); // interrupts ctx1, ctx2 becomes foreground
        ctx2.MarkFirstGraphPaintDone();

        ws.Focus(ctx1); // interrupts ctx2 in turn, promotes ctx1 back to foreground immediately

        Assert.False(ctx2.FirstFullLoad.IsCompleted);

        identity.Release(r1.Path); // only ctx1's gate — ctx2's stays blocked the whole test
        await ctx1.FirstFullLoad.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(ctx1.FirstFullLoad.IsCompleted, "promoted tab must not wait behind the tab it displaced");
        Assert.False(ctx2.FirstFullLoad.IsCompleted, "the displaced tab was never released and must still be pending");

        await ws.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task Rapid_focus_toggling_between_two_loading_tabs_does_not_hang_and_both_eventually_finish()
    {
        var identity = new SlowIdentityService();
        var ws = NewWorkspace(identity);
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");

        var ctx1 = await ws.OpenAsync(r1.Path);
        ctx1.MarkFirstGraphPaintDone();
        var ctx2 = await ws.OpenAsync(r2.Path);
        ctx2.MarkFirstGraphPaintDone();

        for (var i = 0; i < 5; i++)
        {
            ws.Focus(ctx1);
            ws.Focus(ctx2);
        }

        identity.Release(r1.Path);
        identity.Release(r2.Path);

        await ctx1.FirstFullLoad.WaitAsync(TimeSpan.FromSeconds(15));
        await ctx2.FirstFullLoad.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(ctx1.FirstFullLoad.IsCompleted);
        Assert.True(ctx2.FirstFullLoad.IsCompleted);

        await ws.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task Closing_a_tab_while_its_interrupted_attempt_is_queued_does_not_resurrect_it()
    {
        var identity = new SlowIdentityService();
        var ws = NewWorkspace(identity);
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");

        var ctx1 = await ws.OpenAsync(r1.Path);
        ctx1.MarkFirstGraphPaintDone();
        var ctx2 = await ws.OpenAsync(r2.Path); // interrupts + requeues ctx1
        ctx2.MarkFirstGraphPaintDone();
        identity.Release(r2.Path);

        await ws.CloseAsync(ctx1); // closed while its interrupted attempt sits in _bgInitQueue

        identity.Release(r1.Path);
        await Task.Delay(500); // give the pump a chance to (wrongly) resurrect it, if it were going to
        Assert.DoesNotContain(ctx1, ws.OpenRepositories);

        await ws.DisposeAsync();
    }

    // ── Identity resolution runs concurrently with the first refs refresh ──

    [Fact(Timeout = 20000)]
    public async Task Refs_refresh_runs_concurrently_with_identity_resolution_not_after_it()
    {
        var identity = new SlowIdentityService(); // blocks every context until released
        var ws = NewWorkspace(identity);
        using var r1 = Repo("a.txt");

        var ctx1 = await ws.OpenAsync(r1.Path);

        // A foreground first load refreshes refs only; status starts once the load settles, which
        // waits on identity, so refs are the refresh that must not queue behind identity.
        var refsRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx1.State.RefsChanged += (_, _) => refsRaised.TrySetResult();
        ctx1.MarkFirstGraphPaintDone();

        // Identity resolution is still blocked (never released) — if the refs refresh waited for
        // it to finish first (running the two sequentially), this would time out.
        await refsRaised.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(ctx1.FirstFullLoad.IsCompleted, "identity is still blocked, so the attempt as a whole isn't done");
        identity.Release(r1.Path);
        await ctx1.FirstFullLoad.WaitAsync(TimeSpan.FromSeconds(10));

        await ws.DisposeAsync();
    }

    // ── Headless caching for inactive tabs ──

    private sealed class RecordingGraphLayoutService : IGraphLayoutService
    {
        private readonly List<(RepositoryContext Context, RepositoryContext? FocusedThen)> _enqueued = [];

        /// <summary>Reads the workspace's focused tab at each enqueue, so a test can tell which
        /// focus change's pipeline queued it.</summary>
        public Func<RepositoryContext?>? Focused { get; set; }

        public List<RepositoryContext> EnqueuedWhileFocused(RepositoryContext focused)
        {
            lock (_enqueued)
                return [.. _enqueued.Where(e => ReferenceEquals(e.FocusedThen, focused)).Select(e => e.Context)];
        }

        public void EnqueueInactiveTabCaching(RepositoryContext context, GraphQuery query)
        {
            var focused = Focused?.Invoke();
            lock (_enqueued)
                _enqueued.Add((context, focused));
        }
        public Task CancelInactiveTabCachingAsync(RepositoryContext context) { Cancelled.Add(context); return Task.CompletedTask; }
        public List<RepositoryContext> Cancelled { get; } = [];

        public Task<GraphLayoutResult> ComputeLayoutAsync(RepositoryContext context, GraphQuery query, int maxRows = 10000, CancellationToken ct = default)
            => throw new NotImplementedException();
        public GraphLayoutResult? TryOptimisticSnapshot(RepositoryContext context, GraphQuery query, int maxRows) => null;
        public Task<GraphLayoutResult> ExtendLayoutAsync(RepositoryContext context, GraphLayoutResult existing, int additionalRows, CancellationToken ct = default, bool background = false)
            => throw new NotImplementedException();
        public Task<GraphLayoutResult> RecomputeAsync(RepositoryContext context, GraphQuery query, GraphLayoutResult previous, InvalidationReason reason, int maxRows = 10000, CancellationToken ct = default)
            => throw new NotImplementedException();
        public void Forget(RepositoryContext context) { }
        public Task Hibernate(RepositoryContext context) => Task.CompletedTask;
        public int ClearPersistentCache() => 0;
        public Task RunHeadlessCacheAsync(RepositoryContext context, GraphQuery query, CancellationToken ct) => Task.CompletedTask;
        public void RefreshCommitGraphAfterFetch(RepositoryContext context) { }
        public void SweepDiskCache(IReadOnlyCollection<string> openRepositoryPaths) { }

        public Func<RepositoryContext, GraphQuery, IProgress<GitProgress>?, CancellationToken, Task>? PrecacheAsync { get; set; }
        public List<RepositoryContext> Precached { get; } = [];

        public async Task PrecacheClonedRepositoryAsync(RepositoryContext context, GraphQuery query, IProgress<GitProgress>? progress, CancellationToken ct)
        {
            Precached.Add(context);
            if (PrecacheAsync is not null)
                await PrecacheAsync(context, query, progress, ct);
        }
    }

    [Fact(Timeout = 20000)]
    public async Task Focusing_a_tab_queues_every_other_open_tab_for_headless_caching_but_not_itself()
    {
        var fakeGraph = new RecordingGraphLayoutService();
        var provider = new OverrideServiceProvider(_host.Provider, typeof(IGraphLayoutService), fakeGraph);
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var recents = _recents = new RecentRepositoryService(_host.Settings);
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        var identity = new RepoIdentityService(cli);
        var ws = new WorkspaceService(provider, cli, recents, profiles, identity);

        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");
        using var r3 = Repo("c.txt");

        // Force each tab's FirstFullLoad done immediately after it opens, before opening the
        // next one: FocusInternal only requeues an outgoing tab onto WorkspaceService's OWN
        // background-init pump when it's STILL loading, and that pump's own timing is unrelated
        // to what this test cares about (the headless-caching pipeline).
        fakeGraph.Focused = () => ws.Focused;
        var ctx1 = await ws.OpenAsync(r1.Path);
        ctx1.MarkFirstFullLoadDone();
        var ctx2 = await ws.OpenAsync(r2.Path);
        ctx2.MarkFirstFullLoadDone();
        var ctx3 = await ws.OpenAsync(r3.Path); // focus now r3 — this is what triggers the pipeline
        ctx3.MarkFirstGraphPaintDone();
        ctx3.MarkFirstFullLoadDone();

        // Each focus change runs its own pipeline once focus has been stable for 2 s. On a slow
        // machine opening r3 takes longer than that, so r2's pipeline also queues r1 — correctly,
        // and the real writer de-dupes it. Only r3's pipeline is under test here.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (fakeGraph.EnqueuedWhileFocused(ctx3).Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        var queued = fakeGraph.EnqueuedWhileFocused(ctx3);
        Assert.Equal(2, queued.Count);
        Assert.Contains(ctx1, queued);
        Assert.Contains(ctx2, queued);

        await ws.DisposeAsync();
    }

    // ── Focus cancels the newly focused context's own inactive-tab headless job ──

    [Fact(Timeout = 20000)]
    public async Task Focus_cancels_the_newly_focused_contexts_inactive_tab_job()
    {
        var fakeGraph = new RecordingGraphLayoutService();
        var provider = new OverrideServiceProvider(_host.Provider, typeof(IGraphLayoutService), fakeGraph);
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var recents = _recents = new RecentRepositoryService(_host.Settings);
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        var identity = new RepoIdentityService(cli);
        var ws = new WorkspaceService(provider, cli, recents, profiles, identity);

        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");

        var ctx1 = await ws.OpenAsync(r1.Path); // focus lands on ctx1
        ctx1.MarkFirstFullLoadDone();
        var ctx2 = await ws.OpenAsync(r2.Path); // focus moves to ctx2

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (fakeGraph.Cancelled.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        // Both contexts were, at some point, the newly focused one — each focus change must
        // cancel that tab's own inactive-tab job before its foreground graph work can start, even
        // though in this test neither ever actually had one queued.
        Assert.Contains(ctx1, fakeGraph.Cancelled);
        Assert.Contains(ctx2, fakeGraph.Cancelled);

        await ws.DisposeAsync();
    }

    // ── Gate the tab-switch compacting GC on actual heap growth ──

    [Theory]
    [InlineData(0L, 0L, false)]
    [InlineData(1024L * 1024 * 1024, 0L, true)] // heap at the 1 GiB threshold
    [InlineData(1024L * 1024 * 1024 - 1, 0L, false)] // just under the heap threshold, LOH empty
    [InlineData(0L, 256L * 1024 * 1024, true)] // LOH at the 256 MiB threshold
    [InlineData(0L, 256L * 1024 * 1024 - 1, false)] // just under the LOH threshold, heap empty
    [InlineData(2L * 1024 * 1024 * 1024, 1024L, true)] // either threshold alone is sufficient
    public void ShouldCompactAfterHibernate_uses_named_heap_and_LOH_thresholds(long heapBytes, long lohBytes, bool expected)
    {
        Assert.Equal(expected, WorkspaceService.ShouldCompactAfterHibernate(heapBytes, lohBytes));
    }

    // ── The idle collection never runs while the window is active ──

    private const long BigHeap = 2L * 1024 * 1024 * 1024;

    private sealed class CollectRecorder
    {
        private readonly ConcurrentQueue<(int Generation, GCCollectionMode Mode, bool Blocking)> _calls = new();
        public List<(int Generation, GCCollectionMode Mode, bool Blocking)> Calls => _calls.ToList();
        public void Collect(int generation, GCCollectionMode mode, bool blocking) => _calls.Enqueue((generation, mode, blocking));
    }

    private WorkspaceService NewWorkspaceWithRecorder(CollectRecorder recorder)
    {
        var ws = NewWorkspace();
        ws.CollectGarbage = recorder.Collect;
        return ws;
    }

    [Fact]
    public async Task Idle_collection_is_not_requested_while_the_window_is_active()
    {
        var recorder = new CollectRecorder();
        var ws = NewWorkspaceWithRecorder(recorder);
        Assert.True(ws.IsAppActive);

        ws.ReclaimIfHeapGrown(BigHeap, 0);

        Assert.Empty(recorder.Calls);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task Pending_request_runs_one_gentle_collection_when_the_window_deactivates()
    {
        var recorder = new CollectRecorder();
        var ws = NewWorkspaceWithRecorder(recorder);
        ws.ReclaimIfHeapGrown(BigHeap, 0);
        ws.ReclaimIfHeapGrown(0, BigHeap); // a second request while active joins the first

        ws.SetAppActive(false);
        await ws.IdleCollectTask;

        var call = Assert.Single(recorder.Calls);
        Assert.Equal(GC.MaxGeneration, call.Generation);
        Assert.Equal(GCCollectionMode.Optimized, call.Mode);
        Assert.False(call.Blocking);

        // The request was consumed: later activations and deactivations collect nothing more.
        ws.SetAppActive(true);
        ws.SetAppActive(false);
        await ws.IdleCollectTask;
        Assert.Single(recorder.Calls);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task Reactivation_before_deactivation_leaves_the_request_pending()
    {
        var recorder = new CollectRecorder();
        var ws = NewWorkspaceWithRecorder(recorder);
        ws.ReclaimIfHeapGrown(BigHeap, 0);

        ws.SetAppActive(true);
        Assert.Empty(recorder.Calls);

        ws.SetAppActive(false);
        await ws.IdleCollectTask;
        Assert.Single(recorder.Calls);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task Deactivation_without_a_pending_request_collects_nothing()
    {
        var recorder = new CollectRecorder();
        var ws = NewWorkspaceWithRecorder(recorder);

        ws.SetAppActive(false);
        await ws.IdleCollectTask;

        Assert.Empty(recorder.Calls);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task Request_while_the_window_is_inactive_collects_at_once_and_leaves_nothing_pending()
    {
        var recorder = new CollectRecorder();
        var ws = NewWorkspaceWithRecorder(recorder);
        ws.SetAppActive(false);

        ws.ReclaimIfHeapGrown(BigHeap, 0);
        Assert.Single(recorder.Calls);

        ws.SetAppActive(true);
        ws.SetAppActive(false);
        await ws.IdleCollectTask;
        Assert.Single(recorder.Calls);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task Small_heap_requests_no_collection_even_while_inactive()
    {
        var recorder = new CollectRecorder();
        var ws = NewWorkspaceWithRecorder(recorder);
        ws.SetAppActive(false);

        ws.ReclaimIfHeapGrown(1024, 1024);
        ws.SetAppActive(true);
        ws.SetAppActive(false);
        await ws.IdleCollectTask;

        Assert.Empty(recorder.Calls);
        await ws.DisposeAsync();
    }

    // ── Identity: the config read runs beside the root-commit walk ──

    /// <summary>Signals when a git invocation whose first argument is <c>config</c> starts.</summary>
    private sealed class ConfigReadObservingCli(IGitCliService inner) : IGitCliService
    {
        public bool IsGitAvailable => inner.IsGitAvailable;
        public TaskCompletionSource ConfigReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
        {
            if (request.Arguments.Count > 0 && request.Arguments[0] == "config")
                ConfigReadStarted.TrySetResult();
            return inner.RunAsync(request, ct);
        }

        public Task<GitCliResult> RunAsync(
            GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default)
            => inner.RunAsync(request, progress, onStdoutLine, ct);
    }

    /// <summary>An identity service whose walk finishes only after the config read has started, so
    /// a sequential resolve (walk, then read) can never complete.</summary>
    private sealed class WaitsForConfigReadIdentityService(Task configReadStarted) : IRepoIdentityService
    {
        public bool ConfigReadWasStartedFirst { get; private set; }

        public async Task<RepoIdentityKey> ResolveAsync(RepositoryContext context, CancellationToken ct = default)
        {
            await configReadStarted.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            ConfigReadWasStartedFirst = true;
            return RepoIdentityKey.FromRootCommit("0123456789abcdef0123456789abcdef01234567");
        }
    }

    private static GitProfile NewProfile() => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "Test Profile",
        GitUserName = "Profile Name",
        GitUserEmail = "profile@example.com",
        CreatedAt = DateTimeOffset.Now,
    };

    [Fact(Timeout = 30000)]
    public async Task Identity_resolution_starts_the_config_read_while_the_walk_is_running()
    {
        var realCli = _host.Provider.GetRequiredService<IGitCliService>();
        var cli = new ConfigReadObservingCli(realCli);
        var provider = new OverrideServiceProvider(_host.Provider, typeof(IGitCliService), cli);
        var identity = new WaitsForConfigReadIdentityService(cli.ConfigReadStarted.Task);
        var profiles = new ConfigWriteProfileService(NewProfile());
        var recents = _recents = new RecentRepositoryService(_host.Settings);
        var ws = new WorkspaceService(provider, cli, recents, profiles, identity);

        using var r = RepoWithoutLocalIdentity();
        var ctx = await ws.OpenAsync(r.Path);
        ctx.MarkFirstGraphPaintDone();

        // The repo has no local identity, so the profile is written once both reads finished.
        await profiles.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(identity.ConfigReadWasStartedFirst);

        profiles.Release.TrySetResult();
        await ws.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task Profile_is_not_written_when_the_repo_already_has_a_local_identity()
    {
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var profiles = new ConfigWriteProfileService(NewProfile());
        var recents = _recents = new RecentRepositoryService(_host.Settings);
        var ws = new WorkspaceService(_host.Provider, cli, recents, profiles, new RepoIdentityService(cli));

        using var r = Repo("has-identity.txt"); // the builder sets user.name and user.email locally
        var ctx = await ws.OpenAsync(r.Path);
        ctx.MarkFirstGraphPaintDone();
        await ctx.FirstFullLoad.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(profiles.Started.Task.IsCompleted);
        await ws.DisposeAsync();
    }

    // ── A focus switch mid-git-config-write must not cancel the write ──

    /// <summary>A minimal <see cref="IProfileService"/> whose <see cref="ApplyProfileToGitConfigAsync"/>
    /// blocks until released and records the token it was actually invoked with, so the test can
    /// interrupt the caller's attempt token while the "write" is in flight and check that the
    /// write itself never observed the cancellation.</summary>
    private sealed class ConfigWriteProfileService(GitProfile profile) : IProfileService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ReceivedToken { get; private set; }
        public bool WriteCompleted { get; private set; }

        public IReadOnlyList<GitProfile> GetAllProfiles() => [profile];
        public GitProfile? GetProfileById(Guid id) => profile.Id == id ? profile : null;
        public ProfileUsageState? GetUsage(Guid id) => null;
        public Task<GitProfile> CreateProfileAsync(GitProfileCreateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GitProfile> UpdateProfileAsync(Guid id, GitProfileUpdateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProfileDeleteResult> DeleteProfileAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public GitProfile GetGlobalProfile() => profile;
        public Task SyncGlobalIdentityAsync(CancellationToken ct = default) => Task.CompletedTask;
        public GitProfile? GetDefaultProfile() => profile;
        public Task SetDefaultProfileAsync(Guid? profileId, CancellationToken ct = default) => Task.CompletedTask;
        public GitProfile? GetProfileForRepository(RepoIdentityKey repoKey) => null;
        public bool IsLocalConfigSelected(RepoIdentityKey repoKey) => false;
        public Task BindProfileToRepositoryAsync(RepoIdentityKey repoKey, string displayPath, Guid profileId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLocalConfigForRepositoryAsync(RepoIdentityKey repoKey, string displayPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnbindRepositoryAsync(RepoIdentityKey repoKey, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyDictionary<RepoIdentityKey, RepoProfileBinding> GetAllRepoBindings() => new Dictionary<RepoIdentityKey, RepoProfileBinding>();
        public IReadOnlyList<RepoProfileBinding> GetStaleBindings() => [];
        public Task<int> CleanStaleBindingsAsync(CancellationToken ct = default) => Task.FromResult(0);
        public GitProfile? ResolveActiveProfile(RepoIdentityKey? repoKey = null) => profile;

        public async Task ApplyProfileToGitConfigAsync(GitProfile p, string repoPath, GitConfigScope scope, CancellationToken ct = default)
        {
            ReceivedToken = ct;
            Started.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            WriteCompleted = true;
        }

        public Task TouchProfileAsync(Guid profileId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetTokenAsync(Guid profileId, RemoteHost host, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task ExportProfilesAsync(string exportFilePath, bool includeTokens, string? password, CancellationToken ct = default) => Task.CompletedTask;
        public Task ImportProfilesAsync(string importFilePath, string? password, CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler? ProfilesChanged { add { } remove { } }
    }

    private static TestRepositoryBuilder RepoWithoutLocalIdentity()
    {
        var b = Repo("f.txt");
        // The profile-to-git-config write path only triggers when the repo has neither user.name nor user.email set
        // locally — TestRepositoryBuilder.Create() sets both so Commit() works, so clear them here.
        b.Git("config", "--local", "--unset", "user.name");
        b.Git("config", "--local", "--unset", "user.email");
        return b;
    }

    [Fact(Timeout = 20000)]
    public async Task Focus_switch_during_git_config_write_does_not_cancel_the_write()
    {
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var recents = _recents = new RecentRepositoryService(_host.Settings);
        var profile = new GitProfile
        {
            Id = Guid.NewGuid(),
            DisplayName = "Test Profile",
            GitUserName = "Profile Name",
            GitUserEmail = "profile@example.com",
            CreatedAt = DateTimeOffset.Now,
        };
        var profiles = new ConfigWriteProfileService(profile);
        var identity = new RepoIdentityService(cli);
        var ws = new WorkspaceService(_host.Provider, cli, recents, profiles, identity);

        using var r1 = RepoWithoutLocalIdentity();
        using var r2 = Repo("b.txt");

        var ctx1 = await ws.OpenAsync(r1.Path);
        ctx1.MarkFirstGraphPaintDone(); // skip the first-paint ceiling wait — no graph control here

        // The config write is now in flight (blocked on Release) inside ctx1's InitializeContextAsync.
        await profiles.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await ws.OpenAsync(r2.Path); // focus switch: interrupts ctx1's attempt token
        await Task.Delay(200); // give FocusInternal's Cancel() a moment to run

        Assert.False(profiles.ReceivedToken.IsCancellationRequested,
            "the git-config write's own token must never be cancelled by a focus-loss interrupt");
        Assert.False(profiles.WriteCompleted, "the write should still be blocked on Release at this point");

        profiles.Release.TrySetResult();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!profiles.WriteCompleted && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(profiles.WriteCompleted, "the write must run to completion even though the attempt was interrupted");

        await ws.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task CloneAsync_precaches_before_the_tab_opens()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        var locator = _host.Provider.GetRequiredService<IGitExecutableLocator>();
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var realGraph = new GraphLayoutService(_host.Settings, locator, cli);
        var provider = new OverrideServiceProvider(_host.Provider, typeof(IGraphLayoutService), realGraph);
        var recents = _recents = new RecentRepositoryService(_host.Settings);
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        var identity = new RepoIdentityService(cli);
        var ws = new WorkspaceService(provider, cli, recents, profiles, identity);

        using var source = Repo("a.txt");
        var target = TestScratch.NewPath("historia-clone-precache-");

        try
        {
            var ctx = await ws.CloneAsync(source.Path, target, new Historia.Services.Workspace.CloneOptions(), progress: null);
            var hdrPath = GraphCachePaths.File(target, GraphMetaStore.HdrExt);
            Assert.True(File.Exists(hdrPath));

            using var doc = JsonDocument.Parse(File.ReadAllBytes(hdrPath));
            Assert.True(doc.RootElement.GetProperty("Exhausted").GetBoolean());
            Assert.Equal(ctx, ws.Focused);
        }
        finally
        {
            await ws.DisposeAsync();
            TestScratch.Delete(target);
        }
    }

    [Fact(Timeout = 20000)]
    public async Task CloneAsync_cancelled_during_caching_still_opens_the_tab()
    {
        using var cts = new CancellationTokenSource();
        var fakeGraph = new RecordingGraphLayoutService
        {
            PrecacheAsync = async (_, _, _, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                await Task.CompletedTask;
            }
        };
        var provider = new OverrideServiceProvider(_host.Provider, typeof(IGraphLayoutService), fakeGraph);
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var recents = _recents = new RecentRepositoryService(_host.Settings);
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        var identity = new RepoIdentityService(cli);
        var ws = new WorkspaceService(provider, cli, recents, profiles, identity);

        using var source = Repo("a.txt");
        var target = TestScratch.NewPath("historia-clone-cancel-precache-");

        try
        {
            var ctx = await ws.CloneAsync(source.Path, target, new Historia.Services.Workspace.CloneOptions(), progress: null, ct: cts.Token);
            Assert.NotNull(ctx);
            Assert.True(Directory.Exists(Path.Combine(target, ".git")));
            Assert.Equal(ctx, ws.Focused);
        }
        finally
        {
            await ws.DisposeAsync();
            TestScratch.Delete(target);
        }
    }
}

[Trait("tier", "git")]
public class WorkspaceSubmoduleTabTests : WorkspaceServiceTestBase
{
    // ── linked submodule tab groups ──

    [Fact]
    public async Task OpenSubmoduleAsync_inserts_the_new_tab_immediately_after_the_owners_group()
    {
        var ws = NewWorkspace();
        using var ownerRepo = Repo("owner.txt");
        using var otherRepo = Repo("other.txt");

        var owner = await ws.OpenAsync(ownerRepo.Path);
        var other = await ws.OpenAsync(otherRepo.Path);
        var sub = MakeNestedSubmodule(owner, "vendor/lib", "lib.txt");

        var child = await ws.OpenSubmoduleAsync(owner, sub);

        Assert.Equal([owner, child, other], ws.OpenRepositories);
        Assert.Same(owner, child.LinkOwner);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task OpenSubmoduleAsync_on_an_already_open_path_focuses_it_instead_of_duplicating()
    {
        var ws = NewWorkspace();
        using var ownerRepo = Repo("owner.txt");
        var owner = await ws.OpenAsync(ownerRepo.Path);
        var sub = MakeNestedSubmodule(owner, "vendor/lib", "lib.txt");

        var first = await ws.OpenSubmoduleAsync(owner, sub);
        var countAfterFirst = ws.OpenRepositories.Count;

        var second = await ws.OpenSubmoduleAsync(owner, sub);

        Assert.Same(first, second);
        Assert.Equal(countAfterFirst, ws.OpenRepositories.Count);
        Assert.Same(owner, first.LinkOwner);
        Assert.Same(first, ws.Focused);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task OpenSubmoduleAsync_twice_for_two_submodules_keeps_both_contiguous_after_the_owner()
    {
        var ws = NewWorkspace();
        using var ownerRepo = Repo("owner.txt");
        using var otherRepo = Repo("other.txt");
        var owner = await ws.OpenAsync(ownerRepo.Path);
        var other = await ws.OpenAsync(otherRepo.Path);

        var sub1 = MakeNestedSubmodule(owner, "vendor/lib1", "a.txt");
        var sub2 = MakeNestedSubmodule(owner, "vendor/lib2", "b.txt");
        var child1 = await ws.OpenSubmoduleAsync(owner, sub1);
        var child2 = await ws.OpenSubmoduleAsync(owner, sub2);

        Assert.Equal([owner, child1, child2, other], ws.OpenRepositories);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task OpenSubmoduleAsync_refuses_an_uninitialized_submodule()
    {
        var ws = NewWorkspace();
        using var ownerRepo = Repo("owner.txt");
        var owner = await ws.OpenAsync(ownerRepo.Path);

        var emptySubDir = Path.Combine(owner.RepositoryPath, "vendor/empty");
        Directory.CreateDirectory(emptySubDir);

        var sub = new SubmoduleInfo
        {
            Name = "vendor/empty",
            Path = "vendor/empty",
            Url = emptySubDir,
            Status = SubmoduleStatus.Uninitialized,
        };

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(() => ws.OpenSubmoduleAsync(owner, sub));
        Assert.Equal(GitErrorKind.NotAGitRepository, ex.Kind);
        Assert.Null(owner.LinkOwner);
        Assert.Single(ws.OpenRepositories);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task OpenSubmoduleAsync_links_to_the_root_owner_when_opened_from_a_linked_tab()
    {
        var ws = NewWorkspace();
        using var ownerRepo = Repo("owner.txt");
        var owner = await ws.OpenAsync(ownerRepo.Path);
        var sub1 = MakeNestedSubmodule(owner, "vendor/child", "child.txt");
        var child = await ws.OpenSubmoduleAsync(owner, sub1);

        var sub2 = MakeNestedSubmodule(child, "sub2", "sub2.txt");
        var grandchild = await ws.OpenSubmoduleAsync(child, sub2);

        Assert.Same(owner, child.LinkOwner);
        Assert.Same(owner, grandchild.LinkOwner);
        Assert.Equal([owner, child, grandchild], ws.OpenRepositories);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task OpenSubmoduleAsync_adopts_and_moves_an_existing_standalone_tab()
    {
        var ws = NewWorkspace();
        using var ownerRepo = Repo("owner.txt");
        using var otherRepo = Repo("other.txt");
        var owner = await ws.OpenAsync(ownerRepo.Path);
        var other = await ws.OpenAsync(otherRepo.Path);

        var sub = MakeNestedSubmodule(owner, "vendor/lib", "lib.txt");
        var fullSubPath = Path.Combine(owner.RepositoryPath, "vendor/lib");
        var subTab = await ws.OpenAsync(fullSubPath);
        // _open: [owner, other, subTab]
        Assert.Equal([owner, other, subTab], ws.OpenRepositories);
        Assert.Null(subTab.LinkOwner);

        var adopted = await ws.OpenSubmoduleAsync(owner, sub);

        Assert.Same(subTab, adopted);
        Assert.Same(owner, subTab.LinkOwner);
        Assert.Equal([owner, subTab, other], ws.OpenRepositories);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task OpenSubmoduleAsync_never_relinks_a_group_owner()
    {
        var ws = NewWorkspace();
        using var ownerRepo = Repo("owner.txt");
        var owner = await ws.OpenAsync(ownerRepo.Path);

        var sub = MakeNestedSubmodule(owner, "vendor/lib", "lib.txt");
        var fullSubPath = Path.Combine(owner.RepositoryPath, "vendor/lib");
        var subOwner = await ws.OpenAsync(fullSubPath);
        var nestedUnderSub = MakeNestedSubmodule(subOwner, "nested", "n.txt");
        var subChild = await ws.OpenSubmoduleAsync(subOwner, nestedUnderSub);
        // _open: [owner, subOwner, subChild]
        Assert.Null(subOwner.LinkOwner);
        Assert.Same(subOwner, subChild.LinkOwner);

        var result = await ws.OpenSubmoduleAsync(owner, sub);

        Assert.Same(subOwner, result);
        Assert.Null(subOwner.LinkOwner);
        Assert.Equal([owner, subOwner, subChild], ws.OpenRepositories);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task CloseAsync_on_a_group_owner_detaches_its_children_instead_of_closing_them()
    {
        var ws = NewWorkspace();
        using var ownerRepo = Repo("owner.txt");
        var owner = await ws.OpenAsync(ownerRepo.Path);
        var sub = MakeNestedSubmodule(owner, "vendor/lib", "lib.txt");
        var child = await ws.OpenSubmoduleAsync(owner, sub);

        await ws.CloseAsync(owner);

        Assert.Contains(child, ws.OpenRepositories);
        Assert.Null(child.LinkOwner);
        await ws.DisposeAsync();
    }

    [Fact(Timeout = 60000)]
    public async Task CloseAsync_updates_the_strip_and_focus_before_a_running_write_ends()
    {
        var ws = NewWorkspace();
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");
        var a = await ws.OpenAsync(r1.Path);
        var b = await ws.OpenAsync(r2.Path);
        ws.Focus(b);

        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = Task.Run(() => b.WithWriteLockAsync(async _ =>
        {
            acquired.SetResult();
            await release.Task;
            return 1;
        }));
        await acquired.Task;

        var stripUpdated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var focusMoved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ws.OpenRepositoriesChanged += (_, _) => stripUpdated.TrySetResult();
        ws.FocusedRepositoryChanged += (_, e) =>
        {
            if (ReferenceEquals(e.New, a))
                focusMoved.TrySetResult();
        };

        var close = ws.CloseAsync(b);
        await stripUpdated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await focusMoved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(close.IsCompleted, "close finished while a write still held the lock");
        Assert.DoesNotContain(b, ws.OpenRepositories);

        release.SetResult();
        Assert.Equal(1, await write);
        await close.WaitAsync(TimeSpan.FromSeconds(10));
        await ws.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task RestoreSessionAsync_reestablishes_link_groups_from_a_schemaVersion_3_file()
    {
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");
        var payload = new JsonObject
        {
            ["schemaVersion"] = 3,
            ["paths"] = new JsonArray(r1.Path, r2.Path),
            ["linkOwners"] = new JsonArray(null, r1.Path),
            ["focusedPath"] = r1.Path,
        };
        File.WriteAllText(AppPaths.SessionFile, payload.ToJsonString());

        var ws = NewWorkspace();
        await ws.RestoreSessionAsync();

        Assert.Equal(2, ws.OpenRepositories.Count);
        var owner = ws.OpenRepositories.First(c => Norm(c.RepositoryPath) == Norm(r1.Path));
        var child = ws.OpenRepositories.First(c => Norm(c.RepositoryPath) == Norm(r2.Path));
        Assert.Same(owner, child.LinkOwner);
        await ws.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task RestoreSessionAsync_reads_a_schemaVersion_2_file_with_every_tab_ungrouped()
    {
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");
        File.WriteAllText(AppPaths.SessionFile, new JsonObject
        {
            ["schemaVersion"] = 2,
            ["paths"] = new JsonArray(r1.Path, r2.Path),
            ["focusedPath"] = r1.Path,
        }.ToJsonString());

        var ws = NewWorkspace();
        await ws.RestoreSessionAsync();

        Assert.Equal(2, ws.OpenRepositories.Count);
        Assert.All(ws.OpenRepositories, c => Assert.Null(c.LinkOwner));
        await ws.DisposeAsync();
    }
}

[Trait("tier", "git")]
public class WorkspaceTabGroupMoveTests : WorkspaceServiceTestBase
{
    // ── linked submodule tab groups: Move ──

    [Fact]
    public async Task Move_on_a_group_owner_relocates_the_whole_group_as_one_contiguous_block()
    {
        var ws = NewWorkspace();
        using var ownerRepo = Repo("owner.txt");
        using var otherRepo = Repo("other.txt");
        using var thirdRepo = Repo("third.txt");

        var owner = await ws.OpenAsync(ownerRepo.Path);
        var other = await ws.OpenAsync(otherRepo.Path);
        var third = await ws.OpenAsync(thirdRepo.Path);
        var sub = MakeNestedSubmodule(owner, "vendor/lib", "lib.txt");
        var child = await ws.OpenSubmoduleAsync(owner, sub);
        // _open: [owner, child, other, third]

        ws.Move(owner, newIndex: 3);

        // The owner+child block relocates together, snapping to the boundary nearest index 3
        // (third's own start) — matching the ungrouped Move's convention of using
        // the target index exactly as computed pre-removal, with no shift adjustment.
        Assert.Equal([other, third, owner, child], ws.OpenRepositories);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task Move_on_a_linked_child_is_clamped_to_its_own_groups_span()
    {
        var ws = NewWorkspace();
        using var ownerRepo = Repo("owner.txt");
        using var otherRepo = Repo("other.txt");
        var owner = await ws.OpenAsync(ownerRepo.Path);
        var other = await ws.OpenAsync(otherRepo.Path);
        var sub1 = MakeNestedSubmodule(owner, "vendor/lib1", "a.txt");
        var sub2 = MakeNestedSubmodule(owner, "vendor/lib2", "b.txt");
        var child1 = await ws.OpenSubmoduleAsync(owner, sub1);
        var child2 = await ws.OpenSubmoduleAsync(owner, sub2);
        // _open: [owner, child1, child2, other]

        ws.Move(child1, newIndex: 10); // far past the owner's group and past `other`

        // child1 can only reorder among the owner's other children — clamped to the group's tail.
        Assert.Equal([owner, child2, child1, other], ws.OpenRepositories);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task Move_never_splits_a_group_when_dropped_in_the_middle_of_one()
    {
        var ws = NewWorkspace();
        using var ownerARepo = Repo("ownerA.txt");
        using var ownerBRepo = Repo("ownerB.txt");
        using var soloRepo = Repo("solo.txt");

        var ownerA = await ws.OpenAsync(ownerARepo.Path);
        var subA = MakeNestedSubmodule(ownerA, "vendor/a", "a.txt");
        var childA = await ws.OpenSubmoduleAsync(ownerA, subA);

        var ownerB = await ws.OpenAsync(ownerBRepo.Path);
        var subB = MakeNestedSubmodule(ownerB, "vendor/b", "b.txt");
        var childB = await ws.OpenSubmoduleAsync(ownerB, subB);

        var solo = await ws.OpenAsync(soloRepo.Path);
        // _open: [ownerA, childA, ownerB, childB, solo]

        ws.Move(solo, newIndex: 2); // 2 is inside groupB's span (ownerB=2, childB=3)

        // solo snaps to groupB's own start boundary — landing before it, never inside it.
        Assert.Equal([ownerA, childA, solo, ownerB, childB], ws.OpenRepositories);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task Move_right_past_a_multi_tab_group_keeps_that_group_contiguous()
    {
        var ws = NewWorkspace();
        using var aRepo = Repo("a.txt");
        using var bRepo = Repo("b.txt");
        using var cRepo = Repo("c.txt");

        var a = await ws.OpenAsync(aRepo.Path);
        var b = await ws.OpenAsync(bRepo.Path);
        var bSub = MakeNestedSubmodule(b, "vendor/sub", "sub.txt");
        var b1 = await ws.OpenSubmoduleAsync(b, bSub);
        var c = await ws.OpenAsync(cRepo.Path);
        // _open: [a, b, b1, c]

        ws.Move(a, 2);

        // A moves past B's group; B and B1 stay contiguous.
        Assert.Equal([b, b1, a, c], ws.OpenRepositories);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task Move_right_with_an_index_inside_a_group_snaps_forward()
    {
        var ws = NewWorkspace();
        using var aRepo = Repo("a.txt");
        using var bRepo = Repo("b.txt");
        using var cRepo = Repo("c.txt");

        var a = await ws.OpenAsync(aRepo.Path);
        var b = await ws.OpenAsync(bRepo.Path);
        var bSub = MakeNestedSubmodule(b, "vendor/sub", "sub.txt");
        var b1 = await ws.OpenSubmoduleAsync(b, bSub);
        var c = await ws.OpenAsync(cRepo.Path);
        // _open: [a, b, b1, c]

        ws.Move(a, 1);

        // newIndex=1 is inside B's group (boundaries 0 and 2 are equidistant from 1).
        // Tie snaps in direction of travel (forward) -> [b, b1, a, c].
        Assert.Equal([b, b1, a, c], ws.OpenRepositories);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task Move_a_group_right_past_another_group()
    {
        var ws = NewWorkspace();
        using var oRepo = Repo("o.txt");
        using var pRepo = Repo("p.txt");
        using var sRepo = Repo("s.txt");

        var o = await ws.OpenAsync(oRepo.Path);
        var oSub = MakeNestedSubmodule(o, "vendor/c1", "c1.txt");
        var c1 = await ws.OpenSubmoduleAsync(o, oSub);

        var p = await ws.OpenAsync(pRepo.Path);
        var pSub = MakeNestedSubmodule(p, "vendor/p1", "p1.txt");
        var p1 = await ws.OpenSubmoduleAsync(p, pSub);

        var s = await ws.OpenAsync(sRepo.Path);
        // _open: [o, c1, p, p1, s]

        ws.Move(o, 2);
        Assert.Equal([p, p1, o, c1, s], ws.OpenRepositories);

        ws.Move(o, 3);
        Assert.Equal([p, p1, s, o, c1], ws.OpenRepositories);

        await ws.DisposeAsync();
    }

    [Fact]
    public async Task Move_to_its_own_position_raises_nothing()
    {
        var ws = NewWorkspace();
        using var aRepo = Repo("a.txt");
        using var bRepo = Repo("b.txt");
        var a = await ws.OpenAsync(aRepo.Path);
        var b = await ws.OpenAsync(bRepo.Path);

        var raised = false;
        ws.OpenRepositoriesChanged += (_, _) => raised = true;

        ws.Move(a, 0);

        Assert.False(raised);
        Assert.Equal([a, b], ws.OpenRepositories);
        await ws.DisposeAsync();
    }
}

[Trait("tier", "git")]
public class WorkspaceStatusLoadTests : WorkspaceServiceTestBase
{
    private sealed class StatusInterceptingGitCliService(IGitCliService inner) : IGitCliService
    {
        public bool IsGitAvailable => inner.IsGitAvailable;

        public string? TargetRepoPath { get; set; }
        public TaskCompletionSource StatusStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStatus { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailStatus { get; set; }

        public async Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
        {
            if (request.Arguments.Count > 0 && request.Arguments[0] == "status")
            {
                var match = TargetRepoPath is null ||
                    (request.WorkingDirectory is not null &&
                     string.Equals(Norm(request.WorkingDirectory), Norm(TargetRepoPath), StringComparison.OrdinalIgnoreCase));

                if (match)
                {
                    StatusStarted.TrySetResult();
                    if (FailStatus)
                    {
                        return new GitCliResult(
                            ExitCode: 1,
                            StandardOutput: "",
                            StandardError: "fatal: simulated status failure",
                            ErrorKind: GitErrorKind.Unknown,
                            Duration: TimeSpan.FromMilliseconds(1));
                    }

                    using var reg = ct.Register(() => ReleaseStatus.TrySetCanceled());
                    await ReleaseStatus.Task.ConfigureAwait(false);
                }
            }

            return await inner.RunAsync(request, ct).ConfigureAwait(false);
        }

        public Task<GitCliResult> RunAsync(
            GitCliRequest request,
            IProgress<GitProgress>? progress,
            Action<string>? onStdoutLine,
            CancellationToken ct = default)
            => inner.RunAsync(request, progress, onStdoutLine, ct);
    }

    [Fact(Timeout = 30000)]
    public async Task Foreground_first_full_load_does_not_wait_for_a_slow_status_scan()
    {
        using var r = Repo("fg-slow-status.txt");
        var realCli = _host.Provider.GetRequiredService<IGitCliService>();
        var interceptor = new StatusInterceptingGitCliService(realCli) { TargetRepoPath = r.Path };
        var provider = new OverrideServiceProvider(_host.Provider, typeof(IGitCliService), interceptor);
        var ws = NewWorkspace(cli: interceptor, provider: provider);

        var ctx = await ws.OpenAsync(r.Path);
        ctx.MarkFirstGraphPaintDone();

        // FirstFullLoad must complete while status is still blocked in the interceptor
        await ctx.FirstFullLoad.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(ctx.FirstFullLoad.IsCompleted);

        // Status started after init
        await interceptor.StatusStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Now release the status scan
        interceptor.ReleaseStatus.SetResult();

        // State.Status is populated within 10s
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (ctx.State.Status is null && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.NotNull(ctx.State.Status);
        await ws.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task Background_init_still_waits_for_status()
    {
        using var r1 = Repo("bg-slow-status-1.txt");
        using var r2 = Repo("bg-slow-status-2.txt");
        File.WriteAllText(AppPaths.SessionFile, new JsonObject
        {
            ["schemaVersion"] = 3,
            ["paths"] = new JsonArray(r1.Path, r2.Path),
            ["focusedPath"] = r1.Path,
        }.ToJsonString());

        var realCli = _host.Provider.GetRequiredService<IGitCliService>();
        var interceptor = new StatusInterceptingGitCliService(realCli) { TargetRepoPath = r2.Path };
        var provider = new OverrideServiceProvider(_host.Provider, typeof(IGitCliService), interceptor);
        var ws = NewWorkspace(cli: interceptor, provider: provider);

        await ws.RestoreSessionAsync();
        var ctx1 = ws.OpenRepositories.First(c => Norm(c.RepositoryPath) == Norm(r1.Path));
        var ctx2 = ws.OpenRepositories.First(c => Norm(c.RepositoryPath) == Norm(r2.Path));

        ctx1.MarkFirstGraphPaintDone();
        await ctx1.FirstFullLoad.WaitAsync(TimeSpan.FromSeconds(10));

        // Start background init for ctx2. No graph control paints headlessly, so mark it here or
        // init waits out the 8 s graph-priority ceiling before status ever starts.
        ctx2.MarkFirstGraphPaintDone();
        ws.EnsureInitialized(ctx2);

        // Wait until status starts for ctx2
        await interceptor.StatusStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Background-initialized tab stays incomplete while status is blocked
        Assert.False(ctx2.FirstFullLoad.IsCompleted);

        // Release status
        interceptor.ReleaseStatus.SetResult();

        // Completes after release
        await ctx2.FirstFullLoad.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(ctx2.FirstFullLoad.IsCompleted);

        await ws.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task A_failed_status_after_init_does_not_fault_first_load()
    {
        using var r = Repo("failed-status.txt");
        var realCli = _host.Provider.GetRequiredService<IGitCliService>();
        var interceptor = new StatusInterceptingGitCliService(realCli)
        {
            TargetRepoPath = r.Path,
            FailStatus = true,
        };
        var provider = new OverrideServiceProvider(_host.Provider, typeof(IGitCliService), interceptor);
        var ws = NewWorkspace(cli: interceptor, provider: provider);

        var ctx = await ws.OpenAsync(r.Path);
        ctx.MarkFirstGraphPaintDone();

        // FirstFullLoad completes despite failed status
        await ctx.FirstFullLoad.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(ctx.FirstFullLoad.IsCompleted);

        await ws.DisposeAsync();
    }

    [Fact]
    public async Task OpenAsync_non_repo_path_throws_NotAGitRepository()
    {
        var ws = NewWorkspace();
        var nonRepo = TestScratch.NewDirectory("ws-non-repo-");

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(() => ws.OpenAsync(nonRepo));
        Assert.Equal(GitErrorKind.NotAGitRepository, ex.Kind);

        await ws.DisposeAsync();
    }

    [Fact]
    public async Task InitAsync_initializes_and_opens_repository()
    {
        var ws = NewWorkspace();
        var initDir = TestScratch.NewDirectory("ws-init-");

        var ctx = await ws.InitAsync(initDir, "main");
        Assert.NotNull(ctx);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(initDir)), Path.TrimEndingDirectorySeparator(ctx.RepositoryPath));
        Assert.True(Directory.Exists(ctx.GitDirectory));

        await ws.DisposeAsync();
    }
}

/// <summary>Watcher-driven refreshes of the focused tab follow the cost of status, and activating
/// the window refreshes at once. The clock is manual and the refresh is a stand-in that takes the
/// scripted status duration on that clock, so no test waits on wall time.</summary>
[Trait("tier", "git")]
public class WorkspaceServiceRefreshPacingTests : WorkspaceServiceTestBase
{
    private sealed class ManualTime : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly List<(TimerCallback Callback, object? State)> _created = [];
        private long _now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            lock (_gate) return _now;
        }

        public TimeSpan Now => TimeSpan.FromTicks(GetTimestamp());

        /// <summary>The callback and state of the n-th timer created, so a test can fire a timer that was already disposed.</summary>
        public (TimerCallback Callback, object? State) CreatedTimer(int index)
        {
            lock (_gate) return _created[index];
        }

        public int CreatedTimerCount
        {
            get { lock (_gate) return _created.Count; }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate) _created.Add((callback, state));
            timer.Change(dueTime, period);
            return timer;
        }

        /// <summary>Moves the clock forward, firing each timer at its own due time in order —
        /// including timers a callback creates that fall inside the span. The callbacks run on a
        /// pool thread, so the continuations they release run inline there: the work they trigger
        /// has finished when this returns, instead of being queued behind the test's own context.</summary>
        public void Advance(TimeSpan by) => Task.Run(() => AdvanceCore(by)).GetAwaiter().GetResult();

        private void AdvanceCore(TimeSpan by)
        {
            long target;
            lock (_gate) target = _now + by.Ticks;
            while (true)
            {
                ManualTimer? next;
                lock (_gate)
                {
                    next = _timers.Where(t => t.Due is long due && due <= target).OrderBy(t => t.Due).FirstOrDefault();
                    if (next is null)
                    {
                        _now = target;
                        return;
                    }
                    _now = Math.Max(_now, next.Due!.Value);
                    next.Due = next.Period > 0 ? next.Due + next.Period : null;
                }
                next.Callback(next.State);
            }
        }

        private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public long? Due { get; set; }
            public long Period { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime.Ticks;
                    Period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                    if (!owner._timers.Contains(this))
                        owner._timers.Add(this);
                }
                return true;
            }

            public void Dispose()
            {
                lock (owner._gate) owner._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>A workspace whose focused tab's refresh is recorded and takes <c>StatusCost</c> on
    /// the manual clock, with a loaded tab open and the load's own status already finished.</summary>
    private sealed class Rig(WorkspaceService workspace, RepositoryContext context, ManualTime time) : IAsyncDisposable
    {
        public WorkspaceService Workspace => workspace;
        public RepositoryContext Context => context;
        public ManualTime Time => time;
        public TimeSpan StatusCost { get; set; }

        private readonly ConcurrentQueue<(TimeSpan At, bool Full)> _calls = new();
        private readonly SemaphoreSlim _callSignal = new(0);
        private int _baseline;

        /// <summary>The refreshes started since the rig was last reset.</summary>
        public List<(TimeSpan At, bool Full)> Calls => _calls.Skip(_baseline).ToList();

        public void RecordCall(TimeSpan at, bool full)
        {
            _calls.Enqueue((at, full));
            _callSignal.Release();
        }

        public void ResetCalls() => _baseline = _calls.Count;

        public Task Request(bool full = false) => workspace.RequestBackgroundRefreshAsync(context, full);

        /// <summary>Waits until the workspace has seen the paced refresh end. The refresh's thread
        /// reads the clock when it does, so the clock must not move until then.</summary>
        public async Task WaitForPacedRefreshToEndAsync()
        {
            var deadline = Stopwatch.StartNew();
            while (workspace.IsPacedRefreshRunningForTesting(context))
            {
                Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(20), "the refresh never ended");
                await Task.Yield();
            }
        }

        /// <summary>Waits until <paramref name="count"/> refreshes have started since the reset.</summary>
        public async Task WaitForCallsAsync(int count)
        {
            while (Calls.Count < count)
                Assert.True(await _callSignal.WaitAsync(TimeSpan.FromSeconds(20)), "no refresh started");
        }

        public ValueTask DisposeAsync() => workspace.DisposeAsync();
    }

    private async Task<Rig> OpenRigAsync(string path, TimeSpan statusCost)
    {
        var ws = NewWorkspace();
        var time = new ManualTime();
        Rig? rig = null;
        ws.TimeSource = time;
        ws.StatusDurationForTesting = _ => rig!.StatusCost;
        ws.FocusedRefreshForTesting = async (_, full) =>
        {
            // The delay is armed before the call is announced, so a test that reacts to the call
            // advances a clock the refresh is already waiting on.
            var status = rig!.StatusCost > TimeSpan.Zero ? Task.Delay(rig.StatusCost, time) : Task.CompletedTask;
            rig.RecordCall(time.Now, full);
            await status.ConfigureAwait(false);
        };

        var ctx = await ws.OpenAsync(path);
        rig = new Rig(ws, ctx, time) { StatusCost = statusCost };
        ctx.MarkFirstGraphPaintDone();
        await ctx.FirstFullLoad.WaitAsync(TimeSpan.FromSeconds(30));

        // The load's own status runs through the same path; finish it, let pacing see it end at
        // that moment on the clock, then leave the pacing window behind.
        await rig.WaitForCallsAsync(1);
        time.Advance(statusCost);
        await rig.WaitForPacedRefreshToEndAsync();
        time.Advance(TimeSpan.FromSeconds(10));
        rig.ResetCalls();
        return rig;
    }

    private static async Task AllCompleteAsync(IEnumerable<Task> tasks) =>
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20));

    [Fact(Timeout = 60000)]
    public async Task Requests_arriving_during_a_slow_status_lead_to_refreshes_at_least_three_seconds_apart()
    {
        using var repo = Repo("pace.txt");
        await using var rig = await OpenRigAsync(repo.Path, TimeSpan.FromSeconds(1.5));
        var start = rig.Time.Now;

        var requests = new List<Task> { rig.Request() };
        for (var i = 0; i < 60; i++)
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(200));
            requests.Add(rig.Request());
        }

        var starts = rig.Calls.Select(c => c.At - start).ToList();
        Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(9) }, starts);
        for (var i = 1; i < starts.Count; i++)
            Assert.True(starts[i] - starts[i - 1] >= TimeSpan.FromSeconds(3));

        rig.Time.Advance(TimeSpan.FromSeconds(20));
        await AllCompleteAsync(requests);
    }

    [Fact(Timeout = 60000)]
    public async Task A_burst_of_requests_leads_to_one_trailing_refresh()
    {
        using var repo = Repo("burst.txt");
        await using var rig = await OpenRigAsync(repo.Path, TimeSpan.FromSeconds(1.5));
        var start = rig.Time.Now;

        var requests = new List<Task> { rig.Request() };
        for (var i = 0; i < 10; i++)
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(100));
            requests.Add(rig.Request());
        }
        rig.Time.Advance(TimeSpan.FromSeconds(30));
        await AllCompleteAsync(requests);

        Assert.Equal(2, rig.Calls.Count);
        Assert.Equal(TimeSpan.FromSeconds(4.5), rig.Calls[1].At - start);
    }

    [Fact(Timeout = 60000)]
    public async Task A_late_callback_of_a_cancelled_trailing_timer_leaves_the_current_one_alone()
    {
        using var repo = Repo("late-timer.txt");
        await using var rig = await OpenRigAsync(repo.Path, TimeSpan.Zero);

        var first = rig.Request();
        await rig.WaitForPacedRefreshToEndAsync();
        rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        var second = rig.Request();
        var staleIndex = rig.Time.CreatedTimerCount - 1;
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await AllCompleteAsync([first, second]);
        await rig.WaitForPacedRefreshToEndAsync();
        Assert.Equal(2, rig.Calls.Count);

        rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        var third = rig.Request();
        Assert.Equal(2, rig.Calls.Count);

        var stale = rig.Time.CreatedTimer(staleIndex);
        stale.Callback(stale.State);
        await Task.Delay(200);
        Assert.Equal(2, rig.Calls.Count);

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await AllCompleteAsync([third]);
        Assert.Equal(3, rig.Calls.Count);
    }

    [Fact(Timeout = 60000)]
    public async Task A_trailing_refresh_is_full_when_any_request_it_replaces_was()
    {
        using var repo = Repo("trailing-full.txt");
        await using var rig = await OpenRigAsync(repo.Path, TimeSpan.Zero);

        var requests = new List<Task> { rig.Request() };
        rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        requests.Add(rig.Request(full: true));
        rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        requests.Add(rig.Request());
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        await AllCompleteAsync(requests);

        Assert.Equal(new[] { false, true }, rig.Calls.Select(c => c.Full));
    }

    [Fact(Timeout = 60000)]
    public async Task While_the_window_is_active_the_pacing_floor_is_one_second()
    {
        using var repo = Repo("active-floor.txt");
        await using var rig = await OpenRigAsync(repo.Path, TimeSpan.Zero);

        var first = rig.Request();
        rig.Time.Advance(TimeSpan.FromSeconds(0.9));
        var tooSoon = rig.Request();
        Assert.Single(rig.Calls);

        rig.Time.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal(2, rig.Calls.Count);
        await AllCompleteAsync([first, tooSoon]);
    }

    [Fact(Timeout = 60000)]
    public async Task While_the_window_is_inactive_the_pacing_floor_is_three_seconds()
    {
        using var repo = Repo("inactive-floor.txt");
        await using var rig = await OpenRigAsync(repo.Path, TimeSpan.Zero);
        rig.Workspace.SetAppActive(false);
        Assert.False(rig.Workspace.IsAppActive);

        var first = rig.Request();
        rig.Time.Advance(TimeSpan.FromSeconds(2.9));
        var tooSoon = rig.Request();
        Assert.Single(rig.Calls);

        rig.Time.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal(2, rig.Calls.Count);
        await AllCompleteAsync([first, tooSoon]);
    }

    [Fact(Timeout = 60000)]
    public async Task Activation_refreshes_status_at_once_even_inside_the_pacing_window()
    {
        using var repo = Repo("activate.txt");
        await using var rig = await OpenRigAsync(repo.Path, TimeSpan.FromSeconds(1.5));
        rig.Workspace.SetAppActive(false);

        var first = rig.Request();
        rig.Time.Advance(TimeSpan.FromSeconds(1.5));
        await first;
        Assert.Single(rig.Calls);

        var activatedAt = rig.Time.Now;
        rig.Workspace.SetAppActive(true);
        await rig.WaitForCallsAsync(2);

        Assert.Equal(activatedAt, rig.Calls[1].At);
        Assert.False(rig.Calls[1].Full);
        Assert.True(rig.Workspace.IsAppActive);
        rig.Time.Advance(TimeSpan.FromSeconds(2));
    }

    [Fact(Timeout = 60000)]
    public async Task Activation_gives_a_degraded_tab_a_full_refresh()
    {
        using var repo = Repo("degraded.txt");

        // A header claiming a huge index makes the watcher watch .git only and mark itself degraded.
        var index = Path.Combine(repo.Path, ".git", "index");
        var bytes = File.ReadAllBytes(index);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 60_000);
        File.WriteAllBytes(index, bytes);

        await using var rig = await OpenRigAsync(repo.Path, TimeSpan.Zero);
        Assert.True(rig.Context.Watcher.IsDegraded);

        rig.Workspace.SetAppActive(true);
        await rig.WaitForCallsAsync(1);

        Assert.True(rig.Calls[0].Full);
    }

    [Fact(Timeout = 60000)]
    public async Task Deactivation_does_not_refresh()
    {
        using var repo = Repo("deactivate.txt");
        await using var rig = await OpenRigAsync(repo.Path, TimeSpan.Zero);

        rig.Workspace.SetAppActive(false);
        rig.Time.Advance(TimeSpan.FromSeconds(10));

        Assert.Empty(rig.Calls);
    }
}
