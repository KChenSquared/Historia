namespace Historia.Tests;

/// <summary>The diff shell's view model: mode and its persistence, the body states, change
/// navigation, close, hunk and line staging, and the focus line.</summary>
[Trait("tier", "git")]
public sealed class DiffViewModelTests : IAsyncDisposable
{
    private static readonly DiffRequest Unstaged = new() { Source = DiffSource.UnstagedChanges, Path = "a.txt" };

    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;
    private readonly RepositoryContext _ctx;
    private readonly FakePatchService _patches = new();
    private readonly FakeNotificationService _notifications = new();
    private readonly List<DiffViewModel> _created = [];
    private DiffViewModel? _probe;
    private readonly List<(MiniHost Host, RepositoryContext Ctx)> _gated = [];

    public DiffViewModelTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("diff-view-model-seed", b =>
        {
            b.WriteFile("a.txt", string.Join("\n", Enumerable.Range(1, 200).Select(i => $"line {i}")) + "\n").Stage();
            b.Commit("c1");
            b.WriteFile("a.txt", string.Join("\n", Enumerable.Range(1, 200).Select(i =>
                i is 5 or 170 ? $"line {i} changed" : $"line {i}")) + "\n");
        });
        _ctx = _host.Open(_repo.Path);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var vm in _created)
            vm.Dispose();
        foreach (var (host, ctx) in _gated)
        {
            await ctx.DisposeAsync();
            await host.DisposeAsync();
        }
        await _ctx.DisposeAsync();
        await _host.DisposeAsync();
        _repo.Dispose();
    }

    private DiffViewModel Create(
        DiffRequest? request = null,
        AppSettings? settings = null,
        ISettingsService? settingsService = null,
        bool withPatches = true,
        IUiDispatcher? ui = null)
    {
        var vm = new DiffViewModel(
            _ctx,
            request ?? Unstaged,
            ui ?? new ImmediateUiDispatcher(),
            settingsService ?? new FakeSettingsService(settings),
            withPatches ? _patches : null,
            _notifications);
        _created.Add(vm);
        return vm;
    }

    // A context whose file diff read waits until Release is completed.
    private (RepositoryContext Ctx, BlockingGitCliService Cli) OpenGated()
    {
        var host = new MiniHost();
        var cli = new BlockingGitCliService(host.Cli, req => req.Arguments is ["diff", .., "--", "a.txt"]);
        var ctx = host.Open(_repo.Path, cli);
        _gated.Add((host, ctx));
        return (ctx, cli);
    }

    private static async Task<DiffViewModel> Loaded(DiffViewModel vm)
    {
        // A slow read shows the loading message first, which is not the loaded state.
        static bool Settled(DiffViewModel v) => v.ShowsDiff || (v.HasMessage && v.Message != DiffViewModel.LoadingMessage);

        for (var i = 0; i < 250 && !Settled(vm); i++)
            await Task.Delay(20);

        Assert.True(Settled(vm), "the diff never loaded");
        return vm;
    }

    [Fact]
    public async Task The_diff_loads_into_a_display_model_built_off_the_calling_thread()
    {
        var vm = await Loaded(Create());

        Assert.True(vm.ShowsDiff);
        Assert.Null(vm.Message);
        Assert.NotNull(vm.Diff);
        Assert.NotNull(vm.Model);
        Assert.NotEqual(Environment.CurrentManagedThreadId, vm.LastModelCreationThreadId);
        Assert.True(vm.LastModelCreationThreadId > 0);
        Assert.Equal("a.txt", vm.Path);
    }

    [Theory]
    [InlineData(DiffViewMode.Unified, false, "Side-by-side")]
    [InlineData(DiffViewMode.SideBySide, true, "Unified")]
    public void The_saved_mode_selects_the_initial_mode(DiffViewMode saved, bool sideBySide, string label)
    {
        var vm = Create(settings: new AppSettings { DefaultDiffView = saved });

        Assert.Equal(sideBySide, vm.IsSideBySide);
        Assert.Equal(label, vm.ToggleLabel);
    }

    [Fact]
    public void Without_settings_the_diff_opens_unified()
    {
        var vm = new DiffViewModel(_ctx, Unstaged, new ImmediateUiDispatcher());
        _created.Add(vm);

        Assert.False(vm.IsSideBySide);
        Assert.False(vm.WordWrap);
        vm.ToggleModeCommand.Execute(null);
        Assert.True(vm.IsSideBySide);
    }

    [Fact]
    public async Task Toggling_the_mode_saves_it_and_republishes_the_body()
    {
        var settings = new FakeSettingsService(new AppSettings { DefaultDiffView = DiffViewMode.Unified });
        var vm = await Loaded(Create(settingsService: settings));
        var published = 0;
        vm.PresentationChanged += (_, _) => published++;
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.ToggleModeCommand.Execute(null);

        Assert.True(vm.IsSideBySide);
        Assert.Equal(DiffViewMode.SideBySide, settings.Current.DefaultDiffView);
        Assert.Equal("Unified", vm.ToggleLabel);
        Assert.Contains(nameof(DiffViewModel.ToggleLabel), changed);
        Assert.Equal(1, published);

        vm.ToggleModeCommand.Execute(null);

        Assert.False(vm.IsSideBySide);
        Assert.Equal(DiffViewMode.Unified, settings.Current.DefaultDiffView);
        Assert.Equal(2, published);
    }

    [Fact]
    public void Toggling_before_the_diff_has_loaded_flips_the_mode_and_publishes_nothing()
    {
        var (ctx, cli) = OpenGated();
        var vm = new DiffViewModel(ctx, Unstaged, new ImmediateUiDispatcher(), new FakeSettingsService(), _patches, _notifications);
        _created.Add(vm);
        var published = 0;
        vm.PresentationChanged += (_, _) => published++;

        vm.ToggleModeCommand.Execute(null);

        Assert.True(vm.IsSideBySide);
        Assert.Equal(0, published);
        cli.Release.SetResult();
    }

    [Fact]
    public async Task A_failed_save_publishes_a_warning_and_keeps_the_mode()
    {
        var vm = await Loaded(Create(settingsService: new ThrowingSettingsService()));

        vm.ToggleModeCommand.Execute(null);
        for (var i = 0; i < 100 && _notifications.Published.Count == 0; i++)
            await Task.Delay(10);

        Assert.True(vm.IsSideBySide);
        var warning = Assert.Single(_notifications.Published);
        Assert.Equal(NotificationSeverity.Warning, warning.Severity);
        Assert.Equal("Failed to save diff view preference.", warning.Message);
    }

    [Fact]
    public async Task Word_wrap_follows_the_setting()
    {
        var vm = await Loaded(Create(settings: new AppSettings { DiffWordWrap = true }));

        Assert.True(vm.WordWrap);
    }

    [Fact]
    public void Previous_and_next_change_raise_navigation_requests()
    {
        var vm = Create();
        var requests = new List<DiffNavigation>();
        vm.NavigationRequested += (_, d) => requests.Add(d);

        vm.PreviousChangeCommand.Execute(null);
        vm.NextChangeCommand.Execute(null);

        Assert.Equal([DiffNavigation.PreviousChange, DiffNavigation.NextChange], requests);
    }

    [Fact]
    public void Close_clears_the_open_diff()
    {
        _ctx.State.SetOpenDiff(Unstaged);
        var vm = Create();
        Assert.NotNull(_ctx.State.OpenDiff);

        vm.CloseCommand.Execute(null);

        Assert.Null(_ctx.State.OpenDiff);
    }

    [Fact]
    public async Task A_diff_with_no_hunks_shows_a_message_instead_of_the_presenters()
    {
        var vm = await Loaded(Create(new DiffRequest { Source = DiffSource.StagedChanges, Path = "a.txt" }));

        Assert.False(vm.ShowsDiff);
        Assert.Equal("No changes.", vm.Message);
        Assert.False(vm.IsMessageError);
        Assert.True(vm.HasMessage);
    }

    [Fact]
    public async Task A_failed_load_shows_the_error_as_a_message()
    {
        var vm = await Loaded(Create(new DiffRequest
        {
            Source = DiffSource.Commit,
            CommitId = new GitObjectId("0123456789012345678901234567890123456789"),
            Path = "a.txt",
        }));

        Assert.False(vm.ShowsDiff);
        Assert.True(vm.IsMessageError);
        Assert.StartsWith("Could not load diff: ", vm.Message);
    }

    [Fact]
    public async Task Loading_text_appears_only_when_the_read_outlasts_the_delay_and_goes_through_the_dispatcher()
    {
        var (ctx, cli) = OpenGated();
        var ui = new RecordingUiDispatcher();
        var vm = new DiffViewModel(ctx, Unstaged, ui, new FakeSettingsService(), _patches, _notifications);
        _created.Add(vm);

        for (var i = 0; i < 100 && ui.PostCount == 0; i++)
            await Task.Delay(20);
        Assert.Equal(1, ui.PostCount);
        Assert.Null(vm.Message);

        ui.DrainPosted();
        Assert.Equal(DiffViewModel.LoadingMessage, vm.Message);
        Assert.False(vm.IsMessageError);

        cli.Release.SetResult();
        await Loaded(vm);
        for (var i = 0; i < 100 && !vm.ShowsDiff; i++)
            await Task.Delay(20);

        Assert.True(vm.ShowsDiff);
        Assert.Null(vm.Message);
    }

    [Fact]
    public async Task A_hunk_action_stages_the_single_hunk_forward_for_an_unstaged_diff()
    {
        var vm = await Loaded(Create());
        Assert.True(vm.CanStage);
        Assert.Equal("Stage", vm.StageVerb);

        await vm.ApplyHunkAsync(1);

        Assert.Equal(PatchScope.SingleHunk, _patches.LastScope);
        Assert.Equal([new DiffLineRef(1, 0)], _patches.LastSelectedLines);
        Assert.False(_patches.LastReverse);
        Assert.False(_patches.LastAppliedReverse);
        Assert.Equal(PatchTarget.StagingArea, _patches.LastAppliedTarget);
        Assert.Equal(1, _patches.ApplyPatchCount);
    }

    [Fact]
    public async Task A_hunk_action_unstages_in_reverse_for_a_staged_diff()
    {
        _repo.Stage("a.txt");
        var vm = await Loaded(Create(new DiffRequest { Source = DiffSource.StagedChanges, Path = "a.txt" }));
        Assert.True(vm.CanStage);
        Assert.Equal("Unstage", vm.StageVerb);

        await vm.ApplyHunkAsync(0);

        Assert.True(_patches.LastReverse);
        Assert.True(_patches.LastAppliedReverse);
        Assert.Equal(1, _patches.ApplyPatchCount);
    }

    [Fact]
    public async Task A_selection_action_passes_the_selected_lines_scope_and_exact_refs()
    {
        var vm = await Loaded(Create());

        await vm.ApplySelectionAsync([new DiffLineRef(0, 1), new DiffLineRef(0, 2)]);

        Assert.Equal(PatchScope.SelectedLines, _patches.LastScope);
        Assert.Equal(2, _patches.LastSelectedLines!.Count);
        Assert.Contains(new DiffLineRef(0, 1), _patches.LastSelectedLines);
        Assert.Contains(new DiffLineRef(0, 2), _patches.LastSelectedLines);
        Assert.Equal(1, _patches.ApplyPatchCount);
    }

    [Fact]
    public async Task A_commit_diff_and_a_diff_without_a_patch_service_offer_no_staging()
    {
        var commit = await Loaded(Create(new DiffRequest
        {
            Source = DiffSource.Commit,
            CommitId = new GitObjectId(_repo.HeadSha),
            Path = "a.txt",
        }));
        var noPatches = await Loaded(Create(withPatches: false));

        Assert.False(commit.CanStage);
        Assert.False(noPatches.CanStage);
        await noPatches.ApplyHunkAsync(0);
        Assert.Equal(0, _patches.GeneratePatchCount);
    }

    [Fact]
    public async Task A_second_request_while_one_is_in_flight_is_ignored()
    {
        var vm = await Loaded(Create());
        var gate = new TaskCompletionSource<bool>();
        _patches.CheckGate = gate;

        var first = vm.ApplyHunkAsync(0);
        await vm.ApplyHunkAsync(1);
        await vm.ApplySelectionAsync([new DiffLineRef(0, 1)]);

        Assert.Equal(1, _patches.GeneratePatchCount);
        Assert.Equal(PatchScope.SingleHunk, _patches.LastScope);
        Assert.Equal(0, _patches.ApplyPatchCount);

        _patches.CheckGate = null;
        gate.SetResult(true);
        await first;
        Assert.Equal(1, _patches.ApplyPatchCount);

        await vm.ApplyHunkAsync(0);
        Assert.Equal(2, _patches.ApplyPatchCount);
    }

    [Fact]
    public async Task A_refused_check_warns_and_the_guard_clears()
    {
        var vm = await Loaded(Create());
        _patches.CheckResult = new PatchCheckResult(false, "conflict");

        await vm.ApplyHunkAsync(0);

        Assert.Equal(0, _patches.ApplyPatchCount);
        var warning = Assert.Single(_notifications.Published);
        Assert.Equal(NotificationSeverity.Warning, warning.Severity);
        Assert.Contains("Cannot stage this hunk: conflict", warning.Message);

        _patches.CheckResult = new PatchCheckResult(true, null);
        await vm.ApplyHunkAsync(0);
        Assert.Equal(1, _patches.ApplyPatchCount);
    }

    [Fact]
    public async Task A_git_failure_while_building_the_patch_warns_and_skips_the_apply()
    {
        var vm = await Loaded(Create());
        _patches.ThrowOnGenerate = new HistoriaGitException(GitErrorKind.Unknown, "splits an end-of-file newline change");

        await vm.ApplyHunkAsync(0);

        var warning = Assert.Single(_notifications.Published);
        Assert.Equal(NotificationSeverity.Warning, warning.Severity);
        Assert.Contains("splits an end-of-file newline change", warning.Message);
        Assert.Equal(0, _patches.ApplyPatchCount);
    }

    [Fact]
    public async Task Any_other_failure_while_applying_is_an_error_and_the_guard_clears()
    {
        var vm = await Loaded(Create());
        _patches.ThrowOnApply = new InvalidOperationException("simulated failure");

        await vm.ApplyHunkAsync(0);

        var error = Assert.Single(_notifications.Published);
        Assert.Equal(NotificationSeverity.Error, error.Severity);
        Assert.Contains("simulated failure", error.Message);

        _patches.ThrowOnApply = null;
        await vm.ApplyHunkAsync(0);
        Assert.Equal(1, _patches.ApplyPatchCount);
    }

    [Fact]
    public async Task After_an_apply_the_diff_is_reloaded_and_republished()
    {
        var vm = await Loaded(Create());
        var before = vm.Model;
        var published = 0;
        vm.PresentationChanged += (_, _) => published++;

        await vm.ApplyHunkAsync(0);

        Assert.Equal(1, published);
        Assert.NotSame(before, vm.Model);
        Assert.True(vm.ShowsDiff);
    }

    [Fact]
    public async Task A_focus_line_is_applied_once_and_later_mode_switches_do_not_pull_back()
    {
        var vm = await Loaded(Create(new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "a.txt", FocusLine = 170 }));
        var expected = vm.Model!.FindDisplayLineForNewLine(170, vm.IsSideBySide)!.Value;

        Assert.Equal(FocusLineOutcome.Scroll, vm.ResolveFocusLine(presenterReady: true, out var line));
        Assert.Equal(expected, line);

        Assert.Equal(FocusLineOutcome.Nothing, vm.ResolveFocusLine(presenterReady: true, out _));

        var requested = 0;
        vm.FocusLineRequested += (_, _) => requested++;
        vm.ToggleModeCommand.Execute(null);
        Assert.Equal(0, requested);
    }

    [Fact]
    public async Task A_load_with_a_pending_focus_line_asks_the_view_to_scroll()
    {
        var (ctx, cli) = OpenGated();
        var vm = new DiffViewModel(ctx, new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "a.txt", FocusLine = 170 },
            new ImmediateUiDispatcher(), new FakeSettingsService(), _patches, _notifications);
        _created.Add(vm);
        var requested = 0;
        vm.FocusLineRequested += (_, _) => requested++;

        cli.Release.SetResult();
        await Loaded(vm);
        for (var i = 0; i < 100 && requested == 0; i++)
            await Task.Delay(20);

        Assert.Equal(1, requested);
    }

    [Fact]
    public async Task Skipping_the_initial_focus_line_opens_at_the_top_but_a_refocus_still_scrolls()
    {
        var request = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "a.txt", FocusLine = 170 };
        var vm = Create(request);
        vm.SkipInitialFocusLine();
        await Loaded(vm);

        Assert.Equal(FocusLineOutcome.Nothing, vm.ResolveFocusLine(true, out _));

        var requested = 0;
        vm.FocusLineRequested += (_, _) => requested++;
        vm.RefocusLine();

        Assert.Equal(1, requested);
        Assert.Equal(FocusLineOutcome.Scroll, vm.ResolveFocusLine(true, out _));
    }

    [Fact]
    public async Task A_refocus_without_a_focus_line_or_after_dispose_does_nothing()
    {
        var plain = await Loaded(Create());
        var withLine = await Loaded(Create(new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "a.txt", FocusLine = 170 }));
        var requested = 0;
        plain.FocusLineRequested += (_, _) => requested++;
        withLine.FocusLineRequested += (_, _) => requested++;

        plain.RefocusLine();
        withLine.Dispose();
        withLine.RefocusLine();

        Assert.Equal(0, requested);
    }

    [Fact]
    public async Task A_presenter_that_never_gets_laid_out_is_retried_a_limited_number_of_times()
    {
        var vm = await Loaded(Create(new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "a.txt", FocusLine = 170 }));

        var retries = 0;
        FocusLineOutcome outcome;
        while ((outcome = vm.ResolveFocusLine(presenterReady: false, out _)) == FocusLineOutcome.Retry)
            retries++;

        Assert.Equal(19, retries);
        Assert.Equal(FocusLineOutcome.Scroll, outcome);
        Assert.Equal(FocusLineOutcome.Nothing, vm.ResolveFocusLine(false, out _));
    }

    [Fact]
    public async Task A_focus_line_outside_every_hunk_scrolls_to_the_nearest_row_once()
    {
        var vm = await Loaded(Create(new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "a.txt", FocusLine = 100 }));
        var nearest = vm.Model!.FindDisplayLineForNewLine(100, vm.IsSideBySide)!.Value;

        Assert.Equal(FocusLineOutcome.Scroll, vm.ResolveFocusLine(true, out var line));
        Assert.Equal(nearest, line);
        Assert.Equal(FocusLineOutcome.Nothing, vm.ResolveFocusLine(true, out _));
    }

    [Fact]
    public async Task A_disposed_view_model_ignores_a_late_load_result()
    {
        var (ctx, cli) = OpenGated();
        var vm = new DiffViewModel(ctx, Unstaged, new ImmediateUiDispatcher(), new FakeSettingsService(), _patches, _notifications);
        var published = 0;
        vm.PresentationChanged += (_, _) => published++;

        vm.Dispose();
        cli.Release.SetResult();
        await Task.Delay(300);

        Assert.True(vm.IsDisposed);
        Assert.Equal(0, published);
        Assert.Null(vm.Diff);
        Assert.Null(vm.Message);
        Assert.False(vm.ShowsDiff);
    }

    [Fact]
    public async Task Disposing_stops_the_loading_placeholder()
    {
        var (ctx, cli) = OpenGated();
        var ui = new RecordingUiDispatcher();
        var vm = new DiffViewModel(ctx, Unstaged, ui, new FakeSettingsService(), _patches, _notifications);

        vm.Dispose();
        await Task.Delay((int)DiffViewModel.LoadingDelay.TotalMilliseconds + 200);
        ui.DrainPosted();

        Assert.Equal(0, ui.PostCount);
        Assert.Null(vm.Message);
        cli.Release.SetResult();
    }

    [Fact]
    public async Task Disposing_during_a_blocked_read_cancels_the_git_read()
    {
        var (ctx, cli) = OpenGated();
        var vm = new DiffViewModel(ctx, Unstaged, new ImmediateUiDispatcher(), new FakeSettingsService(), _patches, _notifications);
        await cli.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(cli.CancelObserved.Task.IsCompleted);

        vm.Dispose();

        await cli.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(vm.IsDisposed);
        Assert.Null(vm.Diff);
        Assert.Null(vm.Message);
    }

    [Fact]
    public async Task A_disposed_view_model_is_collectable_once_its_read_is_cancelled()
    {
        var (ctx, cli) = OpenGated();
        var weak = StartProbe(ctx);
        await cli.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        DisposeProbe();

        // A loaded machine can delay finalization, so poll with a generous deadline.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (weak.IsAlive && DateTime.UtcNow < deadline)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(50);
        }

        Assert.False(weak.IsAlive, "the disposed diff view model is still reachable");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference StartProbe(RepositoryContext ctx)
    {
        _probe = new DiffViewModel(ctx, Unstaged, new ImmediateUiDispatcher(), new FakeSettingsService(), _patches, _notifications);
        return new WeakReference(_probe);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DisposeProbe()
    {
        _probe!.Dispose();
        _probe = null;
    }

    [Fact]
    public async Task Staging_runs_on_the_repository_lifetime_and_dispose_does_not_cancel_it()
    {
        var vm = await Loaded(Create());
        var gate = new TaskCompletionSource<bool>();
        _patches.CheckGate = gate;

        var apply = vm.ApplyHunkAsync(0);
        vm.Dispose();

        Assert.Equal(_ctx.Lifetime, _patches.LastCheckToken);
        Assert.False(_patches.LastCheckToken.IsCancellationRequested);
        gate.SetResult(true);
        await apply;

        _patches.CheckGate = null;
        var second = await Loaded(Create());
        await second.ApplyHunkAsync(0);

        Assert.Equal(_ctx.Lifetime, _patches.LastApplyToken);
        Assert.False(_patches.LastApplyToken.IsCancellationRequested);
    }

    private sealed class FakePatchService : IPatchService
    {
        public PatchScope LastScope { get; private set; }
        public IReadOnlySet<DiffLineRef>? LastSelectedLines { get; private set; }
        public bool LastReverse { get; private set; }
        public int GeneratePatchCount { get; private set; }
        public int ApplyPatchCount { get; private set; }
        public PatchTarget LastAppliedTarget { get; private set; }
        public bool LastAppliedReverse { get; private set; }
        public Exception? ThrowOnGenerate { get; set; }
        public Exception? ThrowOnApply { get; set; }
        public PatchCheckResult CheckResult { get; set; } = new(true, null);
        public TaskCompletionSource<bool>? CheckGate { get; set; }
        public CancellationToken LastCheckToken { get; private set; }
        public CancellationToken LastApplyToken { get; private set; }

        public string GeneratePatch(FileDiff source, PatchScope scope, IReadOnlySet<DiffLineRef>? selectedLines = null, bool reverse = false)
        {
            if (ThrowOnGenerate is not null)
                throw ThrowOnGenerate;

            LastScope = scope;
            LastSelectedLines = selectedLines;
            LastReverse = reverse;
            GeneratePatchCount++;
            return "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n";
        }

        public async Task<PatchCheckResult> CheckAsync(RepositoryContext context, string patch, PatchTarget target, bool reverse, CancellationToken ct = default)
        {
            LastCheckToken = ct;
            if (CheckGate is { } gate)
                await gate.Task;
            return CheckResult;
        }

        public Task ApplyPatchAsync(RepositoryContext context, string patch, PatchTarget target, bool reverse, CancellationToken ct = default)
        {
            LastApplyToken = ct;
            if (ThrowOnApply is not null)
                throw ThrowOnApply;

            LastAppliedTarget = target;
            LastAppliedReverse = reverse;
            ApplyPatchCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new() { DefaultDiffView = DiffViewMode.Unified };
        public event EventHandler<SettingsChangedEventArgs>? Changed { add { } remove { } }
        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default)
            => Task.FromException(new IOException("disk full"));
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
