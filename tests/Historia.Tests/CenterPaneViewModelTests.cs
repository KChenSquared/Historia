namespace Historia.Tests;

/// <summary>The centre pane's view model: which of graph, diff, conflict resolver and AI window
/// shows, and the events that move it between them.</summary>
[Trait("tier", "git")]
public sealed class CenterPaneViewModelTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;
    private readonly RepositoryContext _ctx;
    private readonly ShellFakeWorkspace _workspace = new();
    private readonly AiIntegrationsPanel _panel = new();
    private readonly List<CenterPaneViewModel> _created = [];

    private static DiffRequest Diff(string path = "a.txt", int? focusLine = null) =>
        new() { Source = DiffSource.UnstagedChanges, Path = path, FocusLine = focusLine };

    public CenterPaneViewModelTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("center-pane-view-model-seed", b =>
        {
            b.WriteFile("a.txt", "one\n").Stage();
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

    private CenterPaneViewModel Create(
        IUiDispatcher? ui = null,
        bool withWorkspace = true,
        bool withPanel = true,
        Func<RepositoryContext, bool>? unsaved = null)
    {
        var vm = new CenterPaneViewModel(
            withWorkspace ? _workspace : null,
            withPanel ? _panel : null,
            ui ?? new ImmediateUiDispatcher(),
            unsaved);
        _created.Add(vm);
        return vm;
    }

    // A merge stopped before its commit with the resolver kept open: a live conflict session.
    // A refresh that sees no operation in progress clears the keep-open flag, so it is set only
    // once the merge is visible to the state.
    private async Task StartSessionAsync()
    {
        _repo.Git("checkout", "-b", "feature");
        _repo.WriteFile("g.txt", "feature\n").Stage();
        _repo.Commit("feature work");
        _repo.Git("checkout", "main");
        _repo.Git("merge", "--no-ff", "--no-commit", "feature");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            await _ctx.State.RefreshStatusAsync();
            if (_ctx.State.PendingOperation == OperationState.Merging)
                break;
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the merge.");
            await Task.Delay(50);
        }

        _ctx.State.SetKeepConflictResolverOpen(true);
        Assert.True(_ctx.State.IsConflictSessionActive, "The conflict session did not start.");
    }

    [Fact]
    public void With_no_repository_and_no_window_the_graph_shows()
    {
        var vm = Create(withWorkspace: false);

        Assert.Equal(CenterPaneMode.Graph, vm.Mode);
        Assert.Null(vm.ActiveRepo);
        Assert.Null(vm.OpenDiff);
    }

    [Fact]
    public void The_focused_repository_is_followed_from_the_start()
    {
        _ctx.State.SetOpenDiff(Diff());

        var vm = Create();

        Assert.Same(_ctx, vm.ActiveRepo);
        Assert.Equal(CenterPaneMode.Diff, vm.Mode);
    }

    [Fact]
    public void An_open_diff_shows_and_closing_it_returns_to_the_graph()
    {
        var vm = Create();
        Assert.Equal(CenterPaneMode.Graph, vm.Mode);

        var request = Diff();
        _ctx.State.SetOpenDiff(request);

        Assert.Equal(CenterPaneMode.Diff, vm.Mode);
        Assert.Equal(request, vm.OpenDiff);

        _ctx.State.SetOpenDiff(null);

        Assert.Equal(CenterPaneMode.Graph, vm.Mode);
        Assert.Null(vm.OpenDiff);
    }

    [Fact]
    public void An_open_AI_window_beats_the_graph_and_works_with_no_repository()
    {
        var vm = Create(withWorkspace: false);

        _panel.Open();
        Assert.Equal(CenterPaneMode.AiIntegrations, vm.Mode);

        _panel.Close();
        Assert.Equal(CenterPaneMode.Graph, vm.Mode);
    }

    [Fact]
    public void An_open_AI_window_beats_an_open_diff_and_closing_restores_the_diff()
    {
        var vm = Create();
        var request = Diff();
        _ctx.State.SetOpenDiff(request);

        _panel.Open();
        Assert.Equal(CenterPaneMode.AiIntegrations, vm.Mode);
        Assert.Null(vm.OpenDiff);

        _panel.Close();
        Assert.Equal(CenterPaneMode.Diff, vm.Mode);
        Assert.Equal(request, vm.OpenDiff);
    }

    [Fact]
    public void A_new_diff_closes_the_AI_window_and_shows_the_diff()
    {
        var vm = Create();
        _panel.Open();

        _ctx.State.SetOpenDiff(Diff());

        Assert.False(_panel.IsOpen);
        Assert.Equal(CenterPaneMode.Diff, vm.Mode);
    }

    [Fact]
    public void Closing_the_diff_leaves_the_AI_window_alone()
    {
        var vm = Create();
        _ctx.State.SetOpenDiff(Diff());
        _panel.Open();

        _ctx.State.SetOpenDiff(null);

        Assert.True(_panel.IsOpen);
        Assert.Equal(CenterPaneMode.AiIntegrations, vm.Mode);
    }

    [Fact]
    public void A_selected_conflict_path_closes_the_AI_window()
    {
        var vm = Create();
        _panel.Open();
        Assert.Equal(CenterPaneMode.AiIntegrations, vm.Mode);

        _ctx.State.SetSelectedConflictPath("a.txt");

        Assert.False(_panel.IsOpen);
        Assert.Equal(CenterPaneMode.Graph, vm.Mode);
    }

    [Fact]
    public async Task A_live_conflict_session_shows_the_resolver_until_the_user_closes_it()
    {
        var vm = Create();
        await StartSessionAsync();

        Assert.Equal(CenterPaneMode.Conflict, vm.Mode);

        _ctx.State.SetConflictResolverClosed(true);
        Assert.Equal(CenterPaneMode.Graph, vm.Mode);

        _ctx.State.SetConflictResolverClosed(false);
        Assert.Equal(CenterPaneMode.Conflict, vm.Mode);
    }

    [Fact]
    public async Task An_open_diff_beats_the_resolver_and_closing_it_returns_to_the_resolver()
    {
        // Every refresh runs on the test thread when it drains, as the UI thread would serialize
        // them. An immediate dispatcher would also run the watcher's status events on pool threads,
        // where a refresh computed before the diff opened can write its mode after this one.
        var ui = new RecordingUiDispatcher { OnUiThread = false };
        var vm = Create(ui);
        await StartSessionAsync();
        ui.DrainPosted();

        _ctx.State.SetOpenDiff(Diff());
        ui.DrainPosted();
        Assert.Equal(CenterPaneMode.Diff, vm.Mode);

        _ctx.State.SetOpenDiff(null);
        ui.DrainPosted();
        Assert.Equal(CenterPaneMode.Conflict, vm.Mode);
    }

    [Fact]
    public async Task Unsaved_resolver_work_keeps_the_resolver_after_the_session_and_the_closed_flag_cannot_hide_it()
    {
        var holdsEdits = true;
        var vm = Create(unsaved: _ => holdsEdits);
        Assert.Equal(CenterPaneMode.Conflict, vm.Mode);

        await StartSessionAsync();
        _repo.Git("merge", "--abort");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_ctx.State.IsConflictSessionActive)
        {
            await _ctx.State.RefreshStatusAsync();
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the session to end.");
            await Task.Delay(50);
        }

        _ctx.State.SetConflictResolverClosed(true);
        vm.Refresh();
        Assert.Equal(CenterPaneMode.Conflict, vm.Mode);

        holdsEdits = false;
        vm.Refresh();
        Assert.Equal(CenterPaneMode.Graph, vm.Mode);
    }

    [Fact]
    public void A_repeated_request_with_a_focus_line_asks_the_shown_diff_to_scroll_again()
    {
        var vm = Create();
        var changes = new List<bool>();
        vm.Changed += (_, e) => changes.Add(e.RefocusDiffLine);

        _ctx.State.SetOpenDiff(Diff(focusLine: 12));
        Assert.Equal([false], changes);

        _ctx.State.SetOpenDiff(Diff(focusLine: 12));
        Assert.Equal([false, true], changes);

        _ctx.State.SetOpenDiff(Diff("b.txt", focusLine: 12));
        Assert.Equal([false, true, false], changes);
    }

    [Fact]
    public void A_diff_without_a_focus_line_never_asks_to_scroll()
    {
        var vm = Create();
        var changes = new List<bool>();
        vm.Changed += (_, e) => changes.Add(e.RefocusDiffLine);

        _ctx.State.SetOpenDiff(Diff());
        _ctx.State.SetOpenDiff(Diff() with { ContextLines = 5 });

        Assert.DoesNotContain(true, changes);
    }

    [Fact]
    public void A_focus_line_is_claimed_once_per_request_instance()
    {
        var vm = Create();
        var request = Diff(focusLine: 7);

        Assert.False(vm.ClaimFocusLine(request));
        Assert.True(vm.ClaimFocusLine(request));
        Assert.False(vm.ClaimFocusLine(Diff(focusLine: 7)));
        Assert.False(vm.ClaimFocusLine(Diff()));
        Assert.False(vm.ClaimFocusLine(Diff()));
    }

    [Fact]
    public void A_repeated_request_marks_its_line_as_shown()
    {
        var vm = Create();
        var first = Diff(focusLine: 3);
        _ctx.State.SetOpenDiff(first);
        var repeated = Diff(focusLine: 3);
        _ctx.State.SetOpenDiff(repeated);

        Assert.True(vm.ClaimFocusLine(repeated));
    }

    [Fact]
    public async Task Following_another_repository_announces_the_one_left_and_rebinds()
    {
        var vm = Create();
        var left = new List<RepositoryContext>();
        vm.RepositoryLeft += (_, ctx) => left.Add(ctx);
        var otherRepo = TestRepositoryBuilder.FromTemplate("center-pane-view-model-seed", _ => { });
        await using var other = _host.Open(otherRepo.Path);

        vm.Bind(_ctx);
        Assert.Empty(left);

        _workspace.FocusAndAnnounce(other);

        Assert.Equal([_ctx], left);
        Assert.Same(other, vm.ActiveRepo);

        _ctx.State.SetOpenDiff(Diff());
        Assert.Equal(CenterPaneMode.Graph, vm.Mode);

        other.State.SetOpenDiff(Diff());
        Assert.Equal(CenterPaneMode.Diff, vm.Mode);

        _workspace.FocusAndAnnounce(null);
        Assert.Equal([_ctx, other], left);
        Assert.Equal(CenterPaneMode.Graph, vm.Mode);
        otherRepo.Dispose();
    }

    [Fact]
    public void Every_state_event_is_applied_on_the_ui_thread()
    {
        var ui = new RecordingUiDispatcher();
        var vm = Create(ui);

        _ctx.State.SetOpenDiff(Diff());
        Assert.Equal(CenterPaneMode.Graph, vm.Mode);
        Assert.True(ui.PostCount >= 1);

        ui.DrainPosted();
        Assert.Equal(CenterPaneMode.Diff, vm.Mode);

        _panel.Open();
        Assert.Equal(CenterPaneMode.Diff, vm.Mode);
        ui.DrainPosted();
        Assert.Equal(CenterPaneMode.AiIntegrations, vm.Mode);

        _workspace.FocusAndAnnounce(null);
        ui.DrainPosted();
        Assert.Null(vm.ActiveRepo);
    }

    [Fact]
    public void Dispose_unsubscribes_from_the_panel_the_workspace_and_the_repository()
    {
        var vm = Create();
        Assert.Equal(1, _workspace.FocusedSubscribers);
        var raised = 0;
        vm.Changed += (_, _) => raised++;

        vm.Dispose();

        Assert.Equal(0, _workspace.FocusedSubscribers);
        _panel.Open();
        _ctx.State.SetOpenDiff(Diff());
        vm.Refresh();
        vm.Bind(null);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void An_update_posted_before_dispose_does_nothing_afterwards()
    {
        var ui = new RecordingUiDispatcher();
        var vm = Create(ui);
        _ctx.State.SetOpenDiff(Diff());

        vm.Dispose();
        ui.DrainPosted();

        Assert.Equal(CenterPaneMode.Graph, vm.Mode);
    }
}
