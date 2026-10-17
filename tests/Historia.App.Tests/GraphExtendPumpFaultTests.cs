namespace HistoriaApp.Tests;

/// <summary>A background extend that fails after its waiter already left (the user switched tabs
/// mid-crawl) must not resurface as an unobserved task exception — the app turns those into an
/// "unexpected error" notification.</summary>
[Trait("tier", "ui")]
public sealed class GraphExtendPumpFaultTests
{
    private const string InjectedMessage = "injected background extend failure";

    private sealed class FakeThemeProbe : ISystemThemeProbe
    {
        public bool IsLightTheme => false;
        public bool IsHighContrast => false;
#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067
    }

    private sealed class FakeVault : ICredentialVaultService
    {
        public Task StoreTokenAsync(string key, string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> RetrieveTokenAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteTokenAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>The real layout service, except that a background extend waits for the test, then
    /// throws.</summary>
    private sealed class FaultingLayoutService(GraphLayoutService inner) : IGraphLayoutService
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Threw = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<GraphLayoutResult> ExtendLayoutAsync(RepositoryContext context, GraphLayoutResult existing,
            int additionalRows, CancellationToken ct = default, bool background = false)
        {
            if (!background)
                return await inner.ExtendLayoutAsync(context, existing, additionalRows, ct, background);
            Entered.TrySetResult();
            await Release.Task;
            Threw.TrySetResult();
            throw new InvalidOperationException(InjectedMessage);
        }

        public Task<GraphLayoutResult> ComputeLayoutAsync(RepositoryContext context, GraphQuery query, int maxRows = 10_000, CancellationToken ct = default)
            => inner.ComputeLayoutAsync(context, query, maxRows, ct);
        public GraphLayoutResult? TryOptimisticSnapshot(RepositoryContext context, GraphQuery query, int maxRows)
            => inner.TryOptimisticSnapshot(context, query, maxRows);
        public Task<GraphLayoutResult> RecomputeAsync(RepositoryContext context, GraphQuery query, GraphLayoutResult previous,
            InvalidationReason reason, int maxRows = 10_000, CancellationToken ct = default)
            => inner.RecomputeAsync(context, query, previous, reason, maxRows, ct);
        public void Forget(RepositoryContext context) => inner.Forget(context);
        public Task Hibernate(RepositoryContext context) => inner.Hibernate(context);
        public int ClearPersistentCache() => inner.ClearPersistentCache();
        public Task RunHeadlessCacheAsync(RepositoryContext context, GraphQuery query, CancellationToken ct)
            => inner.RunHeadlessCacheAsync(context, query, ct);
        public void EnqueueInactiveTabCaching(RepositoryContext context, GraphQuery query) { }
        public Task CancelInactiveTabCachingAsync(RepositoryContext context) => inner.CancelInactiveTabCachingAsync(context);
        public void RefreshCommitGraphAfterFetch(RepositoryContext context) { }
        public void SweepDiskCache(IReadOnlyCollection<string> openRepositoryPaths) { }
        public Task PrecacheClonedRepositoryAsync(RepositoryContext context, GraphQuery query, IProgress<GitProgress>? progress, CancellationToken ct)
            => inner.PrecacheClonedRepositoryAsync(context, query, progress, ct);
    }

    /// <summary>A linear history longer than the 2,500-row first batch, so the silent crawl runs.
    /// Built with one <c>git fast-import</c>.</summary>
    private sealed class LinearRepo : IDisposable
    {
        public string Path { get; }

        public LinearRepo(int commits)
        {
            Path = TestScratch.NewDirectory("historia-extendfault-");
            RunGit(Path, null, "init", "-q");
            var sb = new System.Text.StringBuilder();
            var when = 1_700_000_000L;
            for (var i = 1; i <= commits; i++)
            {
                var message = $"commit {i}";
                sb.Append("commit refs/heads/main\n").Append("mark :").Append(i).Append('\n')
                  .Append("committer Test <test@example.com> ").Append(when + i * 60).Append(" +0000\n")
                  .Append("data ").Append(message.Length).Append('\n').Append(message).Append('\n');
                if (i > 1)
                    sb.Append("from :").Append(i - 1).Append('\n');
                sb.Append('\n');
            }
            sb.Append("done\n");
            RunGit(Path, sb.ToString(), "fast-import", "--quiet", "--done");
            RunGit(Path, null, "symbolic-ref", "HEAD", "refs/heads/main");
        }

        private static void RunGit(string dir, string? stdin, params string[] args)
        {
            TestGit.RunChecked(dir, args, stdin, testIdentity: false);
        }

        public void Dispose() => TestScratch.Delete(Path);
    }

    private static ServiceProvider CreateServices()
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
        sc.AddSingleton<IThemeService, ThemeService>();
        sc.AddSingleton<ICustomThemeService, CustomThemeService>();
        sc.AddSingleton<IWorkspaceService, WorkspaceService>();
        sc.AddSingleton<IProfileService, ProfileService>();
        sc.AddSingleton<IGitCliService, GitCliService>();
        var locator = new GitExecutableLocator();
        locator.ResolveAsync(null).GetAwaiter().GetResult();
        sc.AddSingleton<IGitExecutableLocator>(locator);
        sc.AddSingleton<IBackgroundWorkGate, BackgroundWorkGate>();
        sc.AddSingleton<GraphLayoutService>();
        sc.AddSingleton<FaultingLayoutService>(sp => new FaultingLayoutService(sp.GetRequiredService<GraphLayoutService>()));
        sc.AddSingleton<IGraphLayoutService>(sp => sp.GetRequiredService<FaultingLayoutService>());
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

    private static void PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && sw.Elapsed < timeout)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    [AvaloniaFact]
    public void A_background_extend_failing_after_a_tab_switch_is_never_unobserved()
    {
        var unobserved = new List<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, e) =>
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => x.Message == InjectedMessage))
                lock (unobserved) unobserved.Add(e.Exception);
        };
        TaskScheduler.UnobservedTaskException += onUnobserved;

        using var big = new LinearRepo(3_200);
        using var small = new LinearRepo(3);
        var sp = CreateServices();
        var window = new Window { Width = 1000, Height = 700 };
        try
        {
            var ws = sp.GetRequiredService<IWorkspaceService>();
            var faulting = sp.GetRequiredService<FaultingLayoutService>();
            var ctx = ws.OpenAsync(big.Path).GetAwaiter().GetResult();
            ctx.MarkFirstGraphPaintDone();
            ctx.MarkFirstFullLoadDone();

            var graph = new CommitGraphControl();
            graph.Initialize(sp);
            window.Content = graph;
            window.Show();
            ws.Focus(ctx);

            // The silent crawl asks for its first background batch once the first 2,500 rows land.
            PumpUntil(() => faulting.Entered.Task.IsCompleted, TimeSpan.FromSeconds(60));
            Assert.True(faulting.Entered.Task.IsCompleted, "the silent crawl never requested a background extend");

            // Switch tabs: the crawl's wait is cancelled and it stops waiting for this request.
            var other = ws.OpenAsync(small.Path).GetAwaiter().GetResult();
            other.MarkFirstGraphPaintDone();
            other.MarkFirstFullLoadDone();
            PumpUntil(() => false, TimeSpan.FromMilliseconds(300));

            // Only now does the abandoned extend fail.
            faulting.Release.TrySetResult();
            PumpUntil(() => faulting.Threw.Task.IsCompleted, TimeSpan.FromSeconds(10));
            PumpUntil(() => false, TimeSpan.FromMilliseconds(300));

            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            lock (unobserved)
                Assert.Empty(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= onUnobserved;
            try { window.Close(); } catch { }
            try { sp.Dispose(); } catch { }
        }
    }
}
