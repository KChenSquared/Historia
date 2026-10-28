namespace Historia.Tests;

/// <summary>
/// The landing page's view model: its recents mirror the service, the open / init / clone flows
/// reach the workspace and clone services with the right arguments, the clone box follows the
/// clone service, and a finished clone offers to initialise submodules.
/// </summary>
[Trait("tier", "git")]
public sealed class HomeViewModelTests : IAsyncDisposable
{
    private sealed class FakeRecents : IRecentRepositoryService
    {
        public List<RecentRepository> Entries { get; } = [];
        public List<string> Removed { get; } = [];

        public int ItemsReads { get; private set; }
        public int RefreshCalls { get; private set; }

        public Task RefreshStalenessAsync()
        {
            RefreshCalls++;
            return Task.CompletedTask;
        }

        public IReadOnlyList<RecentRepository> Items
        {
            get
            {
                ItemsReads++;
                return [.. Entries];
            }
        }

        public event EventHandler? Changed;

        public void Set(params (string Path, string Name, bool Stale)[] entries)
        {
            Entries.Clear();
            foreach (var (path, name, stale) in entries)
                Entries.Add(new RecentRepository { Path = path, DisplayName = name, LastOpenedAt = DateTimeOffset.Now, IsStale = stale });
        }

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        public Task AddAsync(string path, string displayName, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveAsync(string path, CancellationToken ct = default)
        {
            Removed.Add(path);
            Entries.RemoveAll(e => e.Path == path);
            RaiseChanged();
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeClone : ICloneService
    {
        public CloneStatus? Current { get; set; }
        public bool IsRunning => Current is not null;
        public List<CloneRequest> Started { get; } = [];
        public Exception? StartFails { get; set; }
        public int CancelCalls { get; private set; }

        public event EventHandler? Changed;

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        public Task StartAsync(CloneRequest request)
        {
            Started.Add(request);
            return StartFails is null ? Task.CompletedTask : Task.FromException(StartFails);
        }

        public void Cancel() => CancelCalls++;
    }

    private sealed class ScriptedWorkspace : IWorkspaceService
    {
        public List<string> Opened { get; } = [];
        public List<(string Path, string? Branch)> Inited { get; } = [];
        public Exception? OpenFails { get; set; }
        public List<RepositoryContext> Open { get; } = [];
        public RepositoryContext? Focused { get; set; }

        public IReadOnlyList<RepositoryContext> OpenRepositories => Open;

        public Task<RepositoryContext> OpenAsync(string path, CancellationToken ct = default)
        {
            Opened.Add(path);
            return OpenFails is null ? Task.FromResult<RepositoryContext>(null!) : Task.FromException<RepositoryContext>(OpenFails);
        }

        public Task<RepositoryContext> InitAsync(string path, string? initialBranch, CancellationToken ct = default)
        {
            Inited.Add((path, initialBranch));
            return Task.FromResult<RepositoryContext>(null!);
        }

        public Task<RepositoryContext> CloneAsync(string url, string targetDirectory, Historia.Services.Workspace.CloneOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RepositoryContext> OpenSubmoduleAsync(RepositoryContext owner, SubmoduleInfo submodule, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CloseAsync(RepositoryContext context) => Task.CompletedTask;
        public void Focus(RepositoryContext context) { }
        public void EnsureInitialized(RepositoryContext context) { }
        public void Move(RepositoryContext context, int newIndex) { }
        public Task RestoreSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public event EventHandler<FocusedRepositoryChangedEventArgs>? FocusedRepositoryChanged { add { } remove { } }
        public event EventHandler<OpenRepositoriesChangedEventArgs>? OpenRepositoriesChanged { add { } remove { } }
    }

    private sealed class RecordingSubmodules : ISubmoduleService
    {
        public List<(RepositoryContext Context, string? Path, bool Recursive)> InitCalls { get; } = [];

        public Task<IReadOnlyList<SubmoduleInfo>> ListAsync(RepositoryContext context, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SubmoduleInfo>>([]);
        public Task AddAsync(RepositoryContext context, string url, string path, CancellationToken ct = default) => Task.CompletedTask;

        public Task InitAndUpdateAsync(RepositoryContext context, string? path, bool recursive, CancellationToken ct = default)
        {
            InitCalls.Add((context, path, recursive));
            return Task.CompletedTask;
        }

        public Task SyncAsync(RepositoryContext context, string? path, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveAsync(RepositoryContext context, SubmoduleInfo submodule, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingLauncher : IExternalLauncher
    {
        public List<string> Revealed { get; } = [];

        public void OpenUrl(string url) { }
        public void RevealInFileManager(string path) => Revealed.Add(path);
        public void OpenFolder(string path) { }
    }

    private sealed class Rig
    {
        public Rig(MiniHost host, string intent, IUiDispatcher? ui, CloneDialogChoice? dialogAnswer, RepositoryContext? focused,
            RecentRepository[]? seed)
        {
            Ui = ui ?? new ImmediateUiDispatcher();
            Profiles = new ProfileService(host.Settings, new FakeVault(), host.Cli);
            DialogAnswer = dialogAnswer;
            Workspace.Focused = focused;
            if (seed is not null)
                Recents.Entries.AddRange(seed);
            Home = new HomeViewModel(intent, Workspace, Clone, Recents, Dialogs, Notifications, Nav, Launcher, Profiles, Submodules,
                Ui, ShowCloneDialog, Clipboard);
        }

        public IUiDispatcher Ui { get; }
        public ScriptedWorkspace Workspace { get; } = new();
        public FakeClone Clone { get; } = new();
        public FakeRecents Recents { get; } = new();
        public FakeDialogService Dialogs { get; } = new();
        public FakeNotificationService Notifications { get; } = new();
        public NavigationBus Nav { get; } = new();
        public RecordingLauncher Launcher { get; } = new();
        public ProfileService Profiles { get; }
        public RecordingSubmodules Submodules { get; } = new();
        public FakeClipboardService Clipboard { get; } = new();
        public HomeViewModel Home { get; }
        public CloneDialogChoice? DialogAnswer { get; set; }
        public List<(string DefaultRoot, string Guessed)> DialogCalls { get; } = [];

        private Task<CloneDialogChoice?> ShowCloneDialog(string defaultRoot, Func<string, string> guess,
            IReadOnlyList<GitProfile> profiles, Guid? defaultProfileId)
        {
            DialogCalls.Add((defaultRoot, guess("https://example.com/owner/repo.git")));
            return Task.FromResult(DialogAnswer);
        }

        public List<string> Navigations()
        {
            var routes = new List<string>();
            Nav.NavigationRequested += routes.Add;
            return routes;
        }
    }

    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private Rig NewRig(string intent = "", IUiDispatcher? ui = null, CloneDialogChoice? dialogAnswer = null,
        RepositoryContext? focused = null, RecentRepository[]? seed = null)
        => new(_host, intent, ui, dialogAnswer, focused, seed);

    private static RecentRepository Recent(string path, string name, bool stale = false) =>
        new() { Path = path, DisplayName = name, LastOpenedAt = DateTimeOffset.Now, IsStale = stale };

    [Fact]
    public void Recents_mirror_the_service_and_mark_missing_folders()
    {
        var rig = NewRig(seed: [Recent("/src/a", "a"), Recent("/src/gone", "gone", stale: true)]);

        Assert.Equal(["a", "gone  (missing)"], rig.Home.Recents.Select(r => r.Title));
        Assert.Equal(["/src/a", "/src/gone"], rig.Home.Recents.Select(r => r.Path));
        Assert.Equal([false, true], rig.Home.Recents.Select(r => r.IsStale));
        Assert.False(rig.Home.HasNoRecents);
    }

    [Fact]
    public void Showing_home_asks_the_service_to_refresh_staleness_without_reading_the_disk_state_again()
    {
        var rig = NewRig(seed: [Recent("/src/a", "a")]);

        Assert.Equal(1, rig.Recents.RefreshCalls);
        Assert.Equal(1, rig.Recents.ItemsReads);
    }

    [Fact]
    public void A_staleness_only_change_updates_the_rows()
    {
        var rig = NewRig(seed: [Recent("/src/a", "a"), Recent("/src/b", "b")]);
        Assert.Equal([false, false], rig.Home.Recents.Select(r => r.IsStale));

        rig.Recents.Set(("/src/a", "a", false), ("/src/b", "b", true));
        rig.Recents.RaiseChanged();

        Assert.Equal([false, true], rig.Home.Recents.Select(r => r.IsStale));
        Assert.Equal("b  (missing)", rig.Home.Recents[1].Title);
    }

    [Fact]
    public async Task Clone_takes_the_default_root_from_the_rows_without_reading_the_service()
    {
        var rig = NewRig(seed: [Recent("/src/projects/a", "a")]);
        var readsBefore = rig.Recents.ItemsReads;

        await rig.Home.CloneCommand.ExecuteAsync(null);

        Assert.Equal(readsBefore, rig.Recents.ItemsReads);
        Assert.Equal(Path.GetDirectoryName("/src/projects/a"), Assert.Single(rig.DialogCalls).DefaultRoot);
    }

    [Fact]
    public void An_empty_service_shows_the_empty_state()
    {
        var rig = NewRig();

        Assert.Empty(rig.Home.Recents);
        Assert.True(rig.Home.HasNoRecents);
    }

    [Fact]
    public void A_recents_change_replaces_the_list_through_the_dispatcher()
    {
        var ui = new RecordingUiDispatcher();
        var rig = NewRig(ui: ui);
        var notified = new List<string?>();
        rig.Home.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        rig.Recents.Set(("/src/a", "a", false), ("/src/b", "b", false));
        rig.Recents.RaiseChanged();

        Assert.True(rig.Home.HasNoRecents);
        Assert.Equal(1, ui.PostCount);

        ui.DrainPosted();

        Assert.Equal(["a", "b"], rig.Home.Recents.Select(r => r.DisplayName));
        Assert.False(rig.Home.HasNoRecents);
        Assert.Equal(1, notified.Count(n => n == nameof(HomeViewModel.Recents)));
        Assert.Contains(nameof(HomeViewModel.HasNoRecents), notified);
    }

    [Fact]
    public void Disposing_stops_following_the_services()
    {
        var rig = NewRig();
        rig.Home.Dispose();

        rig.Recents.Set(("/src/a", "a", false));
        rig.Recents.RaiseChanged();
        rig.Clone.Current = new CloneStatus("u", "t", null, false);
        rig.Clone.RaiseChanged();

        Assert.Empty(rig.Home.Recents);
        Assert.False(rig.Home.IsCloneVisible);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo.git", "repo")]
    [InlineData("https://github.com/owner/repo", "repo")]
    [InlineData("https://github.com/owner/repo/", "repo")]
    [InlineData("git@github.com:owner/repo.git", "repo")]
    [InlineData("git@host:repo.GIT", "repo")]
    [InlineData("  https://example.com/a/b.git  ", "b")]
    [InlineData("plain", "plain")]
    [InlineData("plain.git", "plain")]
    [InlineData("", "")]
    public void GuessRepoName_takes_the_last_segment_without_dot_git(string url, string expected)
        => Assert.Equal(expected, HomeViewModel.GuessRepoName(url));

    [Fact]
    public void PathsEqual_ignores_trailing_and_mixed_separators()
    {
        Assert.True(HomeViewModel.PathsEqual(@"C:\src\repo\", @"C:\src\repo"));
        Assert.True(HomeViewModel.PathsEqual(@"C:\src/repo\sub", "C:/src/repo/sub"));
        Assert.False(HomeViewModel.PathsEqual("C:/src/repo2", "C:/src/repo"));
    }

    [Fact]
    public async Task Open_picks_a_folder_opens_it_and_navigates_to_the_repo()
    {
        var rig = NewRig();
        var routes = rig.Navigations();
        rig.Dialogs.EnqueueFolder("/work/repo");

        await rig.Home.OpenCommand.ExecuteAsync(null);

        Assert.Equal(["/work/repo"], rig.Workspace.Opened);
        Assert.Equal(["repo"], routes);
        Assert.Contains(rig.Dialogs.Calls, c => c.Method == nameof(IDialogService.PickFolderAsync) && c.Title == "Open a Git repository");
    }

    [Fact]
    public async Task Open_does_nothing_when_the_picker_is_cancelled()
    {
        var rig = NewRig();
        var routes = rig.Navigations();

        await rig.Home.OpenCommand.ExecuteAsync(null);

        Assert.Empty(rig.Workspace.Opened);
        Assert.Empty(routes);
        Assert.Empty(rig.Notifications.Published);
    }

    [Fact]
    public async Task A_failed_open_publishes_an_error_and_does_not_navigate()
    {
        var rig = NewRig();
        var routes = rig.Navigations();
        rig.Workspace.OpenFails = new InvalidOperationException("not a repository");

        await rig.Home.OpenPathCommand.ExecuteAsync("/work/nope");

        var notice = Assert.Single(rig.Notifications.Published);
        Assert.Equal(NotificationSeverity.Error, notice.Severity);
        Assert.Equal("Could not open repository: not a repository", notice.Message);
        Assert.Empty(routes);
    }

    [Fact]
    public async Task A_recent_row_opens_its_path()
    {
        var rig = NewRig(seed: [Recent("/src/a", "a")]);
        var routes = rig.Navigations();

        await rig.Home.Recents[0].OpenCommand.ExecuteAsync(null);

        Assert.Equal(["/src/a"], rig.Workspace.Opened);
        Assert.Equal(["repo"], routes);
    }

    [Fact]
    public async Task A_missing_row_does_not_open()
    {
        var rig = NewRig(seed: [Recent("/src/gone", "gone", stale: true)]);

        await rig.Home.Recents[0].OpenCommand.ExecuteAsync(null);

        Assert.Empty(rig.Workspace.Opened);
    }

    [Fact]
    public async Task A_row_does_not_open_while_a_clone_runs()
    {
        var rig = NewRig(seed: [Recent("/src/a", "a")]);
        rig.Clone.Current = new CloneStatus("u", "t", null, false);

        await rig.Home.Recents[0].OpenCommand.ExecuteAsync(null);

        Assert.Empty(rig.Workspace.Opened);
    }

    [Fact]
    public async Task A_row_does_not_open_while_another_open_is_in_flight()
    {
        var rig = NewRig(seed: [Recent("/src/a", "a"), Recent("/src/b", "b")]);
        var gate = new TaskCompletionSource();
        var workspace = new GatedWorkspace(gate.Task);
        var home = new HomeViewModel("", workspace, rig.Clone, rig.Recents, rig.Dialogs, rig.Notifications, rig.Nav, rig.Launcher,
            rig.Profiles, rig.Submodules, rig.Ui, (_, _, _, _) => Task.FromResult<CloneDialogChoice?>(null));

        var first = home.Recents[0].OpenCommand.ExecuteAsync(null);
        await home.Recents[1].OpenCommand.ExecuteAsync(null);
        gate.SetResult();
        await first;

        Assert.Equal(["/src/a"], workspace.Opened);
    }

    private sealed class GatedWorkspace(Task gate) : IWorkspaceService
    {
        public List<string> Opened { get; } = [];
        public IReadOnlyList<RepositoryContext> OpenRepositories => [];
        public RepositoryContext? Focused => null;

        public async Task<RepositoryContext> OpenAsync(string path, CancellationToken ct = default)
        {
            Opened.Add(path);
            await gate;
            return null!;
        }

        public Task<RepositoryContext> InitAsync(string path, string? initialBranch, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RepositoryContext> CloneAsync(string url, string targetDirectory, Historia.Services.Workspace.CloneOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RepositoryContext> OpenSubmoduleAsync(RepositoryContext owner, SubmoduleInfo submodule, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CloseAsync(RepositoryContext context) => Task.CompletedTask;
        public void Focus(RepositoryContext context) { }
        public void EnsureInitialized(RepositoryContext context) { }
        public void Move(RepositoryContext context, int newIndex) { }
        public Task RestoreSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public event EventHandler<FocusedRepositoryChangedEventArgs>? FocusedRepositoryChanged { add { } remove { } }
        public event EventHandler<OpenRepositoriesChangedEventArgs>? OpenRepositoriesChanged { add { } remove { } }
    }

    [Fact]
    public async Task A_row_remove_reveal_and_copy_reach_their_services()
    {
        var rig = NewRig(seed: [Recent("/src/a", "a"), Recent("/src/b", "b")]);
        var row = rig.Home.Recents[0];

        row.RevealCommand.Execute(null);
        await row.CopyPathCommand.ExecuteAsync(null);
        await row.RemoveCommand.ExecuteAsync(null);

        Assert.Equal(["/src/a"], rig.Launcher.Revealed);
        Assert.Equal(["/src/a"], rig.Clipboard.Copied);
        Assert.Equal(["/src/a"], rig.Recents.Removed);
    }

    [Fact]
    public async Task Init_uses_the_chosen_branch_and_falls_back_to_main()
    {
        var rig = NewRig();
        var routes = rig.Navigations();
        rig.Dialogs.EnqueueFolder("/work/new").EnqueuePrompt("  trunk ");
        await rig.Home.InitCommand.ExecuteAsync(null);

        rig.Dialogs.EnqueueFolder("/work/other").EnqueuePrompt("   ");
        await rig.Home.InitCommand.ExecuteAsync(null);

        Assert.Equal([("/work/new", "trunk"), ("/work/other", "main")], rig.Workspace.Inited);
        Assert.Equal(["repo", "repo"], routes);
    }

    [Fact]
    public async Task Init_stops_when_either_dialog_is_cancelled()
    {
        var rig = NewRig();

        await rig.Home.InitCommand.ExecuteAsync(null);
        rig.Dialogs.EnqueueFolder("/work/new").EnqueuePrompt(null);
        await rig.Home.InitCommand.ExecuteAsync(null);

        Assert.Empty(rig.Workspace.Inited);
    }

    [Fact]
    public async Task Clone_offers_the_parent_of_the_latest_recent_as_the_default_root()
    {
        var rig = NewRig(seed: [Recent("/src/projects/a/", "a")]);

        await rig.Home.CloneCommand.ExecuteAsync(null);

        var call = Assert.Single(rig.DialogCalls);
        Assert.Equal(Path.GetDirectoryName("/src/projects/a"), call.DefaultRoot);
        Assert.Equal("repo", call.Guessed);
        Assert.Empty(rig.Clone.Started);
    }

    [Fact]
    public async Task Clone_without_recents_starts_from_the_home_folder()
    {
        var rig = NewRig();

        await rig.Home.CloneCommand.ExecuteAsync(null);

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Assert.Single(rig.DialogCalls).DefaultRoot);
    }

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, null)]
    [InlineData(true, true, 1)]
    public async Task Clone_passes_the_dialog_choice_to_the_clone_service(bool shallow, bool sparse, int? depth)
    {
        var profile = Guid.NewGuid();
        var rig = NewRig(dialogAnswer: new CloneDialogChoice("https://example.com/r.git", "/work/r", shallow, sparse, profile));

        await rig.Home.CloneCommand.ExecuteAsync(null);

        var request = Assert.Single(rig.Clone.Started);
        Assert.Equal("https://example.com/r.git", request.Url);
        Assert.Equal("/work/r", request.TargetDirectory);
        Assert.Equal(depth, request.Options.Depth);
        Assert.Equal(sparse, request.Options.Sparse);
        Assert.Equal(profile, request.ProfileId);
    }

    [Fact]
    public async Task Clone_does_nothing_when_the_dialog_is_cancelled()
    {
        var rig = NewRig();

        await rig.Home.CloneCommand.ExecuteAsync(null);

        Assert.Empty(rig.Clone.Started);
        Assert.Empty(rig.Notifications.Published);
    }

    [Fact]
    public async Task Clone_does_not_open_the_dialog_while_one_is_running()
    {
        var rig = NewRig(dialogAnswer: new CloneDialogChoice("u", "/t"));
        rig.Clone.Current = new CloneStatus("u", "t", null, false);

        await rig.Home.CloneCommand.ExecuteAsync(null);

        Assert.Empty(rig.DialogCalls);
        Assert.Empty(rig.Clone.Started);
    }

    [Fact]
    public async Task A_refused_clone_publishes_the_message()
    {
        var rig = NewRig(dialogAnswer: new CloneDialogChoice("u", "/t"));
        rig.Clone.StartFails = new InvalidOperationException("A clone is already running.");

        await rig.Home.CloneCommand.ExecuteAsync(null);

        var notice = Assert.Single(rig.Notifications.Published);
        Assert.Equal(NotificationSeverity.Error, notice.Severity);
        Assert.Equal("A clone is already running.", notice.Message);
    }

    [Fact]
    public async Task An_unexpected_clone_failure_is_reported_not_thrown()
    {
        var rig = NewRig(dialogAnswer: new CloneDialogChoice("u", "/t"));
        rig.Clone.StartFails = new IOException("disk full");

        await rig.Home.CloneCommand.ExecuteAsync(null);

        Assert.Equal("Could not clone repository: disk full", Assert.Single(rig.Notifications.Published).Message);
    }

    [Fact]
    public void The_clone_box_follows_the_clone_service_through_the_dispatcher()
    {
        var ui = new RecordingUiDispatcher();
        var rig = NewRig(ui: ui);
        Assert.False(rig.Home.IsCloneVisible);
        Assert.True(rig.Home.CloneCommand.CanExecute(null));

        rig.Clone.Current = new CloneStatus("u", "t", new GitProgress { Phase = "Receiving objects", Percent = 40 }, false);
        rig.Clone.RaiseChanged();
        Assert.False(rig.Home.IsCloneVisible);
        ui.DrainPosted();

        Assert.True(rig.Home.IsCloneVisible);
        Assert.Equal("Receiving objects 40%", rig.Home.CloneStatusText);
        Assert.True(rig.Home.CanCancelClone);
        Assert.False(rig.Home.CloneCommand.CanExecute(null));
        Assert.True(rig.Home.CancelCloneCommand.CanExecute(null));

        rig.Clone.Current = rig.Clone.Current with { CancelRequested = true };
        rig.Clone.RaiseChanged();
        ui.DrainPosted();

        Assert.True(rig.Home.IsCloneVisible);
        Assert.False(rig.Home.CanCancelClone);
        Assert.False(rig.Home.CancelCloneCommand.CanExecute(null));

        rig.Clone.Current = null;
        rig.Clone.RaiseChanged();
        ui.DrainPosted();

        Assert.False(rig.Home.IsCloneVisible);
        Assert.True(rig.Home.CloneCommand.CanExecute(null));
    }

    [Fact]
    public void A_clone_already_running_shows_its_box_at_once_and_cancel_reaches_the_service()
    {
        var clone = new FakeClone { Current = new CloneStatus("u", "t", null, false) };
        var home = new HomeViewModel("clone", new ScriptedWorkspace(), clone, new FakeRecents(), new FakeDialogService(),
            new FakeNotificationService(), new NavigationBus(), new RecordingLauncher(),
            new ProfileService(_host.Settings, new FakeVault(), _host.Cli), new RecordingSubmodules(),
            new ImmediateUiDispatcher(), (_, _, _, _) => throw new InvalidOperationException("the dialog must not open"));

        Assert.True(home.IsCloneVisible);
        Assert.Equal("Starting clone…", home.CloneStatusText);

        home.CancelCloneCommand.Execute(null);

        Assert.Equal(1, clone.CancelCalls);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("init")]
    [InlineData("clone")]
    public void An_intent_launches_its_flow_through_one_deferred_post(string intent)
    {
        var ui = new RecordingUiDispatcher();
        var rig = NewRig(intent, ui);

        Assert.Equal(1, ui.PostCount);
        Assert.Empty(rig.Dialogs.Calls);
        Assert.Empty(rig.DialogCalls);

        ui.DrainPosted();

        var expectedPicker = intent is "open" or "init";
        Assert.Equal(expectedPicker, rig.Dialogs.Calls.Any(c => c.Method == nameof(IDialogService.PickFolderAsync)));
        Assert.Equal(intent == "clone", rig.DialogCalls.Count == 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("settings")]
    public void No_intent_launches_nothing(string intent)
    {
        var ui = new RecordingUiDispatcher();
        var rig = NewRig(intent, ui);

        Assert.Equal(0, ui.PostCount);
        Assert.Empty(rig.Dialogs.Calls);
    }

    [Fact]
    public void A_disposed_view_model_drops_its_pending_intent()
    {
        var ui = new RecordingUiDispatcher();
        var rig = NewRig("open", ui);
        rig.Home.Dispose();

        ui.DrainPosted();

        Assert.Empty(rig.Dialogs.Calls);
    }

    [Fact]
    public void The_close_button_exists_only_with_a_focused_repository_and_navigates_back()
    {
        var withoutRepo = NewRig();
        Assert.False(withoutRepo.Home.ShowClose);
        var routes = withoutRepo.Navigations();
        withoutRepo.Home.CloseCommand.Execute(null);
        Assert.Equal([""], routes);

        using var repo = TestRepositoryBuilder.FromTemplate("home-close", r => r.WriteFile("a.txt", "a").Stage().Commit("init"));
        var withRepo = NewRig(focused: _host.Open(repo.Path));
        Assert.True(withRepo.Home.ShowClose);
        routes = withRepo.Navigations();
        withRepo.Home.CloseCommand.Execute(null);
        Assert.Equal(["repo"], routes);
    }

    [Fact]
    public void FindClonedRepository_ignores_the_focused_tab()
    {
        using var repo1 = TestRepositoryBuilder.FromTemplate("home-find-1", r => r.WriteFile("a.txt", "a").Stage().Commit("init"));
        using var repo2 = TestRepositoryBuilder.FromTemplate("home-find-2", r => r.WriteFile("b.txt", "b").Stage().Commit("init"));
        var ctx1 = _host.Open(repo1.Path);
        var ctx2 = _host.Open(repo2.Path);

        Assert.Same(ctx2, HomeViewModel.FindClonedRepository([ctx1, ctx2], repo2.Path));
        Assert.Null(HomeViewModel.FindClonedRepository([ctx1, ctx2], TestScratch.NewPath("home-missing-")));
    }

    [Fact]
    public async Task A_cloned_repository_with_submodules_offers_to_initialise_them()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("home-submodules", r =>
            r.WriteFile(".gitmodules", "[submodule \"lib\"]\n\tpath = lib\n\turl = ../lib.git\n").WriteFile("a.txt", "a").Stage().Commit("init"));
        var ctx = _host.Open(repo.Path);
        var rig = NewRig(dialogAnswer: new CloneDialogChoice("https://example.com/r.git", repo.Path));
        rig.Workspace.Open.Add(ctx);
        rig.Dialogs.EnqueueConfirm(true);

        await rig.Home.CloneCommand.ExecuteAsync(null);

        var confirm = Assert.Single(rig.Dialogs.Calls, c => c.Method == nameof(IDialogService.ConfirmAsync));
        Assert.Equal("Initialize submodules?", confirm.Title);
        var init = Assert.Single(rig.Submodules.InitCalls);
        Assert.Same(ctx, init.Context);
        Assert.Null(init.Path);
        Assert.True(init.Recursive);
        Assert.Equal(["Initializing submodules…", "Submodules initialized."], rig.Notifications.Published.Select(n => n.Message));
        Assert.Empty(rig.Dialogs.ConfirmDestructiveCalls);
    }

    [Fact]
    public async Task Declining_the_submodule_offer_initialises_nothing()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("home-submodules-no", r =>
            r.WriteFile(".gitmodules", "[submodule \"lib\"]\n\tpath = lib\n\turl = ../lib.git\n").WriteFile("a.txt", "a").Stage().Commit("init"));
        var ctx = _host.Open(repo.Path);
        var rig = NewRig();

        await rig.Home.OfferSubmoduleInitCommand.ExecuteAsync(ctx);

        Assert.Single(rig.Dialogs.Calls, c => c.Method == nameof(IDialogService.ConfirmAsync));
        Assert.Empty(rig.Submodules.InitCalls);
        Assert.Empty(rig.Notifications.Published);
    }

    [Fact]
    public async Task A_cloned_repository_without_submodules_asks_nothing()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("home-no-submodules", r => r.WriteFile("a.txt", "a").Stage().Commit("init"));
        var ctx = _host.Open(repo.Path);
        var rig = NewRig();

        await rig.Home.OfferSubmoduleInitCommand.ExecuteAsync(ctx);

        Assert.Empty(rig.Dialogs.Calls);
        Assert.Empty(rig.Submodules.InitCalls);
    }
}
