namespace Historia.Tests;

/// <summary>
/// A tab the user leaves while it is still loading stops loading: it never starts identity or
/// refresh work it hadn't begun, and the focused-tab status refresh it had begun is cancelled —
/// a cold <c>git status</c> on a huge working tree must not compete with the tab the user moved
/// on to. The background queue gives it its status later.
/// </summary>
[Trait("tier", "git")]
public sealed class WorkspaceFocusLossTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];
    private readonly List<ServiceProvider> _providers = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _providers)
            await p.DisposeAsync();
        await _host.DisposeAsync();
        foreach (var r in _repos)
            r.Dispose();
    }

    private TestRepositoryBuilder CreateRepo(string name)
    {
        var r = TestRepositoryBuilder.FromTemplate($"focus-loss-{name}", b =>
        {
            b.WriteFile("a.txt", $"{name}\n").Stage();
            b.Commit("init");
        });
        _repos.Add(r);
        return r;
    }

    private sealed class RecordingIdentity(IRepoIdentityService inner) : IRepoIdentityService
    {
        public readonly ConcurrentQueue<RepositoryContext> Resolved = new();

        public Task<RepoIdentityKey> ResolveAsync(RepositoryContext context, CancellationToken ct = default)
        {
            Resolved.Enqueue(context);
            return inner.ResolveAsync(context, ct);
        }
    }

    /// <summary>Recent-list writes that hold, for one repository, until the test releases them.</summary>
    private sealed class GatedRecents(IRecentRepositoryService inner, string gatedPath) : IRecentRepositoryService
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<RecentRepository> Items => inner.Items;
        public Task RemoveAsync(string path, CancellationToken ct = default) => inner.RemoveAsync(path, ct);
        public Task ClearAsync(CancellationToken ct = default) => inner.ClearAsync(ct);

        public event EventHandler? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public async Task AddAsync(string path, string displayName, CancellationToken ct = default)
        {
            if (SamePath(path, gatedPath))
            {
                Entered.TrySetResult();
                await Release.Task.ConfigureAwait(false);
            }
            await inner.AddAsync(path, displayName, ct).ConfigureAwait(false);
        }
    }

    private (WorkspaceService Workspace, RecordingIdentity Identity) NewWorkspace(IGitCliService cli, IRecentRepositoryService? recents = null)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService>(_host.Settings);
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton(_host.Locator);
        sc.AddSingleton(cli);
        sc.AddSingleton<ICredentialVaultService, FakeVault>();
        var provider = sc.BuildServiceProvider();
        _providers.Add(provider);

        var identity = new RecordingIdentity(new RepoIdentityService(cli));
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        var ws = new WorkspaceService(provider, cli, recents ?? new RecentRepositoryService(_host.Settings), profiles, identity);
        return (ws, identity);
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }
        return condition();
    }

    [Fact(Timeout = 60000)]
    public async Task Leaving_a_tab_before_its_first_paint_starts_none_of_its_loading_work()
    {
        var a = CreateRepo("a");
        var b = CreateRepo("b");
        var cli = new BlockingGitCliService(_host.Cli, _ => false);
        var (ws, identity) = NewWorkspace(cli);

        // A is focused and waits for its graph's first paint, which never comes.
        var ctxA = await ws.OpenAsync(a.Path);
        // Opening B moves focus while A is still waiting.
        var ctxB = await ws.OpenAsync(b.Path);
        await Task.Delay(1000);

        Assert.DoesNotContain(ctxA, identity.Resolved);
        Assert.DoesNotContain(cli.Requests, r => BlockingGitCliService.IsIn(r, ctxA.RepositoryPath)
            && (r.Arguments.Contains("for-each-ref") || r.Arguments.Contains("rev-list")));

        ctxB.MarkFirstGraphPaintDone();
        ctxA.MarkFirstGraphPaintDone();
        await ws.DisposeAsync();
    }

    /// <summary>Opening a tab displaces a still-loading one, which is requeued and the background
    /// pump started. The new tab is the foreground load from that moment: the pump must not re-run
    /// the displaced tab in the gap before the new tab's own load starts (here held open by its
    /// Recent-list write), or the two loads compete.</summary>
    [Fact(Timeout = 60000)]
    public async Task A_displaced_tab_waits_for_the_tab_that_displaced_it_even_before_its_load_starts()
    {
        var a = CreateRepo("a");
        var b = CreateRepo("b");
        var cli = new BlockingGitCliService(_host.Cli, _ => false);
        var recents = new GatedRecents(new RecentRepositoryService(_host.Settings), b.Path);
        var (ws, identity) = NewWorkspace(cli, recents);
        RepositoryContext? ctxB = null;
        try
        {
            // A is focused and waits for its graph's first paint.
            var ctxA = await ws.OpenAsync(a.Path);
            var openB = ws.OpenAsync(b.Path);
            await recents.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

            // B has focus and A is requeued; B's load has not started yet. A's graph paints now, so
            // a re-run of A would go straight on to resolve its identity.
            ctxA.MarkFirstGraphPaintDone();
            Assert.False(await EventuallyAsync(() => identity.Resolved.Contains(ctxA), TimeSpan.FromSeconds(1.5)),
                "the displaced tab was re-run before the tab that displaced it started loading");

            recents.Release.TrySetResult();
            ctxB = await openB.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(await EventuallyAsync(() => identity.Resolved.Contains(ctxA), TimeSpan.FromSeconds(1)),
                "the displaced tab was re-run while the focused tab was still loading");
            Assert.False(ctxA.FirstFullLoad.IsCompleted);
        }
        finally
        {
            recents.Release.TrySetResult();
            ctxB?.MarkFirstGraphPaintDone();
            await ws.DisposeAsync();
        }
    }

    private static bool SamePath(string x, string y) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(x)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(y)), StringComparison.OrdinalIgnoreCase);

    /// <summary>A focus change that lands as a tab's first load ends finds the load either still
    /// running (interrupted and requeued) or settled — never finished with its status already
    /// started. Trap: a status started inside the load, before it is marked done, opens a window in
    /// which a focus change or a focus-cancelled refresh requeues the finished tab as an init,
    /// whose refresh then joins the running status on a token focus loss can't cancel.</summary>
    [Fact(Timeout = 60000)]
    public async Task A_focus_change_as_a_first_load_ends_never_finds_its_status_started()
    {
        var a = CreateRepo("a");
        var b = CreateRepo("b");
        var postLoadStatusStarted = 0;
        var cli = new BlockingGitCliService(_host.Cli, r =>
        {
            if (BlockingGitCliService.IsIn(r, a.Path) && r.Arguments.Contains("status")
                && Environment.StackTrace.Contains("RefreshStatusAfterInitAsync", StringComparison.Ordinal))
                Interlocked.Exchange(ref postLoadStatusStarted, 1);
            return false;
        });
        var (ws, _) = NewWorkspace(cli);

        RepositoryContext? ctxB = null;
        int? statusStartedAtFocusChange = null;
        WorkspaceService.TestBeforeInitSettles = async ctx =>
        {
            if (!SamePath(ctx.RepositoryPath, a.Path))
                return;
            statusStartedAtFocusChange = Volatile.Read(ref postLoadStatusStarted);
            ctxB = await ws.OpenAsync(b.Path);
        };
        try
        {
            var ctxA = await ws.OpenAsync(a.Path);
            ctxA.MarkFirstGraphPaintDone();
            Assert.True(await EventuallyAsync(() => ctxB is not null, TimeSpan.FromSeconds(20)));

            Assert.Equal(0, statusStartedAtFocusChange);
            // The focus change caught the load before it settled: interrupted, not completed.
            Assert.False(ctxA.FirstFullLoad.IsCompleted);
        }
        finally
        {
            WorkspaceService.TestBeforeInitSettles = null;
            ctxB?.MarkFirstGraphPaintDone();
            await ws.DisposeAsync();
        }
    }

    /// <summary>A tab runs one init attempt at a time. When the background queue has already
    /// started one (a refresh queued while the tab was open but not yet focused), the open path
    /// does not start a second: an untracked attempt would keep the tab's status running on a
    /// token a focus change can't cancel.</summary>
    [Fact(Timeout = 60000)]
    public async Task Opening_a_tab_the_queue_already_initialises_starts_no_second_init()
    {
        var a = CreateRepo("a");
        var (ws, identity) = NewWorkspace(_host.Cli);
        WorkspaceService.TestAfterTabAddedBeforeFocus = async ctx =>
        {
            // As a watcher event would: the tab isn't focused yet, so this queues its init.
            _ = ws.RequestBackgroundRefreshAsync(ctx);
            Assert.True(await EventuallyAsync(() => ws.HasInitAttemptForTesting(ctx), TimeSpan.FromSeconds(20)));
        };
        try
        {
            var ctxA = await ws.OpenAsync(a.Path);
            WorkspaceService.TestAfterTabAddedBeforeFocus = null;
            ctxA.MarkFirstGraphPaintDone();
            await ctxA.FirstFullLoad.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(await EventuallyAsync(() => !ws.HasInitAttemptForTesting(ctxA), TimeSpan.FromSeconds(20)));

            Assert.Single(identity.Resolved, c => ReferenceEquals(c, ctxA));
        }
        finally
        {
            WorkspaceService.TestAfterTabAddedBeforeFocus = null;
            await ws.DisposeAsync();
        }
    }

    /// <summary>A tab the background queue loaded while it took focus stays the queue's
    /// foreground tab after its load completes (only a foreground attempt clears that). The queue
    /// must not keep waiting on that finished load: another tab's queued refresh still runs.</summary>
    [Fact(Timeout = 60000)]
    public async Task A_tab_the_queue_loaded_while_it_took_focus_never_holds_up_the_queue()
    {
        var a = CreateRepo("a");
        var b = CreateRepo("b");
        var (ws, _) = NewWorkspace(_host.Cli);
        try
        {
            var ctxB = await ws.OpenAsync(b.Path);
            ctxB.MarkFirstGraphPaintDone();
            await ctxB.FirstFullLoad.WaitAsync(TimeSpan.FromSeconds(30));

            WorkspaceService.TestAfterTabAddedBeforeFocus = async ctx =>
            {
                // As a watcher event would: the tab isn't focused yet, so this queues its init.
                _ = ws.RequestBackgroundRefreshAsync(ctx);
                Assert.True(await EventuallyAsync(() => ws.HasInitAttemptForTesting(ctx), TimeSpan.FromSeconds(20)));
            };
            var ctxA = await ws.OpenAsync(a.Path);
            WorkspaceService.TestAfterTabAddedBeforeFocus = null;
            ctxA.MarkFirstGraphPaintDone();
            await ctxA.FirstFullLoad.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(await EventuallyAsync(() => !ws.HasInitAttemptForTesting(ctxA), TimeSpan.FromSeconds(20)));

            // B is open and unfocused, so its refresh goes through the background queue.
            var refresh = ws.RequestBackgroundRefreshAsync(ctxB, full: true);
            Assert.Same(refresh, await Task.WhenAny(refresh, Task.Delay(TimeSpan.FromSeconds(20))));
        }
        finally
        {
            WorkspaceService.TestAfterTabAddedBeforeFocus = null;
            await ws.DisposeAsync();
        }
    }

    /// <summary>A tab the workspace opens knows its workspace before its file watcher starts.
    /// A watcher started without one would refresh on the tab's Lifetime — a token focus loss
    /// can't cancel — whenever its first event fired before the workspace was attached.</summary>
    [Fact(Timeout = 60000)]
    public async Task A_tab_opened_by_the_workspace_knows_it_before_its_watcher_starts()
    {
        var a = CreateRepo("a");
        var (ws, _) = NewWorkspace(_host.Cli);

        var ctxA = await ws.OpenAsync(a.Path);

        Assert.True(ctxA.Watcher.StartedWithWorkspaceForTesting);
        ctxA.MarkFirstGraphPaintDone();
        await ws.DisposeAsync();
    }

    /// <summary>Likely flaky under load: it failed once in a full-suite run and passed 3 of 3
    /// runs on its own. Its fixed waits (20 s, 10 s, 20 s) are what a loaded machine can
    /// overrun.</summary>
    [Fact(Timeout = 60000)]
    [Trait("tier", "slow")]
    public async Task Leaving_a_tab_cancels_its_status_refresh_and_the_background_queue_redoes_it()
    {
        var a = CreateRepo("a");
        var b = CreateRepo("b");
        var cli = new BlockingGitCliService(_host.Cli,
            r => BlockingGitCliService.IsIn(r, a.Path) && r.Arguments.Contains("status"));
        var (ws, _) = NewWorkspace(cli);

        var ctxA = await ws.OpenAsync(a.Path);
        ctxA.MarkFirstGraphPaintDone(); // identity + refs, then the focused-tab status refresh
        await cli.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var ctxB = await ws.OpenAsync(b.Path);
        await cli.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cli.Release.TrySetResult();

        // Once B has loaded, the background queue refreshes A's status.
        ctxB.MarkFirstGraphPaintDone();
        Assert.True(await EventuallyAsync(
            () => cli.Requests.Count(r => BlockingGitCliService.IsIn(r, a.Path) && r.Arguments.Contains("status")) >= 2,
            TimeSpan.FromSeconds(20)), "A's status was never refreshed again");

        await ws.DisposeAsync();
    }
}
