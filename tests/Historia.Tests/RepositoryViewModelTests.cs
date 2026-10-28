namespace Historia.Tests;

/// <summary>An <see cref="IWorkspaceService"/> whose focused repository a test sets and announces.</summary>
internal sealed class ShellFakeWorkspace : IWorkspaceService
{
    public RepositoryContext? Focused { get; private set; }

    public int FocusedSubscribers { get; private set; }

    public IReadOnlyList<RepositoryContext> OpenRepositories => Focused is null ? [] : [Focused];

    /// <summary>Moves the focus without announcing it, as a window that has not subscribed yet would see it.</summary>
    public void SetFocusedQuietly(RepositoryContext? ctx) => Focused = ctx;

    public void FocusAndAnnounce(RepositoryContext? ctx)
    {
        var old = Focused;
        Focused = ctx;
        _focusedChanged?.Invoke(this, new FocusedRepositoryChangedEventArgs(old, ctx));
    }

    private EventHandler<FocusedRepositoryChangedEventArgs>? _focusedChanged;

    public event EventHandler<FocusedRepositoryChangedEventArgs>? FocusedRepositoryChanged
    {
        add { _focusedChanged += value; FocusedSubscribers++; }
        remove { _focusedChanged -= value; FocusedSubscribers--; }
    }

    public event EventHandler<OpenRepositoriesChangedEventArgs>? OpenRepositoriesChanged { add { } remove { } }

    public Task<RepositoryContext> OpenAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RepositoryContext> InitAsync(string path, string? initialBranch, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RepositoryContext> CloneAsync(string url, string targetDirectory, Historia.Services.Workspace.CloneOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RepositoryContext> OpenSubmoduleAsync(RepositoryContext owner, SubmoduleInfo submodule, CancellationToken ct = default) => throw new NotSupportedException();
    public Task CloseAsync(RepositoryContext context) => Task.CompletedTask;
    public void Focus(RepositoryContext context) => FocusAndAnnounce(context);
    public void EnsureInitialized(RepositoryContext context) { }
    public void Move(RepositoryContext context, int newIndex) { }
    public Task RestoreSessionAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>The repository pane's view model: the top bar in both modes, the operation banner and
/// its Continue / Abort commands, the network progress box, and which body shows.</summary>
[Trait("tier", "git")]
public sealed class RepositoryViewModelTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;
    private readonly RepositoryContext _ctx;
    private readonly ShellFakeWorkspace _workspace = new();
    private readonly FakeNotificationService _notifications = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeNetworkRunner _network = new();
    private readonly FakeConflicts _conflicts = new();
    private readonly UnsavedWorkRegistry _unsavedWork = new();
    private readonly List<RepositoryViewModel> _created = [];

    public RepositoryViewModelTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("repository-view-model-seed", b =>
        {
            b.WriteFile("f.txt", "one\n").Stage();
            b.Commit("init");
        });
        _ctx = _host.Open(_repo.Path);
        _workspace.SetFocusedQuietly(_ctx);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var vm in _created)
            vm.Dispose();
        await _ctx.DisposeAsync();
        await _host.DisposeAsync();
        _repo.Dispose();
    }

    private RepositoryViewModel Create(IUiDispatcher? ui = null, bool withRegistry = true)
    {
        var vm = new RepositoryViewModel(
            _workspace, _notifications, _dialogs, _network, _conflicts, ui ?? new ImmediateUiDispatcher(),
            withRegistry ? _unsavedWork : null);
        _created.Add(vm);
        return vm;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "The condition was not met in time.");
    }

    // A merge stopped before its commit: the repository is Merging with no conflicts.
    private async Task StartMergeWithoutConflictsAsync()
    {
        _repo.Git("checkout", "-b", "feature");
        _repo.WriteFile("g.txt", "feature\n").Stage();
        _repo.Commit("feature work");
        _repo.Git("checkout", "main");
        _repo.Git("merge", "--no-ff", "--no-commit", "feature");
        await WaitUntilRefreshedAsync(() => _ctx.State.PendingOperation == OperationState.Merging);
    }

    private async Task WaitUntilRefreshedAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            await _ctx.State.RefreshStatusAsync();
            if (condition())
                return;
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the repository state.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public void Without_a_repository_the_pane_says_so_and_shows_no_body()
    {
        _workspace.SetFocusedQuietly(null);

        var vm = Create();

        Assert.Equal("No repository open", vm.BranchText);
        Assert.Equal("", vm.AheadBehindText);
        Assert.Equal(RepositoryDetailMode.None, vm.DetailMode);
        Assert.True(vm.IsBranchBarVisible);
        Assert.False(vm.IsWorkingCopyVisible);
        Assert.False(vm.IsOpBannerVisible);
        Assert.Null(vm.Repo);
    }

    [Fact]
    public void Working_copy_change_count_counts_unique_paths_including_untracked()
    {
        static FileStatusEntry Entry(string path, FileChangeKind kind) =>
            new() { Path = path, Kind = kind, IsBinary = false, IsSubmodule = false };

        var status = Historia.Models.RepositoryStatus.Empty with
        {
            Staged = [Entry("file1.txt", FileChangeKind.Added), Entry("file2.txt", FileChangeKind.Modified)],
            Unstaged = [Entry("file2.txt", FileChangeKind.Modified), Entry("file3.txt", FileChangeKind.Modified)],
            Untracked = [Entry("untracked.txt", FileChangeKind.Untracked)],
            Conflicted = [Entry("file4.txt", FileChangeKind.Conflicted)],
        };

        Assert.Equal(5, RepositoryViewModel.WorkingCopyChangeCount(status));
    }

    [Fact]
    public async Task The_focused_repository_shows_its_branch_over_the_staging_body()
    {
        var vm = Create();
        Assert.Same(_ctx, vm.Repo);

        await _ctx.State.RefreshRefsAsync();

        Assert.Equal("main", vm.BranchText);
        Assert.Equal(RepositoryDetailMode.Staging, vm.DetailMode);
        Assert.Null(vm.SelectedCommitId);
        Assert.True(vm.IsBranchBarVisible);
        Assert.False(vm.IsWorkingCopyVisible);
    }

    [Fact]
    public void Selecting_a_commit_swaps_the_branch_bar_for_the_working_copy_button_and_back()
    {
        var vm = Create();
        var head = GitObjectId.Parse(_repo.HeadSha);

        _ctx.State.SetSelectedCommit(head);

        Assert.Equal(RepositoryDetailMode.CommitDetail, vm.DetailMode);
        Assert.Equal(head, vm.SelectedCommitId);
        Assert.False(vm.IsBranchBarVisible);
        Assert.True(vm.IsWorkingCopyVisible);
        Assert.Equal("← Working Copy (Changes: 0)", vm.WorkingCopyText);
        Assert.False(vm.HasWorkingCopyChanges);
        Assert.True(vm.IsWorkingCopyClean);

        _ctx.State.SetSelectedCommit(null);

        Assert.Equal(RepositoryDetailMode.Staging, vm.DetailMode);
        Assert.Null(vm.SelectedCommitId);
        Assert.True(vm.IsBranchBarVisible);
        Assert.False(vm.IsWorkingCopyVisible);
    }

    [Fact]
    public async Task The_working_copy_button_is_primary_only_when_the_working_copy_is_dirty()
    {
        var vm = Create();
        _ctx.State.SetSelectedCommit(GitObjectId.Parse(_repo.HeadSha));
        Assert.False(vm.HasWorkingCopyChanges);

        _repo.WriteFile("f.txt", "one\nmodified\n");
        await WaitUntilRefreshedAsync(() => vm.HasWorkingCopyChanges);

        Assert.Equal("← Working Copy (Changes: 1)", vm.WorkingCopyText);
        Assert.False(vm.IsWorkingCopyClean);
    }

    [Fact]
    public void The_working_copy_command_returns_to_the_staging_body()
    {
        var vm = Create();
        _ctx.State.SetSelectedCommit(GitObjectId.Parse(_repo.HeadSha));
        Assert.Equal(RepositoryDetailMode.CommitDetail, vm.DetailMode);

        vm.ShowWorkingCopyCommand.Execute(null);

        Assert.Null(_ctx.State.SelectedCommitId);
        Assert.Equal(RepositoryDetailMode.Staging, vm.DetailMode);
    }

    [Fact]
    public void Every_refresh_announces_the_detail()
    {
        var vm = Create();
        var raised = 0;
        vm.DetailChanged += (_, _) => raised++;

        _ctx.State.SetSelectedCommit(GitObjectId.Parse(_repo.HeadSha));
        _ctx.State.SetSelectedCommit(null);

        Assert.Equal(2, raised);
    }

    [Fact]
    public void A_focus_change_is_applied_on_the_ui_thread_and_follows_the_new_repository()
    {
        _workspace.SetFocusedQuietly(null);
        var ui = new RecordingUiDispatcher();
        var vm = Create(ui);
        Assert.Equal(RepositoryDetailMode.None, vm.DetailMode);

        _workspace.FocusAndAnnounce(_ctx);

        Assert.Equal(RepositoryDetailMode.None, vm.DetailMode);
        Assert.Equal(1, ui.PostCount);

        ui.DrainPosted();

        Assert.Same(_ctx, vm.Repo);
        Assert.Equal(RepositoryDetailMode.Staging, vm.DetailMode);

        _ctx.State.SetSelectedCommit(GitObjectId.Parse(_repo.HeadSha));
        Assert.Equal(RepositoryDetailMode.Staging, vm.DetailMode);
        ui.DrainPosted();
        Assert.Equal(RepositoryDetailMode.CommitDetail, vm.DetailMode);

        _workspace.FocusAndAnnounce(null);
        ui.DrainPosted();

        Assert.Null(vm.Repo);
        Assert.Equal("No repository open", vm.BranchText);
        Assert.Equal(RepositoryDetailMode.None, vm.DetailMode);
    }

    [Fact]
    public void A_focus_change_posted_before_dispose_does_nothing_afterwards()
    {
        _workspace.SetFocusedQuietly(null);
        var ui = new RecordingUiDispatcher();
        var vm = Create(ui);
        _workspace.FocusAndAnnounce(_ctx);

        vm.Dispose();
        ui.DrainPosted();

        Assert.Null(vm.Repo);
    }

    [Fact]
    public void Dispose_unsubscribes_from_the_workspace_the_runner_and_the_repository()
    {
        var vm = Create();
        Assert.Equal(1, _workspace.FocusedSubscribers);
        Assert.Equal(1, _network.Subscribers);

        vm.Dispose();

        Assert.Equal(0, _workspace.FocusedSubscribers);
        Assert.Equal(0, _network.Subscribers);
        var raised = 0;
        vm.DetailChanged += (_, _) => raised++;
        _ctx.State.SetSelectedCommit(GitObjectId.Parse(_repo.HeadSha));
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task An_operation_with_no_conflicts_shows_the_banner_with_its_buttons()
    {
        var vm = Create();
        Assert.False(vm.IsOpBannerVisible);

        await StartMergeWithoutConflictsAsync();
        await WaitUntilAsync(() => vm.IsOpBannerVisible);

        Assert.Equal("Repository is in Merging state with no conflicts.", vm.OpBannerText);
        Assert.True(vm.ShowOpActions);
        Assert.True(vm.AreOpButtonsEnabled);
    }

    [Fact]
    public async Task A_resolver_the_user_has_not_closed_hides_the_banner_until_it_is_closed()
    {
        var vm = Create();

        await StartMergeWithoutConflictsAsync();
        _ctx.State.SetKeepConflictResolverOpen(true);
        Assert.True(_ctx.State.IsConflictSessionActive);
        await WaitUntilAsync(() => !vm.IsOpBannerVisible);

        _ctx.State.SetConflictResolverClosed(true);

        await WaitUntilAsync(() => vm.IsOpBannerVisible);
    }

    [Fact]
    public async Task Continue_runs_the_operation_with_the_buttons_disabled_meanwhile_and_ignores_a_second_press()
    {
        var vm = Create();
        await StartMergeWithoutConflictsAsync();
        var gate = new TaskCompletionSource();
        _conflicts.ContinueGate = gate.Task;

        var running = vm.ContinueCommand.ExecuteAsync(null);

        Assert.True(vm.IsOpBusy);
        Assert.False(vm.AreOpButtonsEnabled);
        Assert.Equal(1, _conflicts.ContinueCalls);
        Assert.Same(_ctx, _conflicts.LastContext);

        await vm.ContinueCommand.ExecuteAsync(null);
        await vm.AbortCommand.ExecuteAsync(null);
        Assert.Equal(1, _conflicts.ContinueCalls);
        Assert.Empty(_dialogs.ConfirmDestructiveCalls);

        gate.SetResult();
        await running;

        Assert.False(vm.IsOpBusy);
        Assert.True(vm.AreOpButtonsEnabled);
    }

    [Fact]
    public async Task Continue_offers_unsaved_resolver_edits_first_and_a_cancel_stops_it()
    {
        var vm = Create();
        var work = new FakeUnsavedWork(_ctx);
        using var registration = _unsavedWork.Register(work);

        _dialogs.EnqueueChoice(2);
        await vm.ContinueCommand.ExecuteAsync(null);

        Assert.Single(_dialogs.Calls, c => c.Method == nameof(IDialogService.ChooseAsync));
        Assert.Equal(0, work.SaveCalls);
        Assert.Equal(0, _conflicts.ContinueCalls);
        Assert.False(vm.IsOpBusy);

        _dialogs.EnqueueChoice(0);
        await vm.ContinueCommand.ExecuteAsync(null);

        Assert.Equal(1, work.SaveCalls);
        Assert.Equal(1, _conflicts.ContinueCalls);
    }

    [Fact]
    public async Task Continue_without_a_registry_does_not_ask()
    {
        var vm = Create(withRegistry: false);

        await vm.ContinueCommand.ExecuteAsync(null);

        Assert.Empty(_dialogs.Calls);
        Assert.Equal(1, _conflicts.ContinueCalls);
    }

    [Fact]
    public async Task Continue_that_hits_a_conflict_warns_and_frees_the_buttons()
    {
        var vm = Create();
        _conflicts.ContinueFails = new HistoriaGitException(GitErrorKind.MergeConflict, "Conflicts remain.");

        await vm.ContinueCommand.ExecuteAsync(null);

        var notice = Assert.Single(_notifications.Published);
        Assert.Equal(NotificationSeverity.Warning, notice.Severity);
        Assert.Equal("Conflicts remain.", notice.Message);
        Assert.False(vm.IsOpBusy);
    }

    [Fact]
    public async Task Continue_that_fails_reports_an_error_and_frees_the_buttons()
    {
        var vm = Create();
        _conflicts.ContinueFails = new InvalidOperationException("boom");

        await vm.ContinueCommand.ExecuteAsync(null);

        var notice = Assert.Single(_notifications.Published);
        Assert.Equal(NotificationSeverity.Error, notice.Severity);
        Assert.False(vm.IsOpBusy);
    }

    [Fact]
    public async Task Abort_confirms_first_and_a_refusal_aborts_nothing()
    {
        var vm = Create();

        _dialogs.EnqueueConfirmDestructive(false);
        await vm.AbortCommand.ExecuteAsync(null);

        var call = Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Abort operation", call.Title);
        Assert.Equal("Abort", call.ConfirmLabel);
        Assert.Contains("Conflict resolutions made so far will be discarded", call.Effect);
        Assert.Equal(0, _conflicts.AbortCalls);
        Assert.False(vm.IsOpBusy);
    }

    [Fact]
    public async Task Abort_confirmed_aborts_the_operation_of_the_shown_repository()
    {
        var vm = Create();

        _dialogs.EnqueueConfirmDestructive(true);
        await vm.AbortCommand.ExecuteAsync(null);

        Assert.Equal(1, _conflicts.AbortCalls);
        Assert.Same(_ctx, _conflicts.LastContext);
        Assert.False(vm.IsOpBusy);
    }

    [Fact]
    public async Task Abort_that_fails_reports_an_error_and_frees_the_buttons()
    {
        var vm = Create();
        _conflicts.AbortFails = new InvalidOperationException("boom");

        _dialogs.EnqueueConfirmDestructive(true);
        await vm.AbortCommand.ExecuteAsync(null);

        var notice = Assert.Single(_notifications.Published);
        Assert.Equal(NotificationSeverity.Error, notice.Severity);
        Assert.False(vm.IsOpBusy);
    }

    [Fact]
    public async Task Continue_and_abort_do_nothing_without_a_repository()
    {
        _workspace.SetFocusedQuietly(null);
        var vm = Create();

        await vm.ContinueCommand.ExecuteAsync(null);
        await vm.AbortCommand.ExecuteAsync(null);

        Assert.Equal(0, _conflicts.ContinueCalls);
        Assert.Equal(0, _conflicts.AbortCalls);
        Assert.Empty(_dialogs.Calls);
    }

    [Fact]
    public void Network_progress_of_the_shown_repository_fills_the_box_and_hides_with_a_null_tick()
    {
        var vm = Create();
        Assert.False(vm.IsNetworkProgressVisible);

        _network.Report(_ctx, new GitProgress { Phase = "Receiving objects", Percent = 42 });

        Assert.True(vm.IsNetworkProgressVisible);
        Assert.Equal("Receiving objects 42%", vm.NetworkProgressText);

        _network.Report(_ctx, new GitProgress { Phase = "Resolving deltas" });
        Assert.Equal("Resolving deltas", vm.NetworkProgressText);

        _network.Report(_ctx, null);
        Assert.False(vm.IsNetworkProgressVisible);
    }

    [Fact]
    public async Task Network_progress_of_another_repository_is_ignored()
    {
        var vm = Create();
        var otherRepo = TestRepositoryBuilder.FromTemplate("repository-view-model-seed", _ => { });
        await using var other = _host.Open(otherRepo.Path);

        _network.Report(other, new GitProgress { Phase = "Receiving objects", Percent = 10 });

        Assert.False(vm.IsNetworkProgressVisible);
        otherRepo.Dispose();
    }

    [Fact]
    public void A_running_operation_is_shown_when_the_pane_opens_and_cancel_reaches_the_runner()
    {
        _network.Current = new GitProgress { Phase = "Pushing", Percent = 5 };

        var vm = Create();

        Assert.True(vm.IsNetworkProgressVisible);
        Assert.Equal("Pushing 5%", vm.NetworkProgressText);

        vm.CancelNetworkCommand.Execute(null);

        Assert.Same(_ctx, _network.Cancelled);
    }

    [Fact]
    public void Network_progress_off_the_ui_thread_is_posted_to_the_ui_dispatcher()
    {
        var ui = new RecordingUiDispatcher { OnUiThread = false };
        var vm = Create(ui);

        _network.Report(_ctx, new GitProgress { Phase = "Fetching" });
        Assert.False(vm.IsNetworkProgressVisible);

        ui.DrainPosted();
        Assert.True(vm.IsNetworkProgressVisible);
    }

    [Fact]
    public void Network_progress_on_the_ui_thread_is_applied_in_place_without_a_post()
    {
        var ui = new RecordingUiDispatcher { OnUiThread = true };
        var vm = Create(ui);
        var posts = ui.PostCount;

        _network.Report(_ctx, new GitProgress { Phase = "Fetching" });

        Assert.True(vm.IsNetworkProgressVisible);
        Assert.Equal(posts, ui.PostCount);
    }

    [Fact]
    public async Task A_status_change_off_the_ui_thread_is_posted_and_applied_when_drained()
    {
        var ui = new RecordingUiDispatcher { OnUiThread = false };
        var vm = Create(ui);
        _ctx.State.SetSelectedCommit(GitObjectId.Parse(_repo.HeadSha));
        ui.DrainPosted();
        Assert.False(vm.HasWorkingCopyChanges);

        _repo.WriteFile("f.txt", "one\nmodified\n");
        await _ctx.State.RefreshStatusAsync();

        Assert.False(vm.HasWorkingCopyChanges);
        Assert.True(ui.PostCount > 0);
        ui.DrainPosted();
        Assert.True(vm.HasWorkingCopyChanges);
    }

    [Fact]
    public async Task A_status_change_on_the_ui_thread_is_applied_in_place_without_a_post()
    {
        var ui = new RecordingUiDispatcher { OnUiThread = true };
        var vm = Create(ui);
        _ctx.State.SetSelectedCommit(GitObjectId.Parse(_repo.HeadSha));
        ui.DrainPosted();
        Assert.False(vm.HasWorkingCopyChanges);

        _repo.WriteFile("f.txt", "one\nmodified\n");
        await _ctx.State.RefreshStatusAsync();

        // Nothing was drained, so the change was applied inline rather than through a post.
        Assert.True(vm.HasWorkingCopyChanges);
    }

    private sealed class FakeUnsavedWork(RepositoryContext context) : IUnsavedWork
    {
        public int SaveCalls { get; private set; }
        public RepositoryContext Context { get; } = context;
        public string Description => "the conflict resolution in f.txt";

        public Task<bool> SaveAsync()
        {
            SaveCalls++;
            return Task.FromResult(true);
        }

        public void Discard() { }
    }

    private sealed class FakeNetworkRunner : IRepoNetworkActionRunner
    {
        private EventHandler<RepoNetworkProgress>? _progressChanged;

        public int Subscribers { get; private set; }
        public GitProgress? Current { get; set; }
        public RepositoryContext? Cancelled { get; private set; }

        public event EventHandler<RepoNetworkProgress>? ProgressChanged
        {
            add { _progressChanged += value; Subscribers++; }
            remove { _progressChanged -= value; Subscribers--; }
        }

        public void Report(RepositoryContext repo, GitProgress? progress)
            => _progressChanged?.Invoke(this, new RepoNetworkProgress(repo, progress));

        public void Cancel(RepositoryContext repo) => Cancelled = repo;

        public GitProgress? CurrentProgress(RepositoryContext repo) => Current;

        public Task RunExclusiveAsync(RepositoryContext repo, string label, Func<IProgress<GitProgress>, CancellationToken, Task> operation)
            => Task.CompletedTask;
    }

    private sealed class FakeConflicts : IConflictService
    {
        public int ContinueCalls { get; private set; }
        public int AbortCalls { get; private set; }
        public RepositoryContext? LastContext { get; private set; }
        public Task? ContinueGate { get; set; }
        public Exception? ContinueFails { get; set; }
        public Exception? AbortFails { get; set; }

        public async Task ContinueOperationAsync(RepositoryContext context, CancellationToken ct = default)
        {
            ContinueCalls++;
            LastContext = context;
            if (ContinueGate is not null)
                await ContinueGate;
            if (ContinueFails is not null)
                throw ContinueFails;
        }

        public Task AbortOperationAsync(RepositoryContext context, CancellationToken ct = default)
        {
            AbortCalls++;
            LastContext = context;
            return AbortFails is null ? Task.CompletedTask : Task.FromException(AbortFails);
        }

        public Task<IReadOnlyList<ConflictFile>> GetConflictsAsync(RepositoryContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConflictFile>>([]);

        public Task<ConflictWorkingCopy> LoadWorkingCopyAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string> GetDiskHashAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ResolutionSaveResult> SaveResolutionAsync(RepositoryContext context, string filePath, string text, TextFileFormat format, string expectedDiskHash, bool stage, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task ResolveWithSideAsync(RepositoryContext context, string filePath, ConflictSide side, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RecreateConflictAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ConflictSideLabels> GetSideLabelsAsync(RepositoryContext context, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> LaunchExternalMergeToolAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task SkipCurrentAsync(RepositoryContext context, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
