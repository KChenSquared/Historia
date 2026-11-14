namespace Historia.Tests;

/// <summary>
/// Background-tab refresh queue.
/// Verifies that background refreshes drain in app-band tab order,
/// tab reordering updates drain order, interrupted tabs retain front priority,
/// background refreshes wait for the focused tab's initial load, and focused tab
/// refreshes run immediately.
/// </summary>
[Trait("tier", "git")]
public sealed class BackgroundRefreshQueueTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];
    private readonly List<ServiceProvider> _providers = [];

    private WorkspaceService NewWorkspace(IRepoIdentityService? identity = null)
    {
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var recents = new RecentRepositoryService(_host.Settings);
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        identity ??= new RepoIdentityService(cli);
        return new WorkspaceService(_host.Provider, cli, recents, profiles, identity);
    }

    /// <summary>A workspace whose contexts and services all run git through <paramref name="cli"/>.</summary>
    private WorkspaceService NewWorkspace(IGitCliService cli)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService>(_host.Settings);
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton(_host.Locator);
        sc.AddSingleton(cli);
        sc.AddSingleton<ICredentialVaultService, FakeVault>();
        var provider = sc.BuildServiceProvider();
        _providers.Add(provider);

        var recents = new RecentRepositoryService(_host.Settings);
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        return new WorkspaceService(provider, cli, recents, profiles, new RepoIdentityService(cli));
    }

    /// <summary>Delegates to a real CLI and notes, once armed, the repository of each
    /// <c>status</c> run as it starts. The background pump refreshes one tab at a time, so the
    /// order the first status runs start in is the order the pump drained the tabs in; recording it
    /// here avoids the thread-pool hops between a refresh finishing and its awaiter's continuation
    /// running.</summary>
    private sealed class StatusStartRecorder(IGitCliService inner) : IGitCliService
    {
        private readonly List<(string Directory, bool Armed)> _statusStarts = [];
        private int _armed;

        public bool IsGitAvailable => inner.IsGitAvailable;

        /// <summary>Starts counting: only status runs that start from now on are part of the order.</summary>
        public void Arm() => Volatile.Write(ref _armed, 1);

        /// <summary>The labels of <paramref name="tabs"/> in the order their first status run started
        /// after <see cref="Arm"/>; a tab whose status never started is absent.</summary>
        public List<int> FirstStatusOrder(params (int Label, RepositoryContext Context)[] tabs)
        {
            (string Directory, bool Armed)[] starts;
            lock (_statusStarts)
                starts = [.. _statusStarts];

            var order = new List<int>();
            foreach (var (directory, armed) in starts)
            {
                if (!armed)
                    continue;
                foreach (var (label, context) in tabs)
                {
                    if (!order.Contains(label) && SamePath(directory, context.RepositoryPath))
                        order.Add(label);
                }
            }
            return order;
        }

        /// <summary>Every status start in order, for a failure message: the tab label, with a
        /// <c>*</c> for starts before <see cref="Arm"/>.</summary>
        public string Describe(params (int Label, RepositoryContext Context)[] tabs)
        {
            (string Directory, bool Armed)[] starts;
            lock (_statusStarts)
                starts = [.. _statusStarts];

            return string.Join(' ', starts.Select(s =>
            {
                var tab = tabs.FirstOrDefault(t => SamePath(s.Directory, t.Context.RepositoryPath));
                return $"r{(tab.Context is null ? "?" : tab.Label)}{(s.Armed ? "" : "*")}";
            }));
        }

        public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
            => RunAsync(request, progress: null, onStdoutLine: null, ct);

        public Task<GitCliResult> RunAsync(
            GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default)
        {
            if (request.Arguments.Contains("status"))
            {
                lock (_statusStarts)
                    _statusStarts.Add((request.WorkingDirectory, Volatile.Read(ref _armed) == 1));
            }
            return inner.RunAsync(request, progress, onStdoutLine, ct);
        }

        private static bool SamePath(string x, string y) =>
            string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(x)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(y)), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Lets the status refreshes that opening the tabs leaves behind finish before the
    /// queue is loaded: a tab's status after its first load, the refreshes its watcher raises, and
    /// the index stat refresh a slow status starts. Left running, one of them can start a status
    /// after the pump is released and be mistaken for the pump's own run. Every tab but
    /// <paramref name="focused"/> is refreshed through the background queue, which also drains
    /// anything the pump was still holding.</summary>
    private static async Task SettleOpeningRefreshesAsync(
        WorkspaceService ws, RepositoryContext focused, params RepositoryContext[] background)
    {
        var refreshes = background.Select(tab => ws.RequestBackgroundRefreshAsync(tab)).ToList();
        refreshes.Add(focused.State.RefreshStatusAsync());
        await Task.WhenAll(refreshes).WaitAsync(TimeSpan.FromSeconds(30));

        var indexRefreshes = background.Append(focused)
            .Select(tab => tab.State is RepositoryStateService state ? state.IndexStatRefreshTask : Task.CompletedTask);
        await Task.WhenAll(indexRefreshes).WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static void AssertDrainOrder(
        StatusStartRecorder cli, int[] expected, params (int Label, RepositoryContext Context)[] tabs)
    {
        var actual = cli.FirstStatusOrder(tabs);
        Assert.True(expected.SequenceEqual(actual),
            $"Expected drain order [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]. "
            + $"Every status start (* = before the pump was released): {cli.Describe(tabs)}");
    }

    private TestRepositoryBuilder CreateRepo(string name)
    {
        var r = TestRepositoryBuilder.FromTemplate($"brq-{name}", b =>
        {
            b.WriteFile("a.txt", $"{name}\n").Stage();
            b.Commit("init");
        });
        _repos.Add(r);
        return r;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _providers)
            await p.DisposeAsync();
        await _host.DisposeAsync();
        foreach (var r in _repos)
            r.Dispose();
    }

    [Fact(Timeout = 60000)]
    public async Task Pump_drains_in_app_band_tab_order_not_enqueue_order()
    {
        var cli = new StatusStartRecorder(_host.Cli);
        var ws = NewWorkspace(cli);
        var r1 = await ws.OpenAsync(CreateRepo("r1").Path);
        r1.MarkFirstGraphPaintDone();
        await r1.FirstFullLoad;

        var r2 = await ws.OpenAsync(CreateRepo("r2").Path);
        r2.MarkFirstGraphPaintDone();
        await r2.FirstFullLoad;

        var r3 = await ws.OpenAsync(CreateRepo("r3").Path);
        r3.MarkFirstGraphPaintDone();
        await r3.FirstFullLoad;

        await SettleOpeningRefreshesAsync(ws, r3, r1, r2);

        // Open r0 as the focused tab, but do NOT complete its FirstGraphPaint yet so the pump holds.
        var r0 = await ws.OpenAsync(CreateRepo("r0").Path);
        ws.Move(r0, 0); // Put r0 at index 0. Order: [r0 (focused, loading), r1, r2, r3]

        // Enqueue in reverse order: r3, then r2, then r1
        var t3 = ws.RequestBackgroundRefreshAsync(r3);
        var t2 = ws.RequestBackgroundRefreshAsync(r2);
        var t1 = ws.RequestBackgroundRefreshAsync(r1);

        // Release focused tab load so the pump drains:
        cli.Arm();
        r0.MarkFirstGraphPaintDone();
        await r0.FirstFullLoad;

        await Task.WhenAll(t1, t2, t3).WaitAsync(TimeSpan.FromSeconds(30));

        // App-band order was r1 (idx 1), r2 (idx 2), r3 (idx 3):
        AssertDrainOrder(cli, [1, 2, 3], (1, r1), (2, r2), (3, r3));

        await ws.DisposeAsync();
    }

    [Fact(Timeout = 60000)]
    public async Task Reordering_tabs_changes_the_drain_order()
    {
        var cli = new StatusStartRecorder(_host.Cli);
        var ws = NewWorkspace(cli);
        var r1 = await ws.OpenAsync(CreateRepo("r1").Path);
        r1.MarkFirstGraphPaintDone();
        await r1.FirstFullLoad;

        var r2 = await ws.OpenAsync(CreateRepo("r2").Path);
        r2.MarkFirstGraphPaintDone();
        await r2.FirstFullLoad;

        var r3 = await ws.OpenAsync(CreateRepo("r3").Path);
        r3.MarkFirstGraphPaintDone();
        await r3.FirstFullLoad;

        await SettleOpeningRefreshesAsync(ws, r3, r1, r2);

        var r0 = await ws.OpenAsync(CreateRepo("r0").Path);
        ws.Move(r0, 0);

        // Move r3 to index 1: order becomes [r0 (focused, loading), r3 (idx 1), r1 (idx 2), r2 (idx 3)]
        ws.Move(r3, 1);

        var t1 = ws.RequestBackgroundRefreshAsync(r1);
        var t2 = ws.RequestBackgroundRefreshAsync(r2);
        var t3 = ws.RequestBackgroundRefreshAsync(r3);

        // Release focused tab:
        cli.Arm();
        r0.MarkFirstGraphPaintDone();
        await r0.FirstFullLoad;

        await Task.WhenAll(t1, t2, t3).WaitAsync(TimeSpan.FromSeconds(30));

        // After reordering, r3 is index 1, r1 is index 2, r2 is index 3:
        AssertDrainOrder(cli, [3, 1, 2], (1, r1), (2, r2), (3, r3));

        await ws.DisposeAsync();
    }

    [Fact(Timeout = 60000)]
    public async Task Interrupted_focused_tab_still_jumps_the_queue()
    {
        var cli = new StatusStartRecorder(_host.Cli);
        var ws = NewWorkspace(cli);
        var r0 = await ws.OpenAsync(CreateRepo("r0").Path);
        r0.MarkFirstGraphPaintDone();
        await r0.FirstFullLoad;

        var r1 = await ws.OpenAsync(CreateRepo("r1").Path);
        r1.MarkFirstGraphPaintDone();
        await r1.FirstFullLoad;

        await SettleOpeningRefreshesAsync(ws, r1, r0);

        // Open r2 and focus it, but do NOT complete its paint yet:
        var r2 = await ws.OpenAsync(CreateRepo("r2").Path);

        // Now focus r0 again while r2's initial load was in progress.
        // This interrupts r2 and puts it into the background queue with priority.
        cli.Arm();
        ws.Focus(r0);

        // Enqueue r1 as well. In _open, r1 is index 1, r2 is index 2.
        var t1 = ws.RequestBackgroundRefreshAsync(r1);

        // Mark r2 paint done so when the pump picks r2, it can complete:
        r2.MarkFirstGraphPaintDone();

        await Task.WhenAll(r2.FirstFullLoad, t1).WaitAsync(TimeSpan.FromSeconds(30));

        // r2 should have drained before r1 even though r1 had a lower index in _open:
        AssertDrainOrder(cli, [2, 1], (1, r1), (2, r2));

        await ws.DisposeAsync();
    }

    [Fact(Timeout = 60000)]
    public async Task Background_watcher_refresh_waits_for_the_focused_tab()
    {
        var ws = NewWorkspace();
        var r1 = await ws.OpenAsync(CreateRepo("r1").Path);
        r1.MarkFirstGraphPaintDone();
        await r1.FirstFullLoad;

        // Open r0 as focused tab, but do not complete its FirstGraphPaint:
        var r0 = await ws.OpenAsync(CreateRepo("r0").Path);

        // Request background refresh for r1:
        var refreshTask = ws.RequestBackgroundRefreshAsync(r1);

        // Background refresh should be waiting on r0:
        await Task.Delay(200);
        Assert.False(refreshTask.IsCompleted);

        // Complete r0:
        r0.MarkFirstGraphPaintDone();
        await r0.FirstFullLoad;

        // Now r1's refresh completes:
        await refreshTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(refreshTask.IsCompletedSuccessfully);

        await ws.DisposeAsync();
    }

    [Fact(Timeout = 60000)]
    public async Task Focused_tab_refresh_is_not_queued()
    {
        var ws = NewWorkspace();
        var r0 = await ws.OpenAsync(CreateRepo("r0").Path);
        r0.MarkFirstGraphPaintDone();
        await r0.FirstFullLoad;

        Assert.True(ReferenceEquals(ws.Focused, r0));

        var refreshTask = ws.RequestBackgroundRefreshAsync(r0);

        // Focused tab refresh executes immediately:
        await refreshTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(refreshTask.IsCompletedSuccessfully);

        await ws.DisposeAsync();
    }
}
