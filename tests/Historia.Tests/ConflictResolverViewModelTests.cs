namespace Historia.Tests;

/// <summary>
/// The conflict resolver's view model: it loads the conflicted files, drives the editor surface,
/// gates every action on the busy flag, and asks first before each destructive action, with a
/// refusal changing nothing. The repository is real; the conflict service, the dialogs and the
/// editor surface are fakes.
/// </summary>
[Trait("tier", "git")]
public sealed class ConflictResolverViewModelTests : IAsyncDisposable
{
    private const string ConflictText =
        "header\n<<<<<<< HEAD\nours\n=======\ntheirs\n>>>>>>> branch\nfooter\n";

    private static readonly ConflictFile FileA = new() { FilePath = "a.txt", Status = ConflictStatus.BothModified, IsBinary = false };
    private static readonly ConflictFile FileB = new() { FilePath = "b.txt", Status = ConflictStatus.BothModified, IsBinary = false };

    private sealed class FakeConflicts : IConflictService
    {
        public IReadOnlyList<ConflictFile> Conflicts { get; set; } = [];
        public Dictionary<string, ConflictWorkingCopy> Copies { get; } = new(StringComparer.Ordinal);
        public ConflictSideLabels Labels { get; set; } = new()
        {
            OursTitle = "main",
            OursDetail = "main detail",
            TheirsTitle = "feature",
            TheirsDetail = "abc1234 Add feature",
            Operation = OperationState.Merging,
            StopId = "stop-1",
        };
        public ResolutionSaveResult SaveResult { get; set; } = new() { Saved = true, DiskHash = "saved-hash" };
        public Func<Task>? OnContinue { get; set; }
        public Func<Task>? OnBeforeSave { get; set; }
        public string? DiskHash { get; set; }
        public bool LaunchResult { get; set; } = true;
        public bool RemoveOnResolve { get; set; } = true;

        public List<(string Path, string Text, bool Stage)> Saves { get; } = [];
        public List<(string Path, ConflictSide Side)> Sides { get; } = [];
        public List<string> Recreated { get; } = [];
        public List<string> Launched { get; } = [];
        public int ContinueCalls { get; private set; }
        public int SkipCalls { get; private set; }
        public int AbortCalls { get; private set; }
        public int LoadCalls { get; private set; }

        public Task<IReadOnlyList<ConflictFile>> GetConflictsAsync(RepositoryContext context, CancellationToken ct = default)
            => Task.FromResult(Conflicts);

        public Task<ConflictWorkingCopy> LoadWorkingCopyAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
        {
            LoadCalls++;
            var copy = Copies.TryGetValue(filePath, out var found)
                ? found
                : new ConflictWorkingCopy { Path = filePath, Text = "", DiskHash = "default-hash" };
            if (copy.Format is null && !copy.IsBinary && !copy.UnsupportedEncoding)
                copy = copy with { Format = new TextFileFormat(Encoding.UTF8, false) };
            return Task.FromResult(copy);
        }

        public Task<string> GetDiskHashAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
            => Task.FromResult(DiskHash ?? (Copies.TryGetValue(filePath, out var copy) ? copy.DiskHash : "default-hash"));

        public async Task<ResolutionSaveResult> SaveResolutionAsync(RepositoryContext context, string filePath, string text, TextFileFormat format, string expectedDiskHash, bool stage, CancellationToken ct = default)
        {
            Saves.Add((filePath, text, stage));
            if (OnBeforeSave is not null)
                await OnBeforeSave();
            if (stage && RemoveOnResolve && SaveResult.Saved)
                Conflicts = [.. Conflicts.Where(c => c.FilePath != filePath)];
            return SaveResult;
        }

        public Task ResolveWithSideAsync(RepositoryContext context, string filePath, ConflictSide side, CancellationToken ct = default)
        {
            Sides.Add((filePath, side));
            if (RemoveOnResolve)
                Conflicts = [.. Conflicts.Where(c => c.FilePath != filePath)];
            return Task.CompletedTask;
        }

        public Task RecreateConflictAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
        {
            Recreated.Add(filePath);
            return Task.CompletedTask;
        }

        public Task<ConflictSideLabels> GetSideLabelsAsync(RepositoryContext context, CancellationToken ct = default)
            => Task.FromResult(Labels);

        public Task<bool> LaunchExternalMergeToolAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
        {
            Launched.Add(filePath);
            return Task.FromResult(LaunchResult);
        }

        public async Task ContinueOperationAsync(RepositoryContext context, CancellationToken ct = default)
        {
            ContinueCalls++;
            if (OnContinue is not null)
                await OnContinue();
        }

        public Task SkipCurrentAsync(RepositoryContext context, CancellationToken ct = default)
        {
            SkipCalls++;
            return Task.CompletedTask;
        }

        public Task AbortOperationAsync(RepositoryContext context, CancellationToken ct = default)
        {
            AbortCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSurface : IConflictEditorSurface
    {
        public string Text { get; private set; } = "";
        public string? FilePath { get; private set; }
        public int Revision { get; set; }
        public ConflictDocumentSummary Summary { get; set; }
        public List<string> Calls { get; } = [];

        public string ComposeOutput() => Text;
        public object CaptureRevision() => Revision;
        public bool IsRevisionCurrent(object revision) => (int)revision == Revision;
        public void MarkClean() => Calls.Add("MarkClean");
        public ConflictDocumentSummary Summarize() => Summary;

        public void LoadDocument(string filePath, string text)
        {
            Calls.Add($"Load:{filePath}");
            FilePath = filePath;
            Text = text;
        }

        public void EnterEditMode() => Calls.Add("Enter");
        public void LeaveEditMode() => Calls.Add("Leave");
        public void ClearDocument() { Calls.Add("Clear"); Text = ""; }
        public void GoToNextConflict() => Calls.Add("Next");
        public void GoToPreviousConflict() => Calls.Add("Previous");
    }

    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];
    private readonly List<RepositoryContext> _contexts = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var ctx in _contexts)
            await ctx.DisposeAsync();
        await _host.DisposeAsync();
        foreach (var repo in _repos)
            repo.Dispose();
    }

    private sealed class Rig
    {
        public required ConflictResolverViewModel Vm { get; init; }
        public required FakeConflicts Service { get; init; }
        public required FakeSurface Surface { get; init; }
        public required FakeDialogService Dialogs { get; init; }
        public required FakeNotificationService Notifications { get; init; }
        public required UnsavedWorkRegistry Registry { get; init; }
        public required NavigationBus Nav { get; init; }
        public required RepositoryContext Context { get; init; }
        public required TestRepositoryBuilder Repo { get; init; }
    }

    private Rig Create(
        IReadOnlyList<ConflictFile>? files = null,
        IUiDispatcher? ui = null,
        bool attach = true,
        Action<FakeConflicts>? configure = null,
        string text = ConflictText)
    {
        var repo = TestRepositoryBuilder.FromTemplate("conflict-vm", b => b.WriteFile("a.txt", "one\n").Stage().Commit("init"));
        _repos.Add(repo);
        var ctx = _host.Open(repo.Path);
        _contexts.Add(ctx);

        var service = new FakeConflicts { Conflicts = files ?? [FileA] };
        foreach (var file in service.Conflicts)
            service.Copies[file.FilePath] = new ConflictWorkingCopy { Path = file.FilePath, Text = text, DiskHash = "hash-" + file.FilePath };
        configure?.Invoke(service);

        var surface = new FakeSurface();
        var dialogs = new FakeDialogService();
        var notifications = new FakeNotificationService();
        var registry = new UnsavedWorkRegistry();
        var nav = new NavigationBus();
        var vm = new ConflictResolverViewModel(
            ctx, service, dialogs, notifications, ui ?? new ImmediateUiDispatcher(), registry, nav, new FakeSettingsService());
        if (attach)
            vm.Attach(surface);

        return new Rig
        {
            Vm = vm,
            Service = service,
            Surface = surface,
            Dialogs = dialogs,
            Notifications = notifications,
            Registry = registry,
            Nav = nav,
            Context = ctx,
            Repo = repo,
        };
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "The condition was not met in time.");
    }

    private static async Task<Rig> LoadedAsync(Rig rig)
    {
        await WaitUntilAsync(() => rig.Vm.CurrentFile is not null || rig.Vm.ShowAllResolvedCard);
        return rig;
    }

    [Fact(Timeout = 60000)]
    public async Task Attach_opens_the_first_conflicted_file_in_the_editors()
    {
        var rig = await LoadedAsync(Create(files: [FileA, FileB]));

        Assert.Equal("a.txt", rig.Vm.CurrentFile?.FilePath);
        Assert.Equal("a.txt", rig.Context.State.SelectedConflictPath);
        Assert.True(rig.Vm.IsEditable);
        Assert.True(rig.Vm.ShowEditors);
        Assert.True(rig.Vm.FilesRemain);
        Assert.False(rig.Vm.ContinueIsPrimary);
        Assert.Equal("hash-a.txt", rig.Vm.DiskHash);
        Assert.Equal(ConflictText, rig.Surface.Text);
        Assert.Equal(["Load:a.txt", "Enter"], rig.Surface.Calls.Where(c => c is not "MarkClean").ToArray());
        Assert.Equal("Ours · main", rig.Vm.OursCaption);
        Assert.Equal("Theirs · feature", rig.Vm.TheirsCaption);
        Assert.Equal("abc1234 Add feature", rig.Vm.TheirsCaptionToolTip);
    }

    [Fact(Timeout = 60000)]
    public async Task No_conflicted_file_shows_the_all_resolved_card_and_makes_continue_primary()
    {
        var rig = await LoadedAsync(Create(files: []));

        Assert.True(rig.Vm.ShowAllResolvedCard);
        Assert.False(rig.Vm.ShowEditors);
        Assert.Null(rig.Vm.CurrentFile);
        Assert.False(rig.Vm.IsEditable);
        Assert.False(rig.Vm.CanResolveFile);
        Assert.False(rig.Vm.AreTextActionsEnabled);
        Assert.True(rig.Vm.ContinueIsPrimary);
        Assert.Null(rig.Context.State.SelectedConflictPath);
        Assert.Contains("Clear", rig.Surface.Calls);
    }

    [Fact(Timeout = 60000)]
    public async Task A_binary_file_gets_a_card_and_no_text_actions()
    {
        var binary = new ConflictFile { FilePath = "logo.png", Status = ConflictStatus.BothModified, IsBinary = true };
        var rig = Create(files: [binary], configure: s => s.Copies["logo.png"] = new ConflictWorkingCopy { Path = "logo.png", IsBinary = true, DiskHash = "h" });
        await LoadedAsync(rig);

        Assert.True(rig.Vm.ShowFileCard);
        Assert.False(rig.Vm.IsEditable);
        Assert.Equal("logo.png", rig.Vm.CardPath);
        Assert.Equal("Status: BothModified", rig.Vm.CardStatus);
        Assert.Equal("Binary file conflict — textual merge unavailable.", rig.Vm.CardNote);
        Assert.False(rig.Vm.CardNoteIsWarning);
        Assert.True(rig.Vm.CardCanUseExternalTool);
        Assert.True(rig.Vm.CanResolveFile);
        Assert.False(rig.Vm.AreTextActionsEnabled);
        Assert.False(rig.Vm.CanNavigate);
        Assert.Contains("Leave", rig.Surface.Calls);
        Assert.DoesNotContain("Enter", rig.Surface.Calls);
    }

    [Fact(Timeout = 60000)]
    public async Task A_submodule_card_warns_and_offers_no_external_tool()
    {
        var sub = new ConflictFile { FilePath = "lib", Status = ConflictStatus.BothModified, IsBinary = false, IsSubmodule = true };
        var rig = Create(files: [sub], configure: s => s.Copies["lib"] = new ConflictWorkingCopy { Path = "lib", DiskHash = "h" });
        await LoadedAsync(rig);

        Assert.True(rig.Vm.ShowFileCard);
        Assert.Equal("Submodule conflict — take a side to resolve.", rig.Vm.CardNote);
        Assert.True(rig.Vm.CardNoteIsWarning);
        Assert.False(rig.Vm.CardCanUseExternalTool);
    }

    [Fact(Timeout = 60000)]
    public async Task Take_ours_asks_first_and_a_refusal_changes_nothing()
    {
        var rig = await LoadedAsync(Create(files: [FileA, FileB]));

        await rig.Vm.TakeOursCurrentAsync();

        var call = Assert.Single(rig.Dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Take ours", call.Title);
        Assert.Equal("Take ours", call.ConfirmLabel);
        Assert.Contains("a.txt", call.Effect);
        Assert.Empty(rig.Service.Sides);
        Assert.Equal("a.txt", rig.Vm.CurrentFile?.FilePath);
        Assert.False(rig.Vm.IsBusy);
    }

    [Fact(Timeout = 60000)]
    public async Task Take_theirs_confirmed_resolves_the_file_and_moves_to_the_next_one()
    {
        var rig = await LoadedAsync(Create(files: [FileA, FileB]));
        rig.Dialogs.EnqueueConfirmDestructive(true);

        await rig.Vm.TakeTheirsCurrentAsync();

        Assert.Equal([("a.txt", ConflictSide.Theirs)], rig.Service.Sides);
        Assert.Equal("b.txt", rig.Vm.CurrentFile?.FilePath);
        Assert.Equal("b.txt", rig.Context.State.SelectedConflictPath);
    }

    [Fact(Timeout = 60000)]
    public async Task Taking_a_side_of_the_last_file_shows_the_all_resolved_card()
    {
        var rig = await LoadedAsync(Create());
        rig.Dialogs.EnqueueConfirmDestructive(true);

        await rig.Vm.TakeOursCurrentAsync();

        Assert.True(rig.Vm.ShowAllResolvedCard);
        Assert.Null(rig.Vm.CurrentFile);
        Assert.False(rig.Vm.FilesRemain);
        Assert.True(rig.Vm.ContinueIsPrimary);
    }

    [Fact(Timeout = 60000)]
    public async Task Taking_the_side_of_a_both_deleted_file_says_delete()
    {
        var gone = new ConflictFile { FilePath = "a.txt", Status = ConflictStatus.BothDeletedRenamed, IsBinary = false };
        var rig = await LoadedAsync(Create(files: [gone]));
        rig.Dialogs.EnqueueConfirmDestructive(true);

        await rig.Vm.TakeOursCurrentAsync();

        var call = Assert.Single(rig.Dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Delete a.txt", call.Title);
        Assert.Equal("Delete", call.ConfirmLabel);
        Assert.Single(rig.Service.Sides);
    }

    [Fact(Timeout = 60000)]
    public async Task Resetting_a_file_asks_first_and_a_refusal_changes_nothing()
    {
        var rig = await LoadedAsync(Create());

        await rig.Vm.ResetFileAsync();

        var call = Assert.Single(rig.Dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Reset file to conflicted", call.Title);
        Assert.Equal("Reset", call.ConfirmLabel);
        Assert.Empty(rig.Service.Recreated);
        Assert.False(rig.Vm.IsBusy);
    }

    [Fact(Timeout = 60000)]
    public async Task Resetting_a_file_confirmed_recreates_it_and_reloads_it()
    {
        var rig = await LoadedAsync(Create());
        rig.Dialogs.EnqueueConfirmDestructive(true);
        int loadsBefore = rig.Service.LoadCalls;

        await rig.Vm.ResetFileAsync();

        Assert.Equal(["a.txt"], rig.Service.Recreated);
        Assert.Equal("a.txt", rig.Vm.CurrentFile?.FilePath);
        Assert.True(rig.Service.LoadCalls > loadsBefore);
    }

    [Fact(Timeout = 60000)]
    public async Task Skip_asks_first_and_a_refusal_skips_nothing()
    {
        var rig = await LoadedAsync(Create());

        await rig.Vm.SkipAsync();

        var call = Assert.Single(rig.Dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Skip commit", call.Title);
        Assert.Contains("abc1234 Add feature", call.Effect);
        Assert.Equal(0, rig.Service.SkipCalls);
    }

    [Fact(Timeout = 60000)]
    public async Task Skip_confirmed_skips_and_drops_unsaved_work_and_the_keep_open_flag()
    {
        var rig = await LoadedAsync(Create());
        rig.Context.State.SetKeepConflictResolverOpen(true);
        rig.Vm.RegisterUnsavedWork();
        Assert.True(rig.Vm.HasUnsavedWork);
        rig.Dialogs.EnqueueConfirmDestructive(true);

        await rig.Vm.SkipAsync();

        Assert.Equal(1, rig.Service.SkipCalls);
        Assert.False(rig.Vm.HasUnsavedWork);
        Assert.Empty(rig.Registry.For(rig.Context));
        Assert.False(rig.Context.State.KeepConflictResolverOpen);
        Assert.False(rig.Vm.IsBusy);
    }

    [Fact(Timeout = 60000)]
    public async Task Abort_asks_first_and_a_refusal_aborts_nothing()
    {
        var rig = await LoadedAsync(Create());
        rig.Vm.RegisterUnsavedWork();

        await rig.Vm.AbortAsync();

        var call = Assert.Single(rig.Dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Abort operation", call.Title);
        Assert.Equal("Abort", call.ConfirmLabel);
        Assert.Equal(0, rig.Service.AbortCalls);
        Assert.True(rig.Vm.HasUnsavedWork);
    }

    [Fact(Timeout = 60000)]
    public async Task Abort_confirmed_aborts_and_drops_unsaved_work()
    {
        var rig = await LoadedAsync(Create());
        rig.Context.State.SetKeepConflictResolverOpen(true);
        rig.Vm.RegisterUnsavedWork();
        rig.Dialogs.EnqueueConfirmDestructive(true);

        await rig.Vm.AbortAsync();

        Assert.Equal(1, rig.Service.AbortCalls);
        Assert.False(rig.Vm.HasUnsavedWork);
        Assert.False(rig.Context.State.KeepConflictResolverOpen);
        Assert.Contains("MarkClean", rig.Surface.Calls);
    }

    [Fact(Timeout = 60000)]
    public async Task Reload_from_disk_asks_first_and_a_refusal_keeps_the_edits()
    {
        var rig = await LoadedAsync(Create());
        rig.Vm.RegisterUnsavedWork();
        int loadsBefore = rig.Service.LoadCalls;

        await rig.Vm.ReloadFromDiskAsync();

        var call = Assert.Single(rig.Dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Reload from disk", call.Title);
        Assert.True(rig.Vm.HasUnsavedWork);
        Assert.Equal(loadsBefore, rig.Service.LoadCalls);
    }

    [Fact(Timeout = 60000)]
    public async Task Reload_from_disk_confirmed_drops_the_edits_and_loads_the_file_again()
    {
        var rig = await LoadedAsync(Create());
        rig.Vm.RegisterUnsavedWork();
        rig.Dialogs.EnqueueConfirmDestructive(true);
        int loadsBefore = rig.Service.LoadCalls;

        await rig.Vm.ReloadFromDiskAsync();

        Assert.False(rig.Vm.HasUnsavedWork);
        Assert.Equal(loadsBefore + 1, rig.Service.LoadCalls);
        Assert.False(rig.Vm.IsChangedOnDiskBannerVisible);
    }

    [Fact(Timeout = 60000)]
    public async Task Keep_mine_asks_first_and_adopts_the_new_disk_hash_when_confirmed()
    {
        var rig = await LoadedAsync(Create());
        rig.Service.Copies["a.txt"] = rig.Service.Copies["a.txt"] with { DiskHash = "hash-changed" };

        await rig.Vm.KeepMineAsync();
        Assert.Equal("hash-a.txt", rig.Vm.DiskHash);

        rig.Dialogs.EnqueueConfirmDestructive(true);
        await rig.Vm.KeepMineAsync();

        Assert.Equal(2, rig.Dialogs.ConfirmDestructiveCalls.Count);
        Assert.All(rig.Dialogs.ConfirmDestructiveCalls, c => Assert.Equal("Keep my version", c.Title));
        Assert.Equal("hash-changed", rig.Vm.DiskHash);
    }

    [Fact(Timeout = 60000)]
    public async Task Marking_resolved_warns_about_blocks_left_and_a_refusal_saves_nothing()
    {
        var rig = await LoadedAsync(Create());
        rig.Surface.Summary = new ConflictDocumentSummary(RemainingBlocks: 2, EmptiedConflicts: 1);

        await rig.Vm.SaveAndMarkResolvedAsync();

        var call = Assert.Single(rig.Dialogs.Calls, c => c.Method == nameof(IDialogService.ConfirmAsync));
        Assert.Equal("Unresolved conflicts", call.Title);
        Assert.Contains("2 conflict block(s) remain", call.Detail);
        Assert.Contains("1 conflict(s) will save as no lines", call.Detail);
        Assert.Empty(rig.Service.Saves);
    }

    [Fact(Timeout = 60000)]
    public async Task Marking_resolved_with_nothing_left_saves_staged_and_moves_on()
    {
        var rig = await LoadedAsync(Create(files: [FileA, FileB]));
        rig.Vm.RegisterUnsavedWork();

        await rig.Vm.SaveAndMarkResolvedAsync();

        Assert.Empty(rig.Dialogs.Calls);
        var save = Assert.Single(rig.Service.Saves);
        Assert.Equal(("a.txt", ConflictText, true), save);
        Assert.False(rig.Vm.HasUnsavedWork);
        Assert.Equal("b.txt", rig.Vm.CurrentFile?.FilePath);
    }

    [Fact(Timeout = 60000)]
    public async Task A_save_refused_because_the_file_changed_on_disk_shows_the_banner()
    {
        var rig = await LoadedAsync(Create());
        rig.Service.SaveResult = new ResolutionSaveResult { Saved = false, DiskHash = "other" };

        var saved = await rig.Vm.SaveCurrentFileAsync(stage: false);

        Assert.False(saved);
        Assert.True(rig.Vm.IsChangedOnDiskBannerVisible);
        Assert.Equal("hash-a.txt", rig.Vm.DiskHash);
    }

    [Fact(Timeout = 60000)]
    public async Task Edits_made_while_a_save_runs_stay_unsaved()
    {
        var rig = await LoadedAsync(Create());
        rig.Vm.RegisterUnsavedWork();
        rig.Service.OnBeforeSave = () => { rig.Surface.Revision++; return Task.CompletedTask; };

        var saved = await rig.Vm.SaveCurrentFileAsync(stage: false);

        Assert.True(saved);
        Assert.Equal("saved-hash", rig.Vm.DiskHash);
        Assert.True(rig.Vm.HasUnsavedWork);
        Assert.DoesNotContain("MarkClean", rig.Surface.Calls);
    }

    [Fact(Timeout = 60000)]
    public async Task A_save_with_no_edits_since_unregisters_the_unsaved_work()
    {
        var rig = await LoadedAsync(Create());
        rig.Vm.RegisterUnsavedWork();

        var saved = await rig.Vm.SaveCurrentFileAsync(stage: false);

        Assert.True(saved);
        Assert.False(rig.Vm.HasUnsavedWork);
        Assert.Contains("MarkClean", rig.Surface.Calls);
    }

    [Fact(Timeout = 60000)]
    public async Task A_save_does_nothing_when_the_file_is_not_editable()
    {
        var binary = new ConflictFile { FilePath = "logo.png", Status = ConflictStatus.BothModified, IsBinary = true };
        var rig = Create(files: [binary], configure: s => s.Copies["logo.png"] = new ConflictWorkingCopy { Path = "logo.png", IsBinary = true, DiskHash = "h" });
        await LoadedAsync(rig);

        var saved = await rig.Vm.SaveCurrentFileAsync(stage: false);

        Assert.False(saved);
        Assert.Empty(rig.Service.Saves);
    }

    [Fact(Timeout = 60000)]
    public async Task Continue_runs_the_operation_and_blocks_other_actions_meanwhile()
    {
        var rig = await LoadedAsync(Create(files: []));
        var gate = new TaskCompletionSource();
        rig.Service.OnContinue = () => gate.Task;
        var busy = new List<bool>();
        rig.Vm.BusyChanged += (_, _) => busy.Add(rig.Vm.IsBusy);

        var running = rig.Vm.ContinueAsync();

        Assert.True(rig.Vm.IsBusy);
        Assert.False(rig.Vm.AreActionsEnabled);
        Assert.False(rig.Vm.AreTextActionsEnabled);
        await rig.Vm.ContinueAsync();
        await rig.Vm.SkipAsync();
        await rig.Vm.AbortAsync();
        Assert.Equal(1, rig.Service.ContinueCalls);
        Assert.Empty(rig.Dialogs.ConfirmDestructiveCalls);

        gate.SetResult();
        await running;

        Assert.False(rig.Vm.IsBusy);
        Assert.True(rig.Vm.AreActionsEnabled);
        Assert.Equal([true, false], busy);
    }

    [Fact(Timeout = 60000)]
    public async Task Continue_that_ends_the_operation_clears_the_keep_open_flag()
    {
        var rig = await LoadedAsync(Create(files: []));
        rig.Context.State.SetKeepConflictResolverOpen(true);

        await rig.Vm.ContinueAsync();

        Assert.False(rig.Context.State.KeepConflictResolverOpen);
    }

    [Fact(Timeout = 60000)]
    public async Task Continue_with_unsaved_edits_and_a_cancelled_prompt_continues_nothing()
    {
        var rig = await LoadedAsync(Create());
        rig.Vm.RegisterUnsavedWork();
        rig.Dialogs.EnqueueChoice(-1);

        await rig.Vm.ContinueAsync();

        var call = Assert.Single(rig.Dialogs.Calls);
        Assert.Equal(nameof(IDialogService.ChooseAsync), call.Method);
        Assert.Equal(0, rig.Service.ContinueCalls);
        Assert.True(rig.Vm.HasUnsavedWork);
    }

    [Fact(Timeout = 60000)]
    public async Task Continue_with_unsaved_edits_can_discard_them_and_continue()
    {
        var rig = await LoadedAsync(Create());
        rig.Vm.RegisterUnsavedWork();
        rig.Dialogs.EnqueueChoice(1);

        await rig.Vm.ContinueAsync();

        Assert.False(rig.Vm.HasUnsavedWork);
        Assert.Equal(1, rig.Service.ContinueCalls);
        Assert.Empty(rig.Service.Saves);
    }

    [Fact(Timeout = 60000)]
    public async Task Continue_with_unsaved_edits_can_save_and_mark_resolved_first()
    {
        var rig = await LoadedAsync(Create());
        rig.Vm.RegisterUnsavedWork();
        rig.Dialogs.EnqueueChoice(0);

        await rig.Vm.ContinueAsync();

        var save = Assert.Single(rig.Service.Saves);
        Assert.True(save.Stage);
        Assert.Equal(1, rig.Service.ContinueCalls);
    }

    [Fact(Timeout = 60000)]
    public async Task The_external_tool_reports_a_missing_merge_tool_and_leaves_the_file_alone()
    {
        var rig = await LoadedAsync(Create());
        rig.Service.LaunchResult = false;

        await rig.Vm.LaunchExternalToolAsync();

        var notice = Assert.Single(rig.Notifications.Published);
        Assert.Equal(NotificationSeverity.Warning, notice.Severity);
        Assert.Contains("No external merge tool is configured", notice.Message);
        Assert.False(rig.Vm.IsBusy);
    }

    [Fact(Timeout = 60000)]
    public async Task The_external_tool_with_unsaved_edits_asks_and_a_cancel_launches_nothing()
    {
        var rig = await LoadedAsync(Create());
        rig.Vm.RegisterUnsavedWork();
        rig.Dialogs.EnqueueChoice(-1);

        await rig.Vm.LaunchExternalToolAsync();

        Assert.Empty(rig.Service.Launched);
        Assert.True(rig.Vm.HasUnsavedWork);
    }

    [Fact(Timeout = 60000)]
    public async Task Unsaved_work_registered_by_the_view_model_saves_and_discards_through_it()
    {
        var rig = await LoadedAsync(Create());
        rig.Vm.RegisterUnsavedWork();
        var work = Assert.Single(rig.Registry.For(rig.Context));
        Assert.Equal("the conflict resolution in a.txt", work.Description);

        Assert.True(await work.SaveAsync());
        Assert.Single(rig.Service.Saves);
        Assert.False(rig.Vm.HasUnsavedWork);

        rig.Vm.RegisterUnsavedWork();
        work = Assert.Single(rig.Registry.For(rig.Context));
        work.Discard();
        Assert.False(rig.Vm.HasUnsavedWork);
        Assert.Empty(rig.Registry.For(rig.Context));
    }

    [Fact(Timeout = 60000)]
    public async Task Registering_unsaved_work_twice_registers_it_once()
    {
        var rig = await LoadedAsync(Create());

        rig.Vm.RegisterUnsavedWork();
        rig.Vm.RegisterUnsavedWork();

        Assert.Single(rig.Registry.For(rig.Context));
    }

    [Fact(Timeout = 60000)]
    public async Task A_selected_path_is_switched_to_through_the_dispatcher_and_never_dropped()
    {
        var ui = new RecordingUiDispatcher();
        var rig = Create(files: [FileA, FileB], ui: ui);
        await LoadedAsync(rig);
        ui.DrainPosted();
        int postsBefore = ui.PostCount;

        rig.Context.State.SetSelectedConflictPath("b.txt");

        Assert.Equal(postsBefore + 1, ui.PostCount);
        Assert.Equal("a.txt", rig.Vm.CurrentFile?.FilePath);

        ui.DrainPosted();
        await WaitUntilAsync(() => rig.Vm.CurrentFile?.FilePath == "b.txt");
        Assert.Equal("b.txt", rig.Surface.FilePath);
    }

    [Fact(Timeout = 60000)]
    public async Task Selecting_another_file_with_unsaved_edits_prompts_and_a_cancel_restores_the_selection()
    {
        var rig = await LoadedAsync(Create(files: [FileA, FileB]));
        rig.Vm.RegisterUnsavedWork();
        rig.Dialogs.EnqueueChoice(-1);

        rig.Context.State.SetSelectedConflictPath("b.txt");
        await WaitUntilAsync(() => rig.Dialogs.Calls.Count == 1);
        await WaitUntilAsync(() => rig.Context.State.SelectedConflictPath == "a.txt");

        Assert.Equal("a.txt", rig.Vm.CurrentFile?.FilePath);
    }

    [Fact(Timeout = 60000)]
    public async Task A_same_size_edit_of_a_conflicted_file_reaches_the_resolver_through_an_unchanged_status()
    {
        var rig = Create(attach: false);
        var home = rig.Repo.Git("rev-parse", "--abbrev-ref", "HEAD").Trim();
        rig.Repo.Git("checkout", "-b", "feature");
        rig.Repo.WriteFile("a.txt", "two\n").Stage().Commit("feature");
        rig.Repo.Git("checkout", home);
        rig.Repo.WriteFile("a.txt", "six\n").Stage().Commit("main");
        try { rig.Repo.Git("merge", "feature"); } catch { /* expected to conflict */ }
        await rig.Context.State.RefreshStatusAsync();
        Assert.Single(rig.Context.State.Status.Conflicted);
        rig.Vm.Attach(rig.Surface);
        await LoadedAsync(rig);
        rig.Vm.RegisterUnsavedWork();
        Assert.False(rig.Vm.IsChangedOnDiskBannerVisible);

        rig.Service.DiskHash = "edited-outside-with-the-same-size";
        await rig.Context.State.RefreshStatusAsync();

        await WaitUntilAsync(() => rig.Vm.IsChangedOnDiskBannerVisible);
    }

    [Fact(Timeout = 60000)]
    public async Task A_stop_change_with_unsaved_edits_keeps_them_and_shows_the_banner()
    {
        var rig = await LoadedAsync(Create());
        rig.Vm.RegisterUnsavedWork();
        rig.Service.Labels = rig.Service.Labels with { StopId = "stop-2" };

        await rig.Context.State.RefreshStatusAsync();
        await WaitUntilAsync(() => rig.Vm.IsChangedOnDiskBannerVisible);

        Assert.True(rig.Vm.HasUnsavedWork);
        Assert.Equal("a.txt", rig.Vm.CurrentFile?.FilePath);
    }

    [Fact(Timeout = 60000)]
    public async Task A_stop_change_without_unsaved_edits_loads_the_file_again()
    {
        var rig = await LoadedAsync(Create());
        int loadsBefore = rig.Service.LoadCalls;
        rig.Service.Labels = rig.Service.Labels with { StopId = "stop-2" };

        await rig.Context.State.RefreshStatusAsync();
        await WaitUntilAsync(() => rig.Service.LoadCalls > loadsBefore);

        Assert.False(rig.Vm.IsChangedOnDiskBannerVisible);
        Assert.Equal("a.txt", rig.Vm.CurrentFile?.FilePath);
    }

    [Fact(Timeout = 60000)]
    public async Task The_operation_bar_follows_the_pending_operation()
    {
        var rig = Create();
        rig.Repo.WriteFile(".git/MERGE_HEAD", "1111111111111111111111111111111111111111\n");
        await rig.Context.State.RefreshStatusAsync();
        await LoadedAsync(rig);

        Assert.Equal(OperationState.Merging, rig.Context.State.PendingOperation);
        Assert.True(rig.Vm.IsOperationBarVisible);
        Assert.False(rig.Vm.IsSkipVisible);
    }

    [Fact(Timeout = 60000)]
    public async Task Without_an_operation_the_bar_is_hidden_and_unsaved_edits_show_the_ended_banner()
    {
        var rig = await LoadedAsync(Create());
        Assert.False(rig.Vm.IsOperationBarVisible);
        Assert.False(rig.Vm.IsExternalEndedBannerVisible);

        rig.Vm.RegisterUnsavedWork();
        await rig.Vm.LoadAsync();

        Assert.True(rig.Vm.IsExternalEndedBannerVisible);
    }

    [Fact(Timeout = 60000)]
    public async Task Conflict_stats_drive_the_counter_the_hint_and_the_navigation_buttons()
    {
        var rig = await LoadedAsync(Create());

        rig.Vm.SetConflictStats(total: 3, current: 2, unresolved: 1, malformedCount: 1, firstMalformedLine: 7);

        Assert.True(rig.Vm.HasConflictsCount);
        Assert.Equal("2/3", rig.Vm.ConflictsCountText);
        Assert.Equal("Conflict 2 of 3 in this file · 1 unresolved", rig.Vm.ConflictsCountToolTip);
        Assert.True(rig.Vm.HasDamagedMarkers);
        Assert.Equal("Conflict markers damaged near line 7", rig.Vm.DamagedMarkersText);
        Assert.True(rig.Vm.CanNavigate);

        rig.Vm.SetConflictStats(total: 0, current: 0, unresolved: 0, malformedCount: 0, firstMalformedLine: 0);

        Assert.False(rig.Vm.HasConflictsCount);
        Assert.False(rig.Vm.HasDamagedMarkers);
        Assert.False(rig.Vm.CanNavigate);
    }

    [Fact(Timeout = 60000)]
    public async Task The_navigation_commands_reach_the_editor_surface()
    {
        var rig = await LoadedAsync(Create());

        rig.Vm.NextConflictCommand.Execute(null);
        rig.Vm.PreviousConflictCommand.Execute(null);

        Assert.Equal(["Next", "Previous"], rig.Surface.Calls.Where(c => c is "Next" or "Previous").ToArray());
    }

    [Fact(Timeout = 60000)]
    public async Task Closing_marks_the_resolver_closed_without_saving_or_asking()
    {
        var rig = await LoadedAsync(Create());
        rig.Vm.RegisterUnsavedWork();

        rig.Vm.CloseCommand.Execute(null);

        Assert.True(rig.Context.State.IsConflictResolverClosed);
        Assert.Empty(rig.Service.Saves);
        Assert.Empty(rig.Dialogs.Calls);
        Assert.True(rig.Vm.HasUnsavedWork);
    }

    [Fact(Timeout = 60000)]
    public async Task A_press_in_the_resolver_clears_the_commit_selection_and_shows_the_repo_route_once()
    {
        var rig = await LoadedAsync(Create());
        var requested = new List<string>();
        rig.Nav.NavigationRequested += requested.Add;
        rig.Context.State.SetSelectedCommit(GitObjectId.Parse(new string('a', 40)));

        rig.Nav.ReportRoute("");
        rig.Vm.ShowChangesPanel();
        Assert.Null(rig.Context.State.SelectedCommitId);
        Assert.Equal(["repo"], requested);

        rig.Nav.ReportRoute("repo?tab=1");
        rig.Vm.ShowChangesPanel();
        Assert.Equal(["repo"], requested);
    }

    [Fact(Timeout = 60000)]
    public async Task Dispose_drops_unsaved_registration_and_stops_following_the_selection()
    {
        var ui = new RecordingUiDispatcher();
        var rig = Create(files: [FileA, FileB], ui: ui);
        await LoadedAsync(rig);
        ui.DrainPosted();
        rig.Vm.RegisterUnsavedWork();
        int postsBefore = ui.PostCount;

        rig.Vm.Dispose();
        rig.Context.State.SetSelectedConflictPath("b.txt");

        Assert.True(rig.Vm.IsDisposed);
        Assert.Empty(rig.Registry.For(rig.Context));
        Assert.Equal(postsBefore, ui.PostCount);
    }

    [Fact(Timeout = 60000)]
    public async Task A_post_that_runs_after_dispose_does_nothing()
    {
        var ui = new RecordingUiDispatcher();
        var rig = Create(files: [FileA, FileB], ui: ui);
        await LoadedAsync(rig);
        ui.DrainPosted();

        rig.Context.State.SetSelectedConflictPath("b.txt");
        rig.Vm.Dispose();
        ui.DrainPosted();
        await Task.Delay(50);

        Assert.Equal("a.txt", rig.Vm.CurrentFile?.FilePath);
        Assert.Empty(rig.Notifications.Published);
    }

    [Fact(Timeout = 60000)]
    public async Task A_failing_load_publishes_one_error_instead_of_throwing()
    {
        var rig = Create(configure: s => s.Labels = null!);

        await WaitUntilAsync(() => rig.Notifications.Published.Count > 0);

        Assert.Equal(NotificationSeverity.Error, rig.Notifications.Published[0].Severity);
        Assert.Null(rig.Vm.CurrentFile);
    }
}
