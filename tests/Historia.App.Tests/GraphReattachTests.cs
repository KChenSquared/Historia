namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public sealed class GraphReattachTests
{
    private sealed class FakeThemeProbe : ISystemThemeProbe
    {
        public bool IsLightTheme => false;
        public bool IsHighContrast => false;
#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067
    }

    private sealed class StubThemeService : IThemeService
    {
        public EffectiveTheme Effective { get; set; } = EffectiveTheme.Dark;
        public ThemeMode CurrentMode => Effective == EffectiveTheme.Light ? ThemeMode.Light : ThemeMode.Dark;
        public bool HighContrastEnabled => Effective == EffectiveTheme.HighContrast;
        public string EffectiveCssThemeName => Effective == EffectiveTheme.Light ? "light" : "dark";
        public IReadOnlyDictionary<string, Rgb> Palette => HistoriaPalette.For(Effective);
        public IReadOnlyList<Rgb> LaneColors => [];

#pragma warning disable CS0067
        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
#pragma warning restore CS0067

        public Task ApplyModeAsync(ThemeMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public bool CustomThemeActive => false;
        public Task SetCustomThemeEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task SelectCustomThemeAsync(string themeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHighContrastAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeVault : ICredentialVaultService
    {
        public Task StoreTokenAsync(string key, string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> RetrieveTokenAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteTokenAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestRepo : IDisposable
    {
        public string Path { get; }

        public TestRepo()
        {
            Path = TestScratch.NewDirectory("historia-reattach-");
            RunGit("init", "-q");
            RunGit("config", "user.name", "Test User");
            RunGit("config", "user.email", "test@example.com");
            WriteFile("initial.txt", "initial");
            RunGit("add", "initial.txt");
            RunGit("commit", "-m", "initial commit", "--no-gpg-sign");
        }

        public void WriteFile(string file, string content)
        {
            var full = System.IO.Path.Combine(Path, file);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public string RunGit(params string[] args)
        {
            return TestGit.RunChecked(Path, args, timeout: TimeSpan.FromSeconds(30));
        }

        public void Dispose() => TestScratch.Delete(Path);
    }

    private sealed record TestSetup(
        CommitGraphControl Graph,
        ServiceProvider Services,
        TestRepo Repo,
        RepositoryContext Context,
        Window Window,
        StubThemeService Theme,
        ISettingsService Settings,
        bool InitialShowAuthorDate) : IDisposable
    {
        public void Dispose()
        {
            try { Window.Close(); } catch { }
            try
            {
                Settings.UpdateAsync(s => s with { ShowAuthorDate = InitialShowAuthorDate }).GetAwaiter().GetResult();
            }
            catch { }
            try
            {
                Services.GetRequiredService<IRepoLayoutService>()
                    .UpdateGlobalAsync(_ => GraphColumnDefaults.All).GetAwaiter().GetResult();
            }
            catch { }
            try { Context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            try { Services.Dispose(); } catch { }
            try { Repo.Dispose(); } catch { }
        }
    }

    /// <summary>Pumps the dispatcher until <paramref name="condition"/> holds; the generous limit
    /// covers a machine busy with other builds.</summary>
    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
                return true;
            if (Environment.TickCount64 > deadline)
                return false;
            await Task.Delay(20);
        }
    }

    private static async Task<TestSetup> CreateSetupAsync(Action<TestRepo>? seed = null)
    {
        var repo = new TestRepo();
        seed?.Invoke(repo);
        var theme = new StubThemeService();
        var sc = new ServiceCollection();
        var settings = SettingsService.Load();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<SettingsService>(settings);
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton<IDialogService, Historia.Platform.AvaloniaDialogService>();
        sc.AddSingleton<IScrollDirectionProbe, Historia.Platform.ScrollDirectionProbe>();
        sc.AddSingleton<IRepoActionBus, RepoActionBus>();
        sc.AddSingleton<IRepoNetworkActionRunner, RepoNetworkActionRunner>();
        sc.AddSingleton<IAutoFetchHost, AutoFetchHost>();
        sc.AddSingleton<IAutoFetchService, AutoFetchService>();
        sc.AddSingleton<INavigationBus, NavigationBus>();
        sc.AddSingleton<IKeyboardCommandRouter, KeyboardCommandRouter>();
        sc.AddSingleton<INotificationService, NotificationService>();
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton<ISystemThemeProbe, FakeThemeProbe>();
        sc.AddSingleton<IThemeService>(theme);
        sc.AddSingleton<ICustomThemeService, CustomThemeService>();
        sc.AddSingleton<IWorkspaceService, WorkspaceService>();
        sc.AddSingleton<IProfileService, ProfileService>();
        sc.AddSingleton<IGitCliService, GitCliService>();
        var locator = new GitExecutableLocator();
        locator.ResolveAsync(null).GetAwaiter().GetResult();
        sc.AddSingleton<IGitExecutableLocator>(locator);
        sc.AddSingleton<IBackgroundWorkGate, BackgroundWorkGate>();
        sc.AddSingleton<IGraphLayoutService, GraphLayoutService>();
        sc.AddSingleton<IRepoStateCache, RepoStateCache>();
        sc.AddSingleton<IStashService, StashService>();
        sc.AddSingleton<IConflictService, ConflictService>();
        sc.AddSingleton<IPatchService, PatchService>();
        sc.AddSingleton<ICherryPickService, CherryPickService>();
        sc.AddSingleton<IRebaseService, RebaseService>();
        sc.AddSingleton<ISubmoduleService, SubmoduleService>();
        sc.AddSingleton<IWorktreeService, WorktreeService>();
        sc.AddSingleton<ISshKeyService, SshKeyService>();
        sc.AddSingleton<ICloneService, CloneService>();
        sc.AddSingleton<ITerminalBackend, PortaPtyBackend>();
        sc.AddSingleton<ITerminalSessionService, TerminalSessionService>();
        sc.AddSingleton<IRecentRepositoryService, RecentRepositoryService>();
        sc.AddSingleton<IRepoIdentityService, RepoIdentityService>();
        sc.AddSingleton<ICredentialVaultService, FakeVault>();
        sc.AddSingleton<IExternalLauncher, ExternalLauncher>();
        var sp = sc.BuildServiceProvider();

        var ws = sp.GetRequiredService<IWorkspaceService>();
        var ctx = await ws.OpenAsync(repo.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();
        await ctx.State.RefreshRefsAsync();

        var graph = new CommitGraphControl();
        graph.Initialize(sp);
        var window = new Window { Width = 1200, Height = 800, Content = graph };
        window.Show();
        ws.Focus(ctx);
        Dispatcher.UIThread.RunJobs();

        for (var i = 0; i < 200 && graph.LayoutForTesting is null; i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }

        return new TestSetup(graph, sp, repo, ctx, window, theme, settings, settings.Current.ShowAuthorDate);
    }

    [AvaloniaFact]
    public async Task A_commit_made_while_detached_paints_on_reattach()
    {
        using var setup = await CreateSetupAsync();
        var graph = setup.Graph;
        var window = setup.Window;
        var placeholder = new Border();

        window.Content = placeholder;
        Dispatcher.UIThread.RunJobs();

        setup.Repo.WriteFile("f2.txt", "second");
        setup.Repo.RunGit("add", "f2.txt");
        setup.Repo.RunGit("commit", "-m", "second commit", "--no-gpg-sign");
        var hash = setup.Repo.RunGit("rev-parse", "HEAD").Trim();
        var newCommitId = GitObjectId.Parse(hash);

        await setup.Context.State.RefreshRefsAsync();
        Dispatcher.UIThread.RunJobs();

        window.Content = graph;
        Dispatcher.UIThread.RunJobs();

        if (graph.ActiveRefsRecomputeTask is { } task)
            await task;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, graph.ResolveRowForTesting(newCommitId));
    }

    [AvaloniaFact]
    public async Task Reattach_with_unchanged_refs_keeps_the_layout()
    {
        using var setup = await CreateSetupAsync();
        var graph = setup.Graph;
        var window = setup.Window;
        var placeholder = new Border();

        var initialLayout = graph.LayoutForTesting;
        Assert.NotNull(initialLayout);
        var initialRecomputes = graph.RefsRecomputeCount;

        window.Content = placeholder;
        Dispatcher.UIThread.RunJobs();

        window.Content = graph;
        Dispatcher.UIThread.RunJobs();

        if (graph.ActiveRefsRecomputeTask is { } task)
            await task;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(initialRecomputes + 1, graph.RefsRecomputeCount);
        Assert.Same(initialLayout, graph.LayoutForTesting);
    }

    [AvaloniaFact]
    public async Task Reattach_with_unchanged_refs_keeps_the_find_cursor()
    {
        using var setup = await CreateSetupAsync(repo =>
        {
            foreach (var name in new[] { "one", "two", "three" })
            {
                repo.WriteFile($"{name}.txt", name);
                repo.RunGit("add", $"{name}.txt");
                repo.RunGit("commit", "-m", $"needle {name}", "--no-gpg-sign");
            }
        });
        var graph = setup.Graph;
        var window = setup.Window;
        var placeholder = new Border();

        graph.SearchBoxForTesting.Text = "needle";
        Assert.True(await WaitForAsync(() => graph.MatchRowsForTesting.Count == 3), "the search never landed");
        graph.StepMatchForTesting(1);
        var steppedRow = graph.CurrentMatchRowForTesting;
        Assert.NotEqual(-1, steppedRow);

        window.Content = placeholder;
        Dispatcher.UIThread.RunJobs();

        window.Content = graph;
        Dispatcher.UIThread.RunJobs();

        if (graph.ActiveRefsRecomputeTask is { } task)
            await task;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(steppedRow, graph.CurrentMatchRowForTesting);
    }

    /// <summary>A graph off the visual tree, its repository still open. Holds the repository
    /// only through a weak reference, so the test can watch it being collected.</summary>
    private sealed record DetachedGraph(
        CommitGraphControl Graph, Window Window, ServiceProvider Services, TestRepo Repo,
        WeakReference Context) : IDisposable
    {
        public void Dispose()
        {
            try { Window.Close(); } catch { }
            try { Services.Dispose(); } catch { }
            try { Repo.Dispose(); } catch { }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<DetachedGraph> OpenDetachedGraphAsync()
    {
        var setup = await CreateSetupAsync();
        setup.Window.Content = new Border();
        Dispatcher.UIThread.RunJobs();
        return new DetachedGraph(setup.Graph, setup.Window, setup.Services, setup.Repo, new WeakReference(setup.Context));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task CloseTheOpenRepositoryAsync(IWorkspaceService workspace)
    {
        var context = Assert.Single(workspace.OpenRepositories);
        await workspace.CloseAsync(context);
    }

    [AvaloniaFact]
    public async Task A_detached_graph_lets_a_closed_repository_be_collected()
    {
        using var detached = await OpenDetachedGraphAsync();
        Assert.NotNull(detached.Graph.LayoutForTesting);

        await CloseTheOpenRepositoryAsync(detached.Services.GetRequiredService<IWorkspaceService>());

        // The release is posted to the dispatcher when the tab closes; the wait is long because
        // collection and the post both slow down on a loaded machine.
        var collected = await WaitForAsync(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return !detached.Context.IsAlive;
        }, timeoutMs: 30_000);

        Assert.True(collected, "the detached graph still holds the closed repository");
        Assert.Null(detached.Graph.ContextForTesting);
        Assert.Null(detached.Graph.LayoutForTesting);
    }

    [AvaloniaFact]
    public async Task A_detached_graph_keeps_its_layout_while_the_repository_stays_open()
    {
        using var detached = await OpenDetachedGraphAsync();
        var layout = detached.Graph.LayoutForTesting;
        Assert.NotNull(layout);

        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(50);
            GC.Collect();
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(detached.Context.IsAlive);
        Assert.NotNull(detached.Graph.ContextForTesting);
        Assert.Same(layout, detached.Graph.LayoutForTesting);
    }

    [AvaloniaFact]
    public async Task A_head_lookup_that_found_no_row_is_not_repeated_for_the_same_layout()
    {
        using var setup = await CreateSetupAsync();
        var graph = setup.Graph;
        Assert.NotNull(setup.Context.State.CurrentBranch?.TipCommitId);

        // The empty layout has no rows, so HEAD's commit is never found in it.
        var before = graph.HeadRowLookupsForTesting;
        for (var i = 0; i < 3; i++)
            Assert.Equal(-1, graph.ResolveHeadRowForTesting(GraphLayout.Empty));

        Assert.Equal(1, graph.HeadRowLookupsForTesting - before);
    }

    [AvaloniaFact]
    public async Task A_head_lookup_miss_on_one_layout_does_not_answer_for_another_layout()
    {
        using var setup = await CreateSetupAsync();
        var graph = setup.Graph;
        Assert.NotNull(setup.Context.State.CurrentBranch?.TipCommitId);
        var other = new GraphLayout
        {
            Decorations = [],
            RowCount = 0,
            EdgeCount = 0,
            TotalLaneCount = 0,
            IsComplete = true,
            RowByCommitId = new Dictionary<GitObjectId, int>(),
        };

        var before = graph.HeadRowLookupsForTesting;
        Assert.Equal(-1, graph.ResolveHeadRowForTesting(GraphLayout.Empty));
        Assert.Equal(-1, graph.ResolveHeadRowForTesting(GraphLayout.Empty));
        Assert.Equal(1, graph.HeadRowLookupsForTesting - before);

        Assert.Equal(-1, graph.ResolveHeadRowForTesting(other));
        Assert.Equal(2, graph.HeadRowLookupsForTesting - before);
        Assert.Equal(-1, graph.ResolveHeadRowForTesting(other));
        Assert.Equal(2, graph.HeadRowLookupsForTesting - before);
    }

    /// <summary>A layout with no engine behind it. <paramref name="headCommit"/> is findable at
    /// <paramref name="headRow"/>; <paramref name="placed"/> also records that row as the layout's
    /// own HEAD row.</summary>
    private static GraphLayout FakeLayout(int rows, int lanes, GitObjectId? headCommit = null, int headRow = -1,
        bool placed = false)
    {
        var map = new Dictionary<GitObjectId, int>();
        if (headCommit is { } id)
            map[id] = headRow;
        return new GraphLayout
        {
            Decorations = [],
            RowCount = rows,
            EdgeCount = 0,
            TotalLaneCount = lanes,
            IsComplete = false,
            RowByCommitId = map,
            HeadRow = placed ? headRow : -1,
        };
    }

    [AvaloniaFact]
    public async Task A_quiet_extend_that_adds_only_off_screen_rows_does_not_repaint()
    {
        using var setup = await CreateSetupAsync();

        Assert.False(setup.Graph.NoteQuietExtendForTesting(FakeLayout(5_000, 3), FakeLayout(6_000, 3)));
    }

    [AvaloniaFact]
    public async Task A_quiet_extend_that_reaches_the_viewport_repaints()
    {
        using var setup = await CreateSetupAsync();

        Assert.True(setup.Graph.NoteQuietExtendForTesting(FakeLayout(10, 3), FakeLayout(5_010, 3)));
    }

    [AvaloniaFact]
    public async Task A_quiet_extend_that_adds_a_lane_repaints()
    {
        using var setup = await CreateSetupAsync();

        Assert.True(setup.Graph.NoteQuietExtendForTesting(FakeLayout(5_000, 3), FakeLayout(6_000, 4)));
    }

    [AvaloniaFact]
    public async Task A_quiet_extend_that_places_head_repaints()
    {
        using var setup = await CreateSetupAsync();
        var tip = setup.Context.State.CurrentBranch!.TipCommitId!.Value;

        Assert.True(setup.Graph.NoteQuietExtendForTesting(
            FakeLayout(5_000, 3), FakeLayout(6_000, 3, tip, headRow: 5_500, placed: true)));
    }

    [AvaloniaFact]
    public async Task A_quiet_extend_looks_for_a_missed_head_once_per_batch()
    {
        using var setup = await CreateSetupAsync();
        var graph = setup.Graph;
        var tip = setup.Context.State.CurrentBranch!.TipCommitId!.Value;
        var first = FakeLayout(5_000, 3);

        // Nothing has missed HEAD yet, so a batch that does not reach the screen does no lookup.
        var before = graph.HeadRowLookupsForTesting;
        Assert.False(graph.NoteQuietExtendForTesting(first, FakeLayout(5_500, 3)));
        Assert.Equal(before, graph.HeadRowLookupsForTesting);

        Assert.Equal(-1, graph.ResolveHeadRowForTesting(first));
        before = graph.HeadRowLookupsForTesting;

        // A batch that still lacks HEAD costs one lookup and no repaint.
        Assert.False(graph.NoteQuietExtendForTesting(first, FakeLayout(5_500, 3)));
        Assert.Equal(before + 1, graph.HeadRowLookupsForTesting);

        // A batch that brings HEAD in without the layout having placed it repaints.
        Assert.True(graph.NoteQuietExtendForTesting(first, FakeLayout(6_000, 3, tip, headRow: 5_800)));
    }

    [AvaloniaFact]
    public async Task A_quiet_extend_updates_the_scroll_range()
    {
        using var setup = await CreateSetupAsync();
        var graph = setup.Graph;
        var original = graph.LayoutForTesting!;
        var grown = new GraphLayoutResult(FakeLayout(6_000, 3), original.State);

        // Nothing renders between the swap and the restore: no dispatcher jobs run in between.
        try
        {
            Assert.True(graph.SetLayoutForTesting(grown));
            setup.Window.UpdateLayout();
            var stale = graph.VerticalScrollBarForTesting.Maximum;
            Assert.True(stale < 1_000 * graph.RowHeightForTesting, "the scroll range moved before the quiet extend was noted");

            graph.NoteQuietExtendForTesting(original.Layout, grown.Layout);
            setup.Window.UpdateLayout();

            Assert.True(graph.VerticalScrollBarForTesting.Maximum > 5_000 * graph.RowHeightForTesting);
        }
        finally
        {
            graph.SetLayoutForTesting(original);
        }
    }

    [AvaloniaFact]
    public async Task Reattach_restarts_the_extend_pump()
    {
        using var setup = await CreateSetupAsync();
        var graph = setup.Graph;
        var window = setup.Window;
        var placeholder = new Border();

        window.Content = placeholder;
        Dispatcher.UIThread.RunJobs();

        Assert.False(graph.ExtendPumpActiveForTesting);

        window.Content = graph;
        Dispatcher.UIThread.RunJobs();

        if (graph.ActiveRefsRecomputeTask is { } task)
            await task;
        Dispatcher.UIThread.RunJobs();

        Assert.True(graph.ExtendPumpActiveForTesting);
    }

    [AvaloniaFact]
    public async Task Theme_change_while_detached_applies_on_reattach()
    {
        using var setup = await CreateSetupAsync();
        var graph = setup.Graph;
        var window = setup.Window;
        var placeholder = new Border();

        window.Content = placeholder;
        Dispatcher.UIThread.RunJobs();

        setup.Theme.Effective = EffectiveTheme.Light;

        window.Content = graph;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(EffectiveTheme.Light, graph.ThemeModeForTesting);
    }

    [AvaloniaFact]
    public async Task Author_date_setting_changed_while_detached_applies_on_reattach()
    {
        using var setup = await CreateSetupAsync();
        var graph = setup.Graph;
        var window = setup.Window;
        var placeholder = new Border();

        var initialShowAuthorDate = graph.ShowAuthorDateForTesting;

        window.Content = placeholder;
        Dispatcher.UIThread.RunJobs();

        await setup.Settings.UpdateAsync(s => s with { ShowAuthorDate = !initialShowAuthorDate });

        window.Content = graph;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(!initialShowAuthorDate, graph.ShowAuthorDateForTesting);
    }

    [AvaloniaFact]
    public async Task Column_change_while_detached_applies_on_reattach()
    {
        using var setup = await CreateSetupAsync();
        var graph = setup.Graph;
        var window = setup.Window;
        var placeholder = new Border();

        window.Content = placeholder;
        Dispatcher.UIThread.RunJobs();

        var repoLayout = setup.Services.GetRequiredService<IRepoLayoutService>();
        await repoLayout.UpdateGlobalAsync(cols =>
            cols.Select(c => c.Kind == GraphColumnKind.Author ? c with { Visible = false } : c).ToList());

        window.Content = graph;
        Dispatcher.UIThread.RunJobs();

        var expected = repoLayout.Get(setup.Context.RepositoryPath).Columns;
        Assert.Equal(expected.Select(c => (c.Kind, c.Visible)), graph.ColumnsForTesting.Select(c => (c.Kind, c.Visible)));
    }
}
