namespace HistoriaApp.Tests;

/// <summary>
/// The sidebar branch menu's pull request items: a local row offers Push and create pull request…,
/// a remote row offers Create pull request… only, both after the merge item, and neither appears
/// when the repository has no remotes or no flow is supplied.
/// </summary>
[Trait("tier", "ui")]
public sealed class SidebarPullRequestMenuTests
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

    private sealed class FakeDialogs : IPullRequestDialogs
    {
        public List<CreatePullRequestRequest> Requests { get; } = [];

        public Task ShowAsync(CreatePullRequestRequest request)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class RepoFixture : IDisposable
    {
        public string Path { get; }

        public RepoFixture(bool withRemote)
        {
            Path = TestScratch.NewDirectory("historia-sidebar-prmenu-");
            RunGit("init", "-q", "-b", "main");
            RunGit("config", "user.name", "Test");
            RunGit("config", "user.email", "test@example.com");
            RunGit("config", "commit.gpgsign", "false");
            File.WriteAllText(System.IO.Path.Combine(Path, "f.txt"), "init\n");
            RunGit("add", ".");
            RunGit("commit", "-q", "-m", "init");
            if (withRemote)
            {
                RunGit("remote", "add", "origin", "https://example.com/repo.git");
                RunGit("update-ref", "refs/remotes/origin/feat", "HEAD");
            }
        }

        private void RunGit(params string[] args) => TestGit.Run(Path, args, testIdentity: false);

        public void Dispose() => TestScratch.Delete(Path);
    }

    private static ServiceProvider CreateServices(FakeDialogs dialogs)
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

    private sealed class Harness : IAsyncDisposable
    {
        public FakeDialogs PullRequestDialogs { get; } = new();
        public ServiceProvider Services { get; }
        public IWorkspaceService Workspace => Services.GetRequiredService<IWorkspaceService>();
        public IRepoActionBus Bus => Services.GetRequiredService<IRepoActionBus>();
        public IDialogService Dialogs => Services.GetRequiredService<IDialogService>();
        public INotificationService Notify => Services.GetRequiredService<INotificationService>();
        public IStashService Stash => Services.GetRequiredService<IStashService>();
        public PullRequestFlow Flow => PullRequestFlow.From(Services)!;

        public Harness() => Services = CreateServices(PullRequestDialogs);

        public async Task<RepositoryContext> OpenAsync(string path)
        {
            var ctx = await Workspace.OpenAsync(path);
            ctx.MarkFirstGraphPaintDone();
            ctx.MarkFirstFullLoadDone();
            await ctx.State.RefreshRefsAsync();
            return ctx;
        }

        public async ValueTask DisposeAsync() => await Services.DisposeAsync();
    }

    private static BranchMenu.Target LocalTarget() => new(
        Name: "feat", CopyName: "feat", TrackingRef: "feat", IsLocal: true,
        TipSha: "1234567890abcdef", CurrentBranch: "main");

    private static BranchMenu.Target RemoteTarget() => new(
        Name: "feat", CopyName: "origin/feat", TrackingRef: "origin/feat", IsLocal: false,
        TipSha: "1234567890abcdef", CurrentBranch: "main", RemoteAlias: "origin");

    private static List<string?> Headers(IEnumerable<Control> items) =>
        items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToList();

    [AvaloniaFact]
    public async Task Local_row_offers_push_and_create_pull_request()
    {
        using var fixture = new RepoFixture(withRemote: true);
        await using var h = new Harness();
        var ctx = await h.OpenAsync(fixture.Path);

        var items = BranchMenu.Build(LocalTarget(), ctx, h.Dialogs, h.Notify, h.Stash, h.Bus, _ => { }, h.Flow);

        Assert.Equal(
            ["Checkout", "Create branch here…", "Create tag here…", "Rename…", "Delete…", "Merge into main branch",
                "Push and create pull request…", "Copy branch name"],
            Headers(items));
    }

    [AvaloniaFact]
    public async Task Remote_row_offers_create_pull_request_only()
    {
        using var fixture = new RepoFixture(withRemote: true);
        await using var h = new Harness();
        var ctx = await h.OpenAsync(fixture.Path);

        var items = BranchMenu.Build(RemoteTarget(), ctx, h.Dialogs, h.Notify, h.Stash, h.Bus, _ => { }, h.Flow);

        Assert.Equal(
            ["Checkout", "Create branch here…", "Create tag here…", "Merge into main branch",
                "Create pull request…", "Delete…", "Copy branch name"],
            Headers(items));
        Assert.DoesNotContain("Push and create pull request…", Headers(items));

        ((MenuItem)items.First(i => i is MenuItem m && m.Header?.ToString() == "Create pull request…"))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        for (var i = 0; i < 200 && h.PullRequestDialogs.Requests.Count == 0; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }

        Assert.Equal("origin.feat", Assert.Single(h.PullRequestDialogs.Requests).InitialSource);
    }

    [AvaloniaFact]
    public async Task No_items_without_remotes()
    {
        using var fixture = new RepoFixture(withRemote: false);
        await using var h = new Harness();
        var ctx = await h.OpenAsync(fixture.Path);

        var local = BranchMenu.Build(LocalTarget(), ctx, h.Dialogs, h.Notify, h.Stash, h.Bus, _ => { }, h.Flow);
        var remote = BranchMenu.Build(RemoteTarget(), ctx, h.Dialogs, h.Notify, h.Stash, h.Bus, _ => { }, h.Flow);

        Assert.DoesNotContain(Headers(local), x => x!.Contains("pull request", StringComparison.Ordinal));
        Assert.DoesNotContain(Headers(remote), x => x!.Contains("pull request", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task No_items_without_a_flow()
    {
        using var fixture = new RepoFixture(withRemote: true);
        await using var h = new Harness();
        var ctx = await h.OpenAsync(fixture.Path);

        var items = BranchMenu.Build(LocalTarget(), ctx, h.Dialogs, h.Notify, h.Stash, h.Bus, _ => { });

        Assert.DoesNotContain(Headers(items), x => x!.Contains("pull request", StringComparison.Ordinal));
    }
}
