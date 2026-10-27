namespace Historia.Tests;

/// <summary>
/// The staging panel view model against real repositories: the lists and their check boxes, the
/// stage, unstage, discard, abort and stash actions with their confirmations, the commit box
/// (draft store, counter, author override, commit and push), commit messages proposed by a coding
/// agent, and AI message generation with the model picker.
/// </summary>
[Trait("tier", "git")]
public sealed class StagingPanelViewModelTests : IAsyncDisposable
{
    public class ServiceProxy : DispatchProxy
    {
        public List<string> Calls { get; } = [];
        public Func<Task>? Gate { get; set; }
        public bool StashResult { get; set; } = true;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls.Add(targetMethod!.Name);
            var returnType = targetMethod.ReturnType;
            if (returnType == typeof(Task))
                return Gate is { } gate ? gate() : Task.CompletedTask;
            if (returnType == typeof(Task<bool>))
                return Task.FromResult(StashResult);
            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }

    private sealed class RecordingLauncher : IExternalLauncher
    {
        public List<string> Revealed { get; } = [];

        public void OpenUrl(string url)
        {
        }

        public void RevealInFileManager(string path) => Revealed.Add(path);

        public void OpenFolder(string path)
        {
        }
    }

    /// <summary>A dispatcher whose posts wait for <see cref="DrainPosted"/> and whose timers tick
    /// only when <see cref="FireTimers"/> says so, so a test controls the busy indicator's delay
    /// without waiting on a clock.</summary>
    private sealed class ScriptedUiDispatcher : IUiDispatcher
    {
        private readonly object _sync = new();
        private readonly Queue<Action> _posted = new();
        private readonly List<(TimeSpan Interval, ScriptedTimer Timer)> _timers = [];

        public bool OnUiThread { get; set; } = true;
        public int PostCount { get; private set; }

        public bool CheckAccess() => OnUiThread;

        public void Post(Action action)
        {
            lock (_sync)
            {
                PostCount++;
                _posted.Enqueue(action);
            }
        }

        public void DrainPosted()
        {
            while (true)
            {
                Action? next;
                lock (_sync)
                    next = _posted.Count > 0 ? _posted.Dequeue() : null;
                if (next is null)
                    return;
                next();
            }
        }

        public IUiTimer CreateTimer(TimeSpan interval, Action tick)
        {
            var timer = new ScriptedTimer(tick);
            lock (_sync)
                _timers.Add((interval, timer));
            return timer;
        }

        /// <summary>Ticks every running timer created with <paramref name="interval"/>; returns how many ticked.</summary>
        public int FireTimers(TimeSpan interval)
        {
            List<ScriptedTimer> running;
            lock (_sync)
                running = [.. _timers.Where(t => t.Interval == interval && t.Timer.IsRunning).Select(t => t.Timer)];
            foreach (var timer in running)
                timer.Fire();
            return running.Count;
        }

        private sealed class ScriptedTimer(Action tick) : IUiTimer
        {
            public bool IsRunning { get; private set; }
            public void Start() => IsRunning = true;
            public void Stop() => IsRunning = false;
            public void Dispose() => IsRunning = false;
            public void Fire() => tick();
        }
    }

    private sealed class FakeGenerator : ICommitMessageGenerator
    {
        public int Calls { get; private set; }
        public bool? LastAmend { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Func<CancellationToken, Task<CommitMessageResult>> Behavior { get; set; } =
            _ => Task.FromResult(new CommitMessageResult("feat: from ai", null, null));

        public Task<CommitMessageResult> GenerateAsync(RepositoryContext ctx, bool amend, CancellationToken ct)
        {
            Calls++;
            LastAmend = amend;
            LastToken = ct;
            return Behavior(ct);
        }
    }

    private sealed class FakeCatalog : IAiModelCatalog
    {
        public Dictionary<string, IReadOnlyList<string>> Lists { get; } = [];
        public int LoadCalls { get; private set; }
        public event EventHandler<string>? Changed;

        public IReadOnlyList<string>? Get(AiServiceProfile profile) => Lists.GetValueOrDefault(profile.Id);

        public Task<IReadOnlyList<string>> LoadAsync(AiServiceProfile profile, CancellationToken ct = default)
        {
            LoadCalls++;
            IReadOnlyList<string> ids = ["model-a", "model-b"];
            Lists[profile.Id] = ids;
            Changed?.Invoke(this, profile.Id);
            return Task.FromResult(ids);
        }

        public void Remember(AiServiceProfile profile, IReadOnlyList<string> ids)
        {
            Lists[profile.Id] = ids;
            Changed?.Invoke(this, profile.Id);
        }

        public bool TryClaimAutoSwitch(string profileId) => false;
    }

    private sealed class FakePanel : IAiIntegrationsPanel
    {
        public bool IsOpen { get; private set; }
        public string? FocusProfileId { get; private set; }
        public event EventHandler? OpenChanged;

        public void Open(string? focusProfileId = null)
        {
            IsOpen = true;
            FocusProfileId = focusProfileId;
            OpenChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Close() => IsOpen = false;
    }

    private sealed class FakeUnsavedWork(RepositoryContext context) : IUnsavedWork
    {
        public int DiscardCalls { get; private set; }
        public RepositoryContext Context { get; } = context;
        public string Description => "the conflict resolution in f.txt";
        public Task<bool> SaveAsync() => Task.FromResult(true);
        public void Discard() => DiscardCalls++;
    }

    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];
    private readonly List<StagingPanelViewModel> _viewModels = [];

    public FakeDialogService Dialogs { get; } = new();
    public FakeNotificationService Notifications { get; } = new();
    public FakeCommitDraftStore Drafts { get; } = new();
    public FakeClipboardService Clipboard { get; } = new();
    public FakeSettingsService Settings { get; } = new();
    private RecordingLauncher Launcher { get; } = new();
    private ServiceProxy Conflicts { get; } = (ServiceProxy)(object)DispatchProxy.Create<IConflictService, ServiceProxy>();
    private ServiceProxy Stash { get; } = (ServiceProxy)(object)DispatchProxy.Create<IStashService, ServiceProxy>();
    private UnsavedWorkRegistry UnsavedWork { get; } = new();
    private RepoActionBus Bus { get; } = new();
    private KeyboardCommandRouter Keyboard { get; } = new();
    private List<RepoNetworkRequest> PushRequests { get; } = [];

    private AiProfileService? _profiles;
    private FakeGenerator? _generator;
    private FakeCatalog? _catalog;
    private FakePanel? _panel;

    public StagingPanelViewModelTests()
    {
        Bus.NetworkActionRequested += (_, request) => PushRequests.Add(request);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var vm in _viewModels)
            vm.Dispose();
        foreach (var repo in _repos)
            repo.Dispose();
        await _host.DisposeAsync();
    }

    private bool GitMissing => !_host.Locator.Current.Found;

    private TestRepositoryBuilder NewRepo()
    {
        var repo = TestRepositoryBuilder.Create()
            .WriteFile("a.txt", "one\n")
            .WriteFile("b.txt", "bee\n")
            .Stage();
        repo.Commit("initial");
        _repos.Add(repo);
        return repo;
    }

    private RepositoryContext Open(TestRepositoryBuilder repo)
    {
        var ctx = _host.Open(repo.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();
        return ctx;
    }

    private void UseAi()
    {
        _profiles = new AiProfileService(Settings);
        _generator = new FakeGenerator();
        _catalog = new FakeCatalog();
        _panel = new FakePanel();
    }

    private StagingPanelViewModel NewViewModel(RepositoryContext ctx, IUiDispatcher? ui = null)
    {
        var vm = new StagingPanelViewModel(
            ctx, Notifications, Dialogs, Settings, Keyboard, Launcher,
            (IStashService)(object)Stash, Bus, (IConflictService)(object)Conflicts,
            ui ?? new ImmediateUiDispatcher(), UnsavedWork,
            _profiles, _panel, _generator, _catalog, Drafts, Clipboard);
        _viewModels.Add(vm);
        return vm;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what = "the condition")
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(20);
        }
    }

    /// <summary>Refreshes the status until <paramref name="condition"/> holds. A refresh that joins
    /// one the file watcher already started can answer with an older snapshot.</summary>
    private static async Task RefreshUntilAsync(RepositoryContext ctx, Func<bool> condition, string what = "the status")
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            await ctx.State.RefreshStatusAsync();
            if (condition())
                return;
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(30);
        }
    }

    private static List<FileRowViewModel> Rows(IReadOnlyList<StagingListItem> list) => [.. list.OfType<FileRowViewModel>()];

    private static FileRowViewModel Row(IReadOnlyList<StagingListItem> list, string path) => Rows(list).Single(r => r.Path == path);

    private static string Head(TestRepositoryBuilder repo) => repo.Git("rev-parse", "HEAD").Trim();

    private async Task<(TestRepositoryBuilder Repo, RepositoryContext Ctx, StagingPanelViewModel Vm)> ModifiedAsync()
    {
        var repo = NewRepo();
        repo.WriteFile("a.txt", "one\ntwo\n");
        repo.WriteFile("b.txt", "bee\nbop\n");
        var ctx = Open(repo);
        await RefreshUntilAsync(ctx, () => ctx.State.Status.Unstaged.Count == 2);
        return (repo, ctx, NewViewModel(ctx));
    }

    // ── Lists ──

    [Fact]
    public async Task Changes_and_staged_follow_the_status_in_path_order()
    {
        if (GitMissing) return;
        var repo = NewRepo();
        repo.WriteFile("b.txt", "bee\nbop\n");
        repo.WriteFile("z-new.txt", "new\n");
        repo.WriteFile("a-new.txt", "new\n");
        repo.Git("add", "a-new.txt");
        var ctx = Open(repo);
        await RefreshUntilAsync(ctx, () => ctx.State.Status.Staged.Count == 1 && ctx.State.Status.Unstaged.Count == 1);

        var vm = NewViewModel(ctx);

        Assert.Equal(["b.txt", "z-new.txt"], Rows(vm.Changes).Select(r => r.Path));
        Assert.Equal(["a-new.txt"], Rows(vm.Staged).Select(r => r.Path));
        Assert.Equal("Changes (2)", vm.ChangesHeader);
        Assert.Equal("Staged (1)", vm.StagedHeader);
        Assert.All(Rows(vm.Changes), r => Assert.False(r.IsStaged));
        Assert.All(Rows(vm.Staged), r => Assert.True(r.IsStaged));
    }

    [Fact]
    public async Task Empty_lists_show_a_note_each()
    {
        if (GitMissing) return;
        var ctx = Open(NewRepo());
        await ctx.State.RefreshStatusAsync();

        var vm = NewViewModel(ctx);

        Assert.Equal("No changes.", Assert.IsType<StagingNoteRowViewModel>(Assert.Single(vm.Changes)).Text);
        Assert.Equal("Nothing staged.", Assert.IsType<StagingNoteRowViewModel>(Assert.Single(vm.Staged)).Text);
        Assert.Equal("Changes (0)", vm.ChangesHeader);
    }

    [Fact]
    public async Task A_bare_repository_lists_no_rows()
    {
        if (GitMissing) return;
        var source = NewRepo();
        var bare = TestScratch.NewDirectory("historia-staging-bare-");
        try
        {
            TestGit.RunChecked(bare, ["clone", "--bare", "--", source.Path, bare]);
            var ctx = _host.Open(bare);
            Assert.True(ctx.IsBare);
            await ctx.State.RefreshStatusAsync();

            var status = ctx.State.Status;
            Assert.Empty(status.Staged);
            Assert.Empty(status.Unstaged);
            Assert.Empty(status.Untracked);
            Assert.Empty(status.Conflicted);

            var vm = NewViewModel(ctx);

            Assert.Empty(vm.Changes.OfType<StagingPathRowViewModel>());
            Assert.Empty(vm.Staged.OfType<StagingPathRowViewModel>());
            Assert.Equal("No changes.", Assert.IsType<StagingNoteRowViewModel>(Assert.Single(vm.Changes)).Text);
            Assert.Equal("Nothing staged.", Assert.IsType<StagingNoteRowViewModel>(Assert.Single(vm.Staged)).Text);
        }
        finally
        {
            TestScratch.Delete(bare);
        }
    }

    [Fact]
    public async Task An_equivalent_status_leaves_the_lists_alone()
    {
        if (GitMissing) return;
        var (_, ctx, vm) = await ModifiedAsync();
        var before = vm.Changes;

        await ctx.State.RefreshStatusAsync();

        Assert.Same(before, vm.Changes);
    }

    [Fact]
    public async Task A_changed_status_replaces_the_lists_wholesale()
    {
        if (GitMissing) return;
        var (repo, ctx, vm) = await ModifiedAsync();
        var before = vm.Changes;
        var notifications = 0;
        vm.PropertyChanged += (_, e) => notifications += e.PropertyName == nameof(StagingPanelViewModel.Changes) ? 1 : 0;

        repo.WriteFile("c.txt", "new\n");
        await RefreshUntilAsync(ctx, () => ctx.State.Status.Untracked.Count == 1);

        Assert.NotSame(before, vm.Changes);
        Assert.Equal(1, notifications);
        Assert.Equal(["a.txt", "b.txt", "c.txt"], Rows(vm.Changes).Select(r => r.Path));
    }

    [Fact]
    public async Task A_status_change_reaches_the_lists_only_through_the_dispatcher()
    {
        if (GitMissing) return;
        var repo = NewRepo();
        var ctx = Open(repo);
        await ctx.State.RefreshStatusAsync();
        var ui = new ScriptedUiDispatcher { OnUiThread = false };
        var vm = NewViewModel(ctx, ui);
        ui.DrainPosted();

        repo.WriteFile("a.txt", "one\ntwo\n");
        await RefreshUntilAsync(ctx, () => ctx.State.Status.Unstaged.Count == 1);

        Assert.True(ui.PostCount > 0);
        Assert.Equal("No changes.", Assert.IsType<StagingNoteRowViewModel>(Assert.Single(vm.Changes)).Text);

        ui.DrainPosted();

        Assert.Equal(["a.txt"], Rows(vm.Changes).Select(r => r.Path));
    }

    [Fact]
    public async Task A_status_change_on_the_ui_thread_renders_without_another_hop()
    {
        if (GitMissing) return;
        var repo = NewRepo();
        var ctx = Open(repo);
        await ctx.State.RefreshStatusAsync();
        var ui = new ScriptedUiDispatcher { OnUiThread = true };
        var vm = NewViewModel(ctx, ui);

        repo.WriteFile("a.txt", "one\ntwo\n");
        await RefreshUntilAsync(ctx, () => ctx.State.Status.Unstaged.Count == 1);

        Assert.Equal(0, ui.PostCount);
        Assert.Equal(["a.txt"], Rows(vm.Changes).Select(r => r.Path));
    }

    [Fact]
    public async Task A_disposed_view_model_ignores_status_and_proposals()
    {
        if (GitMissing) return;
        var repo = NewRepo();
        var ctx = Open(repo);
        await ctx.State.RefreshStatusAsync();
        var vm = NewViewModel(ctx);
        vm.Dispose();

        repo.WriteFile("a.txt", "one\ntwo\n");
        await RefreshUntilAsync(ctx, () => ctx.State.Status.Unstaged.Count == 1);
        ctx.State.ProposeCommitMessage("feat: agent");

        Assert.Empty(Rows(vm.Changes));
        Assert.Equal("", vm.MessageText);
        Assert.Equal("feat: agent", ctx.State.PendingCommitMessage);
    }

    [Fact]
    public void An_equivalent_status_is_one_with_the_same_paths_and_kinds()
    {
        static FileStatusEntry Entry(string path, FileChangeKind kind) =>
            new() { Path = path, Kind = kind, IsBinary = false, IsSubmodule = false };

        static Historia.Models.RepositoryStatus Status(params FileStatusEntry[] unstaged) => new()
        {
            Staged = [],
            Unstaged = unstaged,
            Untracked = [],
            Conflicted = [],
            IsDirty = unstaged.Length > 0,
            AheadBy = 0,
            BehindBy = 0,
            ComputedAt = DateTimeOffset.UtcNow,
        };

        var a = Status(Entry("x.txt", FileChangeKind.Modified));

        Assert.True(StagingPanelViewModel.StatusEquivalent(a, Status(Entry("x.txt", FileChangeKind.Modified))));
        Assert.False(StagingPanelViewModel.StatusEquivalent(a, Status(Entry("x.txt", FileChangeKind.Deleted))));
        Assert.False(StagingPanelViewModel.StatusEquivalent(a, Status(Entry("y.txt", FileChangeKind.Modified))));
        Assert.False(StagingPanelViewModel.StatusEquivalent(a, Status()));
    }

    // ── Checks, stage and unstage ──

    [Fact]
    public async Task Select_all_checks_every_row_and_the_box_is_tri_state()
    {
        if (GitMissing) return;
        var (_, _, vm) = await ModifiedAsync();
        Assert.False(vm.ChangesCheckState);
        Assert.False(vm.StageCheckedCommand.CanExecute(null));

        vm.SelectAllChangesCommand.Execute(true);

        Assert.True(vm.ChangesCheckState);
        Assert.All(Rows(vm.Changes), r => Assert.True(r.IsChecked));
        Assert.True(vm.StageCheckedCommand.CanExecute(null));

        Row(vm.Changes, "a.txt").IsChecked = false;

        Assert.Null(vm.ChangesCheckState);

        vm.SelectAllChangesCommand.Execute(false);

        Assert.False(vm.ChangesCheckState);
        Assert.All(Rows(vm.Changes), r => Assert.False(r.IsChecked));
    }

    [Fact]
    public async Task Checks_survive_a_refresh_for_paths_that_still_exist()
    {
        if (GitMissing) return;
        var (repo, ctx, vm) = await ModifiedAsync();
        Row(vm.Changes, "a.txt").IsChecked = true;

        repo.WriteFile("c.txt", "new\n");
        await RefreshUntilAsync(ctx, () => ctx.State.Status.Untracked.Count == 1);

        Assert.True(Row(vm.Changes, "a.txt").IsChecked);
        Assert.False(Row(vm.Changes, "b.txt").IsChecked);
        Assert.Null(vm.ChangesCheckState);
    }

    [Fact]
    public async Task Stage_checked_stages_only_the_checked_rows_and_unstage_all_reverses_them()
    {
        if (GitMissing) return;
        var (_, ctx, vm) = await ModifiedAsync();
        Row(vm.Changes, "a.txt").IsChecked = true;

        await vm.StageCheckedCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => Rows(vm.Staged).Count == 1, "the staged row");

        Assert.Equal(["a.txt"], Rows(vm.Staged).Select(r => r.Path));
        Assert.Equal(["b.txt"], Rows(vm.Changes).Select(r => r.Path));

        await vm.UnstageAllCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => Rows(vm.Staged).Count == 0, "the unstaged rows");

        Assert.Empty(ctx.State.Status.Staged);
    }

    [Fact]
    public async Task Stage_all_stages_every_change()
    {
        if (GitMissing) return;
        var (_, ctx, vm) = await ModifiedAsync();

        await vm.StageAllChangesCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => Rows(vm.Staged).Count == 2, "both staged rows");

        Assert.Equal("Staged (2)", vm.StagedHeader);
        Assert.Empty(ctx.State.Status.Unstaged);
    }

    // ── Busy state, fresh list, no silent no-op ──

    private static bool IsStatusRun(GitCliRequest request) => request.Arguments[0] == "status";

    private TestRepositoryBuilder ModifiedRepo()
    {
        var repo = NewRepo();
        repo.WriteFile("a.txt", "one\ntwo\n");
        repo.WriteFile("b.txt", "bee\nbop\n");
        return repo;
    }

    private RepositoryContext OpenWith(TestRepositoryBuilder repo, IGitCliService cli)
    {
        var ctx = _host.Open(repo.Path, cli);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();
        return ctx;
    }

    /// <summary>A CLI that holds the second status run after <paramref name="armed"/> is set: the
    /// first is the one Stage all and Commit start with, the second the refresh that follows the change.</summary>
    private BlockingGitCliService HoldSecondStatusRun(Func<bool> armed)
    {
        var statusRuns = 0;
        return new BlockingGitCliService(_host.Cli, r => armed() && IsStatusRun(r) && Interlocked.Increment(ref statusRuns) == 2);
    }

    [Fact]
    public async Task Stage_all_shows_the_file_count_while_busy_and_clears_the_state_afterwards()
    {
        if (GitMissing) return;
        var (_, _, vm) = await ModifiedAsync();
        var texts = new List<string>();
        var shown = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StagingPanelViewModel.BusyText)) texts.Add(vm.BusyText);
            if (e.PropertyName == nameof(StagingPanelViewModel.ShowBusyIndicator)) shown.Add(vm.ShowBusyIndicator);
        };

        await vm.StageAllChangesCommand.ExecuteAsync(null);

        Assert.Contains("Staging 2 files…", texts);
        Assert.Contains(true, shown);
        Assert.False(vm.IsStagingBusy);
        Assert.False(vm.ShowBusyIndicator);
        Assert.Equal("", vm.BusyText);
    }

    [Fact]
    public async Task The_indicator_waits_for_its_delay_and_names_the_operation_holding_the_write_lock()
    {
        if (GitMissing) return;
        var (_, ctx, _) = await ModifiedAsync();
        var ui = new ScriptedUiDispatcher();
        var vm = NewViewModel(ctx, ui);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = ctx.WithWriteLockAsync(async ct =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
        }, label: "Commit");
        await entered.Task;

        var stage = vm.StageAllChangesCommand.ExecuteAsync(null);

        Assert.True(vm.IsStagingBusy);
        Assert.False(vm.ShowBusyIndicator);

        Assert.Equal(1, ui.FireTimers(StagingPanelViewModel.BusyIndicatorDelay));

        Assert.True(vm.ShowBusyIndicator);
        Assert.Equal("Waiting for Commit…", vm.BusyText);

        release.SetResult();
        await holder;
        await stage;

        Assert.False(vm.IsStagingBusy);
        Assert.False(vm.ShowBusyIndicator);
        Assert.Equal("", vm.BusyText);
        Assert.Equal(0, ui.FireTimers(StagingPanelViewModel.BusyIndicatorDelay));
        await WaitUntilAsync(() => Rows(vm.Staged).Count == 2, "both staged rows");
    }

    [Fact]
    public async Task The_command_ends_when_git_returns_and_the_refresh_continues_without_it()
    {
        if (GitMissing) return;
        var armed = false;
        var cli = HoldSecondStatusRun(() => armed);
        var ctx = OpenWith(ModifiedRepo(), cli);
        await RefreshUntilAsync(ctx, () => ctx.State.Status.Unstaged.Count == 2);
        var vm = NewViewModel(ctx);
        armed = true;

        await vm.StageAllChangesCommand.ExecuteAsync(null);

        await cli.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(vm.IsStagingBusy);
        Assert.False(vm.ShowBusyIndicator);
        Assert.Empty(Rows(vm.Staged));

        cli.Release.SetResult();
        await WaitUntilAsync(() => Rows(vm.Staged).Count == 2, "both staged rows");
    }

    [Fact]
    public async Task A_failing_refresh_after_the_change_is_still_reported()
    {
        if (GitMissing) return;
        var armed = false;
        var statusRuns = 0;
        var cli = new InterceptingGitCliService(_host.Cli)
        {
            Intercept = r => armed && IsStatusRun(r) && Interlocked.Increment(ref statusRuns) > 1
                ? InterceptingGitCliService.Failure()
                : null,
        };
        var ctx = OpenWith(ModifiedRepo(), cli);
        await RefreshUntilAsync(ctx, () => ctx.State.Status.Unstaged.Count == 2);
        var vm = NewViewModel(ctx);
        armed = true;

        await vm.StageAllChangesCommand.ExecuteAsync(null);

        await WaitUntilAsync(() => Notifications.Published.Any(n => n.Severity == NotificationSeverity.Error), "the refresh error");
        Assert.False(vm.IsStagingBusy);
    }

    [Fact]
    public async Task Stage_all_stages_what_a_status_run_already_in_flight_will_report()
    {
        if (GitMissing) return;
        var repo = ModifiedRepo();
        var armed = false;
        var statusRuns = 0;
        var cli = new BlockingGitCliService(_host.Cli, r => armed && IsStatusRun(r) && Interlocked.Increment(ref statusRuns) == 1);
        var ctx = OpenWith(repo, cli);
        await RefreshUntilAsync(ctx, () => ctx.State.Status.Unstaged.Count == 2);
        var vm = NewViewModel(ctx);
        armed = true;
        repo.WriteFile("c.txt", "new\n");
        var inFlight = ctx.State.RefreshStatusAsync();
        await cli.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, Rows(vm.Changes).Count);

        var stage = vm.StageAllChangesCommand.ExecuteAsync(null);
        cli.Release.SetResult();
        await inFlight;
        await stage;

        await WaitUntilAsync(() => Rows(vm.Staged).Count == 3, "all three staged rows");
        Assert.Equal(["a.txt", "b.txt", "c.txt"], Rows(vm.Staged).Select(r => r.Path));
    }

    [Fact]
    public async Task Stage_all_and_unstage_all_say_so_when_there_is_nothing_to_move()
    {
        if (GitMissing) return;
        var ctx = Open(NewRepo());
        await ctx.State.RefreshStatusAsync();
        var vm = NewViewModel(ctx);

        await vm.StageAllChangesCommand.ExecuteAsync(null);
        await vm.UnstageAllCommand.ExecuteAsync(null);

        Assert.Contains(Notifications.Published, n => n.Severity == NotificationSeverity.Info && n.Message == "Nothing to stage");
        Assert.Contains(Notifications.Published, n => n.Severity == NotificationSeverity.Info && n.Message == "Nothing to unstage");
        Assert.False(vm.IsStagingBusy);
    }

    [Fact]
    public async Task A_commit_after_stage_all_waits_for_the_refresh_and_sees_the_staged_files()
    {
        if (GitMissing) return;
        var repo = ModifiedRepo();
        var armed = false;
        var cli = HoldSecondStatusRun(() => armed);
        var ctx = OpenWith(repo, cli);
        await RefreshUntilAsync(ctx, () => ctx.State.Status.Unstaged.Count == 2);
        var vm = NewViewModel(ctx);
        armed = true;
        await vm.StageAllChangesCommand.ExecuteAsync(null);
        await cli.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var before = Head(repo);
        vm.MessageText = "feat: both files";

        var commit = vm.CommitCommand.ExecuteAsync(null);

        Assert.False(commit.IsCompleted);
        cli.Release.SetResult();
        await commit;

        Assert.NotEqual(before, Head(repo));
        Assert.Equal("feat: both files", repo.Git("log", "-1", "--format=%s").Trim());
        Assert.DoesNotContain(Notifications.Published, n => n.Severity == NotificationSeverity.Warning);
    }

    // ── Rows ──

    [Fact]
    public async Task A_row_click_opens_the_diff_of_its_section()
    {
        if (GitMissing) return;
        var (_, ctx, vm) = await ModifiedAsync();
        await vm.StageAllChangesCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => Rows(vm.Staged).Count == 2);

        Row(vm.Staged, "a.txt").ActivateCommand.Execute(null);

        Assert.Equal(DiffSource.StagedChanges, ctx.State.OpenDiff!.Source);
        Assert.Equal("a.txt", ctx.State.OpenDiff.Path);
    }

    [Fact]
    public async Task The_row_menu_reveals_the_file_and_copies_its_absolute_path()
    {
        if (GitMissing) return;
        var (_, ctx, vm) = await ModifiedAsync();
        var row = Row(vm.Changes, "a.txt");
        var expected = System.IO.Path.Combine(ctx.RepositoryPath, "a.txt");

        row.RevealCommand.Execute(null);
        await row.CopyPathCommand.ExecuteAsync(null);

        Assert.Equal([expected], Launcher.Revealed);
        Assert.Equal(expected, Clipboard.Last);
    }

    // ── Discard ──

    [Fact]
    public async Task Discard_asks_first_and_cancel_keeps_the_file()
    {
        if (GitMissing) return;
        var (repo, _, vm) = await ModifiedAsync();
        Dialogs.EnqueueConfirmDestructive(false);

        await vm.DiscardCommand.ExecuteAsync(Row(vm.Changes, "a.txt"));

        var call = Assert.Single(Dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Discard changes", call.Title);
        Assert.Equal("Discard", call.ConfirmLabel);
        Assert.Contains("'a.txt'", call.Effect);
        Assert.Equal("one\ntwo\n", File.ReadAllText(System.IO.Path.Combine(repo.Path, "a.txt")).ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Discard_after_confirming_restores_that_file_only()
    {
        if (GitMissing) return;
        var (repo, ctx, vm) = await ModifiedAsync();
        Dialogs.EnqueueConfirmDestructive(true);

        await vm.DiscardCommand.ExecuteAsync(Row(vm.Changes, "a.txt"));

        Assert.Equal("one\n", File.ReadAllText(System.IO.Path.Combine(repo.Path, "a.txt")).ReplaceLineEndings("\n"));
        Assert.Equal("bee\nbop\n", File.ReadAllText(System.IO.Path.Combine(repo.Path, "b.txt")).ReplaceLineEndings("\n"));
        await RefreshUntilAsync(ctx, () => ctx.State.Status.Unstaged.Count == 1);
        Assert.Equal(["b.txt"], ctx.State.Status.Unstaged.Select(f => f.Path));
    }

    [Fact]
    public async Task A_staged_row_cannot_be_discarded()
    {
        if (GitMissing) return;
        var (_, _, vm) = await ModifiedAsync();
        await vm.StageAllChangesCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => Rows(vm.Staged).Count == 2);

        await Row(vm.Staged, "a.txt").DiscardCommand.ExecuteAsync(null);

        Assert.Empty(Dialogs.ConfirmDestructiveCalls);
        Assert.False(Row(vm.Staged, "a.txt").CanDiscard);
    }

    [Fact]
    public async Task Discard_all_asks_first_and_cancel_keeps_every_file()
    {
        if (GitMissing) return;
        var (repo, _, vm) = await ModifiedAsync();
        Dialogs.EnqueueConfirmDestructive(false);

        await vm.DiscardAllChangesCommand.ExecuteAsync(null);

        var call = Assert.Single(Dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Discard all changes", call.Title);
        Assert.Equal("Discard all", call.ConfirmLabel);
        Assert.Equal("one\ntwo\n", File.ReadAllText(System.IO.Path.Combine(repo.Path, "a.txt")).ReplaceLineEndings("\n"));
        Assert.Equal("bee\nbop\n", File.ReadAllText(System.IO.Path.Combine(repo.Path, "b.txt")).ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Discard_all_after_confirming_restores_every_tracked_file()
    {
        if (GitMissing) return;
        var (repo, ctx, vm) = await ModifiedAsync();
        Dialogs.EnqueueConfirmDestructive(true);

        await vm.DiscardAllChangesCommand.ExecuteAsync(null);

        Assert.Equal("one\n", File.ReadAllText(System.IO.Path.Combine(repo.Path, "a.txt")).ReplaceLineEndings("\n"));
        Assert.Equal("bee\n", File.ReadAllText(System.IO.Path.Combine(repo.Path, "b.txt")).ReplaceLineEndings("\n"));
        await RefreshUntilAsync(ctx, () => !ctx.State.Status.IsDirty);
    }

    [Fact]
    public async Task Discard_all_does_not_ask_when_there_is_nothing_to_discard()
    {
        if (GitMissing) return;
        var ctx = Open(NewRepo());
        await ctx.State.RefreshStatusAsync();
        var vm = NewViewModel(ctx);

        await vm.DiscardAllChangesCommand.ExecuteAsync(null);

        Assert.Empty(Dialogs.ConfirmDestructiveCalls);
        Assert.False(vm.DiscardAllChangesCommand.CanExecute(null));
    }

    // ── Abort and stash ──

    [Fact]
    public async Task Abort_asks_first_and_cancel_calls_nothing()
    {
        if (GitMissing) return;
        var ctx = Open(NewRepo());
        var vm = NewViewModel(ctx);
        var work = new FakeUnsavedWork(ctx);
        using var registration = UnsavedWork.Register(work);
        Dialogs.EnqueueConfirmDestructive(false);

        await vm.AbortOperationCommand.ExecuteAsync(null);

        var call = Assert.Single(Dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Abort operation", call.Title);
        Assert.Equal("Abort", call.ConfirmLabel);
        Assert.Empty(Conflicts.Calls);
        Assert.Equal(0, work.DiscardCalls);
    }

    [Fact]
    public async Task Abort_after_confirming_aborts_and_discards_the_unsaved_resolutions()
    {
        if (GitMissing) return;
        var ctx = Open(NewRepo());
        var vm = NewViewModel(ctx);
        var work = new FakeUnsavedWork(ctx);
        using var registration = UnsavedWork.Register(work);
        Dialogs.EnqueueConfirmDestructive(true);

        await vm.AbortOperationCommand.ExecuteAsync(null);

        Assert.Equal([nameof(IConflictService.AbortOperationAsync)], Conflicts.Calls);
        Assert.Equal(1, work.DiscardCalls);
    }

    [Fact]
    public async Task Abort_while_running_is_ignored_and_the_command_is_disabled()
    {
        if (GitMissing) return;
        var ctx = Open(NewRepo());
        var vm = NewViewModel(ctx);
        var gate = new TaskCompletionSource();
        Conflicts.Gate = () => gate.Task;
        Dialogs.EnqueueConfirmDestructive(true).EnqueueConfirmDestructive(true);

        var first = vm.AbortOperationCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => Conflicts.Calls.Count == 1, "the abort call");

        Assert.False(vm.AbortOperationCommand.CanExecute(null));

        await vm.AbortOperationCommand.ExecuteAsync(null);

        Assert.Single(Conflicts.Calls);
        Assert.Single(Dialogs.ConfirmDestructiveCalls);

        gate.SetResult();
        await first;

        Assert.True(vm.AbortOperationCommand.CanExecute(null));
    }

    [Fact]
    public async Task Stash_staged_reports_the_result_and_needs_staged_files()
    {
        if (GitMissing) return;
        var (_, _, vm) = await ModifiedAsync();
        Assert.False(vm.StashStagedCommand.CanExecute(null));

        await vm.StageAllChangesCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => Rows(vm.Staged).Count == 2);
        Assert.True(vm.StashStagedCommand.CanExecute(null));

        await vm.StashStagedCommand.ExecuteAsync(null);

        Assert.Equal([nameof(IStashService.StashStagedAsync)], Stash.Calls);
        Assert.Contains(Notifications.Published, n => n.Severity == NotificationSeverity.Success && n.Message == "Stashed the staged changes.");
        Assert.True(vm.StashStagedCommand.CanExecute(null));
    }

    // ── Commit box ──

    [Theory]
    [InlineData(0, "0 / 72", false, false)]
    [InlineData(71, "71 / 72", false, false)]
    [InlineData(72, "72 / 72", true, false)]
    [InlineData(73, "73 / 500", true, false)]
    [InlineData(499, "499 / 500", true, false)]
    [InlineData(500, "500 / -", false, true)]
    public async Task The_counter_follows_the_message_length(int length, string text, bool warning, bool danger)
    {
        if (GitMissing) return;
        var vm = NewViewModel(Open(NewRepo()));

        vm.MessageText = new string('x', length);

        Assert.Equal(text, vm.CounterText);
        Assert.Equal(warning, vm.IsCounterWarning);
        Assert.Equal(danger, vm.IsCounterDanger);
    }

    [Theory]
    [InlineData("Ann Lee <ann@example.com>", true, "Ann Lee", "ann@example.com")]
    [InlineData("  Ann <a@b.c>  ", true, "Ann", "a@b.c")]
    [InlineData("Ann", false, "", "")]
    [InlineData("<a@b.c>", false, "", "")]
    [InlineData("Ann <>", false, "", "")]
    [InlineData("", false, "", "")]
    public void An_author_override_needs_a_name_and_an_email(string text, bool parsed, string name, string email)
    {
        Assert.Equal(parsed, StagingPanelViewModel.TryParseAuthor(text, out var n, out var e));
        if (parsed)
        {
            Assert.Equal(name, n);
            Assert.Equal(email, e);
        }
    }

    [Fact]
    public async Task The_draft_survives_a_new_view_model_through_the_store()
    {
        if (GitMissing) return;
        var ctx = Open(NewRepo());
        var first = NewViewModel(ctx);
        Assert.Equal("", first.MessageText);

        first.MessageText = "feat: half written";
        first.Dispose();

        Assert.Equal("feat: half written", Drafts.Drafts[ctx.Id]);
        var second = NewViewModel(ctx);
        Assert.Equal("feat: half written", second.MessageText);
    }

    [Fact]
    public async Task An_empty_draft_forgets_the_stored_one()
    {
        if (GitMissing) return;
        var ctx = Open(NewRepo());
        Drafts.Set(ctx.Id, "old");
        var vm = NewViewModel(ctx);
        Assert.Equal("old", vm.MessageText);

        vm.MessageText = "";
        vm.Dispose();

        Assert.False(Drafts.Drafts.ContainsKey(ctx.Id));
    }

    [Fact]
    public async Task Drafts_are_kept_per_tab()
    {
        if (GitMissing) return;
        var one = Open(NewRepo());
        var two = Open(NewRepo());
        var vmOne = NewViewModel(one);
        vmOne.MessageText = "for one";
        vmOne.Dispose();

        Assert.Equal("", NewViewModel(two).MessageText);
        Assert.Equal("for one", NewViewModel(one).MessageText);
    }

    [Fact]
    public async Task Commit_creates_a_commit_and_clears_the_box_the_amend_flag_and_the_stored_draft()
    {
        if (GitMissing) return;
        var (repo, ctx, vm) = await ModifiedAsync();
        await vm.StageAllChangesCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => Rows(vm.Staged).Count == 2);
        var before = Head(repo);
        Drafts.Set(ctx.Id, "stale");
        vm.MessageText = "feat: two files";

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.NotEqual(before, Head(repo));
        Assert.Equal("feat: two files", repo.Git("log", "-1", "--format=%s").Trim());
        Assert.Equal("", vm.MessageText);
        Assert.False(vm.IsAmend);
        Assert.False(Drafts.Drafts.ContainsKey(ctx.Id));
        Assert.Contains(Notifications.Published, n => n.Severity == NotificationSeverity.Success && n.Message == "Commit created.");
        Assert.Empty(PushRequests);
    }

    [Fact]
    public async Task Commit_needs_a_message_and_staged_files()
    {
        if (GitMissing) return;
        var (repo, _, vm) = await ModifiedAsync();
        var before = Head(repo);

        vm.MessageText = "feat: nothing staged";
        await vm.CommitCommand.ExecuteAsync(null);
        await vm.StageAllChangesCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => Rows(vm.Staged).Count == 2);
        vm.MessageText = "  ";
        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Equal(before, Head(repo));
        Assert.Equal(2, Notifications.Published.Count(n =>
            n.Severity == NotificationSeverity.Warning && n.Message == "Stage changes and enter a message before committing."));
    }

    [Fact]
    public async Task The_author_override_names_the_commit_author()
    {
        if (GitMissing) return;
        var (repo, _, vm) = await ModifiedAsync();
        await vm.StageAllChangesCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => Rows(vm.Staged).Count == 2);
        vm.MessageText = "feat: authored";
        vm.AuthorOverride = "Ann Lee <ann@example.com>";

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Equal("Ann Lee <ann@example.com>", repo.Git("log", "-1", "--format=%an <%ae>").Trim());
    }

    [Fact]
    public async Task Commit_and_push_requests_a_push_of_this_repository()
    {
        if (GitMissing) return;
        var (repo, ctx, vm) = await ModifiedAsync();
        var remote = TestScratch.NewDirectory("historia-staging-remote-");
        try
        {
            repo.Git("init", "--bare", "-b", "main", remote);
            repo.Git("remote", "add", "origin", remote);
            await ctx.State.RefreshAllAsync();
            await vm.StageAllChangesCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => Rows(vm.Staged).Count == 2);
            vm.MessageText = "feat: pushed";

            await vm.CommitAndPushCommand.ExecuteAsync(null);

            var request = Assert.Single(PushRequests);
            Assert.Equal(RepoNetworkAction.Push, request.Action);
            Assert.Equal("origin", request.Remote);
            Assert.Equal("main", request.Branch);
            Assert.Same(ctx, request.Repo);
        }
        finally
        {
            TestScratch.Delete(remote);
        }
    }

    [Fact]
    public async Task The_push_request_needs_a_remote_and_prefers_origin()
    {
        if (GitMissing) return;
        var repo = NewRepo();
        var ctx = Open(repo);
        await ctx.State.RefreshAllAsync();
        Assert.Null(StagingPanelViewModel.BuildPushRequest(ctx));

        repo.Git("remote", "add", "upstream", "https://example.invalid/u.git");
        repo.Git("remote", "add", "origin", "https://example.invalid/o.git");
        await ctx.State.RefreshAllAsync();

        var request = StagingPanelViewModel.BuildPushRequest(ctx);
        Assert.NotNull(request);
        Assert.Equal(RepoNetworkAction.Push, request!.Value.Action);
        Assert.Equal("origin", request.Value.Remote);
        Assert.Equal("main", request.Value.Branch);
        Assert.Same(ctx, request.Value.Repo);
    }

    [Fact]
    public async Task Ctrl_Enter_commits()
    {
        if (GitMissing) return;
        var (repo, _, vm) = await ModifiedAsync();
        await vm.StageAllChangesCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => Rows(vm.Staged).Count == 2);
        var before = Head(repo);
        vm.MessageText = "feat: by keyboard";

        Assert.True(await Keyboard.DispatchAsync("Ctrl+Enter"));

        Assert.NotEqual(before, Head(repo));
    }

    [Fact]
    public async Task Disposing_stops_the_Ctrl_Enter_shortcut()
    {
        if (GitMissing) return;
        var vm = NewViewModel(Open(NewRepo()));
        Assert.True(Keyboard.IsRegistered("Ctrl+Enter"));

        vm.Dispose();

        Assert.False(Keyboard.IsRegistered("Ctrl+Enter"));
    }

    // ── Commit messages proposed by a coding agent ──

    [Fact]
    public async Task An_empty_box_takes_a_proposal_without_asking()
    {
        if (GitMissing) return;
        var ctx = Open(NewRepo());
        var vm = NewViewModel(ctx);

        ctx.State.ProposeCommitMessage("feat: from an agent\n\nBody.");

        Assert.Equal("feat: from an agent\n\nBody.", vm.MessageText);
        Assert.Null(ctx.State.PendingCommitMessage);
        Assert.False(vm.IsOfferVisible);
        Assert.True(vm.IsAgentNoteVisible);
        Assert.Empty(Dialogs.Calls);
    }

    [Fact]
    public async Task A_typed_draft_is_kept_the_proposal_is_offered_and_nothing_is_committed()
    {
        if (GitMissing) return;
        var (repo, ctx, vm) = await ModifiedAsync();
        await vm.StageAllChangesCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => Rows(vm.Staged).Count == 2);
        var before = Head(repo);
        vm.MessageText = "my own draft";
        vm.IsAmend = true;

        ctx.State.ProposeCommitMessage("feat: from an agent");

        Assert.Equal("my own draft", vm.MessageText);
        Assert.True(vm.IsOfferVisible);
        Assert.False(vm.IsAgentNoteVisible);
        Assert.Equal("feat: from an agent", vm.OfferTip);
        Assert.Equal("feat: from an agent", ctx.State.PendingCommitMessage);
        Assert.True(vm.IsAmend);
        Assert.Empty(Dialogs.Calls);
        Assert.Equal(before, Head(repo));
    }

    [Fact]
    public async Task The_same_text_in_the_box_is_taken_without_asking()
    {
        if (GitMissing) return;
        var ctx = Open(NewRepo());
        var vm = NewViewModel(ctx);
        vm.MessageText = "  feat: same\n";

        ctx.State.ProposeCommitMessage("feat: same");

        Assert.False(vm.IsOfferVisible);
        Assert.Null(ctx.State.PendingCommitMessage);
        Assert.Empty(Dialogs.Calls);
    }

    [Fact]
    public async Task Using_the_offer_replaces_the_draft_only_after_a_destructive_confirm()
    {
        if (GitMissing) return;
        var ctx = Open(NewRepo());
        var vm = NewViewModel(ctx);
        vm.MessageText = "my own draft";
        ctx.State.ProposeCommitMessage("feat: from an agent");

        Dialogs.EnqueueConfirmDestructive(false);
        await vm.UseOfferCommand.ExecuteAsync(null);

        Assert.Equal("my own draft", vm.MessageText);
        Assert.True(vm.IsOfferVisible);
        Assert.Equal("feat: from an agent", ctx.State.PendingCommitMessage);

        Dialogs.EnqueueConfirmDestructive(true);
        await vm.UseOfferCommand.ExecuteAsync(null);

        Assert.Equal("feat: from an agent", vm.MessageText);
        Assert.False(vm.IsOfferVisible);
        Assert.Null(ctx.State.PendingCommitMessage);
        Assert.Equal(2, Dialogs.ConfirmDestructiveCalls.Count);
        var call = Dialogs.ConfirmDestructiveCalls[1];
        Assert.Equal("Replace commit message", call.Title);
        Assert.Equal("Replace your commit message draft (12 characters) with the coding agent's message? Your draft is discarded.", call.Effect);
        Assert.Equal("Replace", call.ConfirmLabel);
        Assert.DoesNotContain(Dialogs.Calls, c => c.Method == nameof(IDialogService.ConfirmAsync));
    }

    [Fact]
    public async Task Dismissing_the_offer_forgets_the_proposal_and_keeps_the_draft()
    {
        if (GitMissing) return;
        var ctx = Open(NewRepo());
        var vm = NewViewModel(ctx);
        vm.MessageText = "my own draft";
        ctx.State.ProposeCommitMessage("feat: from an agent");

        vm.DismissOfferCommand.Execute(null);

        Assert.False(vm.IsOfferVisible);
        Assert.Null(ctx.State.PendingCommitMessage);
        Assert.Equal("my own draft", vm.MessageText);
        Assert.Empty(Dialogs.Calls);
    }

    [Fact]
    public async Task A_busy_box_is_never_written_and_the_offer_waits_until_it_is_free()
    {
        if (GitMissing) return;
        var ctx = Open(NewRepo());
        var vm = NewViewModel(ctx);
        vm.IsMessageReadOnly = true;

        ctx.State.ProposeCommitMessage("feat: from an agent");

        Assert.True(vm.IsOfferVisible);
        Assert.Equal("", vm.MessageText);

        await vm.UseOfferCommand.ExecuteAsync(null);

        Assert.Equal("", vm.MessageText);
        Assert.Equal("feat: from an agent", ctx.State.PendingCommitMessage);

        vm.IsMessageReadOnly = false;
        await vm.UseOfferCommand.ExecuteAsync(null);

        Assert.Equal("feat: from an agent", vm.MessageText);
        Assert.Empty(Dialogs.Calls);
    }

    [Fact]
    public async Task A_long_offer_is_cut_at_300_characters_in_the_tip()
    {
        if (GitMissing) return;
        var ctx = Open(NewRepo());
        var vm = NewViewModel(ctx);
        vm.MessageText = "draft";

        ctx.State.ProposeCommitMessage(new string('x', 500));

        Assert.Equal(301, vm.OfferTip!.Length);
        Assert.EndsWith("…", vm.OfferTip);
    }

    [Fact]
    public async Task A_proposal_made_before_the_view_model_exists_is_applied_on_construction()
    {
        if (GitMissing) return;
        var ctx = Open(NewRepo());
        ctx.State.ProposeCommitMessage("feat: early");

        var vm = NewViewModel(ctx);

        Assert.Equal("feat: early", vm.MessageText);
        Assert.Null(ctx.State.PendingCommitMessage);
    }

    [Fact]
    public async Task Editing_the_text_hides_the_agent_note()
    {
        if (GitMissing) return;
        var ctx = Open(NewRepo());
        var vm = NewViewModel(ctx);
        ctx.State.ProposeCommitMessage("feat: from an agent");
        Assert.True(vm.IsAgentNoteVisible);

        vm.MessageText += " edited";

        Assert.False(vm.IsAgentNoteVisible);
    }

    [Fact]
    public async Task Committing_spends_the_pending_proposal_and_hides_the_offer()
    {
        if (GitMissing) return;
        var (_, ctx, vm) = await ModifiedAsync();
        await vm.StageAllChangesCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => Rows(vm.Staged).Count == 2);
        vm.MessageText = "feat: mine";
        ctx.State.ProposeCommitMessage("feat: theirs");
        Assert.True(vm.IsOfferVisible);

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Null(ctx.State.PendingCommitMessage);
        Assert.False(vm.IsOfferVisible);
        Assert.False(vm.IsAgentNoteVisible);
        Assert.Equal("", vm.MessageText);
    }

    // ── AI generation and the model picker ──

    private async Task<AiServiceProfile> ActivateAsync(Func<AiServiceProfile, AiServiceProfile>? configure = null)
    {
        var added = await _profiles!.AddAsync();
        var profile = added with { Server = "https://ai.example.test/v1", Model = "m", ApiKey = "key", Name = "Gemini" };
        var configured = configure is null ? profile : configure(profile);
        await _profiles.UpdateAsync(added.Id, _ => configured);
        await _profiles.SetActiveAsync(added.Id);
        return _profiles.Profiles.Single(p => p.Id == added.Id);
    }

    private async Task StageChangeAsync(TestRepositoryBuilder repo, RepositoryContext ctx)
    {
        repo.WriteFile("a.txt", "one\ntwo\n");
        repo.Git("add", "a.txt");
        await RefreshUntilAsync(ctx, () => ctx.State.Status.Staged.Count == 1);
    }

    [Fact]
    public async Task Without_ai_services_nothing_of_the_ai_row_is_offered()
    {
        if (GitMissing) return;
        var vm = NewViewModel(Open(NewRepo()));

        Assert.False(vm.HasAiServices);
        Assert.False(vm.IsWandEnabled);
        await vm.GenerateCommand.ExecuteAsync(null);
        vm.OpenAiSettingsCommand.Execute(null);
    }

    [Fact]
    public async Task The_wand_follows_the_active_profile()
    {
        if (GitMissing) return;
        UseAi();
        var vm = NewViewModel(Open(NewRepo()));

        Assert.True(vm.HasAiServices);
        Assert.False(vm.IsWandEnabled);
        Assert.False(vm.IsWandActive);
        Assert.Equal("Generate Commit Message", vm.WandTip);
        Assert.False(vm.IsModelPickerEnabled);
        Assert.Equal("Select a Model", vm.ModelPlaceholder);

        await ActivateAsync();

        Assert.True(vm.IsWandEnabled);
        Assert.True(vm.IsWandActive);
        Assert.True(vm.IsModelPickerEnabled);
        Assert.Equal("m", vm.SelectedModel);
    }

    [Fact]
    public async Task The_settings_button_opens_the_panel_on_the_active_profile()
    {
        if (GitMissing) return;
        UseAi();
        var active = await ActivateAsync();
        var vm = NewViewModel(Open(NewRepo()));

        vm.OpenAiSettingsCommand.Execute(null);

        Assert.True(_panel!.IsOpen);
        Assert.Equal(active.Id, _panel.FocusProfileId);
    }

    [Fact]
    public async Task Generating_fills_an_empty_box_without_asking()
    {
        if (GitMissing) return;
        UseAi();
        await ActivateAsync();
        var repo = NewRepo();
        var ctx = Open(repo);
        await StageChangeAsync(repo, ctx);
        var vm = NewViewModel(ctx);

        await vm.GenerateCommand.ExecuteAsync(null);

        Assert.Equal("feat: from ai", vm.MessageText);
        Assert.Equal(false, _generator!.LastAmend);
        Assert.Empty(Dialogs.ConfirmDestructiveCalls);
        Assert.False(vm.IsMessageReadOnly);
        Assert.False(vm.IsGenerating);
    }

    [Fact]
    public async Task Generating_over_a_draft_asks_first_and_cancel_changes_nothing()
    {
        if (GitMissing) return;
        UseAi();
        await ActivateAsync();
        var repo = NewRepo();
        var ctx = Open(repo);
        await StageChangeAsync(repo, ctx);
        var vm = NewViewModel(ctx);
        vm.MessageText = "my draft";
        Dialogs.EnqueueConfirmDestructive(false);

        await vm.GenerateCommand.ExecuteAsync(null);

        var call = Assert.Single(Dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Replace commit message", call.Title);
        Assert.Equal("Replace your commit message draft (8 characters) with an AI-generated message? Your draft is discarded.", call.Effect);
        Assert.Equal("my draft", vm.MessageText);
        Assert.Equal(0, _generator!.Calls);

        Dialogs.EnqueueConfirmDestructive(true);
        await vm.GenerateCommand.ExecuteAsync(null);

        Assert.Equal("feat: from ai", vm.MessageText);
        Assert.Equal(1, _generator.Calls);
    }

    [Fact]
    public async Task Generating_with_nothing_staged_warns_and_amend_lets_it_through()
    {
        if (GitMissing) return;
        UseAi();
        await ActivateAsync();
        var vm = NewViewModel(Open(NewRepo()));

        await vm.GenerateCommand.ExecuteAsync(null);

        Assert.Equal(0, _generator!.Calls);
        Assert.Contains(Notifications.Published, n =>
            n.Severity == NotificationSeverity.Warning && n.Message == "Stage changes before generating a commit message.");

        vm.IsAmend = true;
        await vm.GenerateCommand.ExecuteAsync(null);

        Assert.Equal(1, _generator.Calls);
        Assert.Equal(true, _generator.LastAmend);
    }

    [Fact]
    public async Task A_missing_key_opens_the_panel_and_calls_no_generator()
    {
        if (GitMissing) return;
        UseAi();
        var active = await ActivateAsync(p => p with { ApiKey = "", ReadApiKeyFromEnv = true });
        var repo = NewRepo();
        var ctx = Open(repo);
        await StageChangeAsync(repo, ctx);
        var vm = NewViewModel(ctx);

        await vm.GenerateCommand.ExecuteAsync(null);

        Assert.Equal(0, _generator!.Calls);
        Assert.True(_panel!.IsOpen);
        Assert.Equal(active.Id, _panel.FocusProfileId);
        Assert.Contains(Notifications.Published, n => n.Severity == NotificationSeverity.Warning && n.Message.Contains("API key"));
    }

    [Fact]
    public async Task A_second_call_cancels_the_running_generation_and_keeps_the_draft()
    {
        if (GitMissing) return;
        UseAi();
        await ActivateAsync();
        var repo = NewRepo();
        var ctx = Open(repo);
        await StageChangeAsync(repo, ctx);
        var vm = NewViewModel(ctx);
        vm.MessageText = "keep me";
        Dialogs.EnqueueConfirmDestructive(true);
        var gate = new TaskCompletionSource<CommitMessageResult>();
        _generator!.Behavior = ct =>
        {
            ct.Register(() => gate.TrySetCanceled(ct));
            return gate.Task;
        };

        var first = vm.GenerateCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.IsGenerating, "the generation to start");

        Assert.True(vm.IsMessageReadOnly);
        Assert.Equal("Generating… click to cancel", vm.WandTip);
        Assert.True(vm.IsWandEnabled);

        await vm.GenerateCommand.ExecuteAsync(null);
        await first;

        Assert.True(_generator.LastToken.IsCancellationRequested);
        Assert.Equal("keep me", vm.MessageText);
        Assert.False(vm.IsMessageReadOnly);
        Assert.Equal("Generate Commit Message", vm.WandTip);
        Assert.DoesNotContain(Notifications.Published, n => n.Severity is NotificationSeverity.Error or NotificationSeverity.Warning);
    }

    [Fact]
    public async Task A_failed_generation_reports_the_classified_error_and_keeps_the_draft()
    {
        if (GitMissing) return;
        UseAi();
        await ActivateAsync();
        var repo = NewRepo();
        var ctx = Open(repo);
        await StageChangeAsync(repo, ctx);
        var vm = NewViewModel(ctx);
        _generator!.Behavior = _ => throw new AiException(AiErrorKind.Unauthorized, "The AI service rejected the API key.", "HTTP 401");

        await vm.GenerateCommand.ExecuteAsync(null);

        var error = Assert.Single(Notifications.Published, n => n.Severity == NotificationSeverity.Error);
        Assert.Equal("The AI service rejected the API key.", error.Message);
        Assert.Equal("HTTP 401", error.Details);
        Assert.False(vm.IsMessageReadOnly);
    }

    [Fact]
    public async Task Committing_during_a_generation_does_nothing()
    {
        if (GitMissing) return;
        UseAi();
        await ActivateAsync();
        var repo = NewRepo();
        var ctx = Open(repo);
        await StageChangeAsync(repo, ctx);
        var vm = NewViewModel(ctx);
        vm.MessageText = "feat: old draft";
        Dialogs.EnqueueConfirmDestructive(true);
        var gate = new TaskCompletionSource<CommitMessageResult>();
        _generator!.Behavior = ct =>
        {
            ct.Register(() => gate.TrySetCanceled(ct));
            return gate.Task;
        };
        var before = Head(repo);
        var running = vm.GenerateCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.IsGenerating);

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Equal(before, Head(repo));
        Assert.Equal("feat: old draft", vm.MessageText);

        await vm.GenerateCommand.ExecuteAsync(null);
        await running;
    }

    [Fact]
    public async Task Disposing_during_a_generation_cancels_it_and_stores_the_old_draft()
    {
        if (GitMissing) return;
        UseAi();
        await ActivateAsync();
        var repo = NewRepo();
        var ctx = Open(repo);
        await StageChangeAsync(repo, ctx);
        var vm = NewViewModel(ctx);
        vm.MessageText = "old draft";
        Dialogs.EnqueueConfirmDestructive(true);
        var gate = new TaskCompletionSource<CommitMessageResult>();
        _generator!.Behavior = ct =>
        {
            ct.Register(() => gate.TrySetCanceled(ct));
            return gate.Task;
        };
        var running = vm.GenerateCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.IsGenerating);

        vm.Dispose();
        await running;

        Assert.True(_generator.LastToken.IsCancellationRequested);
        Assert.Equal("old draft", Drafts.Drafts[ctx.Id]);
    }

    [Fact]
    public async Task Disposing_stops_listening_to_profile_changes()
    {
        if (GitMissing) return;
        UseAi();
        var vm = NewViewModel(Open(NewRepo()));
        vm.Dispose();

        await ActivateAsync();

        Assert.False(vm.IsWandEnabled);
    }

    [Fact]
    public async Task Picking_a_model_saves_it_to_the_active_profile_only()
    {
        if (GitMissing) return;
        UseAi();
        var first = await ActivateAsync(p => p with { Model = "m1" });
        var other = await _profiles!.AddAsync();
        await _profiles.UpdateAsync(other.Id, p => p with { Name = "Other", Model = "m2" });
        _catalog!.Remember(first, ["m1", "m1-new"]);
        var vm = NewViewModel(Open(NewRepo()));
        Assert.Equal("m1", vm.SelectedModel);
        Assert.Equal(["m1", "m1-new"], vm.ModelItems);

        vm.SelectedModel = "m1-new";
        await WaitUntilAsync(() => _profiles.ActiveProfile!.Model == "m1-new", "the model to be saved");

        Assert.Equal("m2", _profiles.Profiles.Single(p => p.Id == other.Id).Model);
    }

    [Fact]
    public async Task A_saved_model_missing_from_the_loaded_list_reads_as_unselected_and_changes_nothing()
    {
        if (GitMissing) return;
        UseAi();
        var active = await ActivateAsync(p => p with { Model = "retired" });
        _catalog!.Remember(active, ["new-1", "new-2"]);

        var vm = NewViewModel(Open(NewRepo()));

        Assert.Null(vm.SelectedModel);
        Assert.Equal("retired", _profiles!.ActiveProfile!.Model);
    }

    [Fact]
    public async Task An_unrelated_profile_save_keeps_the_item_list_instance()
    {
        if (GitMissing) return;
        UseAi();
        var active = await ActivateAsync(p => p with { Model = "m1" });
        _catalog!.Remember(active, ["m1", "m2"]);
        var vm = NewViewModel(Open(NewRepo()));
        var items = vm.ModelItems;

        await _profiles!.UpdateAsync(active.Id, p => p with { Name = "Renamed" });

        Assert.Same(items, vm.ModelItems);
        Assert.Equal("m1", vm.SelectedModel);
    }

    [Fact]
    public async Task Opening_the_picker_loads_the_list_once_when_the_profile_fetches_on_its_own()
    {
        if (GitMissing) return;
        UseAi();
        await ActivateAsync(p => p with { AutoFetchModels = true });
        var vm = NewViewModel(Open(NewRepo()));
        Assert.Equal(0, _catalog!.LoadCalls);

        vm.OpenModelPickerCommand.Execute(null);
        await vm.ModelLoad;
        vm.OpenModelPickerCommand.Execute(null);
        await vm.ModelLoad;

        Assert.Equal(1, _catalog.LoadCalls);
        Assert.Equal(["model-a", "model-b"], vm.ModelItems);
        Assert.Equal("Select a Model", vm.ModelPlaceholder);
    }

    [Fact]
    public async Task Opening_the_picker_without_auto_fetch_makes_no_request_and_the_tip_says_how_to_load()
    {
        if (GitMissing) return;
        UseAi();
        await ActivateAsync(p => p with { AutoFetchModels = false, Model = "current" });
        var vm = NewViewModel(Open(NewRepo()));

        vm.OpenModelPickerCommand.Execute(null);
        await vm.ModelLoad;

        Assert.Equal(0, _catalog!.LoadCalls);
        Assert.Equal(["current"], vm.ModelItems);
        Assert.Contains("Press refresh in AI Integrations to load models", vm.ModelTip);
    }

    [Fact]
    public async Task Disposing_stops_listening_to_the_model_catalog()
    {
        if (GitMissing) return;
        UseAi();
        var active = await ActivateAsync();
        var vm = NewViewModel(Open(NewRepo()));
        var items = vm.ModelItems;

        vm.Dispose();
        _catalog!.Remember(active, ["new-model"]);

        Assert.Same(items, vm.ModelItems);
    }
}
