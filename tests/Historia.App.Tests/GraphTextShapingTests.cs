namespace HistoriaApp.Tests;

/// <summary>
/// The graph render path creates nothing per frame once warm: every pool and the shaped-text
/// cache count their misses, and a repeat frame must record none. A cached
/// <see cref="FormattedText"/> would pass a miss count while re-shaping every visible string on
/// every draw, which is why the graph draws <c>TextLayout</c>s, and why trimming parity with
/// <see cref="FormattedText"/> output is pinned here too.
/// </summary>
[Trait("tier", "ui")]
public sealed class GraphTextShapingTests
{
    private const int Branches = 6;
    private const int CommitsPerBranch = 40;

    private sealed class FakeThemeProbe : ISystemThemeProbe
    {
        public bool IsLightTheme => false;
        public bool IsHighContrast => false;
#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067
    }

    private sealed class SwitchableThemeService : IThemeService
    {
        public EffectiveTheme Effective { get; private set; } = EffectiveTheme.Dark;
        public ThemeMode CurrentMode => Effective == EffectiveTheme.Light ? ThemeMode.Light : ThemeMode.Dark;
        public bool HighContrastEnabled => false;
        public string EffectiveCssThemeName => Effective == EffectiveTheme.Light ? "light" : "dark";
        public IReadOnlyDictionary<string, Rgb> Palette => HistoriaPalette.For(Effective);
        public IReadOnlyList<Rgb> LaneColors => [];
        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

        public void Switch(EffectiveTheme next)
        {
            var previous = Effective;
            Effective = next;
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(previous, next));
        }

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

    /// <summary><see cref="Branches"/> long-lived branches off one root, commits interleaved by
    /// date, every branch tip a ref badge. Built with one <c>git fast-import</c> run. With
    /// <c>twoRemotes</c>, every branch is also on <c>origin</c> and <c>upstream</c>, so each badge
    /// draws two numbered clouds.</summary>
    private sealed class ParallelBranchesRepo : IDisposable
    {
        public string Path { get; }
        public int CommitCount => 1 + Branches * CommitsPerBranch;

        public ParallelBranchesRepo(bool twoRemotes = false)
        {
            Path = TestScratch.NewDirectory("historia-textshape-");
            RunGit(Path, null, "init", "-q");

            var sb = new System.Text.StringBuilder();
            var when = 1_700_000_000L;
            AppendCommit(sb, "refs/heads/main", mark: 1, from: null, when, "root");
            var tip = new int[Branches];
            var mark = 2;
            for (var n = 0; n < CommitsPerBranch; n++)
            {
                for (var k = 0; k < Branches; k++)
                {
                    when += 60;
                    AppendCommit(sb, k == 0 ? "refs/heads/main" : $"refs/heads/b{k}", mark,
                        from: tip[k] == 0 ? 1 : tip[k], when,
                        $"b{k} commit {n}: a long enough message that a narrow column has to trim it");
                    tip[k] = mark++;
                }
            }
            sb.Append("done\n");
            RunGit(Path, sb.ToString(), "fast-import", "--quiet", "--done");
            RunGit(Path, null, "symbolic-ref", "HEAD", "refs/heads/main");
            if (twoRemotes)
            {
                foreach (var remote in new[] { "origin", "upstream" })
                {
                    RunGit(Path, null, "remote", "add", remote, $"git@example.com:o/{remote}.git");
                    for (var k = 0; k < Branches; k++)
                    {
                        var branch = k == 0 ? "main" : $"b{k}";
                        RunGit(Path, null, "update-ref", $"refs/remotes/{remote}/{branch}", $"refs/heads/{branch}");
                    }
                }
            }
        }

        private static void AppendCommit(System.Text.StringBuilder sb, string branch, int mark, int? from, long when, string message)
        {
            sb.Append("commit ").Append(branch).Append('\n');
            sb.Append("mark :").Append(mark).Append('\n');
            sb.Append("author Test Author <test@example.com> ").Append(when).Append(" +0000\n");
            sb.Append("committer Test Author <test@example.com> ").Append(when).Append(" +0000\n");
            sb.Append("data ").Append(System.Text.Encoding.UTF8.GetByteCount(message)).Append('\n').Append(message).Append('\n');
            if (from is { } f)
                sb.Append("from :").Append(f).Append('\n');
            sb.Append('\n');
        }

        private static void RunGit(string dir, string? stdin, params string[] args)
        {
            TestGit.RunChecked(dir, args, stdin, testIdentity: false);
        }

        public void Dispose() => TestScratch.Delete(Path);
    }

    private static ServiceProvider CreateServices(SwitchableThemeService theme)
    {
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
        return sc.BuildServiceProvider();
    }

    private sealed record Setup(
        CommitGraphControl Graph, ServiceProvider Sp, ParallelBranchesRepo Repo,
        RepositoryContext Context, Window Window, SwitchableThemeService Theme) : IDisposable
    {
        public void Dispose()
        {
            try { Window.Close(); } catch { }
            try
            {
                Sp.GetRequiredService<IRepoLayoutService>()
                    .UpdateGlobalAsync(_ => GraphColumnDefaults.All).GetAwaiter().GetResult();
            }
            catch { }
            try { Context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            try { Sp.Dispose(); } catch { }
            try { Repo.Dispose(); } catch { }
        }
    }

    /// <summary>A graph showing every column (Refs, Graph, Message, Author, Date, SHA).</summary>
    private static Setup CreateGraph(double width = 1600, double height = 900, bool twoRemotes = false)
    {
        var repo = new ParallelBranchesRepo(twoRemotes);
        var theme = new SwitchableThemeService();
        var sp = CreateServices(theme);
        sp.GetRequiredService<IRepoLayoutService>()
            .UpdateGlobalAsync(cols => cols.Select(c => c with { Visible = true }).ToList()).GetAwaiter().GetResult();
        var ws = sp.GetRequiredService<IWorkspaceService>();
        var ctx = ws.OpenAsync(repo.Path).GetAwaiter().GetResult();
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();
        ctx.State.RefreshRefsAsync().GetAwaiter().GetResult();

        var graph = new CommitGraphControl();
        graph.Initialize(sp);
        var window = new Window { Width = width, Height = height, Content = graph };
        window.Show();
        ws.Focus(ctx);
        Dispatcher.UIThread.RunJobs();

        for (var i = 0; i < 500 && graph.RowCountForTesting < repo.CommitCount; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.Equal(repo.CommitCount, graph.RowCountForTesting);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var s = new Setup(graph, sp, repo, ctx, window, theme);
        Assert.All(new[] { GraphColumnKind.Author, GraphColumnKind.Date, GraphColumnKind.Sha },
            k => Assert.Contains(graph.ResolvedColumnsForTesting, c => c.Kind == k));
        return s;
    }

    private static void RenderFrame(CommitGraphControl graph)
    {
        var size = new PixelSize((int)graph.Bounds.Width, (int)graph.Bounds.Height);
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
        using var dc = bitmap.CreateDrawingContext();
        graph.Render(dc);
    }

    private static int VisibleRows(Setup s) => (int)(s.Graph.Bounds.Height / s.Graph.RowHeightValue);

    [AvaloniaFact]
    public void A_repeat_frame_shapes_no_text_and_builds_no_pooled_object()
    {
        using var s = CreateGraph();
        Assert.True(VisibleRows(s) > 20);

        RenderFrame(s.Graph);
        Assert.True(s.Graph.TextMissesForTesting > 0, "the first frame must shape the visible strings");

        s.Graph.ResetMissCountersForTesting();
        RenderFrame(s.Graph);

        Assert.Equal(0, s.Graph.TextMissesForTesting);
        Assert.Equal(0, s.Graph.PoolMissesForTesting);
    }

    [AvaloniaFact]
    public void A_repeat_frame_builds_no_date_or_sha_cell_text()
    {
        using var s = CreateGraph();
        RenderFrame(s.Graph);
        Assert.True(s.Graph.CellTextMissesForTesting >= VisibleRows(s),
            $"the first frame built only {s.Graph.CellTextMissesForTesting} date and SHA cells");

        // A date cell is valid for one minute, so a pass that happens to straddle a minute
        // boundary rebuilds once; a cache that never hits fails every pass.
        var misses = -1;
        for (var pass = 0; pass < 3 && misses != 0; pass++)
        {
            s.Graph.ResetMissCountersForTesting();
            RenderFrame(s.Graph);
            misses = s.Graph.CellTextMissesForTesting;
        }

        Assert.Equal(0, misses);
        Assert.Equal(0, s.Graph.TextMissesForTesting);
        Assert.Equal(0, s.Graph.PoolMissesForTesting);
    }

    [AvaloniaFact]
    public void A_repeat_frame_with_numbered_remote_clouds_shapes_no_text_and_builds_no_pooled_object()
    {
        using var s = CreateGraph(twoRemotes: true);
        RenderFrame(s.Graph);
        Assert.Contains(s.Graph.IconRectsForTesting, r => r.Tip == "2 · upstream");

        s.Graph.ResetMissCountersForTesting();
        RenderFrame(s.Graph);

        Assert.Equal(0, s.Graph.TextMissesForTesting);
        Assert.Equal(0, s.Graph.PoolMissesForTesting);
    }

    [AvaloniaFact]
    public void Scrolling_one_row_shapes_only_the_revealed_rows_strings()
    {
        using var s = CreateGraph();
        RenderFrame(s.Graph);

        // Counters reset before the scroll: the window may render the graph itself on the way.
        s.Graph.ResetMissCountersForTesting();
        s.Graph.SetVerticalOffsetForBench(s.Graph.ScrollOffsetForTesting + s.Graph.RowHeightValue);
        Dispatcher.UIThread.RunJobs();
        RenderFrame(s.Graph);

        // At most two newly exposed rows (one full, one partial), each with four text columns
        // (Message, Author, Date, SHA) and a few badge strings.
        const int perRow = 4 + 4;
        Assert.InRange(s.Graph.TextMissesForTesting, 0, 2 * perRow);
        Assert.Equal(0, s.Graph.PoolMissesForTesting);

        // A jump to unseen rows does shape: the counter is live.
        s.Graph.ResetMissCountersForTesting();
        s.Graph.SetVerticalOffsetForBench(s.Graph.ScrollOffsetForTesting + 3 * VisibleRows(s) * s.Graph.RowHeightValue);
        Dispatcher.UIThread.RunJobs();
        RenderFrame(s.Graph);
        Assert.True(s.Graph.TextMissesForTesting >= VisibleRows(s),
            $"a jump to unseen rows shaped only {s.Graph.TextMissesForTesting} strings");
    }

    [AvaloniaFact]
    public void A_theme_change_clears_the_text_cache_and_reshapes_with_the_new_brushes()
    {
        using var s = CreateGraph();
        RenderFrame(s.Graph);
        RenderFrame(s.Graph);
        var warmCount = s.Graph.TextCacheCountForTesting;
        Assert.True(warmCount > 0);

        // Counters reset before the switch: the window may render the graph itself on the way.
        s.Graph.ResetMissCountersForTesting();
        s.Theme.Switch(EffectiveTheme.Light);
        Dispatcher.UIThread.RunJobs();
        RenderFrame(s.Graph);

        // The same viewport shaped again, with new brushes, and the old layouts are gone rather
        // than kept alongside the new ones.
        Assert.True(s.Graph.TextMissesForTesting > 0, "the first frame after a theme change must re-shape");
        Assert.True(s.Graph.PoolMissesForTesting > 0, "the pools must rebuild with the new palette");
        Assert.True(s.Graph.TextCacheCountForTesting <= warmCount,
            $"the theme change must clear the cache: {warmCount} layouts before, {s.Graph.TextCacheCountForTesting} after");

        s.Graph.ResetMissCountersForTesting();
        RenderFrame(s.Graph);
        Assert.Equal(0, s.Graph.TextMissesForTesting);
        Assert.Equal(0, s.Graph.PoolMissesForTesting);
    }

    [AvaloniaFact]
    public void Trimmed_layouts_match_formatted_text()
    {
        var face = new Typeface(FontFamily.Default);
        var samples = new[]
        {
            "Fix the frame budget regression in the diff minimap when scrolling long files",
            "Kevin Chen",
            "Very Long Author Name With Several Parts",
            "origin/feature/a-rather-long-branch-name",
            "v11.5-terminalUpgrades",
            "HEAD",
            "+3",
            "10/04/2026 - 05:12 AM (2h)",
            "1b4e8e7",
        };
        var cache = new ShapedTextCache();

        foreach (var text in samples)
        {
            foreach (var size in new[] { 11.0, 12.0 })
            {
                var full = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, face, size, Brushes.Black);

                foreach (var width in new[] { 20.0, 48.5, 90.0, 160.0, 600.0, double.PositiveInfinity })
                {
                    var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight, face, size, Brushes.Black)
                    {
                        Trimming = TextTrimming.CharacterEllipsis,
                        MaxLineCount = 1,
                    };
                    if (!double.IsPositiveInfinity(width))
                        ft.MaxTextWidth = width;

                    var layout = cache.Get(text, face, size, Brushes.Black, width, TextTrimming.CharacterEllipsis, maxLines: 1);

                    Assert.True(Math.Abs(ft.Width - layout.Width) <= 0.5,
                        $"\"{text}\" @{size}/{width}: FormattedText {ft.Width:F2} vs TextLayout {layout.Width:F2}");
                    Assert.Equal(ft.Height, layout.Height, 3);
                    Assert.Equal(ft.Baseline, layout.Baseline, 3);

                    var trimmed = ft.Width < full.Width - 0.01;
                    var line = Assert.Single(layout.TextLines);
                    Assert.Equal(trimmed, line.HasCollapsed);
                    if (trimmed)
                    {
                        var last = line.TextRuns[^1];
                        Assert.Contains('…', last.Text.ToString());
                    }
                }
            }
        }
    }
}
