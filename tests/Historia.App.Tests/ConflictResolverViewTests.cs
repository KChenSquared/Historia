namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class ConflictResolverViewTests : IDisposable
{
    private readonly TestRepoFixture _fixture;
    private readonly ServiceProvider _sp;
    private readonly FakeConflictService _conflicts;
    private readonly FakeDialogService _dialogs;
    private readonly UnsavedWorkRegistry _unsavedRegistry;
    private readonly SyntaxThemeHost _syntaxHost;
    private readonly ConflictResolverHost _conflictHost;
    private readonly IWorkspaceService _workspace;
    private readonly RepositoryContext _context;
    private readonly NavigationBus _nav = new();

    private sealed class StubThemeService : IThemeService
    {
        public EffectiveTheme Effective { get; set; } = EffectiveTheme.Dark;
        public ThemeMode CurrentMode => ThemeMode.Dark;
        public bool HighContrastEnabled => Effective == EffectiveTheme.HighContrast;
        public string EffectiveCssThemeName => Effective == EffectiveTheme.Light ? "light" : "dark";
        public IReadOnlyDictionary<string, Rgb> Palette => HistoriaPalette.For(Effective);
        public IReadOnlyList<Rgb> LaneColors => [];

        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

        public Task ApplyModeAsync(ThemeMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public bool CustomThemeActive => false;
        public Task SetCustomThemeEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task SelectCustomThemeAsync(string themeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHighContrastAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;

        public void ChangeTheme(EffectiveTheme newTheme)
        {
            var old = Effective;
            Effective = newTheme;
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(old, newTheme));
        }
    }

    public ConflictResolverViewTests()
    {
        _fixture = new TestRepoFixture();
        _conflicts = new FakeConflictService();
        _dialogs = new FakeDialogService();
        _unsavedRegistry = new UnsavedWorkRegistry();
        var themeService = new StubThemeService();
        _syntaxHost = new SyntaxThemeHost(themeService);

        var sc = new ServiceCollection();
        sc.AddSingleton<IConflictService>(_conflicts);
        sc.AddSingleton<IDialogService>(_dialogs);
        sc.AddSingleton<IRepoActionBus, RepoActionBus>();
        sc.AddSingleton<INavigationBus>(_nav);
        sc.AddSingleton<IUnsavedWorkRegistry>(_unsavedRegistry);
        sc.AddSingleton<IThemeService>(themeService);
        sc.AddSingleton(_syntaxHost);
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton<ConflictResolverHost>();

        sc.AddSingleton<IKeyboardCommandRouter, KeyboardCommandRouter>();
        sc.AddSingleton<INotificationService, NotificationService>();
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton<ISystemThemeProbe, FakeThemeProbe>();
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
        sc.AddSingleton<IRepoNetworkActionRunner, FakeNetworkRunner>();
        sc.AddSingleton<ISettingsService>(new FakeSettingsService());

        _sp = sc.BuildServiceProvider();
        _conflictHost = _sp.GetRequiredService<ConflictResolverHost>();
        var ws = _sp.GetRequiredService<IWorkspaceService>();
        _workspace = ws;
        _context = ws.OpenAsync(_fixture.Path).GetAwaiter().GetResult();
        ws.Focus(_context);
    }

    public void Dispose()
    {
        try { _conflictHost.Dispose(); } catch { }
        try { _syntaxHost.Dispose(); } catch { }
        try { _context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _sp.Dispose(); } catch { }
        try { _fixture.Dispose(); } catch { }
    }

    private const string TwoConflictText =
        "header\n" +
        "<<<<<<< HEAD\n" +
        "ours 1\n" +
        "=======\n" +
        "theirs 1\n" +
        ">>>>>>> branch\n" +
        "middle\n" +
        "<<<<<<< HEAD\n" +
        "ours 2\n" +
        "=======\n" +
        "theirs 2\n" +
        ">>>>>>> branch\n" +
        "footer\n";

    [AvaloniaFact]
    public async Task Result_shows_working_file_text_with_markers()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = TwoConflictText,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("<<<<<<< HEAD", view.ResultEditorForTesting.Text);
        Assert.Contains("theirs 1", view.ResultEditorForTesting.Text);
        Assert.Contains("middle", view.ResultEditorForTesting.Text);
    }

    [AvaloniaFact]
    public async Task Use_theirs_on_region_0_manages_block_and_undo_restores()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = TwoConflictText,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var initialText = view.ResultEditorForTesting.Text;
        var r0 = view.CurrentParseForTesting!.Regions[0];
        ClickHunk(view, ConflictSide.Theirs, r0);

        // Ours stays visible, struck, so it can be re-checked; the saved text drops it.
        Assert.Contains("ours 1", view.ResultEditorForTesting.Text);
        Assert.DoesNotContain("ours 1", view.ComposedTextForTesting);
        Assert.Contains("theirs 1", view.ComposedTextForTesting);
        Assert.Single(view.SessionForTesting.Regions);

        view.ResultEditorForTesting.Document.UndoStack.Undo();
        Assert.Equal(initialText, view.ResultEditorForTesting.Text);
        Assert.Empty(view.SessionForTesting.Regions);
    }

    [AvaloniaFact]
    public async Task Ours_and_theirs_panes_have_same_line_count_as_result()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = TwoConflictText,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        int resultLines = view.ResultEditorForTesting.Document.LineCount;
        Assert.Equal(resultLines, view.OursEditorForTesting.Document.LineCount);
        Assert.Equal(resultLines, view.TheirsEditorForTesting.Document.LineCount);

        // Edit result
        ClickHunk(view, ConflictSide.Ours, view.CurrentParseForTesting!.Regions[0]);
        resultLines = view.ResultEditorForTesting.Document.LineCount;
        Assert.Equal(resultLines, view.OursEditorForTesting.Document.LineCount);
        Assert.Equal(resultLines, view.TheirsEditorForTesting.Document.LineCount);
    }

    [AvaloniaFact]
    public async Task Remaining_count_updates_as_regions_resolved()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = TwoConflictText,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, view.CurrentParseForTesting!.Regions.Count);

        ClickHunk(view, ConflictSide.Ours, view.CurrentParseForTesting.Regions[0]);
        Assert.Single(view.CurrentParseForTesting.Regions);

        ClickHunk(view, ConflictSide.Theirs, view.CurrentParseForTesting.Regions[0]);
        Assert.Empty(view.CurrentParseForTesting.Regions);
    }

    [AvaloniaFact]
    public async Task Typing_registers_unsaved_work_and_save_unregisters()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = TwoConflictText,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.HasUnsavedWork);

        view.ResultEditorForTesting.Document.Insert(0, "custom edit\n");
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.HasUnsavedWork);

        await view.SaveCurrentFileAsync(stage: false);
        Assert.False(view.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task Status_changed_while_dirty_keeps_buffer()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = TwoConflictText,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "dirty line\n");
        Assert.True(view.HasUnsavedWork);

        // Outside change occurs
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = "outside change",
            DiskHash = "hash2"
        };

        await _context.State.RefreshStatusAsync();
        await PumpUntilAsync(() => view.ChangedOnDiskBannerForTesting.IsVisible, "the changed-on-disk banner");

        Assert.Contains("dirty line", view.ResultEditorForTesting.Text);
    }

    [AvaloniaFact]
    public async Task Take_ours_with_dialog_returning_false_never_calls_service()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = TwoConflictText,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        _dialogs.ConfirmDestructiveResult = false;
        await view.TakeOursCurrentAsync();

        Assert.Empty(_conflicts.ResolvedWithSideCalls);
    }

    [AvaloniaFact]
    public async Task Skip_button_visible_for_rebasing_hidden_for_merging_op_bar_hidden_for_none()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);

        _fixture.SetOperation(OperationState.Rebasing);
        await _context.State.RefreshStatusAsync();
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.OpBarForTesting.IsVisible);
        Assert.True(view.SkipButtonForTesting.IsVisible);

        _fixture.SetOperation(OperationState.Merging);
        await _context.State.RefreshStatusAsync();
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.OpBarForTesting.IsVisible);
        Assert.False(view.SkipButtonForTesting.IsVisible);

        _fixture.ClearOperation();
        await _context.State.RefreshStatusAsync();
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.OpBarForTesting.IsVisible);
    }

    [AvaloniaFact]
    public async Task Last_file_resolved_with_pending_operation_keeps_resolver_open_for_continue()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        _fixture.SetOperation(OperationState.Merging);
        _fixture.CreateConflict();
        await RefreshUntilAsync(() => _context.State.IsConflictSessionActive, "the conflict session to start");

        var centerHost = new ContentControl();
        var graph = new Border();
        using var controller = new CenterPaneController(centerHost, graph, _conflictHost, _workspace, _sp);
        Dispatcher.UIThread.RunJobs();

        var resolver = _conflictHost.TryGet(_context);
        Assert.NotNull(resolver);
        Assert.True(_context.State.KeepConflictResolverOpen);
        Assert.True(_context.State.IsConflictSessionActive);
        Assert.Same(resolver, centerHost.Content);

        // Mark resolved
        _fixture.MarkResolved();
        await RefreshUntilAsync(() => _context.State.Status.Conflicted.Count == 0, "the resolved file to leave the conflict list");

        // Center pane should still retain the resolver because KeepConflictResolverOpen is true and op is Merging
        Assert.NotNull(_conflictHost.TryGet(_context));
        Assert.True(_context.State.IsConflictSessionActive);
        Assert.Same(resolver, centerHost.Content);
    }

    [AvaloniaFact]
    public async Task Continue_that_stops_on_new_conflicts_keeps_same_resolver_instance()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        _fixture.SetOperation(OperationState.Rebasing);
        _fixture.CreateConflict();
        await RefreshUntilAsync(() => _context.State.IsConflictSessionActive, "the conflict session to start");

        var centerHost = new ContentControl();
        var graph = new Border();
        using var controller = new CenterPaneController(centerHost, graph, _conflictHost, _workspace, _sp);
        Dispatcher.UIThread.RunJobs();

        var firstInstance = _conflictHost.TryGet(_context);
        Assert.NotNull(firstInstance);
        Assert.Same(firstInstance, centerHost.Content);

        // Fake Continue that stops on new conflicts
        _conflicts.OnContinueAsync = async () =>
        {
            _fixture.SetOperation(OperationState.Rebasing);
            await _context.State.RefreshStatusAsync();
        };

        await firstInstance.ContinueAsync();
        Dispatcher.UIThread.RunJobs();

        var currentInstance = _conflictHost.TryGet(_context);
        Assert.Same(firstInstance, currentInstance);
        Assert.Same(currentInstance, centerHost.Content);
    }

    [AvaloniaFact]
    public async Task Bisecting_with_0_conflicts_shows_op_banner_without_continue_button()
    {
        var repoView = new RepositoryView(_sp);
        var window = new Window { Content = repoView };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        _fixture.SetOperation(OperationState.Bisecting);
        await _context.State.RefreshStatusAsync();
        await PumpUntilAsync(() => repoView.OpBoxForTesting.IsVisible, "the operation banner");

        Assert.False(repoView.OpContinueButtonForTesting.IsEffectivelyVisible);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Dirty_resolver_shows_external_ended_banner_when_op_ends_outside_historia()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        _fixture.SetOperation(OperationState.Merging);
        await _context.State.RefreshStatusAsync();

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "edit\n");
        Assert.True(view.HasUnsavedWork);

        _fixture.ClearOperation();
        await RefreshUntilAsync(() => view.ExternalEndedBannerForTesting.IsVisible, "the operation-ended banner");
    }

    [AvaloniaFact]
    public async Task Region_action_right_after_typing_acts_on_fresh_offsets()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var r0 = view.CurrentParseForTesting!.Regions[0];
        // Insert text above region 0 without waiting for debounce
        view.ResultEditorForTesting.Document.Insert(0, "new top line\n");

        // Immediately trigger region action
        ClickHunk(view, ConflictSide.Theirs, r0);

        Assert.Contains("new top line", view.ComposedTextForTesting);
        Assert.Contains("theirs 1", view.ComposedTextForTesting);
        Assert.DoesNotContain("ours 1", view.ComposedTextForTesting);
        Assert.StartsWith("new top line\nheader\ntheirs 1\nmiddle\n", view.ComposedTextForTesting);
    }

    [AvaloniaFact]
    public async Task Damaged_markers_detected_when_separator_deleted()
    {
        const string malformed =
            "<<<<<<< HEAD\n" +
            "ours\n" +
            "theirs\n" +
            ">>>>>>> branch\n";

        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = malformed, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("damaged", view.DamagedMarkersHintForTesting.Text);
    }

    [AvaloniaFact]
    public async Task Status_changed_while_save_in_flight_skips_disk_check()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        _conflicts.OnBeforeSaveAsync = async () =>
        {
            await _context.State.RefreshStatusAsync();
        };

        await view.SaveCurrentFileAsync(stage: false);
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.ChangedOnDiskBannerForTesting.IsVisible);
    }

    [AvaloniaFact]
    public async Task Submodule_conflict_shows_file_card_only()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "sub", Status = ConflictStatus.BothModified, IsBinary = false, IsSubmodule = true }];
        _conflicts.WorkingCopies["sub"] = new ConflictWorkingCopy { Path = "sub", DiskHash = "hashSub" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.FileCardForTesting.IsVisible);
        Assert.False(view.AllResolvedCardForTesting.IsVisible);
        Assert.False(view.SplitGridForTesting.IsVisible);
    }

    [AvaloniaFact]
    public async Task Typing_character_in_common_line_updates_panes_incrementally()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        // Type character in line 0 ("header")
        view.ResultEditorForTesting.Document.Insert(6, "!");
        await PumpUntilAsync(() => view.OursEditorForTesting.Document.Text.Contains("header!"), "the edit to reach the panes");

        Assert.Contains("header!", view.TheirsEditorForTesting.Document.Text);
    }

    [AvaloniaFact]
    public async Task Save_after_file_changed_on_disk_shows_banner_and_keep_mine_updates_hash()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "edit\n");
        _conflicts.SaveResult = new ResolutionSaveResult { Saved = false, DiskHash = "hash-new-disk" };
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash-new-disk" };

        var saved = await view.SaveCurrentFileAsync(stage: false);
        Assert.False(saved);
        Assert.True(view.ChangedOnDiskBannerForTesting.IsVisible);

        // Keep mine
        await view.KeepMineAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.ChangedOnDiskBannerForTesting.IsVisible);
    }

    [AvaloniaFact]
    public async Task External_tool_with_dirty_buffer_prompts_and_cancels_if_rejected()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "dirty\n");
        _dialogs.ChooseResult = -1; // Cancel

        await view.LaunchExternalToolAsync();

        Assert.Empty(_conflicts.ExternalToolCalls);
    }

    [AvaloniaFact]
    public async Task Take_theirs_confirmed_on_dirty_buffer_reloads_and_unregisters_unsaved()
    {
        _conflicts.Conflicts = [
            new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false },
            new ConflictFile { FilePath = "g.txt", Status = ConflictStatus.BothModified, IsBinary = false }
        ];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.WorkingCopies["g.txt"] = new ConflictWorkingCopy { Path = "g.txt", Text = "g text", DiskHash = "hash-g" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "dirty\n");
        Assert.True(view.HasUnsavedWork);

        _dialogs.ConfirmDestructiveResult = true;

        await view.TakeTheirsCurrentAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.HasUnsavedWork);
        Assert.Equal("g.txt", view.CurrentFileForTesting?.FilePath);
        Assert.Equal("g text", view.ResultEditorForTesting.Text);
    }

    [AvaloniaFact]
    public async Task Dirty_buffer_survives_repository_view_reconstruction()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        _fixture.CreateConflict();
        await RefreshUntilAsync(() => _context.State.IsConflictSessionActive, "the conflict session to start");

        var centerHost = new ContentControl();
        var graph = new Border();
        var controller1 = new CenterPaneController(centerHost, graph, _conflictHost, _workspace, _sp);
        var repoView1 = new RepositoryView(_sp);
        var window = new Window { Content = repoView1 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var resolver = _conflictHost.TryGet(_context);
        Assert.NotNull(resolver);
        resolver.ResultEditorForTesting.Document.Insert(0, "preserved edit\n");
        Assert.True(resolver.HasUnsavedWork);

        // Dispose first RepositoryView (e.g. Settings navigation)
        repoView1.Dispose();
        controller1.Dispose();
        window.Content = null;
        Dispatcher.UIThread.RunJobs();

        // Resolver in host is not disposed
        var cachedResolver = _conflictHost.TryGet(_context);
        Assert.NotNull(cachedResolver);
        Assert.Same(resolver, cachedResolver);
        Assert.True(cachedResolver.HasUnsavedWork);

        // Mount second RepositoryView
        var controller2 = new CenterPaneController(centerHost, graph, _conflictHost, _workspace, _sp);
        var repoView2 = new RepositoryView(_sp);
        window.Content = repoView2;
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("preserved edit", cachedResolver.ResultEditorForTesting.Text);
        Assert.True(cachedResolver.HasUnsavedWork);

        controller2.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Closing_context_disposes_resolver_on_thread_pool_thread_safely()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var resolver = _conflictHost.GetOrCreate(_context)!;
        await resolver.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var ws = _sp.GetRequiredService<IWorkspaceService>();
        await Task.Run(async () =>
        {
            await ws.CloseAsync(_context);
        });

        await PumpUntilAsync(() => _conflictHost.TryGet(_context) is null, "the closed tab's resolver to be released");
    }

    [AvaloniaFact]
    public async Task Stop_id_change_with_zero_conflicts_clears_keep_open()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        _context.State.SetKeepConflictResolverOpen(true);
        Dispatcher.UIThread.RunJobs();

        // Terminal continue moves sequencer to next stop with 0 conflicts
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-2" };
        _conflicts.Conflicts = [];
        await _context.State.RefreshStatusAsync();
        await PumpUntilAsync(() => !_context.State.KeepConflictResolverOpen, "keep-open to clear");
    }

    [AvaloniaFact]
    public async Task Resolver_has_no_file_list()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(view.GetLogicalDescendants().OfType<ListBox>());
    }

    [AvaloniaFact]
    public async Task Setting_SelectedConflictPath_switches_file()
    {
        _conflicts.Conflicts = [
            new ConflictFile { FilePath = "fileA.txt", Status = ConflictStatus.BothModified, IsBinary = false },
            new ConflictFile { FilePath = "fileB.txt", Status = ConflictStatus.BothModified, IsBinary = false }
        ];
        _conflicts.WorkingCopies["fileA.txt"] = new ConflictWorkingCopy { Path = "fileA.txt", Text = "TEXT A", DiskHash = "hashA" };
        _conflicts.WorkingCopies["fileB.txt"] = new ConflictWorkingCopy { Path = "fileB.txt", Text = "TEXT B", DiskHash = "hashB" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("fileA.txt", view.CurrentFileForTesting?.FilePath);
        Assert.Equal("TEXT A", view.ResultEditorForTesting.Text);

        _context.State.SetSelectedConflictPath("fileB.txt");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("fileB.txt", view.CurrentFileForTesting?.FilePath);
        Assert.Equal("TEXT B", view.ResultEditorForTesting.Text);
    }

    [AvaloniaFact]
    public async Task Selecting_other_path_with_unsaved_edits_prompts_and_cancel_restores_selection()
    {
        _conflicts.Conflicts = [
            new ConflictFile { FilePath = "fileA.txt", Status = ConflictStatus.BothModified, IsBinary = false },
            new ConflictFile { FilePath = "fileB.txt", Status = ConflictStatus.BothModified, IsBinary = false }
        ];
        _conflicts.WorkingCopies["fileA.txt"] = new ConflictWorkingCopy { Path = "fileA.txt", Text = "TEXT A", DiskHash = "hashA" };
        _conflicts.WorkingCopies["fileB.txt"] = new ConflictWorkingCopy { Path = "fileB.txt", Text = "TEXT B", DiskHash = "hashB" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("fileA.txt", view.CurrentFileForTesting?.FilePath);

        view.ResultEditorForTesting.Text = "unsaved edits";
        Assert.True(view.HasUnsavedWork);

        _dialogs.ChooseResult = -1; // Cancel

        _context.State.SetSelectedConflictPath("fileB.txt");
        Dispatcher.UIThread.RunJobs();

        Assert.Single(_dialogs.ChooseCalls);
        Assert.Equal("fileA.txt", view.CurrentFileForTesting?.FilePath);
        Assert.Equal("fileA.txt", _context.State.SelectedConflictPath);
    }

    [AvaloniaFact]
    public async Task Resolving_a_file_advances_to_next_conflict_and_publishes_its_path()
    {
        _conflicts.Conflicts = [
            new ConflictFile { FilePath = "fileA.txt", Status = ConflictStatus.BothModified, IsBinary = false },
            new ConflictFile { FilePath = "fileB.txt", Status = ConflictStatus.BothModified, IsBinary = false }
        ];
        _conflicts.WorkingCopies["fileA.txt"] = new ConflictWorkingCopy { Path = "fileA.txt", Text = "TEXT A", DiskHash = "hashA" };
        _conflicts.WorkingCopies["fileB.txt"] = new ConflictWorkingCopy { Path = "fileB.txt", Text = "TEXT B", DiskHash = "hashB" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("fileA.txt", view.CurrentFileForTesting?.FilePath);
        Assert.Equal("fileA.txt", _context.State.SelectedConflictPath);

        await view.SaveCurrentFileAsync(stage: true);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("fileB.txt", view.CurrentFileForTesting?.FilePath);
        Assert.Equal("fileB.txt", _context.State.SelectedConflictPath);
    }

    [AvaloniaFact]
    public async Task Resolving_last_file_shows_all_resolved_card_not_editor()
    {
        _conflicts.Conflicts = [
            new ConflictFile { FilePath = "fileA.txt", Status = ConflictStatus.BothModified, IsBinary = false }
        ];
        _conflicts.WorkingCopies["fileA.txt"] = new ConflictWorkingCopy { Path = "fileA.txt", Text = "TEXT A", DiskHash = "hashA" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("fileA.txt", view.CurrentFileForTesting?.FilePath);

        await view.SaveCurrentFileAsync(stage: true);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(view.CurrentFileForTesting);
        Assert.Null(_context.State.SelectedConflictPath);
        Assert.True(view.AllResolvedCardForTesting.IsVisible);
        Assert.False(view.SplitGridForTesting.IsVisible);
        var card = view.AllResolvedCardForTesting;
        Assert.Contains(card.Children.OfType<TextBlock>(), tb => tb.Text?.Contains("All conflicts resolved") == true);
    }

    [AvaloniaFact]
    public async Task Rapid_switch_A_then_B_ends_on_B()
    {
        var tcsA = new TaskCompletionSource<bool>();
        _conflicts.Conflicts = [
            new ConflictFile { FilePath = "a.txt", Status = ConflictStatus.BothModified, IsBinary = false },
            new ConflictFile { FilePath = "b.txt", Status = ConflictStatus.BothModified, IsBinary = false }
        ];
        _conflicts.WorkingCopies["a.txt"] = new ConflictWorkingCopy { Path = "a.txt", Text = "TEXT A", DiskHash = "hashA" };
        _conflicts.WorkingCopies["b.txt"] = new ConflictWorkingCopy { Path = "b.txt", Text = "TEXT B", DiskHash = "hashB" };

        _conflicts.OnLoadWorkingCopyAsync = async (path, ct) =>
        {
            if (path == "a.txt")
            {
                await tcsA.Task;
            }
        };

        var view = new ConflictResolverView(_sp, _context);
        // Start switch to A
        _context.State.SetSelectedConflictPath("a.txt");
        Dispatcher.UIThread.RunJobs();

        // While A is waiting on tcsA, switch to B
        _context.State.SetSelectedConflictPath("b.txt");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("b.txt", view.CurrentFileForTesting?.FilePath);
        Assert.Equal("TEXT B", view.ResultEditorForTesting.Text);
        Assert.Equal("b.txt", _context.State.SelectedConflictPath);

        // Now release A
        tcsA.SetResult(true);
        Dispatcher.UIThread.RunJobs();

        // State remains B!
        Assert.Equal("b.txt", view.CurrentFileForTesting?.FilePath);
        Assert.Equal("TEXT B", view.ResultEditorForTesting.Text);
        Assert.Equal("b.txt", _context.State.SelectedConflictPath);
    }

    [AvaloniaFact]
    public void Showing_resolver_raises_SyntaxThemeHost_LiveCount_and_detaching_brings_it_down()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var resolver = _conflictHost.GetOrCreate(_context)!;
        Assert.Equal(0, _syntaxHost.LiveCount);

        var window = new Window { Content = resolver };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, _syntaxHost.LiveCount);

        window.Content = null;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, _syntaxHost.LiveCount);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Region_action_in_crlf_file_keeps_crlf()
    {
        string crlfConflict = "<<<<<<< HEAD\r\nours\r\n=======\r\ntheirs\r\n>>>>>>> branch\r\n";
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = crlfConflict,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var r0 = view.CurrentParseForTesting!.Regions[0];
        ClickHunk(view, ConflictSide.Ours, r0);

        Assert.Equal("ours\r\ntheirs\r\n", view.ResultEditorForTesting.Text);
        string result = view.ComposedTextForTesting;
        Assert.Equal("ours\r\n", result);
        Assert.DoesNotContain("\r\r\n", result);
        Assert.DoesNotContain("\n\r\n", result);
    }

    [AvaloniaFact]
    public async Task Unchecking_the_only_side_leaves_no_blank_line()
    {
        string text = "before\n<<<<<<< HEAD\n=======\ntheirs line\n>>>>>>> branch\nafter\n";
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = text,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var r0 = view.CurrentParseForTesting!.Regions[0];
        ClickHunk(view, ConflictSide.Theirs, r0);
        view.ClickHunkForTesting(ConflictSide.Theirs, view.SessionForTesting.Regions[0].StartLine);

        Assert.Equal("before\nafter\n", view.ComposedTextForTesting);
        Assert.Equal(1, view.SessionForTesting.EmptyRegionCount);
    }

    [AvaloniaFact]
    public async Task Marker_size_attribute_is_used_for_parsing()
    {
        string textWith9Markers = "<<<<<<<<< HEAD\nours line\n=========\ntheirs line\n>>>>>>>>> branch\n";
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = textWith9Markers,
            MarkerSize = 9,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(view.CurrentParseForTesting);
        Assert.Single(view.CurrentParseForTesting.Regions);
        Assert.Empty(view.CurrentParseForTesting.MalformedAt);
    }

    [AvaloniaFact]
    public async Task Pane_lines_are_not_rebuilt_per_render()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = TwoConflictText,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        // The counter is process-wide: let builds still in flight from the load, or from earlier
        // tests, land before taking the baseline the renders are measured against.
        int countAfterLoad = await SettledBuildCountAsync();
        Assert.True(countAfterLoad > 0);

        // Render twice
        view.OursEditorForTesting.TextArea.TextView.Redraw();
        view.TheirsEditorForTesting.TextArea.TextView.Redraw();
        Dispatcher.UIThread.RunJobs();

        view.OursEditorForTesting.TextArea.TextView.Redraw();
        view.TheirsEditorForTesting.TextArea.TextView.Redraw();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(countAfterLoad, ConflictPaneBuilder.BuildCallCount);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Continue_with_all_resolved_runs_to_completion_without_cancellation()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        CancellationToken capturedToken = default;
        bool wasCancelledDuringRun = false;
        _conflicts.OnContinueWithTokenAsync = async (ct) =>
        {
            capturedToken = ct;
            await Task.Delay(50);
            wasCancelledDuringRun = ct.IsCancellationRequested;
        };

        var continueTask = view.ContinueAsync();
        view.Dispose(); // Disposing the view while continue is in flight must NOT cancel the token
        await continueTask;

        Assert.False(wasCancelledDuringRun);
        Assert.False(capturedToken.IsCancellationRequested);
    }

    [AvaloniaFact]
    public async Task Next_stop_updates_labels()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-1", OursTitle = "Ours1", TheirsTitle = "Theirs1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        // Advance stop
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-2", OursTitle = "Ours2", TheirsTitle = "Theirs2" };
        await _context.State.RefreshStatusAsync();
        await PumpUntilAsync(() => view.OursCaptionForTesting.Text == "Ours · Ours2", "the next stop's labels");

        Assert.Equal("Theirs · Theirs2", view.TheirsCaptionForTesting.Text);
    }

    [AvaloniaFact]
    public async Task Same_path_reconflicting_at_next_stop_is_editable()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        // Mark resolved in stop 1
        await view.SaveCurrentFileAsync(stage: true);
        Dispatcher.UIThread.RunJobs();

        // Advance to stop 2, where f.txt is conflicted again
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-2" };
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash2" };
        await _context.State.RefreshStatusAsync();
        await PumpUntilAsync(() => !view.ResultEditorForTesting.IsReadOnly, "the reconflicted file to become editable");
    }

    [AvaloniaFact]
    public async Task Skip_requires_destructive_confirm()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.SideLabels = _conflicts.SideLabels with { TheirsDetail = "abc1234 Add feature" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        // Cancel confirmation
        _dialogs.ConfirmDestructiveResult = false;
        await view.SkipAsync();
        Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Skip commit", _dialogs.ConfirmDestructiveCalls[0].Title);
        Assert.Contains("abc1234 Add feature", _dialogs.ConfirmDestructiveCalls[0].Effect);
        Assert.Equal("Skip", _dialogs.ConfirmDestructiveCalls[0].ConfirmLabel);
        Assert.Equal(0, _conflicts.SkipCalls);

        // Accept confirmation
        _dialogs.ConfirmDestructiveResult = true;
        await view.SkipAsync();
        Assert.Equal(1, _conflicts.SkipCalls);
    }

    [AvaloniaFact]
    public async Task Take_deleted_side_names_delete_effect()
    {
        // Test DeletedByUs + Ours -> Delete effect
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.DeletedByUs, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.SideLabels = _conflicts.SideLabels with { OursTitle = "HEAD" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        _dialogs.ConfirmDestructiveCalls.Clear();
        _dialogs.ConfirmDestructiveResult = false;

        await view.TakeOursCurrentAsync();
        Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Delete f.txt", _dialogs.ConfirmDestructiveCalls[0].Title);
        Assert.Contains("Delete f.txt — HEAD deleted it", _dialogs.ConfirmDestructiveCalls[0].Effect);
        Assert.Equal("Delete", _dialogs.ConfirmDestructiveCalls[0].ConfirmLabel);

        // Test DeletedByThem + Theirs -> Delete effect
        _conflicts.Conflicts = [new ConflictFile { FilePath = "g.txt", Status = ConflictStatus.DeletedByThem, IsBinary = false }];
        _conflicts.WorkingCopies["g.txt"] = new ConflictWorkingCopy { Path = "g.txt", Text = TwoConflictText, DiskHash = "hash2" };
        _conflicts.SideLabels = _conflicts.SideLabels with { TheirsTitle = "feature" };

        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        _dialogs.ConfirmDestructiveCalls.Clear();
        await view.TakeTheirsCurrentAsync();
        Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Delete g.txt", _dialogs.ConfirmDestructiveCalls[0].Title);
        Assert.Contains("Delete g.txt — feature deleted it", _dialogs.ConfirmDestructiveCalls[0].Effect);
        Assert.Equal("Delete", _dialogs.ConfirmDestructiveCalls[0].ConfirmLabel);
    }

    [AvaloniaFact]
    public async Task Keep_mine_requires_destructive_confirm()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        _dialogs.ConfirmDestructiveCalls.Clear();
        _dialogs.ConfirmDestructiveResult = true;

        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash-from-disk" };
        await view.KeepMineAsync();

        Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Keep my version", _dialogs.ConfirmDestructiveCalls[0].Title);
        Assert.Contains("Your next save will overwrite f.txt on disk", _dialogs.ConfirmDestructiveCalls[0].Effect);
        Assert.Equal("Keep mine", _dialogs.ConfirmDestructiveCalls[0].ConfirmLabel);
    }

    [AvaloniaFact]
    public async Task StopChange_with_unsaved_edits_keeps_text_and_shows_banner()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "unsaved edit\n");
        Assert.True(view.HasUnsavedWork);

        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-2" };
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = "different disk text", DiskHash = "hash2" };

        await _context.State.RefreshStatusAsync();
        await PumpUntilAsync(() => view.ChangedOnDiskBannerForTesting.IsVisible, "the changed-on-disk banner");

        Assert.Contains("unsaved edit", view.ResultEditorForTesting.Text);
    }

    [AvaloniaFact]
    public async Task StopChange_with_unsaved_edits_to_other_file_keeps_text()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "unsaved edit in f\n");
        Assert.True(view.HasUnsavedWork);

        _conflicts.Conflicts = [new ConflictFile { FilePath = "g.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["g.txt"] = new ConflictWorkingCopy { Path = "g.txt", Text = "g content", DiskHash = "hash-g" };
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-2" };

        await _context.State.RefreshStatusAsync();
        await PumpUntilAsync(() => view.ChangedOnDiskBannerForTesting.IsVisible, "the changed-on-disk banner");

        Assert.Contains("unsaved edit in f", view.ResultEditorForTesting.Text);
        Assert.Equal("f.txt", view.CurrentFileForTesting?.FilePath);
    }

    [AvaloniaFact]
    public async Task StopChange_without_unsaved_edits_reloads()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.HasUnsavedWork);

        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-2" };
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = "reloaded stop 2 text", DiskHash = "hash2" };

        await _context.State.RefreshStatusAsync();
        await PumpUntilAsync(() => view.ResultEditorForTesting.Text == "reloaded stop 2 text", "the next stop's text");

        Assert.False(view.ChangedOnDiskBannerForTesting.IsVisible);
    }

    [AvaloniaFact]
    public async Task Continue_with_unsaved_edits_prompts_choice_0_saves_and_continues()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "dirty\n");
        Assert.True(view.HasUnsavedWork);

        _dialogs.ChooseResult = 0; // Save & mark resolved
        _dialogs.ConfirmResult = true; // Confirm remaining conflicts if prompted
        _conflicts.SaveResolutionCalls.Clear();
        _conflicts.ContinueCalls = 0;

        await view.ContinueAsync();

        Assert.Single(_dialogs.ChooseCalls);
        Assert.Single(_conflicts.SaveResolutionCalls);
        Assert.True(_conflicts.SaveResolutionCalls[0].Stage);
        Assert.Equal(1, _conflicts.ContinueCalls);
    }

    [AvaloniaFact]
    public async Task Continue_with_unsaved_edits_prompts_choice_1_discards_reloads_disk_text_and_continues()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = "clean disk text", DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "dirty\n");
        Assert.True(view.HasUnsavedWork);

        _dialogs.ChooseResult = 1; // Discard changes
        _conflicts.SaveResolutionCalls.Clear();
        _conflicts.ContinueCalls = 0;

        await view.ContinueAsync();

        Assert.Single(_dialogs.ChooseCalls);
        Assert.Empty(_conflicts.SaveResolutionCalls);
        Assert.Equal("clean disk text", view.ResultEditorForTesting.Text);
        Assert.False(view.HasUnsavedWork);
        Assert.Equal(1, _conflicts.ContinueCalls);
    }

    [AvaloniaFact]
    public async Task Continue_with_unsaved_edits_prompts_choice_negative_1_cancels()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = "disk text", DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "dirty\n");
        Assert.True(view.HasUnsavedWork);

        _dialogs.ChooseResult = -1; // Cancel
        _conflicts.ContinueCalls = 0;

        await view.ContinueAsync();

        Assert.Single(_dialogs.ChooseCalls);
        Assert.Equal(0, _conflicts.ContinueCalls);
        Assert.True(view.HasUnsavedWork);
        Assert.Contains("dirty", view.ResultEditorForTesting.Text);
    }

    [AvaloniaFact]
    public async Task Continue_discard_then_failed_continue_leaves_disk_text_in_editor()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = "clean disk text", DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "dirty\n");
        Assert.True(view.HasUnsavedWork);

        _dialogs.ChooseResult = 1; // Discard
        _conflicts.OnContinueAsync = () => throw new Exception("Continue failed on git error");

        await view.ContinueAsync();

        Assert.Equal("clean disk text", view.ResultEditorForTesting.Text);
        Assert.False(view.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task StopChange_refresh_does_not_prompt()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "dirty edits\n");
        Assert.True(view.HasUnsavedWork);

        _dialogs.ChooseCalls.Clear();

        // Stop change where current file drops out of the list
        _conflicts.Conflicts = [new ConflictFile { FilePath = "other.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["other.txt"] = new ConflictWorkingCopy { Path = "other.txt", Text = "other text", DiskHash = "hash-other" };
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-2" };

        await _context.State.RefreshStatusAsync();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(_dialogs.ChooseCalls);
        Assert.Contains("dirty edits", view.ResultEditorForTesting.Text);
        Assert.True(view.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task Skip_clears_unsaved_registration_on_success()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "dirty\n");
        Assert.True(view.HasUnsavedWork);

        _dialogs.ConfirmDestructiveResult = true;
        await view.SkipAsync();

        Assert.False(view.HasUnsavedWork);
        Assert.False(view.ChangedOnDiskBannerForTesting.IsVisible);
        Assert.Equal(1, _conflicts.SkipCalls);
    }

    [AvaloniaFact]
    public async Task Abort_clears_unsaved_registration_on_success()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "dirty\n");
        Assert.True(view.HasUnsavedWork);

        _dialogs.ConfirmDestructiveCalls.Clear();
        _dialogs.ConfirmDestructiveResult = true;
        _conflicts.AbortCalls = 0;

        await view.AbortAsync();

        Assert.False(view.HasUnsavedWork);
        Assert.False(view.ChangedOnDiskBannerForTesting.IsVisible);
        Assert.Equal(1, _conflicts.AbortCalls);
        Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.Contains("Conflict resolutions and unsaved edits made so far will be discarded.", _dialogs.ConfirmDestructiveCalls[0].Effect);
    }

    [AvaloniaFact]
    public async Task Busy_disables_action_buttons_and_ignores_reentry()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = TwoConflictText,
            DiskHash = "hash1",
            Format = new TextFileFormat(System.Text.Encoding.UTF8, false),
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var tcs = new TaskCompletionSource<bool>();
        _conflicts.OnContinueAsync = () => tcs.Task;
        _conflicts.ContinueCalls = 0;

        var continueTask = view.ContinueAsync();

        Assert.True(view.IsBusy);
        Assert.NotEmpty(view.ActionButtonsForTesting);
        Assert.All(view.ActionButtonsForTesting, btn => Assert.False(btn.IsEnabled));
        Assert.NotEmpty(view.TextActionButtonsForTesting);
        Assert.All(view.TextActionButtonsForTesting, btn => Assert.False(btn.IsEnabled));

        // Re-entry calls should be ignored while busy
        await view.ContinueAsync();
        await view.SkipAsync();
        await view.AbortAsync();
        var saveResult = await view.SaveCurrentFileAsync(false);
        Assert.False(saveResult);

        Assert.Equal(1, _conflicts.ContinueCalls);
        Assert.Empty(_conflicts.SaveResolutionCalls);

        tcs.SetResult(true);
        await continueTask;

        Assert.False(view.IsBusy);
        Assert.All(view.ActionButtonsForTesting, btn => Assert.True(btn.IsEnabled));
        Assert.All(view.TextActionButtonsForTesting, btn => Assert.True(btn.IsEnabled));
    }

    [AvaloniaFact]
    public async Task External_change_check_uses_GetDiskHash_not_LoadWorkingCopy()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        _conflicts.GetDiskHashCalls = 0;
        _conflicts.LoadWorkingCopyCalls = 0;

        await _context.State.RefreshStatusAsync();
        await PumpUntilAsync(() => _conflicts.GetDiskHashCalls >= 1, "the disk-hash check");

        Assert.Equal(0, _conflicts.LoadWorkingCopyCalls);
    }

    [AvaloniaFact]
    public async Task Overlapping_status_changes_run_refresh_at_most_twice()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var tcs = new TaskCompletionSource<IReadOnlyList<ConflictFile>>();
        int callCount = 0;
        _conflicts.OnGetConflictsAsync = () =>
        {
            callCount++;
            if (callCount == 1)
            {
                return tcs.Task;
            }
            return Task.FromResult(_conflicts.Conflicts);
        };

        // Status changes reach the view through a coalescing timer; counting the raised events
        // makes each step wait for delivery instead of for a fixed delay. The state service raises
        // only when the status differs from the last raise, so each step first changes the
        // working tree: an untracked file adds an entry the previous status did not have.
        var raised = 0;
        EventHandler<StatusChangedEventArgs> countRaised = (_, _) => raised++;
        _context.State.StatusChanged += countRaised;
        try
        {
            // Fire status change 1 and wait for it to be delivered
            await File.WriteAllTextAsync(System.IO.Path.Combine(_fixture.Path, "change1.txt"), "1");
            await _context.State.RefreshStatusAsync();
            await PumpUntilAsync(() => raised >= 1 && callCount >= 1, "the first conflict refresh");

            // Refresh 1 is now in-flight, awaiting tcs.Task in GetConflictsAsync
            Assert.Equal(1, callCount);

            // Fire status changes 2 and 3 while 1 is in-flight, each delivered before the next
            await File.WriteAllTextAsync(System.IO.Path.Combine(_fixture.Path, "change2.txt"), "2");
            await _context.State.RefreshStatusAsync();
            await PumpUntilAsync(() => raised >= 2, "the second status change");

            await File.WriteAllTextAsync(System.IO.Path.Combine(_fixture.Path, "change3.txt"), "3");
            await _context.State.RefreshStatusAsync();
            await PumpUntilAsync(() => raised >= 3, "the third status change");
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            _context.State.StatusChanged -= countRaised;
        }

        // Still only 1 call to GetConflictsAsync because refresh is in flight and extra ones folded into pending
        Assert.Equal(1, callCount);

        // Complete the in-flight refresh 1
        tcs.SetResult(_conflicts.Conflicts);
        await PumpUntilAsync(() => callCount >= 2, "the pending conflict refresh");

        // Now the second (pending) refresh should have run and finished
        Assert.Equal(2, callCount);
    }

    [AvaloniaFact]
    public async Task SwitchToFile_builds_panes_once()
    {
        _conflicts.Conflicts = [
            new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false },
            new ConflictFile { FilePath = "g.txt", Status = ConflictStatus.BothModified, IsBinary = false }
        ];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.WorkingCopies["g.txt"] = new ConflictWorkingCopy { Path = "g.txt", Text = TwoConflictText, DiskHash = "hash2" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        await Task.Delay(250);
        Dispatcher.UIThread.RunJobs();

        int initialCount = ConflictPaneBuilder.BuildCallCount;

        _context.State.SetSelectedConflictPath("g.txt");
        Dispatcher.UIThread.RunJobs();

        // Wait past debounce (150ms) for both panes to build
        await PumpUntilAsync(() => ConflictPaneBuilder.BuildCallCount - initialCount >= 2, "the panes to build");

        int finalCount = ConflictPaneBuilder.BuildCallCount;
        Assert.Equal(2, finalCount - initialCount);
    }

    [AvaloniaFact]
    public async Task Region_analysis_arrives_after_switch_and_is_dropped_if_document_changed()
    {
        _conflicts.Conflicts = [
            new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false },
            new ConflictFile { FilePath = "g.txt", Status = ConflictStatus.BothModified, IsBinary = false }
        ];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.WorkingCopies["g.txt"] = new ConflictWorkingCopy { Path = "g.txt", Text = TwoConflictText, DiskHash = "hash2" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        // Immediately after load, carried analyses is empty
        Assert.NotNull(view.SnapshotForTesting);
        Assert.Empty(view.SnapshotForTesting.Analyses);

        // Part 1: Region analysis arrives after switch
        await PumpUntilAsync(() => (view.SnapshotForTesting?.Analyses.Count ?? 0) > 0, "the region analysis");
        Assert.Equal(2, view.SnapshotForTesting!.Analyses.Count);

        // Part 2: Switch to g.txt and immediately change document so version moves
        // The switch runs as a dispatcher job and starts region analysis on the thread pool, whose
        // result is posted back to the same queue. Queue the edit right behind the switch so it is
        // always ahead of that post, instead of racing the analysis after RunJobs returns.
        var versionBeforeSwitch = view.ResultEditorForTesting.Document.Version;
        int analysesAtSwitch = -1;
        var staleInstalls = 0;
        view.SnapshotInstalledForTesting += snapshot =>
        {
            if (view.ResultEditorForTesting.Document.Version.CompareAge(snapshot.Version) != 0)
                staleInstalls++;
        };
        _context.State.SetSelectedConflictPath("g.txt");
        Dispatcher.UIThread.Post(() =>
        {
            // Right after the switch the snapshot carries no analyses
            analysesAtSwitch = view.SnapshotForTesting!.Analyses.Count;

            // Move document version before background analysis posts back
            view.ResultEditorForTesting.Document.Insert(0, "immediate change\n");
        });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, analysesAtSwitch);

        // The debounced analysis of the edited text arrives (150ms)...
        await PumpUntilAsync(() => view.SnapshotForTesting!.Analyses.Count == 2
            && view.ResultEditorForTesting.Document.Version.CompareAge(view.SnapshotForTesting.Version) == 0,
            "the analysis of the edited text");
        Assert.True(view.ResultEditorForTesting.Document.Version.CompareAge(versionBeforeSwitch) > 0);

        // ...and the analysis the switch started for the pre-edit text was dropped, never installed.
        Assert.Equal(0, staleInstalls);
    }

    [AvaloniaFact]
    public async Task Region_action_keeps_highlights_of_untouched_regions()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        await PumpUntilAsync(() => (view.SnapshotForTesting?.Analyses.Count ?? 0) >= 2, "the region analyses");

        Assert.NotNull(view.SnapshotForTesting);
        Assert.Equal(2, view.SnapshotForTesting.Analyses.Count);

        var parse = view.CurrentParseForTesting!;
        Assert.Equal(2, parse.Regions.Count);

        // Check ours on Region 1
        ClickHunk(view, ConflictSide.Ours, parse.Regions[0]);

        // Immediately after the hunk click (before any background parse finishes): the untouched
        // region keeps its highlights, and the managed one keeps its own on the rows' new lines.
        var snapshot = view.SnapshotForTesting;
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot.Analyses.Count);
        var untouched = Assert.Single(snapshot.Analyses, a => a.Region.TheirsLines[0] == "theirs 2");
        Assert.NotEmpty(untouched.OursHighlights);
        var managed = Assert.Single(snapshot.Analyses, a => a.Region.TheirsLines[0] == "theirs 1");
        var row = view.SessionForTesting.Regions[0];
        Assert.Equal(row.RowLine(ConflictSide.Ours, 0), managed.OursHighlights[0].DocumentLineIndex);
        Assert.Equal(row.RowLine(ConflictSide.Theirs, 0), managed.TheirsHighlights[0].DocumentLineIndex);
    }

    [AvaloniaFact]
    public async Task File_card_disables_text_actions()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "binary.png", Status = ConflictStatus.BothModified, IsBinary = true }];
        _conflicts.WorkingCopies["binary.png"] = new ConflictWorkingCopy
        {
            Path = "binary.png",
            IsBinary = true,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.IsEditableForTesting);
        Assert.NotEmpty(view.TextActionButtonsForTesting);
        Assert.All(view.TextActionButtonsForTesting, b => Assert.False(b.IsEnabled));
    }

    [AvaloniaFact]
    public async Task File_card_keeps_take_recreate_and_external_toolbar_actions_enabled()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "binary.png", Status = ConflictStatus.BothModified, IsBinary = true }];
        _conflicts.WorkingCopies["binary.png"] = new ConflictWorkingCopy
        {
            Path = "binary.png",
            IsBinary = true,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.IsEditableForTesting);
        Assert.NotEmpty(view.TextActionButtonsForTesting);
        Assert.All(view.TextActionButtonsForTesting, b => Assert.False(b.IsEnabled));

        Assert.True(view.ResolveFileButtonForTesting.IsEnabled);
        Assert.Equal(
            new[] { "Take ours (whole file)", "Take theirs (whole file)", "Reset file to conflicted", "Open in external tool" },
            view.ResolveFileMenuItemsForTesting.Select(m => m.Header as string));
        Assert.All(view.ResolveFileMenuItemsForTesting, m => Assert.True(m.IsEnabled));
    }

    [AvaloniaFact]
    public async Task Toolbar_recreate_on_a_card_asks_for_confirmation_and_recreates()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "binary.png", Status = ConflictStatus.BothModified, IsBinary = true }];
        _conflicts.WorkingCopies["binary.png"] = new ConflictWorkingCopy
        {
            Path = "binary.png",
            IsBinary = true,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        _dialogs.ConfirmDestructiveResult = true;
        Assert.True(view.ResolveFileButtonForTesting.IsEnabled);
        ClickMenuItem(view, "Reset file to conflicted");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_conflicts.RecreatedCalls.Count == 0 && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Reset file to conflicted", _dialogs.ConfirmDestructiveCalls[0].Title);
        Assert.Single(_conflicts.RecreatedCalls);
        Assert.Equal("binary.png", _conflicts.RecreatedCalls[0]);
    }

    [AvaloniaFact]
    public async Task Save_on_file_card_never_calls_SaveResolutionAsync()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "binary.png", Status = ConflictStatus.BothModified, IsBinary = true }];
        _conflicts.WorkingCopies["binary.png"] = new ConflictWorkingCopy
        {
            Path = "binary.png",
            IsBinary = true,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var saved = await view.SaveCurrentFileAsync(stage: false);
        Assert.False(saved);
        Assert.Empty(_conflicts.SaveResolutionCalls);
    }

    [AvaloniaFact]
    public async Task All_resolved_card_disables_text_actions()
    {
        _conflicts.Conflicts = [];

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.IsEditableForTesting);
        Assert.NotEmpty(view.TextActionButtonsForTesting);
        Assert.All(view.TextActionButtonsForTesting, b => Assert.False(b.IsEnabled));
    }

    [AvaloniaFact]
    public async Task Switching_to_file_card_clears_parse_and_count()
    {
        var textFile = new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false };
        var binFile = new ConflictFile { FilePath = "bin.png", Status = ConflictStatus.BothModified, IsBinary = true };
        _conflicts.Conflicts = [textFile, binFile];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = TwoConflictText,
            DiskHash = "hash1"
        };
        _conflicts.WorkingCopies["bin.png"] = new ConflictWorkingCopy
        {
            Path = "bin.png",
            IsBinary = true,
            DiskHash = "hash2"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.IsEditableForTesting);
        Assert.NotNull(view.CurrentParseForTesting);
        Assert.True(view.ConflictsCountTextForTesting.IsVisible);

        _context.State.SetSelectedConflictPath("bin.png");
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.IsEditableForTesting);
        Assert.Null(view.CurrentParseForTesting);
        Assert.False(view.ConflictsCountTextForTesting.IsVisible);
    }

    [AvaloniaFact]
    public async Task Stage_error_adopts_new_hash_notifies_and_stays_on_file()
    {
        var f1 = new ConflictFile { FilePath = "f1.txt", Status = ConflictStatus.BothModified, IsBinary = false };
        var f2 = new ConflictFile { FilePath = "f2.txt", Status = ConflictStatus.BothModified, IsBinary = false };
        _conflicts.Conflicts = [f1, f2];
        _conflicts.WorkingCopies["f1.txt"] = new ConflictWorkingCopy
        {
            Path = "f1.txt",
            Text = TwoConflictText,
            DiskHash = "hash1",
            Format = new TextFileFormat(System.Text.Encoding.UTF8, false)
        };
        _conflicts.WorkingCopies["f2.txt"] = new ConflictWorkingCopy
        {
            Path = "f2.txt",
            Text = TwoConflictText,
            DiskHash = "hash2",
            Format = new TextFileFormat(System.Text.Encoding.UTF8, false)
        };
        _conflicts.SaveResult = new ResolutionSaveResult
        {
            Saved = true,
            DiskHash = "h2",
            StageError = new HistoriaGitException(GitErrorKind.Unknown, "git add failed")
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var saved = await view.SaveCurrentFileAsync(stage: true);
        Dispatcher.UIThread.RunJobs();

        Assert.False(saved);
        Assert.Equal("h2", view.DiskHashForTesting);
        var notifications = _sp.GetRequiredService<INotificationService>().Current;
        Assert.Contains(notifications, n => n.Severity == NotificationSeverity.Error && n.Message.Contains("git add failed"));
        Assert.Equal("f1.txt", view.CurrentFileForTesting?.FilePath);
    }

    [AvaloniaFact]
    public async Task Stage_error_message_says_the_file_was_saved_and_carries_details()
    {
        var f1 = new ConflictFile { FilePath = "f1.txt", Status = ConflictStatus.BothModified, IsBinary = false };
        _conflicts.Conflicts = [f1];
        _conflicts.WorkingCopies["f1.txt"] = new ConflictWorkingCopy
        {
            Path = "f1.txt",
            Text = TwoConflictText,
            DiskHash = "hash1",
            Format = new TextFileFormat(System.Text.Encoding.UTF8, false)
        };
        _conflicts.SaveResult = new ResolutionSaveResult
        {
            Saved = true,
            DiskHash = "h2",
            StageError = new HistoriaGitException(GitErrorKind.Unknown, "git add failed", details: "fatal: lockfile present")
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var saved = await view.SaveCurrentFileAsync(stage: true);
        Dispatcher.UIThread.RunJobs();

        Assert.False(saved);
        var notifications = _sp.GetRequiredService<INotificationService>().Current;
        var errNotification = notifications.FirstOrDefault(n => n.Severity == NotificationSeverity.Error && n.Message.StartsWith("Saved f1.txt, but could not mark it resolved"));
        Assert.NotNull(errNotification);
        Assert.False(string.IsNullOrEmpty(errNotification.Details));
        Assert.Contains("fatal: lockfile present", errNotification.Details);
    }

    [AvaloniaFact]
    public async Task Edits_during_save_stay_unsaved()
    {
        var f1 = new ConflictFile { FilePath = "f1.txt", Status = ConflictStatus.BothModified, IsBinary = false };
        var f2 = new ConflictFile { FilePath = "f2.txt", Status = ConflictStatus.BothModified, IsBinary = false };
        _conflicts.Conflicts = [f1, f2];
        _conflicts.WorkingCopies["f1.txt"] = new ConflictWorkingCopy
        {
            Path = "f1.txt",
            Text = TwoConflictText,
            DiskHash = "hash1",
            Format = new TextFileFormat(System.Text.Encoding.UTF8, false)
        };
        _conflicts.WorkingCopies["f2.txt"] = new ConflictWorkingCopy
        {
            Path = "f2.txt",
            Text = TwoConflictText,
            DiskHash = "hash2",
            Format = new TextFileFormat(System.Text.Encoding.UTF8, false)
        };
        _conflicts.SaveResult = new ResolutionSaveResult { Saved = true, DiskHash = "hash1-saved" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var gate = new TaskCompletionSource<bool>();
        _conflicts.SaveGate = gate;

        var saveTask = view.SaveCurrentFileAsync(stage: true);

        // While save is in flight, mutate document text
        view.ResultEditorForTesting.Document.Insert(0, "// extra edit\n");
        Dispatcher.UIThread.RunJobs();

        // Release save gate
        gate.SetResult(true);
        var saved = await saveTask;
        Dispatcher.UIThread.RunJobs();

        Assert.True(saved);
        Assert.True(view.HasUnsavedWork);
        Assert.Equal("f1.txt", view.CurrentFileForTesting?.FilePath);
    }

    [AvaloniaFact]
    public async Task Dispose_during_status_refresh_publishes_no_error()
    {
        var f1 = new ConflictFile { FilePath = "f1.txt", Status = ConflictStatus.BothModified, IsBinary = false };
        _conflicts.Conflicts = [f1];
        _conflicts.WorkingCopies["f1.txt"] = new ConflictWorkingCopy { Path = "f1.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var gate = new TaskCompletionSource<ConflictSideLabels>();
        _conflicts.OnGetSideLabelsAsync = () => gate.Task;

        _ = _context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();

        view.Dispose();
        gate.SetResult(_conflicts.SideLabels);
        Dispatcher.UIThread.RunJobs();

        var notifications = _sp.GetRequiredService<INotificationService>().Current;
        Assert.DoesNotContain(notifications, n => n.Severity == NotificationSeverity.Error);
    }

    [AvaloniaFact]
    public async Task Skip_label_failure_publishes_error_and_does_not_skip()
    {
        var f1 = new ConflictFile { FilePath = "f1.txt", Status = ConflictStatus.BothModified, IsBinary = false };
        _conflicts.Conflicts = [f1];
        _conflicts.WorkingCopies["f1.txt"] = new ConflictWorkingCopy { Path = "f1.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        _conflicts.OnGetSideLabelsAsync = () => throw new InvalidOperationException("Failed to read commit labels");

        await view.SkipAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, _conflicts.SkipCalls);
        var notifications = _sp.GetRequiredService<INotificationService>().Current;
        Assert.Contains(notifications, n => n.Severity == NotificationSeverity.Error && n.Message.Contains("Failed to read commit labels"));
    }

    [AvaloniaFact]
    public async Task Continue_unsaved_prompt_uses_warning_chooser()
    {
        var f1 = new ConflictFile { FilePath = "f1.txt", Status = ConflictStatus.BothModified, IsBinary = false };
        _conflicts.Conflicts = [f1];
        _conflicts.WorkingCopies["f1.txt"] = new ConflictWorkingCopy
        {
            Path = "f1.txt",
            Text = TwoConflictText,
            DiskHash = "hash1",
            Format = new TextFileFormat(System.Text.Encoding.UTF8, false)
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        // Mutate document to create unsaved work
        view.ResultEditorForTesting.Document.Insert(0, "unsaved change\n");
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.HasUnsavedWork);

        _dialogs.ChooseResult = 2; // Cancel

        await view.ContinueAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(_dialogs.ChooseCalls);
        Assert.Equal(DialogIcon.Warning, _dialogs.LastChooseIcon);
        Assert.Equal(1, _dialogs.LastChooseDestructiveIndex);
    }

    [AvaloniaFact]
    public async Task Changed_on_disk_banner_is_cleared_when_the_file_switches()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }, new ConflictFile { FilePath = "g.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.WorkingCopies["g.txt"] = new ConflictWorkingCopy { Path = "g.txt", Text = "g content", DiskHash = "hash-g" };
        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        view.ResultEditorForTesting.Document.Insert(0, "unsaved edit" + Environment.NewLine);
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash-changed-on-disk" };
        await _context.State.RefreshStatusAsync();
        await PumpUntilAsync(() => view.ChangedOnDiskBannerForTesting.IsVisible, "precondition: banner shown for f.txt");
        _dialogs.ChooseResult = 1; // Discard
        _context.State.SetSelectedConflictPath("g.txt");
        await PumpUntilAsync(() => view.CurrentFileForTesting?.FilePath == "g.txt", "the switch to g.txt");
        Assert.False(view.ChangedOnDiskBannerForTesting.IsVisible, "banner should not carry over to g.txt");
    }

    [AvaloniaFact]
    public async Task Selection_switch_load_failure_publishes_an_error_and_keeps_the_current_file()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }, new ConflictFile { FilePath = "g.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.WorkingCopies["g.txt"] = new ConflictWorkingCopy { Path = "g.txt", Text = "g content", DiskHash = "hash-g" };
        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var notify = _sp.GetRequiredService<INotificationService>();
        Notification? lastError = null;
        notify.Changed += (_, e) =>
        {
            var err = e.Current.LastOrDefault(n => n.Severity == NotificationSeverity.Error);
            if (err != null) lastError = err;
        };

        Exception? unhandled = null;
        void H(object? s, DispatcherUnhandledExceptionEventArgs e) { unhandled = e.Exception; e.Handled = true; }
        Dispatcher.UIThread.UnhandledException += H;
        try
        {
            _conflicts.OnLoadWorkingCopyAsync = (path, ct) => path == "g.txt" ? throw new IOException("locked") : Task.CompletedTask;
            _context.State.SetSelectedConflictPath("g.txt");
            await PumpUntilAsync(() => lastError is not null || unhandled is not null, "the load failure to be reported");
        }
        finally { Dispatcher.UIThread.UnhandledException -= H; }

        Assert.Null(unhandled);
        Assert.NotNull(lastError);
        Assert.Contains("Could not open g.txt", lastError.Message);
        Assert.False(string.IsNullOrWhiteSpace(lastError.Details));
        Assert.Equal("f.txt", _context.State.SelectedConflictPath);
    }

    [AvaloniaFact]
    public async Task Selection_switch_cancelled_by_dispose_is_silent()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }, new ConflictFile { FilePath = "g.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.WorkingCopies["g.txt"] = new ConflictWorkingCopy { Path = "g.txt", Text = "g content", DiskHash = "hash-g" };
        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var notify = _sp.GetRequiredService<INotificationService>();
        Notification? lastError = null;
        notify.Changed += (_, e) =>
        {
            var err = e.Current.LastOrDefault(n => n.Severity == NotificationSeverity.Error);
            if (err != null) lastError = err;
        };

        var tcs = new TaskCompletionSource();
        _conflicts.OnLoadWorkingCopyAsync = async (path, ct) =>
        {
            if (path == "g.txt")
            {
                tcs.SetResult();
                await Task.Delay(5000, ct);
            }
        };

        _context.State.SetSelectedConflictPath("g.txt");
        await tcs.Task;
        view.Dispose();
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(lastError);
    }

    [AvaloniaFact]
    public async Task Selection_of_an_unlisted_file_with_a_cancelled_refresh_is_silent()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var notify = _sp.GetRequiredService<INotificationService>();
        var errorsBefore = notify.Current.Count(n => n.Severity == NotificationSeverity.Error);

        Exception? unhandled = null;
        void H(object? s, DispatcherUnhandledExceptionEventArgs e) { unhandled = e.Exception; e.Handled = true; }
        Dispatcher.UIThread.UnhandledException += H;
        try
        {
            _conflicts.OnGetConflictsAsync = () => throw new OperationCanceledException();
            _context.State.SetSelectedConflictPath("h.txt");
            await Task.Delay(100);
            Dispatcher.UIThread.RunJobs();
        }
        finally { Dispatcher.UIThread.UnhandledException -= H; }

        Assert.Null(unhandled);
        Assert.Equal(errorsBefore, notify.Current.Count(n => n.Severity == NotificationSeverity.Error));
        Assert.Equal("f.txt", view.CurrentFileForTesting?.FilePath);
    }

    [AvaloniaFact]
    public async Task Take_side_on_a_both_deleted_conflict_says_delete()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothDeletedRenamed, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = "", DiskHash = "hash1" };
        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        _dialogs.ConfirmDestructiveResult = true;
        await view.TakeOursCurrentAsync();
        Dispatcher.UIThread.RunJobs();

        var call = Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.StartsWith("Delete", call.Title);
        Assert.Equal("Delete", call.ConfirmLabel);
        var resolved = Assert.Single(_conflicts.ResolvedWithSideCalls);
        Assert.Equal("f.txt", resolved.Path);
        Assert.Equal(ConflictSide.Ours, resolved.Side);
    }

    #region Fakes

    internal sealed class FakeConflictService : IConflictService
    {
        public IReadOnlyList<ConflictFile> Conflicts { get; set; } = [];
        public Dictionary<string, ConflictWorkingCopy> WorkingCopies { get; } = new(StringComparer.Ordinal);
        public ConflictSideLabels SideLabels { get; set; } = new()
        {
            OursTitle = "Ours",
            OursDetail = "Ours detail",
            TheirsTitle = "Theirs",
            TheirsDetail = "Theirs detail",
            Operation = OperationState.Merging,
            StopId = "stop-init"
        };
        public ResolutionSaveResult SaveResult { get; set; } = new() { Saved = true, DiskHash = "saved-hash" };
        public TaskCompletionSource<bool>? SaveGate { get; set; }
        public Func<Task<ConflictSideLabels>>? OnGetSideLabelsAsync { get; set; }
        public Func<Task>? OnBeforeSaveAsync { get; set; }
        public Func<Task>? OnContinueAsync { get; set; }
        public Func<string, CancellationToken, Task>? OnLoadWorkingCopyAsync { get; set; }
        public Func<Task<IReadOnlyList<ConflictFile>>>? OnGetConflictsAsync { get; set; }
        public int GetConflictsCalls { get; set; }
        public bool AutoRemoveResolvedConflicts { get; set; } = true;
        public List<(string FilePath, string Text, bool Stage)> SaveResolutionCalls { get; } = [];
        public int ContinueCalls { get; set; }
        public int AbortCalls { get; set; }
        public List<(string Path, ConflictSide Side)> ResolvedWithSideCalls { get; } = [];
        public List<string> RecreatedCalls { get; } = [];
        public List<string> ExternalToolCalls { get; } = [];

        public async Task<IReadOnlyList<ConflictFile>> GetConflictsAsync(RepositoryContext context, CancellationToken ct = default)
        {
            GetConflictsCalls++;
            if (OnGetConflictsAsync != null)
                return await OnGetConflictsAsync();
            return Conflicts;
        }

        public string? DiskHash { get; set; }
        public int GetDiskHashCalls { get; set; }
        public int LoadWorkingCopyCalls { get; set; }

        public Task<string> GetDiskHashAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
        {
            GetDiskHashCalls++;
            if (DiskHash != null)
                return Task.FromResult(DiskHash);
            if (WorkingCopies.TryGetValue(filePath, out var copy))
                return Task.FromResult(copy.DiskHash);
            return Task.FromResult("default-hash");
        }

        public async Task<ConflictWorkingCopy> LoadWorkingCopyAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
        {
            LoadWorkingCopyCalls++;
            if (OnLoadWorkingCopyAsync != null)
                await OnLoadWorkingCopyAsync(filePath, ct);

            if (WorkingCopies.TryGetValue(filePath, out var copy))
            {
                if (copy.Format == null && !copy.IsBinary && !copy.UnsupportedEncoding)
                {
                    return copy with { Format = new TextFileFormat(System.Text.Encoding.UTF8, false) };
                }
                return copy;
            }

            return new ConflictWorkingCopy
            {
                Path = filePath,
                Text = "",
                DiskHash = "default-hash",
                Format = new TextFileFormat(System.Text.Encoding.UTF8, false)
            };
        }

        public async Task<ResolutionSaveResult> SaveResolutionAsync(RepositoryContext context, string filePath, string text, TextFileFormat format, string expectedDiskHash, bool stage, CancellationToken ct = default)
        {
            SaveResolutionCalls.Add((filePath, text, stage));
            if (SaveGate != null)
                await SaveGate.Task;
            if (OnBeforeSaveAsync != null)
                await OnBeforeSaveAsync();

            if (stage && AutoRemoveResolvedConflicts && SaveResult.Saved)
            {
                Conflicts = Conflicts.Where(c => !string.Equals(c.FilePath, filePath, StringComparison.Ordinal)).ToList();
            }

            return SaveResult;
        }

        public Task ResolveWithSideAsync(RepositoryContext context, string filePath, ConflictSide side, CancellationToken ct = default)
        {
            ResolvedWithSideCalls.Add((filePath, side));
            if (AutoRemoveResolvedConflicts)
            {
                Conflicts = Conflicts.Where(c => !string.Equals(c.FilePath, filePath, StringComparison.Ordinal)).ToList();
            }
            return Task.CompletedTask;
        }

        public Task RecreateConflictAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
        {
            RecreatedCalls.Add(filePath);
            if (AutoRemoveResolvedConflicts && !Conflicts.Any(c => string.Equals(c.FilePath, filePath, StringComparison.Ordinal)))
            {
                var list = Conflicts.ToList();
                list.Add(new ConflictFile { FilePath = filePath, Status = ConflictStatus.BothModified, IsBinary = false });
                Conflicts = list;
            }
            return Task.CompletedTask;
        }

        public async Task<ConflictSideLabels> GetSideLabelsAsync(RepositoryContext context, CancellationToken ct = default)
        {
            if (OnGetSideLabelsAsync != null)
                return await OnGetSideLabelsAsync();
            return SideLabels;
        }

        public Task<bool> LaunchExternalMergeToolAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
        {
            ExternalToolCalls.Add(filePath);
            return Task.FromResult(true);
        }

        public Func<CancellationToken, Task>? OnContinueWithTokenAsync { get; set; }
        public int SkipCalls { get; set; }

        public async Task ContinueOperationAsync(RepositoryContext context, CancellationToken ct = default)
        {
            ContinueCalls++;
            if (OnContinueWithTokenAsync != null)
                await OnContinueWithTokenAsync(ct);
            else if (OnContinueAsync != null)
                await OnContinueAsync();
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

    internal sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public bool ConfirmDestructiveResult { get; set; } = true;
        public int ChooseResult { get; set; } = 0;
        public DialogIcon LastChooseIcon { get; set; } = DialogIcon.None;
        public int LastChooseDestructiveIndex { get; set; } = -1;
        public List<(string Title, string Effect, string ConfirmLabel)> ConfirmDestructiveCalls { get; } = [];
        public List<(string Title, string Message, IReadOnlyList<string> Options)> ChooseCalls { get; } = [];
        public List<(string Title, string Message, bool Destructive)> ConfirmCalls { get; } = [];

        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, bool destructive = false, string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None)
        {
            ConfirmCalls.Add((title, message, destructive));
            return Task.FromResult(ConfirmResult);
        }

        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel)
        {
            ConfirmDestructiveCalls.Add((title, effect, confirmLabel));
            return Task.FromResult(ConfirmDestructiveResult);
        }

        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options, int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None)
        {
            LastChooseIcon = icon;
            LastChooseDestructiveIndex = destructiveIndex;
            ChooseCalls.Add((title, message, options));
            return Task.FromResult(ChooseResult);
        }

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

        public Task<string?> PromptAsync(string title, string message, string? defaultValue = null, string confirmLabel = "OK", bool primaryConfirm = false)
            => Task.FromResult<string?>(null);
    }

    internal sealed class FakeSettingsService(AppSettings? initial = null) : ISettingsService
    {
        public AppSettings Current { get; } = initial ?? new();
        public event EventHandler<SettingsChangedEventArgs>? Changed { add { } remove { } }
        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    internal sealed class FakeThemeProbe : ISystemThemeProbe
    {
        public bool IsLightTheme => false;
        public bool IsHighContrast => false;
#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067
    }

    internal sealed class FakeVault : ICredentialVaultService
    {
        public Task StoreTokenAsync(string key, string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> RetrieveTokenAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteTokenAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }

    internal sealed class FakeNetworkRunner : IRepoNetworkActionRunner
    {
        public bool IsBusy => false;
        public RepoNetworkProgress? CurrentProgress => null;
#pragma warning disable CS0067
        public event EventHandler<RepoNetworkProgress>? ProgressChanged;
#pragma warning restore CS0067
        public Task RunAsync(RepositoryContext repo, string actionName, Func<IProgress<RepoNetworkProgress>?, CancellationToken, Task> action, CancellationToken ct = default)
            => Task.CompletedTask;
        public void Cancel(RepositoryContext repo) { }
        GitProgress? IRepoNetworkActionRunner.CurrentProgress(RepositoryContext repo) => null;
        public Task RunExclusiveAsync(RepositoryContext repo, string label, Func<IProgress<GitProgress>, CancellationToken, Task> operation)
            => operation(new Progress<GitProgress>(), CancellationToken.None);
    }

    internal sealed class TestRepoFixture : IDisposable
    {
        public string Path { get; private set; }
        public string GitDir => System.IO.Path.Combine(Path, ".git");

        public TestRepoFixture()
        {
            Path = NewPath();
            RepoTemplates.Initial(Path);
        }

        private static string NewPath() =>
            TestScratch.NewPath("historia-conflict-tests-");

        /// <summary>Moves the fixture to a fresh copy of the merge-conflict template: the state
        /// <see cref="CreateConflict"/> produces, without running git. The copy is not atomic, so
        /// only call this while no tab has the repository open; <see cref="Path"/> changes.</summary>
        public void LoadMergeConflict()
        {
            var old = Path;
            Path = NewPath();
            RepoTemplates.MergeConflict(Path);
            RepoTemplates.Delete(old);
        }

        public void SetOperation(OperationState op)
        {
            ClearOperation();
            switch (op)
            {
                case OperationState.Merging:
                    File.WriteAllText(System.IO.Path.Combine(GitDir, "MERGE_HEAD"), "1111111111111111111111111111111111111111\n");
                    break;
                case OperationState.Rebasing:
                    Directory.CreateDirectory(System.IO.Path.Combine(GitDir, "rebase-merge"));
                    break;
                case OperationState.CherryPicking:
                    File.WriteAllText(System.IO.Path.Combine(GitDir, "CHERRY_PICK_HEAD"), "1111111111111111111111111111111111111111\n");
                    break;
                case OperationState.Reverting:
                    File.WriteAllText(System.IO.Path.Combine(GitDir, "REVERT_HEAD"), "1111111111111111111111111111111111111111\n");
                    break;
                case OperationState.Bisecting:
                    File.WriteAllText(System.IO.Path.Combine(GitDir, "BISECT_LOG"), "bisect log\n");
                    break;
            }
        }

        public void ClearOperation()
        {
            try { File.Delete(System.IO.Path.Combine(GitDir, "MERGE_HEAD")); } catch { }
            try { File.Delete(System.IO.Path.Combine(GitDir, "CHERRY_PICK_HEAD")); } catch { }
            try { File.Delete(System.IO.Path.Combine(GitDir, "REVERT_HEAD")); } catch { }
            try { File.Delete(System.IO.Path.Combine(GitDir, "BISECT_LOG")); } catch { }
            try { Directory.Delete(System.IO.Path.Combine(GitDir, "rebase-merge"), true); } catch { }
            try { Directory.Delete(System.IO.Path.Combine(GitDir, "rebase-apply"), true); } catch { }
        }

        public void CreateConflict() => RepoTemplates.CreateMergeConflict(Path);

        public void MarkResolved() => RepoTemplates.Git(Path, "add", "f.txt");

        public void Dispose() => RepoTemplates.Delete(Path);
    }

    #endregion

    #region Toolbar

    private static void ClickMenuItem(ConflictResolverView view, string header)
    {
        var item = view.ResolveFileMenuItemsForTesting.Single(m => m.Header as string == header);
        // The flyout sits outside the view's tree, so the view's code-behind assigns these commands.
        var expected = header switch
        {
            "Take ours (whole file)" => view.ViewModel.TakeOursCurrentCommand,
            "Take theirs (whole file)" => view.ViewModel.TakeTheirsCurrentCommand,
            "Reset file to conflicted" => view.ViewModel.ResetFileCommand,
            "Open in external tool" => view.ViewModel.LaunchExternalToolCommand,
            _ => throw new ArgumentException($"No view-model command is known for the menu item '{header}'.", nameof(header)),
        };
        Assert.Same(expected, item.Command);
        item.Command!.Execute(null);
    }

    /// <summary>Runs posted UI work until <paramref name="condition"/> holds, for at most 10 seconds.
    /// Status changes reach the view through a 50 ms debounce and then its own reload, so a fixed
    /// sleep can end before either has run on a busy machine.</summary>
    /// <summary>Runs posted UI work until <see cref="ConflictPaneBuilder.BuildCallCount"/> holds
    /// still for 300 ms, for at most 10 seconds, and returns it.</summary>
    private static async Task<int> SettledBuildCountAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var count = ConflictPaneBuilder.BuildCallCount;
        var stableSince = DateTime.UtcNow;
        while (DateTime.UtcNow - stableSince < TimeSpan.FromMilliseconds(300))
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for pane builds to settle.");
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
            var now = ConflictPaneBuilder.BuildCallCount;
            if (now != count)
            {
                count = now;
                stableSince = DateTime.UtcNow;
            }
        }
        return count;
    }

    private static async Task PumpUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
                return;
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(20);
        }
    }

    /// <summary>Refreshes the tab's status and runs posted UI work until <paramref name="condition"/>
    /// holds, for at most 10 seconds. After a git command on the open repository, a refresh that
    /// joins one its file watcher already started can return a snapshot taken before the command.</summary>
    private async Task RefreshUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            await _context.State.RefreshStatusAsync();
            Dispatcher.UIThread.RunJobs();
            if (condition())
                return;
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(50);
        }
    }

    private async Task<ConflictResolverView> LoadTwoConflictViewAsync()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    [AvaloniaFact]
    public async Task Take_ours_menu_item_confirms_before_resolving()
    {
        var view = await LoadTwoConflictViewAsync();

        _dialogs.ConfirmDestructiveResult = false;
        ClickMenuItem(view, "Take ours (whole file)");
        await PumpUntilAsync(() => _dialogs.ConfirmDestructiveCalls.Count > 0, "the confirmation");

        var call = Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Take ours", call.Title);
        Assert.Empty(_conflicts.ResolvedWithSideCalls);
    }

    [AvaloniaFact]
    public async Task Resolve_file_disabled_on_all_resolved_card()
    {
        _conflicts.Conflicts = [];
        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(view.CurrentFileForTesting);
        Assert.False(view.ResolveFileButtonForTesting.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Counter_shows_region_under_caret_then_next_region()
    {
        var view = await LoadTwoConflictViewAsync();
        var caret = view.ResultEditorForTesting.TextArea.Caret;

        caret.Line = 3; // "ours 1", inside region 1
        Assert.Equal(0, view.CurrentRegionIndex());
        Assert.Equal("1/2", view.ConflictsCountTextForTesting.Text);

        caret.Line = 7; // "middle", between the regions
        Assert.Equal("2/2", view.ConflictsCountTextForTesting.Text);

        caret.Line = 1; // "header", above both
        Assert.Equal("1/2", view.ConflictsCountTextForTesting.Text);

        view.GoToNextConflict(); // from above both: the first region
        Assert.Equal(2, caret.Line);
        Assert.Equal("1/2", view.ConflictsCountTextForTesting.Text);

        view.GoToNextConflict();
        Assert.Equal(8, caret.Line);
        Assert.Equal("2/2", view.ConflictsCountTextForTesting.Text);

        caret.Line = 13; // "footer", below both
        Assert.Equal("2/2", view.ConflictsCountTextForTesting.Text);
    }

    [AvaloniaFact]
    public async Task Nav_buttons_disabled_without_regions()
    {
        var view = await LoadTwoConflictViewAsync();
        Assert.True(view.PrevConflictButtonForTesting.IsEnabled);
        Assert.True(view.NextConflictButtonForTesting.IsEnabled);
        Assert.True(view.ConflictsCountTextForTesting.IsVisible);

        _conflicts.Conflicts = [new ConflictFile { FilePath = "plain.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["plain.txt"] = new ConflictWorkingCopy { Path = "plain.txt", Text = "no markers here\n", DiskHash = "hash-p" };
        _context.State.SetSelectedConflictPath("plain.txt");
        await PumpUntilAsync(() => view.CurrentFileForTesting?.FilePath == "plain.txt", "the switch to plain.txt");

        Assert.Equal("plain.txt", view.CurrentFileForTesting?.FilePath);
        Assert.Empty(view.NavigableRegions());
        Assert.False(view.PrevConflictButtonForTesting.IsEnabled);
        Assert.False(view.NextConflictButtonForTesting.IsEnabled);
        Assert.False(view.ConflictsCountTextForTesting.IsVisible);
    }

    private static void PressCtrlS(ConflictResolverView view)
        => view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.S, KeyModifiers = KeyModifiers.Control });

    [AvaloniaFact]
    public async Task Ctrl_S_saves_without_staging()
    {
        var view = await LoadTwoConflictViewAsync();
        view.ResultEditorForTesting.Document.Insert(0, "edit\n");
        Dispatcher.UIThread.RunJobs();

        PressCtrlS(view);
        await PumpUntilAsync(() => _conflicts.SaveResolutionCalls.Count > 0, "the save");

        var call = Assert.Single(_conflicts.SaveResolutionCalls);
        Assert.False(call.Stage);
        Assert.StartsWith("edit\n", call.Text);
    }

    [AvaloniaFact]
    public async Task Ctrl_S_does_nothing_when_not_editable()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "binary.png", Status = ConflictStatus.BothModified, IsBinary = true }];
        _conflicts.WorkingCopies["binary.png"] = new ConflictWorkingCopy { Path = "binary.png", IsBinary = true, DiskHash = "hash1" };
        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        PressCtrlS(view);
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(20);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(_conflicts.SaveResolutionCalls);
    }

    [AvaloniaFact]
    public async Task Continue_becomes_primary_when_all_files_resolved()
    {
        _context.State.SetKeepConflictResolverOpen(true);
        var view = await LoadTwoConflictViewAsync();
        Assert.Contains("primary", view.SaveAndResolveButtonForTesting.Classes);
        Assert.DoesNotContain("primary", view.ContinueButtonForTesting.Classes);

        _dialogs.ConfirmResult = true;
        await view.SaveAndMarkResolvedAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(view.ConflictedFilesForTesting);
        Assert.Contains("primary", view.ContinueButtonForTesting.Classes);
        Assert.DoesNotContain("primary", view.SaveAndResolveButtonForTesting.Classes);
        Assert.Contains("secondary", view.SaveAndResolveButtonForTesting.Classes);
    }

    [AvaloniaFact]
    public async Task Header_has_no_file_count_and_captions_show_side_titles()
    {
        _conflicts.SideLabels = _conflicts.SideLabels with { OursTitle = "main", TheirsTitle = "feature", TheirsDetail = "abc1234 Add feature" };
        var view = await LoadTwoConflictViewAsync();

        Assert.Equal("Ours · main", view.OursCaptionForTesting.Text);
        Assert.Equal("Theirs · feature", view.TheirsCaptionForTesting.Text);
        Assert.Equal("abc1234 Add feature", ToolTip.GetTip(view.TheirsCaptionForTesting));
        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
        Assert.DoesNotContain(texts, t => t.Contains("conflicted file", StringComparison.Ordinal));
    }

    #endregion

    #region Line toggles

    private const string TwoOursLinesText =
        "header\n" +
        "<<<<<<< HEAD\n" +
        "ours 1\n" +
        "ours 2\n" +
        "=======\n" +
        "theirs 1\n" +
        ">>>>>>> branch\n" +
        "footer\n";

    private async Task<(ConflictResolverView View, Window Window)> ShowViewAsync(string text)
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = text, DiskHash = "hash1" };
        var view = new ConflictResolverView(_sp, _context);
        var window = new Window { Content = view, Width = 1000, Height = 800 };
        window.Show();
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (view, window);
    }

    /// <summary>Clicks the hunk check box of <paramref name="side"/> on a marker region's first line for that side.</summary>
    private static void ClickHunk(ConflictResolverView view, ConflictSide side, ConflictRegion region)
        => view.ClickHunkForTesting(side, region.StartLine + 1);

    /// <summary>Clicks the check box a toggle margin draws on <paramref name="docLine"/> (0-based).</summary>
    private static void ClickToggle(ConflictToggleMargin margin, TextEditor editor, int docLine)
    {
        var textView = editor.TextArea.TextView;
        textView.EnsureVisualLines();
        double y = textView.GetVisualTopByDocumentLine(docLine + 1) - textView.VerticalOffset + textView.DefaultLineHeight / 2;
        Assert.True(margin.ToggleAt(y), $"no check box on line {docLine}");
    }

    private static void AssertPanesAligned(ConflictResolverView view)
    {
        int resultLines = view.ResultEditorForTesting.Document.LineCount;
        Assert.Equal(resultLines, view.OursEditorForTesting.Document.LineCount);
        Assert.Equal(resultLines, view.TheirsEditorForTesting.Document.LineCount);
    }

    private static int LineOf(ConflictResolverView view, string text)
    {
        var doc = view.ResultEditorForTesting.Document;
        return doc.Lines.First(l => doc.GetText(l.Offset, l.Length) == text).LineNumber - 1;
    }

    /// <summary>Presses Ctrl+Space with <paramref name="target"/> as the focused element, so the
    /// view's tunnelling handler sees it as the event source.</summary>
    private static void PressCtrlSpace(Interactive target, out bool handled)
    {
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space, KeyModifiers = KeyModifiers.Control };
        target.RaiseEvent(args);
        handled = args.Handled;
    }

    [AvaloniaFact]
    public async Task Checking_ours_hunk_in_top_pane_manages_region()
    {
        var (view, window) = await ShowViewAsync(TwoConflictText);
        try
        {
            ClickToggle(view.OursToggleMarginForTesting, view.OursEditorForTesting, 2); // region 1's first ours row

            var region = Assert.Single(view.SessionForTesting.Regions);
            Assert.Equal(ConflictHunkState.Checked, region.Selection.HunkState(ConflictSide.Ours));
            Assert.StartsWith("header\nours 1\ntheirs 1\nmiddle\n<<<<<<< HEAD\n", view.ResultEditorForTesting.Text);
            Assert.Single(view.CurrentParseForTesting!.Regions);
            AssertPanesAligned(view);
            Assert.Equal("ours 1", view.OursEditorForTesting.Document.GetText(view.OursEditorForTesting.Document.GetLineByNumber(2)));
            Assert.Equal("theirs 1", view.TheirsEditorForTesting.Document.GetText(view.TheirsEditorForTesting.Document.GetLineByNumber(2)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Hunk_boxes_sit_on_each_sides_first_row()
    {
        var (view, window) = await ShowViewAsync(TwoConflictText);
        try
        {
            // Region 1 spans lines 1-5, region 2 lines 7-11; the first content row of each is the next line.
            foreach (var firstRow in new[] { 2, 8 })
            {
                Assert.Equal(ConflictToggleGlyph.Unchecked, view.HunkGlyphForTesting(ConflictSide.Ours, firstRow));
                Assert.Equal(ConflictToggleGlyph.Unchecked, view.HunkGlyphForTesting(ConflictSide.Theirs, firstRow));
            }

            var boxRows = Enumerable.Range(0, view.ResultEditorForTesting.Document.LineCount)
                .Where(row => view.HunkGlyphForTesting(ConflictSide.Theirs, row) != ConflictToggleGlyph.None)
                .ToList();
            Assert.Equal([2, 8], boxRows);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Padding_row_at_hunk_start_index_has_no_box()
    {
        var (view, window) = await ShowViewAsync(TwoConflictText);
        try
        {
            // Region 1's theirs line is Result line 4, but the theirs pane's row 4 is padding. The
            // session keys its box by Result line, so only a side row may use it.
            Assert.Equal(ConflictToggleGlyph.Unchecked, view.SessionForTesting.HunkGlyph(ConflictSide.Theirs, 4));
            Assert.Equal(ConflictPaneLineType.Padding, view.SnapshotForTesting!.TheirsLines[4].Type);
            Assert.Equal(4, view.SnapshotForTesting.TheirsLines[4].DocumentLineIndex);
            Assert.Equal(ConflictToggleGlyph.None, view.HunkGlyphForTesting(ConflictSide.Theirs, 4));

            var text = view.ResultEditorForTesting.Text;
            view.ClickHunkForTesting(ConflictSide.Theirs, 4);

            Assert.Empty(view.SessionForTesting.Regions);
            Assert.Equal(text, view.ResultEditorForTesting.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Clicking_theirs_box_selects_theirs()
    {
        var (view, window) = await ShowViewAsync(TwoConflictText);
        try
        {
            ClickToggle(view.TheirsToggleMarginForTesting, view.TheirsEditorForTesting, 2);

            var region = Assert.Single(view.SessionForTesting.Regions);
            Assert.Equal(ConflictHunkState.Checked, region.Selection.HunkState(ConflictSide.Theirs));
            Assert.Equal(ConflictHunkState.Unchecked, region.Selection.HunkState(ConflictSide.Ours));
            Assert.StartsWith("header\ntheirs 1\nmiddle\n", view.ComposedTextForTesting);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Unchecking_line_in_result_updates_top_hunk_to_partial()
    {
        var (view, window) = await ShowViewAsync(TwoOursLinesText);
        try
        {
            ClickToggle(view.OursToggleMarginForTesting, view.OursEditorForTesting, 2);
            var session = view.SessionForTesting;
            int ours1 = LineOf(view, "ours 1");

            ClickToggle(view.ResultToggleMarginForTesting, view.ResultEditorForTesting, LineOf(view, "ours 2"));

            Assert.Equal(ConflictToggleGlyph.Partial, session.HunkGlyph(ConflictSide.Ours, ours1));
            Assert.Equal(ConflictToggleGlyph.Unchecked, session.RowGlyph(LineOf(view, "ours 2")));
            Assert.Equal("header\nours 1\nfooter\n", view.ComposedTextForTesting);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Unchecking_every_line_unchecks_top_hunk()
    {
        var (view, window) = await ShowViewAsync(TwoOursLinesText);
        try
        {
            ClickToggle(view.OursToggleMarginForTesting, view.OursEditorForTesting, 2);
            int ours1 = LineOf(view, "ours 1");

            ClickToggle(view.ResultToggleMarginForTesting, view.ResultEditorForTesting, ours1);
            ClickToggle(view.ResultToggleMarginForTesting, view.ResultEditorForTesting, LineOf(view, "ours 2"));

            Assert.Equal(ConflictToggleGlyph.Unchecked, view.SessionForTesting.HunkGlyph(ConflictSide.Ours, ours1));
            Assert.Equal("header\nfooter\n", view.ComposedTextForTesting);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Unchecking_top_hunk_resets_result_lines()
    {
        var (view, window) = await ShowViewAsync(TwoOursLinesText);
        try
        {
            ClickToggle(view.OursToggleMarginForTesting, view.OursEditorForTesting, 2);
            int ours1 = LineOf(view, "ours 1");
            int ours2 = LineOf(view, "ours 2");
            Assert.Equal(ConflictToggleGlyph.Checked, view.SessionForTesting.RowGlyph(ours2));

            ClickToggle(view.OursToggleMarginForTesting, view.OursEditorForTesting, ours1); // the box sits on the first ours row

            Assert.Equal(ConflictToggleGlyph.Unchecked, view.SessionForTesting.RowGlyph(ours1));
            Assert.Equal(ConflictToggleGlyph.Unchecked, view.SessionForTesting.RowGlyph(ours2));
            Assert.Equal(ConflictToggleGlyph.Unchecked, view.SessionForTesting.HunkGlyph(ConflictSide.Ours, ours1));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Save_writes_composed_text()
    {
        var view = await LoadTwoConflictViewAsync();
        ClickHunk(view, ConflictSide.Ours, view.CurrentParseForTesting!.Regions[0]);
        ClickHunk(view, ConflictSide.Theirs, view.CurrentParseForTesting!.Regions[0]);
        var second = view.SessionForTesting.Regions[1];
        view.SessionForTesting.ToggleHunk(second, ConflictSide.Theirs); // both unchecked: saves nothing

        var saved = await view.SaveCurrentFileAsync(stage: false);

        Assert.True(saved);
        var call = Assert.Single(_conflicts.SaveResolutionCalls);
        Assert.Equal("header\nours 1\nmiddle\nfooter\n", call.Text);
        Assert.DoesNotContain("<<<<<<<", call.Text);
    }

    [AvaloniaFact]
    public async Task Toggle_registers_unsaved_work()
    {
        var view = await LoadTwoConflictViewAsync();
        ClickHunk(view, ConflictSide.Ours, view.CurrentParseForTesting!.Regions[0]);
        await view.SaveCurrentFileAsync(stage: false);
        Assert.False(view.HasUnsavedWork);
        var text = view.ResultEditorForTesting.Text;

        Assert.True(view.SessionForTesting.ToggleRow(LineOf(view, "theirs 1")));

        Assert.Equal(text, view.ResultEditorForTesting.Text);
        Assert.True(view.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task Ours_hunk_click_creates_toggleable_region()
    {
        var view = await LoadTwoConflictViewAsync();
        ClickHunk(view, ConflictSide.Ours, view.CurrentParseForTesting!.Regions[0]);
        int theirs1 = LineOf(view, "theirs 1");
        Assert.Equal(ConflictToggleGlyph.Unchecked, view.SessionForTesting.RowGlyph(theirs1));

        view.SessionForTesting.ToggleRow(theirs1);

        Assert.Equal(ConflictToggleGlyph.Checked, view.SessionForTesting.RowGlyph(theirs1));
        Assert.StartsWith("header\nours 1\ntheirs 1\nmiddle\n", view.ComposedTextForTesting);

        // Re-checking ours after unchecking it puts it behind theirs.
        view.ClickHunkForTesting(ConflictSide.Ours, view.SessionForTesting.Regions[0].StartLine);
        view.ClickHunkForTesting(ConflictSide.Ours, view.SessionForTesting.Regions[0].StartLine);
        Assert.StartsWith("header\ntheirs 1\nours 1\nmiddle\n", view.ResultEditorForTesting.Text);
        AssertPanesAligned(view);
    }

    [AvaloniaFact]
    public async Task Take_ours_whole_file_clears_session()
    {
        var view = await LoadTwoConflictViewAsync();
        ClickHunk(view, ConflictSide.Ours, view.CurrentParseForTesting!.Regions[0]);
        Assert.Single(view.SessionForTesting.Regions);

        _dialogs.ConfirmDestructiveResult = true;
        await view.TakeOursCurrentAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(_conflicts.ResolvedWithSideCalls);
        Assert.Empty(view.SessionForTesting.Regions);
        Assert.Empty(view.SessionForTesting.Spans);
    }

    [AvaloniaFact]
    public async Task Counter_includes_managed_regions()
    {
        var view = await LoadTwoConflictViewAsync();
        ClickHunk(view, ConflictSide.Ours, view.CurrentParseForTesting!.Regions[0]);

        Assert.Equal(2, view.NavigableRegions().Count);
        view.ResultEditorForTesting.TextArea.Caret.Line = 1;
        Assert.Equal("1/2", view.ConflictsCountTextForTesting.Text);
        Assert.Contains("1 unresolved", ToolTip.GetTip(view.ConflictsCountTextForTesting) as string);

        view.GoToNextConflict();
        Assert.Equal(2, view.ResultEditorForTesting.TextArea.Caret.Line);
        view.GoToNextConflict();
        Assert.Equal("2/2", view.ConflictsCountTextForTesting.Text);
    }

    [AvaloniaFact]
    public async Task Switching_files_with_managed_regions_leaves_new_file_untouched()
    {
        _conflicts.Conflicts =
        [
            new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false },
            new ConflictFile { FilePath = "g.txt", Status = ConflictStatus.BothModified, IsBinary = false },
        ];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.WorkingCopies["g.txt"] = new ConflictWorkingCopy { Path = "g.txt", Text = TwoOursLinesText, DiskHash = "hash2" };
        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        ClickHunk(view, ConflictSide.Ours, view.CurrentParseForTesting!.Regions[0]);
        Assert.True(view.HasUnsavedWork);

        _dialogs.ChooseResult = 1; // Discard
        _context.State.SetSelectedConflictPath("g.txt");
        await PumpUntilAsync(() => view.CurrentFileForTesting?.FilePath == "g.txt", "the switch to g.txt");

        Assert.Equal("g.txt", view.CurrentFileForTesting?.FilePath);
        Assert.Equal(TwoOursLinesText, view.ResultEditorForTesting.Text);
        Assert.False(view.ResultEditorForTesting.Document.UndoStack.CanUndo);
        Assert.False(view.HasUnsavedWork);
        Assert.Empty(view.SessionForTesting.Regions);
        Assert.Equal(TwoOursLinesText, view.ComposedTextForTesting);
    }

    [AvaloniaFact]
    public async Task Session_edit_realigns_panes_without_waiting()
    {
        var (view, window) = await ShowViewAsync(TwoConflictText);
        try
        {
            ClickToggle(view.TheirsToggleMarginForTesting, view.TheirsEditorForTesting, 2); // region 1's theirs row
            AssertPanesAligned(view);

            // The managed region's ours rows start on its first row in the ours pane.
            ClickToggle(view.OursToggleMarginForTesting, view.OursEditorForTesting, view.SessionForTesting.Regions[0].StartLine);
            AssertPanesAligned(view);

            view.ResultEditorForTesting.Document.UndoStack.Undo();
            AssertPanesAligned(view);
            view.ResultEditorForTesting.Document.UndoStack.Undo();
            AssertPanesAligned(view);
            Assert.Equal(TwoConflictText, view.ResultEditorForTesting.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Ctrl_Space_toggles_row_at_caret()
    {
        var (view, window) = await ShowViewAsync(TwoConflictText);
        try
        {
            ClickHunk(view, ConflictSide.Ours, view.CurrentParseForTesting!.Regions[0]);
            int theirs1 = LineOf(view, "theirs 1");
            Assert.True(view.SessionForTesting.IsExcluded(theirs1));

            view.ResultEditorForTesting.TextArea.Caret.Line = theirs1 + 1;
            PressCtrlSpace(view.ResultEditorForTesting.TextArea, out bool handled);

            Assert.True(handled);
            Assert.False(view.SessionForTesting.IsExcluded(theirs1));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Ctrl_Space_outside_managed_row_does_nothing()
    {
        var (view, window) = await ShowViewAsync(TwoConflictText);
        try
        {
            ClickHunk(view, ConflictSide.Ours, view.CurrentParseForTesting!.Regions[0]);
            var text = view.ComposedTextForTesting;

            view.ResultEditorForTesting.TextArea.Caret.Line = 1; // "header"
            PressCtrlSpace(view.ResultEditorForTesting.TextArea, out bool handled);

            Assert.False(handled);
            Assert.Equal(text, view.ComposedTextForTesting);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Ctrl_Space_in_a_top_pane_does_not_toggle_the_result_row()
    {
        var (view, window) = await ShowViewAsync(TwoConflictText);
        try
        {
            ClickHunk(view, ConflictSide.Ours, view.CurrentParseForTesting!.Regions[0]);
            int theirs1 = LineOf(view, "theirs 1");
            view.ResultEditorForTesting.TextArea.Caret.Line = theirs1 + 1;

            PressCtrlSpace(view.OursEditorForTesting.TextArea, out bool handled);

            Assert.False(handled);
            Assert.True(view.SessionForTesting.IsExcluded(theirs1));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Mark_resolved_warns_about_emptied_conflicts()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoOursLinesText, DiskHash = "hash1" };
        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        ClickHunk(view, ConflictSide.Ours, view.CurrentParseForTesting!.Regions[0]);
        view.SessionForTesting.ToggleHunk(view.SessionForTesting.Regions[0], ConflictSide.Ours);
        Assert.Empty(view.CurrentParseForTesting!.Regions);
        Assert.Equal(1, view.SessionForTesting.EmptyRegionCount);

        _dialogs.ConfirmResult = false;
        await view.SaveAndMarkResolvedAsync();

        var confirm = Assert.Single(_dialogs.ConfirmCalls);
        Assert.Equal("Emptied conflicts", confirm.Title);
        Assert.Contains("1 conflict(s) will save as no lines", confirm.Message);
        Assert.False(confirm.Destructive);
        Assert.Empty(_dialogs.ConfirmDestructiveCalls);
        Assert.Empty(_conflicts.SaveResolutionCalls);

        _dialogs.ConfirmResult = true;
        await view.SaveAndMarkResolvedAsync();
        var call = Assert.Single(_conflicts.SaveResolutionCalls);
        Assert.Equal("header\nfooter\n", call.Text);
        Assert.True(call.Stage);
    }

    [AvaloniaFact]
    public async Task Mark_resolved_with_markers_left_mentions_emptied_conflicts()
    {
        var view = await LoadTwoConflictViewAsync();
        ClickHunk(view, ConflictSide.Ours, view.CurrentParseForTesting!.Regions[0]);
        view.SessionForTesting.ToggleHunk(view.SessionForTesting.Regions[0], ConflictSide.Ours);

        _dialogs.ConfirmResult = false;
        await view.SaveAndMarkResolvedAsync();

        var confirm = Assert.Single(_dialogs.ConfirmCalls);
        Assert.Equal("Unresolved conflicts", confirm.Title);
        Assert.Contains("1 conflict block(s) remain", confirm.Message);
        Assert.Contains("1 conflict(s) will save as no lines", confirm.Message);
    }

    [AvaloniaFact]
    public async Task Minimap_gets_managed_ranges()
    {
        var view = await LoadTwoConflictViewAsync();
        ClickHunk(view, ConflictSide.Ours, view.CurrentParseForTesting!.Regions[0]);

        var region = view.SessionForTesting.Regions[0];
        Assert.Equal(new[] { (region.StartLine, region.EndLine) }, view.MinimapForTesting.ResolvedRanges);
        Assert.Equal((1, 2), (region.StartLine, region.EndLine));
    }

    [AvaloniaFact]
    public async Task Excluded_rows_reject_typing()
    {
        var view = await LoadTwoConflictViewAsync();
        ClickHunk(view, ConflictSide.Ours, view.CurrentParseForTesting!.Regions[0]);
        var doc = view.ResultEditorForTesting.Document;
        var struck = doc.GetLineByNumber(LineOf(view, "theirs 1") + 1);
        var kept = doc.GetLineByNumber(LineOf(view, "ours 1") + 1);
        var provider = view.ResultEditorForTesting.TextArea.ReadOnlySectionProvider;

        Assert.Same(view.SessionForTesting.ReadOnlySections, provider);
        Assert.False(provider.CanInsert(struck.Offset));
        Assert.False(provider.CanInsert(struck.EndOffset));
        Assert.True(provider.CanInsert(kept.Offset + 1));
    }

    private static void AssertNoRegionPopup(ConflictResolverView view)
    {
        string[] popupLabels = ["Use ours", "Use theirs", "Both (ours first)", "Both (theirs first)", "Use base", "Restore conflict"];
        var labels = view.GetLogicalDescendants().OfType<Button>().Select(b => b.Content as string).Where(c => c != null).ToList();
        Assert.DoesNotContain(labels, l => popupLabels.Contains(l!));
    }

    [AvaloniaFact]
    public async Task No_region_popup_when_caret_in_conflict()
    {
        var (view, window) = await ShowViewAsync(TwoConflictText);
        try
        {
            view.ResultEditorForTesting.TextArea.Caret.Line = 3; // inside an unresolved block
            AssertNoRegionPopup(view);

            ClickHunk(view, ConflictSide.Ours, view.CurrentParseForTesting!.Regions[0]);
            view.ResultEditorForTesting.TextArea.Caret.Line = 3; // inside the managed block
            AssertNoRegionPopup(view);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Resolve_file_menu_keeps_its_items()
    {
        var view = await LoadTwoConflictViewAsync();

        Assert.Equal(
            ["Take ours (whole file)", "Take theirs (whole file)", "Reset file to conflicted", "Open in external tool"],
            view.ResolveFileMenuItemsForTesting.Select(m => m.Header as string));
    }

    [AvaloniaFact]
    public async Task Pane_dividers_are_four_px_border()
    {
        using var applier = new AvaloniaThemeApplier(new StubThemeService());
        var (view, window) = await ShowViewAsync(TwoConflictText);
        try
        {
            var border = HistoriaPalette.Get(EffectiveTheme.Dark, "border").ToAvalonia();

            Assert.Equal(4, view.PaneDividerForTesting.Width);
            Assert.Equal(border, Assert.IsAssignableFrom<ISolidColorBrush>(view.PaneDividerForTesting.Fill).Color);
            Assert.Equal(4, view.ResultDividerForTesting.Height);
            Assert.Equal(border, Assert.IsAssignableFrom<ISolidColorBrush>(view.ResultDividerForTesting.Fill).Color);
            Assert.Equal(4, view.ResultSplitterForTesting.Height);
            Assert.Equal(new GridLength(4), view.SplitGridForTesting.RowDefinitions[1].Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Result_gutter_tints_kept_and_dropped_rows()
    {
        var (view, window) = await ShowViewAsync(TwoConflictText);
        try
        {
            var margin = view.ResultNumberMarginForTesting;
            Assert.Equal(ConflictRowTint.None, margin.TintForRowForTesting(LineOf(view, "header")));
            Assert.Equal(ConflictRowTint.Add, margin.TintForRowForTesting(LineOf(view, "ours 2")));

            ClickHunk(view, ConflictSide.Ours, view.CurrentParseForTesting!.Regions[0]);
            int ours1 = LineOf(view, "ours 1");
            Assert.Equal(ConflictRowTint.Add, margin.TintForRowForTesting(ours1));
            Assert.Equal(ConflictRowTint.Del, margin.TintForRowForTesting(LineOf(view, "theirs 1")));

            Assert.True(view.SessionForTesting.ToggleRow(ours1));
            Assert.Equal(ConflictRowTint.Del, margin.TintForRowForTesting(ours1));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Puts the repository into a conflict session; a stash-style one when
    /// <paramref name="withOperation"/> is false (conflicted files, no operation).</summary>
    private async Task StartConflictSessionAsync(bool withOperation = true)
    {
        _fixture.CreateConflict();
        if (!withOperation)
        {
            _fixture.ClearOperation();
        }

        await RefreshUntilAsync(() => _context.State.IsConflictSessionActive, "the conflict session to start");
    }

    [AvaloniaFact]
    public async Task Close_button_marks_resolver_closed_without_saving()
    {
        await StartConflictSessionAsync();
        var view = await LoadTwoConflictViewAsync();
        Assert.True(view.CloseButtonForTesting.IsVisible);
        ClickHunk(view, ConflictSide.Ours, view.CurrentParseForTesting!.Regions[0]);
        Assert.True(view.HasUnsavedWork);
        Assert.False(_context.State.IsConflictResolverClosed);

        Assert.Same(view.ViewModel.CloseCommand, view.CloseButtonForTesting.Command);
        view.CloseButtonForTesting.Command!.Execute(null);

        Assert.True(_context.State.IsConflictResolverClosed);
        Assert.Empty(_conflicts.SaveResolutionCalls);
        Assert.Empty(_dialogs.ConfirmDestructiveCalls);
        Assert.True(view.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task Close_button_visible_without_an_operation()
    {
        await StartConflictSessionAsync(withOperation: false);
        var (view, window) = await ShowViewAsync(TwoConflictText);
        try
        {
            Assert.Equal(OperationState.None, _context.State.PendingOperation);
            Assert.False(view.OpBarForTesting.IsVisible);
            Assert.True(view.CloseButtonForTesting.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Close_button_hidden_once_the_session_has_ended()
    {
        await StartConflictSessionAsync();
        var view = await LoadTwoConflictViewAsync();
        ClickHunk(view, ConflictSide.Ours, view.CurrentParseForTesting!.Regions[0]);
        Assert.True(view.CloseButtonForTesting.IsVisible);

        // The merge ends outside Historia while the resolver still holds edits.
        _fixture.MarkResolved();
        _fixture.ClearOperation();
        await RefreshUntilAsync(() => !_context.State.IsConflictSessionActive, "the conflict session to end");

        Assert.True(view.HasUnsavedWork);
        Assert.False(view.CloseButtonForTesting.IsVisible);
    }

    #endregion

    #region Click-to-show changes panel

    [AvaloniaFact]
    public async Task Press_on_result_from_home_route_navigates_to_repo()
    {
        _nav.ReportRoute("");
        var requested = new List<string>();
        _nav.NavigationRequested += requested.Add;

        var view = await LoadTwoConflictViewAsync();
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        try
        {
            var editor = view.ResultEditorForTesting;
            var point = editor.TranslatePoint(new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Single(requested, "repo");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Press_when_already_on_repo_route_does_not_renavigate()
    {
        _nav.ReportRoute("repo");
        var requested = new List<string>();
        _nav.NavigationRequested += requested.Add;

        var view = await LoadTwoConflictViewAsync();
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        try
        {
            var editor = view.ResultEditorForTesting;
            var point = editor.TranslatePoint(new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(requested);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Press_with_commit_selected_clears_selection()
    {
        _nav.ReportRoute("repo");
        var commitId = new GitObjectId("1234567890abcdef1234567890abcdef12345678");
        _context.State.SetSelectedCommit(commitId);
        Assert.NotNull(_context.State.SelectedCommitId);

        var view = await LoadTwoConflictViewAsync();
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        try
        {
            var editor = view.ResultEditorForTesting;
            var point = editor.TranslatePoint(new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(_context.State.SelectedCommitId);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Press_on_toolbar_button_still_runs_button_and_shows_panel()
    {
        _nav.ReportRoute("");
        var requested = new List<string>();
        _nav.NavigationRequested += requested.Add;

        var view = await LoadTwoConflictViewAsync();
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        try
        {
            var btn = view.NextConflictButtonForTesting;
            var point = btn.TranslatePoint(new Point(btn.Bounds.Width / 2, btn.Bounds.Height / 2), window)!.Value;
            int initialLine = view.ResultEditorForTesting.TextArea.Caret.Line;

            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Single(requested, "repo");
            Assert.NotEqual(initialLine, view.ResultEditorForTesting.TextArea.Caret.Line);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Press_on_hunk_toggle_still_toggles()
    {
        var (view, window) = await ShowViewAsync(TwoConflictText);
        try
        {
            _nav.ReportRoute("");
            var requested = new List<string>();
            _nav.NavigationRequested += requested.Add;

            var r0 = view.CurrentParseForTesting!.Regions[0];
            Assert.Equal(ConflictToggleGlyph.Unchecked, view.HunkGlyphForTesting(ConflictSide.Theirs, 2));

            var editor = view.TheirsEditorForTesting;
            var margin = view.TheirsToggleMarginForTesting;
            var textView = editor.TextArea.TextView;
            textView.EnsureVisualLines();
            double y = textView.GetVisualTopByDocumentLine(2 + 1) - textView.VerticalOffset + textView.DefaultLineHeight / 2;
            var point = margin.TranslatePoint(new Point(margin.Bounds.Width / 2, y), window)!.Value;

            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Single(requested, "repo");
            Assert.NotNull(view.SessionForTesting.RegionAt(r0.OursStartLine));
        }
        finally
        {
            window.Close();
        }
    }

    #endregion

    #region Deletion rows in the resolver

    private const string GuideConflictText =
        "Introduction\n<<<<<<< HEAD\n=======\nStatus: Staging Preview v1.1\n>>>>>>> 209e2e8\nMaintainer: team\n";

    [AvaloniaFact]
    public async Task Empty_ours_conflict_shows_deletion_row_with_box()
    {
        var (view, window) = await ShowViewAsync(GuideConflictText);
        try
        {
            var region = Assert.Single(view.CurrentParseForTesting!.Regions);
            Assert.Equal(0, region.OursLineCount);
            Assert.Equal(1, region.TheirsLineCount);

            int row = region.StartLine + 1;
            var oursLine = view.SnapshotForTesting!.OursLines[row];
            Assert.Equal(ConflictPaneLineType.Deletion, oursLine.Type);
            Assert.Equal(ConflictToggleGlyph.Unchecked, view.HunkGlyphForTesting(ConflictSide.Ours, row));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Checking_deletion_box_activates_region_with_deletion_row()
    {
        var (view, window) = await ShowViewAsync(GuideConflictText);
        try
        {
            var r0 = Assert.Single(view.CurrentParseForTesting!.Regions);
            int row = r0.StartLine + 1;

            view.ClickHunkForTesting(ConflictSide.Ours, row);

            Assert.Empty(view.CurrentParseForTesting!.Regions);
            var managedRegion = Assert.Single(view.SessionForTesting.Regions);
            Assert.True(managedRegion.Selection.IsDeletion(ConflictSide.Ours));
            Assert.Equal(ConflictHunkState.Checked, managedRegion.Selection.HunkState(ConflictSide.Ours));
            Assert.Equal(ConflictHunkState.Unchecked, managedRegion.Selection.HunkState(ConflictSide.Theirs));

            Assert.True(view.SessionForTesting.IsDeletionRow(1));
            Assert.False(view.SessionForTesting.IsExcluded(1));
            Assert.True(view.SessionForTesting.IsExcluded(2));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Deletion_and_theirs_checked_saves_only_theirs()
    {
        var (view, window) = await ShowViewAsync(GuideConflictText);
        try
        {
            var r0 = Assert.Single(view.CurrentParseForTesting!.Regions);
            int row = r0.StartLine + 1;

            view.ClickHunkForTesting(ConflictSide.Ours, row);
            view.ClickHunkForTesting(ConflictSide.Theirs, view.SessionForTesting.Regions[0].StartLine);

            Assert.Equal(ConflictHunkState.Checked, view.SessionForTesting.Regions[0].Selection.HunkState(ConflictSide.Ours));
            Assert.Equal(ConflictHunkState.Checked, view.SessionForTesting.Regions[0].Selection.HunkState(ConflictSide.Theirs));
            Assert.Equal(
                "Introduction\nStatus: Staging Preview v1.1\nMaintainer: team\n",
                view.ComposedTextForTesting);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Deletion_alone_mark_resolved_does_not_prompt_emptied()
    {
        var (view, window) = await ShowViewAsync(GuideConflictText);
        try
        {
            var r0 = Assert.Single(view.CurrentParseForTesting!.Regions);
            int row = r0.StartLine + 1;

            view.ClickHunkForTesting(ConflictSide.Ours, row);

            Assert.Empty(view.CurrentParseForTesting!.Regions);
            Assert.Equal(0, view.SessionForTesting.EmptyRegionCount);

            await view.SaveAndMarkResolvedAsync();

            Assert.Empty(_dialogs.ConfirmCalls);
            var call = Assert.Single(_conflicts.SaveResolutionCalls);
            Assert.Equal("Introduction\nMaintainer: team\n", call.Text);
            Assert.True(call.Stage);
        }
        finally
        {
            window.Close();
        }
    }

    #endregion
}
