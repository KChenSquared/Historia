namespace HistoriaApp.Tests;

/// <summary>
/// The graph's Push and create pull request item on a real repository whose remotes are local bare
/// repositories: it pushes the branch through the network runner to its upstream remote (else
/// <c>origin</c>), then opens the dialog; a failed push, a busy repository and a cancelled run open
/// nothing. Create pull request opens the dialog without pushing.
/// </summary>
[Trait("tier", "ui")]
public sealed class GraphPullRequestMenuTests
{
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

    private enum RunMode
    {
        Run,
        Busy,
        Fail,
    }

    /// <summary>Runs the delegate (or, like a busy repository, skips it, or fails) and records the label.</summary>
    private sealed class FakeRunner : IRepoNetworkActionRunner
    {
        public RunMode Mode { get; set; }
        public List<string> Labels { get; } = [];

#pragma warning disable CS0067
        public event EventHandler<RepoNetworkProgress>? ProgressChanged;
#pragma warning restore CS0067

        public void Cancel(RepositoryContext repo)
        {
        }

        public GitProgress? CurrentProgress(RepositoryContext repo) => null;

        public async Task RunExclusiveAsync(
            RepositoryContext repo, string label, Func<IProgress<GitProgress>, CancellationToken, Task> operation)
        {
            Labels.Add(label);
            switch (Mode)
            {
                case RunMode.Busy:
                    return;
                case RunMode.Fail:
                    throw new InvalidOperationException("The push failed.");
                default:
                    await operation(new Progress<GitProgress>(), CancellationToken.None);
                    break;
            }
        }
    }

    private sealed class FakeDialogs : IPullRequestDialogs
    {
        public List<CreatePullRequestRequest> Requests { get; } = [];

        public Task ShowAsync(CreatePullRequestRequest request)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    /// <summary>Two commits on <c>main</c>; the tip also carries <c>feat</c>. Remotes <c>origin</c> and
    /// <c>fork</c> are empty bare repositories; <c>withForkUpstream</c> makes <c>feat</c> track <c>fork/feat</c>.</summary>
    private sealed class RepoFixture : IDisposable
    {
        public string Path { get; }
        public string Origin { get; }
        public string Fork { get; }

        public RepoFixture(bool withForkUpstream)
        {
            Path = TestScratch.NewDirectory("historia-prmenu-");
            Origin = TestScratch.NewDirectory("historia-prmenu-origin-");
            Fork = TestScratch.NewDirectory("historia-prmenu-fork-");
            TestGit.RunChecked(Origin, ["init", "-q", "--bare"], testIdentity: false);
            TestGit.RunChecked(Fork, ["init", "-q", "--bare"], testIdentity: false);

            Git("init", "-q", "-b", "main");
            Git("config", "user.name", "Test");
            Git("config", "user.email", "test@example.com");
            for (var i = 0; i < 2; i++)
            {
                File.WriteAllText(System.IO.Path.Combine(Path, "f.txt"), $"{i}\n");
                Git("add", ".");
                Git("commit", "-q", "-m", $"c{i}", "--no-gpg-sign");
            }
            Git("branch", "feat");
            Git("remote", "add", "origin", Origin);
            Git("remote", "add", "fork", Fork);
            Git("update-ref", "refs/remotes/origin/main", "HEAD");
            if (withForkUpstream)
            {
                Git("update-ref", "refs/remotes/fork/feat", "HEAD~1");
                Git("config", "branch.feat.remote", "fork");
                Git("config", "branch.feat.merge", "refs/heads/feat");
            }
        }

        public string Git(params string[] args) => TestGit.RunChecked(Path, args, testIdentity: false);

        public bool BareHasBranch(string bare, string branch) =>
            TestGit.RunChecked(bare, ["for-each-ref", $"refs/heads/{branch}"], testIdentity: false).Trim().Length > 0;

        public void Dispose()
        {
            TestScratch.Delete(Path);
            TestScratch.Delete(Origin);
            TestScratch.Delete(Fork);
        }
    }

    private static ServiceProvider CreateServices(FakeRunner runner, FakeDialogs dialogs)
    {
        var sc = new ServiceCollection();
        var settings = SettingsService.Load();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<SettingsService>(settings);
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton<IDialogService, Historia.Platform.AvaloniaDialogService>();
        sc.AddSingleton<IScrollDirectionProbe, Historia.Platform.ScrollDirectionProbe>();
        sc.AddSingleton<IRepoActionBus, RepoActionBus>();
        sc.AddSingleton<IRepoNetworkActionRunner>(runner);
        sc.AddSingleton<IPullRequestDialogs>(dialogs);
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
        CommitGraphControl Graph, ServiceProvider Sp, RepoFixture Repo, RepositoryContext Context, Window Window,
        FakeRunner Runner, FakeDialogs Dialogs) : IDisposable
    {
        public INotificationService Notifications => Sp.GetRequiredService<INotificationService>();

        public void Dispose()
        {
            try { Window.Close(); } catch { }
            try { Context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            try { Sp.Dispose(); } catch { }
            try { Repo.Dispose(); } catch { }
        }
    }

    private static Setup CreateGraph(bool withForkUpstream = false, RunMode mode = RunMode.Run)
    {
        var repo = new RepoFixture(withForkUpstream);
        var runner = new FakeRunner { Mode = mode };
        var dialogs = new FakeDialogs();
        var sp = CreateServices(runner, dialogs);
        var ws = sp.GetRequiredService<IWorkspaceService>();
        var ctx = ws.OpenAsync(repo.Path).GetAwaiter().GetResult();
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();
        ctx.State.RefreshRefsAsync().GetAwaiter().GetResult();

        var graph = new CommitGraphControl();
        graph.Initialize(sp);
        var window = new Window { Width = 1400, Height = 600, Content = graph };
        window.Show();
        ws.Focus(ctx);
        Dispatcher.UIThread.RunJobs();

        for (var i = 0; i < 500 && graph.RowCountForTesting < 2; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.Equal(2, graph.RowCountForTesting);
        return new Setup(graph, sp, repo, ctx, window, runner, dialogs);
    }

    private static MenuItem FeatItem(Setup s, string header)
    {
        var feat = s.Graph.BuildGraphContextMenuItems(0, null).OfType<MenuItem>().Single(m => m.Header?.ToString() == "feat");
        return Assert.IsAssignableFrom<IEnumerable<Control>>(feat.ItemsSource).OfType<MenuItem>()
            .Single(m => m.Header?.ToString() == header);
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static async Task PumpUntilAsync(Func<bool> done, string what)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
        Assert.True(done(), $"Timed out waiting for {what}.");
    }

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
    }

    [AvaloniaFact]
    public async Task Push_and_create_pushes_to_upstream_remote_then_opens_dialog()
    {
        using var s = CreateGraph(withForkUpstream: true);

        Click(FeatItem(s, "Push and create pull request…"));
        await PumpUntilAsync(() => s.Dialogs.Requests.Count > 0, "the dialog");

        Assert.Equal(["Push"], s.Runner.Labels);
        Assert.True(s.Repo.BareHasBranch(s.Repo.Fork, "feat"));
        Assert.False(s.Repo.BareHasBranch(s.Repo.Origin, "feat"));
        var request = Assert.Single(s.Dialogs.Requests);
        Assert.Equal("local.feat", request.InitialSource);
        Assert.Contains(request.Branches, b => b.IsRemote && b.Name == "fork/feat");
        Assert.Contains(s.Notifications.Current, n => n.Severity == NotificationSeverity.Success && n.Message == "Pushed feat to fork.");
    }

    [AvaloniaFact]
    public async Task Push_and_create_falls_back_to_origin_and_sets_the_upstream()
    {
        using var s = CreateGraph();

        Click(FeatItem(s, "Push and create pull request…"));
        await PumpUntilAsync(() => s.Dialogs.Requests.Count > 0, "the dialog");

        Assert.True(s.Repo.BareHasBranch(s.Repo.Origin, "feat"));
        Assert.False(s.Repo.BareHasBranch(s.Repo.Fork, "feat"));
        Assert.Equal("origin/feat", s.Context.State.Branches.Single(b => !b.IsRemote && b.Name == "feat").UpstreamName);
    }

    [AvaloniaFact]
    public async Task Push_from_the_dialog_pushes_through_the_same_path()
    {
        using var s = CreateGraph();
        Click(FeatItem(s, "Push and create pull request…"));
        await PumpUntilAsync(() => s.Dialogs.Requests.Count > 0, "the dialog");
        s.Repo.Git("branch", "other");
        await s.Context.State.RefreshRefsAsync();

        var request = s.Dialogs.Requests[0];
        var pushed = await request.Push("other", "origin", CancellationToken.None);

        Assert.True(pushed);
        Assert.Equal(["Push", "Push"], s.Runner.Labels);
        Assert.True(s.Repo.BareHasBranch(s.Repo.Origin, "other"));
        Assert.Contains(request.RefreshBranches(), b => b.IsRemote && b.Name == "origin/other");
    }

    [AvaloniaFact]
    public async Task Push_failure_shows_card_and_no_dialog()
    {
        using var s = CreateGraph(mode: RunMode.Fail);

        Click(FeatItem(s, "Push and create pull request…"));
        await PumpUntilAsync(() => s.Notifications.Current.Any(n => n.Severity == NotificationSeverity.Error), "the error card");
        await SettleAsync();

        Assert.Empty(s.Dialogs.Requests);
        Assert.False(s.Repo.BareHasBranch(s.Repo.Origin, "feat"));
    }

    [AvaloniaFact]
    public async Task Busy_repository_opens_no_dialog()
    {
        using var s = CreateGraph(mode: RunMode.Busy);

        Click(FeatItem(s, "Push and create pull request…"));
        await PumpUntilAsync(() => s.Runner.Labels.Count == 1, "the runner call");
        await SettleAsync();

        Assert.Empty(s.Dialogs.Requests);
        Assert.False(s.Repo.BareHasBranch(s.Repo.Origin, "feat"));
        Assert.DoesNotContain(s.Notifications.Current, n => n.Severity == NotificationSeverity.Success);
    }

    [AvaloniaFact]
    public async Task Create_from_remote_opens_dialog_without_push()
    {
        using var s = CreateGraph();

        await ((IGraphMenuActions)s.Graph).CreatePullRequest("origin", "main");
        await PumpUntilAsync(() => s.Dialogs.Requests.Count > 0, "the dialog");

        Assert.Empty(s.Runner.Labels);
        var request = Assert.Single(s.Dialogs.Requests);
        Assert.Equal("origin.main", request.InitialSource);
        Assert.Contains(request.Remotes, r => r.Name == "origin");
    }
}
