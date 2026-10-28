namespace Historia.Tests;

/// <summary>
/// The app band's view model: one tab per open repository with the focused one active, the linked
/// tab groups and their separators, the pending clone tab and its lifecycle, reordering (a group
/// moves as one unit, a linked tab stays inside its group, the active tab stays active) and the
/// drag completion that saves the order through the workspace, the tab commands, the developer
/// auto-fetch bars and the Historia menu.
/// </summary>
[Trait("tier", "git")]
public sealed class AppBandViewModelTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];
    private readonly List<RepositoryContext> _contexts = [];
    private readonly List<IDisposable> _disposables = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
            disposable.Dispose();
        foreach (var context in _contexts)
            await context.DisposeAsync();
        foreach (var repo in _repos)
            repo.Dispose();
        await _host.DisposeAsync();
    }

    private RepositoryContext OpenRepo()
    {
        var repo = TestRepositoryBuilder.FromTemplate("appband", b => b.WriteFile("f.txt", "base\n").Stage().Commit("c1"));
        _repos.Add(repo);
        var context = _host.Open(repo.Path);
        _contexts.Add(context);
        return context;
    }

    private static TerminalHostEnvironment Environment() => new()
    {
        Platform = OSPlatformKind.Linux,
        FileExists = p => p == "/bin/bash",
        DirectoryExists = _ => true,
        GetEnvironmentVariable = _ => null,
        FindOnPath = _ => null,
        GetPasswdShell = () => "/bin/bash",
        GetInheritedEnvironment = () => new Dictionary<string, string> { ["PATH"] = "/usr/bin" },
        HomeDirectory = "/home/me",
        CurrentDirectory = "/tmp",
        ExecutablePath = "/opt/historia/Historia",
        Version = "5.1.0",
    };

    private sealed class FakeWorkspace : IWorkspaceService
    {
        private List<RepositoryContext> _open = [];

        public int OpenSubscribers => OpenRepositoriesChanged?.GetInvocationList().Length ?? 0;
        public int FocusSubscribers => FocusedRepositoryChanged?.GetInvocationList().Length ?? 0;
        public List<(RepositoryContext Context, int Index)> Moves { get; } = [];
        public List<RepositoryContext> Closed { get; } = [];
        public RepositoryContext? Focused { get; private set; }
        public IReadOnlyList<RepositoryContext> OpenRepositories => _open;

        public event EventHandler<FocusedRepositoryChangedEventArgs>? FocusedRepositoryChanged;
        public event EventHandler<OpenRepositoriesChangedEventArgs>? OpenRepositoriesChanged;

        public void Set(RepositoryContext? focused, params RepositoryContext[] open)
        {
            _open = [.. open];
            Focused = focused;
        }

        public void RaiseOpenChanged() => OpenRepositoriesChanged?.Invoke(this, new OpenRepositoriesChangedEventArgs(_open));

        public void Focus(RepositoryContext context)
        {
            var old = Focused;
            Focused = context;
            FocusedRepositoryChanged?.Invoke(this, new FocusedRepositoryChangedEventArgs(old, context));
        }

        public void Move(RepositoryContext context, int newIndex)
        {
            Moves.Add((context, newIndex));
            var group = context.LinkOwner is null
                ? _open.Where(c => ReferenceEquals(c, context) || ReferenceEquals(c.LinkOwner, context)).ToList()
                : [context];
            _open.RemoveAll(group.Contains);
            _open.InsertRange(Math.Clamp(newIndex, 0, _open.Count), group);
            RaiseOpenChanged();
        }

        /// <summary>When set, a close raises its events and then waits on this task, like the real
        /// workspace waiting for a running write before it disposes the context.</summary>
        public Task? DisposeGate { get; set; }

        public async Task CloseAsync(RepositoryContext context)
        {
            Closed.Add(context);
            _open.Remove(context);
            var wasFocused = ReferenceEquals(Focused, context);
            if (wasFocused)
                Focused = _open.FirstOrDefault();
            RaiseOpenChanged();
            if (wasFocused)
                FocusedRepositoryChanged?.Invoke(this, new FocusedRepositoryChangedEventArgs(context, Focused));
            if (DisposeGate is not null)
                await DisposeGate;
        }

        public Task<RepositoryContext> OpenAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RepositoryContext> InitAsync(string path, string? initialBranch, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RepositoryContext> CloneAsync(string url, string targetDirectory, Historia.Services.Workspace.CloneOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RepositoryContext> OpenSubmoduleAsync(RepositoryContext owner, SubmoduleInfo submodule, CancellationToken ct = default) => throw new NotSupportedException();
        public void EnsureInitialized(RepositoryContext context) { }
        public Task RestoreSessionAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeAutoFetch : IAutoFetchService
    {
        public Dictionary<RepositoryContext, AutoFetchTabState> States { get; } = [];
        public int Subscribers => TabStatesChanged?.GetInvocationList().Length ?? 0;
        public bool IsEnabled => true;
        public event EventHandler? TabStatesChanged;
        public void RaiseChanged() => TabStatesChanged?.Invoke(this, EventArgs.Empty);
        public void Start() { }
        public void Stop() { }
        public AutoFetchTabState GetTabState(RepositoryContext context) => States.GetValueOrDefault(context);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubClone : ICloneService
    {
        public CloneStatus? Current { get; set; }
        public bool IsRunning => Current is not null;
        public int Cancels { get; private set; }
        public int Subscribers => Changed?.GetInvocationList().Length ?? 0;
        public event EventHandler? Changed;
        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
        public Task StartAsync(CloneRequest request) => Task.CompletedTask;
        public void Cancel() => Cancels++;
    }

    private sealed class RecordingLauncher : IExternalLauncher
    {
        public List<string> Folders { get; } = [];
        public void OpenUrl(string url) { }
        public void RevealInFileManager(string path) { }
        public void OpenFolder(string path) => Folders.Add(path);
    }

    private sealed class FixedScrollProbe(bool natural) : IScrollDirectionProbe
    {
        public bool NaturalScrolling => natural;
        public void Refresh() { }
    }

    private sealed class FakePlatform : IAppBandPlatform
    {
        public bool DeveloperMenuEnabled { get; set; }
        public bool AutoFetchBarsVisible { get; set; }
        public int AboutCalls { get; private set; }
        public int Exits { get; private set; }
        public int Subscribers => AutoFetchBarsVisibleChanged?.GetInvocationList().Length ?? 0;
        public event Action? AutoFetchBarsVisibleChanged;
        public void RaiseVisibleChanged() => AutoFetchBarsVisibleChanged?.Invoke();
        public void ShowAbout() => AboutCalls++;
        public void Exit() => Exits++;
    }

    private sealed class FakeWork(RepositoryContext context, bool saves) : IUnsavedWork
    {
        public RepositoryContext Context => context;
        public string Description => "notes.txt";
        public bool Saved { get; private set; }
        public bool Discarded { get; private set; }

        public Task<bool> SaveAsync()
        {
            Saved = saves;
            return Task.FromResult(saves);
        }

        public void Discard() => Discarded = true;
    }

    private sealed class Rig : IDisposable
    {
        private readonly StateDir _state = new();
        private readonly ServiceProvider _sp;

        public Rig(IUiDispatcher? ui = null, AppSettings? settings = null, bool natural = false)
        {
            Workspace = new FakeWorkspace();
            AutoFetch = new FakeAutoFetch();
            Clone = new StubClone();
            Launcher = new RecordingLauncher();
            Settings = new FakeSettingsService(settings);
            Navigation = new NavigationBus();
            Clipboard = new FakeClipboardService();
            Dialogs = new FakeDialogService();
            Notifications = new FakeNotificationService();
            Platform = new FakePlatform();
            Registry = new UnsavedWorkRegistry();
            Backend = new FakeTerminalBackend();
            Terminals = new TerminalSessionService(Backend, Settings, new ImmediateUiDispatcher(), Environment());
            Profiles = new ProfileService(Settings, new FakeVault(), new NoGitCli());
            var services = new ServiceCollection();
            services.AddSingleton<IProfileService>(Profiles);
            services.AddSingleton<IDialogService>(Dialogs);
            services.AddSingleton<INotificationService>(Notifications);
            _sp = services.BuildServiceProvider();
            Ui = ui ?? new ImmediateUiDispatcher();
            ScrollProbe = new FixedScrollProbe(natural);
        }

        public FakeWorkspace Workspace { get; }
        public FakeAutoFetch AutoFetch { get; }
        public StubClone Clone { get; }
        public RecordingLauncher Launcher { get; }
        public FakeSettingsService Settings { get; }
        public NavigationBus Navigation { get; }
        public FakeClipboardService Clipboard { get; }
        public FakeDialogService Dialogs { get; }
        public FakeNotificationService Notifications { get; }
        public FakePlatform Platform { get; }
        public UnsavedWorkRegistry Registry { get; }
        public FakeTerminalBackend Backend { get; }
        public TerminalSessionService Terminals { get; }
        public ProfileService Profiles { get; }
        public IUiDispatcher Ui { get; }
        public IScrollDirectionProbe ScrollProbe { get; }
        public List<string> Routes { get; } = [];

        public AppBandViewModel Create()
        {
            Navigation.NavigationRequested += Routes.Add;
            return new AppBandViewModel(
                Ui, Workspace, AutoFetch, Terminals, Navigation, Profiles, Settings, Launcher, ScrollProbe, Clone,
                _sp, Registry, Dialogs, Clipboard, Platform, "/home/me");
        }

        public void Dispose()
        {
            _sp.Dispose();
            _state.Dispose();
        }
    }

    private Rig NewRig(IUiDispatcher? ui = null, AppSettings? settings = null, bool natural = false)
    {
        var rig = new Rig(ui, settings, natural);
        _disposables.Add(rig);
        return rig;
    }

    private static List<RepoTabViewModel> RepoTabs(AppBandViewModel vm) => [.. vm.Tabs.OfType<RepoTabViewModel>()];

    private static RepositoryContext[] Order(AppBandViewModel vm) => [.. RepoTabs(vm).Select(t => t.Context)];

    // Four tabs: an owner with one linked child, then two standalone tabs.
    private (RepositoryContext Owner, RepositoryContext Child, RepositoryContext B, RepositoryContext C) Group(Rig rig)
    {
        var owner = OpenRepo();
        var child = OpenRepo();
        child.LinkOwner = owner;
        var b = OpenRepo();
        var c = OpenRepo();
        rig.Workspace.Set(owner, owner, child, b, c);
        return (owner, child, b, c);
    }

    // ── tabs ─────────────────────────────────────────────────────────────────

    [Fact]
    public void The_strip_starts_empty_without_open_repositories()
    {
        var rig = NewRig();
        using var vm = rig.Create();

        Assert.Empty(vm.Tabs);
        Assert.Same(vm.AddTab, Assert.Single(vm.StripItems));
        Assert.False(vm.IsDragging);
    }

    [Fact]
    public void There_is_one_tab_per_open_repository_and_only_the_focused_one_is_active()
    {
        var rig = NewRig();
        var a = OpenRepo();
        var b = OpenRepo();
        rig.Workspace.Set(b, a, b);

        using var vm = rig.Create();

        Assert.Equal([a, b], Order(vm));
        Assert.Equal([false, true], RepoTabs(vm).Select(t => t.IsActive));
        Assert.Equal(a.DisplayName, RepoTabs(vm)[0].Title);
        Assert.Equal(a.RepositoryPath, RepoTabs(vm)[0].RepositoryPath);
    }

    [Fact]
    public void The_strip_follows_the_open_set_and_the_focus_and_is_replaced_wholesale()
    {
        var rig = NewRig();
        var a = OpenRepo();
        var b = OpenRepo();
        rig.Workspace.Set(a, a);
        using var vm = rig.Create();
        var before = vm.Tabs;
        var rebuilds = 0;
        vm.TabsRebuilt += (_, _) => rebuilds++;

        rig.Workspace.Set(a, a, b);
        rig.Workspace.RaiseOpenChanged();

        Assert.NotSame(before, vm.Tabs);
        Assert.Equal([a, b], Order(vm));
        Assert.Equal(1, rebuilds);

        rig.Workspace.Focus(b);

        Assert.Equal([false, true], RepoTabs(vm).Select(t => t.IsActive));
        Assert.Equal(2, rebuilds);
    }

    [Fact]
    public void The_first_tab_and_every_linked_tab_have_no_leading_separator()
    {
        var rig = NewRig();
        Group(rig);
        using var vm = rig.Create();

        Assert.Equal([false, false, true, true], vm.Tabs.Select(t => t.ShowLeadingSeparator));
        Assert.Equal([false, true, false, false], RepoTabs(vm).Select(t => t.IsLinked));
    }

    [Fact]
    public void The_events_post_through_the_dispatcher_and_a_disposed_view_model_ignores_them()
    {
        var ui = new RecordingUiDispatcher();
        var rig = NewRig(ui);
        var a = OpenRepo();
        var b = OpenRepo();
        rig.Workspace.Set(a, a);
        var vm = rig.Create();

        rig.Workspace.Set(a, a, b);
        rig.Workspace.RaiseOpenChanged();
        Assert.Single(vm.Tabs);

        ui.DrainPosted();
        Assert.Equal(2, vm.Tabs.Count);

        rig.Workspace.Focus(b);
        vm.Dispose();
        ui.DrainPosted();

        Assert.True(RepoTabs(vm)[0].IsActive);
    }

    [Fact]
    public void Disposing_unsubscribes_from_every_service()
    {
        var rig = NewRig();
        var vm = rig.Create();
        Assert.Equal(1, rig.Workspace.OpenSubscribers);
        Assert.Equal(1, rig.Workspace.FocusSubscribers);
        Assert.Equal(1, rig.AutoFetch.Subscribers);
        Assert.Equal(1, rig.Clone.Subscribers);
        Assert.Equal(1, rig.Platform.Subscribers);

        vm.Dispose();

        Assert.Equal(0, rig.Workspace.OpenSubscribers);
        Assert.Equal(0, rig.Workspace.FocusSubscribers);
        Assert.Equal(0, rig.AutoFetch.Subscribers);
        Assert.Equal(0, rig.Clone.Subscribers);
        Assert.Equal(0, rig.Platform.Subscribers);
    }

    [Fact]
    public void The_strip_ends_with_the_add_button_after_every_tab_including_the_pending_clone_tab()
    {
        var rig = NewRig();
        var (owner, child, b, c) = Group(rig);
        using var vm = rig.Create();

        Assert.Equal(5, vm.StripItems.Count);
        Assert.Equal<object>([.. vm.Tabs, vm.AddTab], vm.StripItems);

        rig.Clone.Current = new CloneStatus("https://example.com/r.git", "/tmp/my-repo", null, false);
        rig.Clone.RaiseChanged();
        Assert.IsType<CloneTabViewModel>(vm.StripItems[4]);
        Assert.Same(vm.AddTab, vm.StripItems[^1]);

        vm.MoveTab(0, 2);
        Assert.Equal([b, c, owner, child], Order(vm));
        Assert.Same(vm.AddTab, vm.StripItems[^1]);
        Assert.Equal(6, vm.StripItems.Count);
    }

    [Fact]
    public void The_add_button_requests_the_open_clone_and_initialize_intents()
    {
        var rig = NewRig();
        using var vm = rig.Create();

        vm.AddTab.OpenCommand.Execute(null);
        vm.AddTab.CloneCommand.Execute(null);
        vm.AddTab.InitializeCommand.Execute(null);

        Assert.Equal(["?intent=open", "?intent=clone", "?intent=init"], rig.Routes);
    }

    // ── tab width settings ───────────────────────────────────────────────────

    [Fact]
    public void Dynamic_width_tabs_size_to_their_name_up_to_a_cap()
    {
        var rig = NewRig();
        var a = OpenRepo();
        rig.Workspace.Set(a, a);

        using var vm = rig.Create();

        var tab = Assert.Single(RepoTabs(vm));
        Assert.True(double.IsNaN(tab.BodyWidth));
        Assert.Equal(AppBandViewModel.TabNameMaxChars * AppBandViewModel.ApproxCharPx, tab.NameMaxWidth);
        Assert.False(vm.IsFixedTabWidth);
    }

    [Fact]
    public async Task A_settings_change_rereads_the_tab_settings_and_rebuilds_the_strip()
    {
        var rig = NewRig();
        var a = OpenRepo();
        rig.Workspace.Set(a, a);
        using var vm = rig.Create();
        var changed = 0;
        vm.TabSettingsChanged += (_, _) => changed++;
        var before = vm.Tabs;

        await rig.Settings.UpdateAsync(s => s with { TabWidthMode = TabWidthMode.Fixed, FixedTabWidth = 200, EnableMultiLineTabs = true, MaxTabRows = 4 });

        Assert.Equal(1, changed);
        Assert.NotSame(before, vm.Tabs);
        Assert.True(vm.IsFixedTabWidth);
        Assert.True(vm.IsMultiLine);
        Assert.Equal(4, vm.MaxRows);
        var tab = Assert.Single(RepoTabs(vm));
        Assert.Equal(AppSettings.ClampFixedTabWidth(200), tab.BodyWidth);
        Assert.True(double.IsPositiveInfinity(tab.NameMaxWidth));
    }

    [Theory]
    [InlineData(WheelScrollDirection.Standard, true, 1)]
    [InlineData(WheelScrollDirection.Inverted, false, -1)]
    [InlineData(WheelScrollDirection.Auto, true, -1)]
    [InlineData(WheelScrollDirection.Auto, false, 1)]
    public void The_wheel_sign_follows_the_setting_and_the_system_preference(WheelScrollDirection setting, bool natural, double expected)
    {
        var rig = NewRig(settings: new AppSettings { WheelScrollDirection = setting }, natural: natural);
        using var vm = rig.Create();

        Assert.Equal(expected, vm.WheelSign());
    }

    // ── reordering ───────────────────────────────────────────────────────────

    [Fact]
    public void Moving_an_owner_moves_its_linked_tabs_with_it_as_one_unit()
    {
        var rig = NewRig();
        var (owner, child, b, c) = Group(rig);
        using var vm = rig.Create();

        Assert.True(vm.MoveTab(0, 2));

        Assert.Equal([b, c, owner, child], Order(vm));
        Assert.Equal([false, true, true, false], vm.Tabs.Select(t => t.ShowLeadingSeparator));
    }

    [Fact]
    public void Moving_a_standalone_tab_in_front_of_a_group_keeps_the_group_together()
    {
        var rig = NewRig();
        var (owner, child, b, c) = Group(rig);
        using var vm = rig.Create();

        Assert.True(vm.MoveTab(3, 0));

        Assert.Equal([c, owner, child, b], Order(vm));
        Assert.Equal([false, true, false, true], vm.Tabs.Select(t => t.ShowLeadingSeparator));
    }

    [Fact]
    public void A_group_cannot_land_inside_another_group_or_past_the_end()
    {
        var rig = NewRig();
        var (owner, child, b, c) = Group(rig);
        using var vm = rig.Create();

        Assert.True(vm.MoveTab(2, 99));

        Assert.Equal([owner, child, c, b], Order(vm));
    }

    [Fact]
    public void Moving_a_tab_to_its_own_slot_changes_nothing()
    {
        var rig = NewRig();
        var (owner, child, b, c) = Group(rig);
        using var vm = rig.Create();
        var before = vm.Tabs;

        Assert.False(vm.MoveTab(0, 0));
        Assert.False(vm.MoveTab(2, 2));
        Assert.False(vm.MoveTab(9, 0));
        Assert.False(vm.MoveTab(-1, 0));

        Assert.Same(before, vm.Tabs);
        Assert.Equal([owner, child, b, c], Order(vm));
    }

    [Fact]
    public void A_linked_tab_only_moves_among_the_members_of_its_own_group()
    {
        var rig = NewRig();
        var owner = OpenRepo();
        var first = OpenRepo();
        var second = OpenRepo();
        var b = OpenRepo();
        first.LinkOwner = owner;
        second.LinkOwner = owner;
        rig.Workspace.Set(owner, owner, first, second, b);
        using var vm = rig.Create();

        // Out of its group on either side clamps to the nearest sibling slot.
        Assert.True(vm.MoveTab(1, 3));
        Assert.Equal([owner, second, first, b], Order(vm));
        Assert.True(vm.MoveTab(2, 0));
        Assert.Equal([owner, first, second, b], Order(vm));
        Assert.False(vm.MoveTab(1, 0));
    }

    [Fact]
    public void A_linked_tab_with_no_siblings_has_no_legal_slot()
    {
        var rig = NewRig();
        var (owner, child, b, c) = Group(rig);
        using var vm = rig.Create();

        Assert.False(vm.MoveTab(1, 3));
        Assert.False(vm.MoveTab(1, 0));

        Assert.Equal([owner, child, b, c], Order(vm));
    }

    [Fact]
    public void Moving_tabs_keeps_the_active_tab_active()
    {
        var rig = NewRig();
        var (owner, child, b, c) = Group(rig);
        rig.Workspace.Set(b, owner, child, b, c);
        using var vm = rig.Create();

        vm.MoveTab(2, 0);
        vm.MoveTab(1, 3);

        var active = Assert.Single(RepoTabs(vm), t => t.IsActive);
        Assert.Same(b, active.Context);
        Assert.NotSame(owner, active.Context);
        Assert.NotSame(c, active.Context);
    }

    [Fact]
    public void A_moved_tab_never_passes_the_pending_clone_tab()
    {
        var rig = NewRig();
        var a = OpenRepo();
        var b = OpenRepo();
        rig.Workspace.Set(a, a, b);
        rig.Clone.Current = new CloneStatus("https://example.com/r.git", "/tmp/elsewhere", null, false);
        using var vm = rig.Create();
        Assert.IsType<CloneTabViewModel>(vm.Tabs[^1]);

        Assert.True(vm.MoveTab(0, 99));

        Assert.Equal([b, a], Order(vm));
        Assert.IsType<CloneTabViewModel>(vm.Tabs[^1]);
        Assert.False(vm.MoveTab(2, 0));
    }

    [Fact]
    public void The_drag_preview_moves_the_dragged_tab_to_the_slot_under_the_cursor()
    {
        var rig = NewRig();
        var a = OpenRepo();
        var b = OpenRepo();
        var c = OpenRepo();
        rig.Workspace.Set(a, a, b, c);
        using var vm = rig.Create();
        double[] widths = [100, 100, 100];

        Assert.True(vm.BeginDrag(vm.Tabs[0]));
        Assert.True(vm.IsDragging);
        Assert.True(vm.Tabs[0].IsDragging);

        // Past the first tab's mid-point (50) but before the second's (150).
        Assert.True(vm.ReorderTo(100, widths, 186));
        Assert.Equal([b, a, c], Order(vm));
        Assert.False(vm.ReorderTo(100, widths, 186));

        Assert.True(vm.ReorderTo(280, widths, 186));
        Assert.Equal([b, c, a], Order(vm));
        Assert.True(RepoTabs(vm)[2].IsDragging);

        Assert.True(vm.ReorderTo(10, widths, 186));
        Assert.Equal([a, b, c], Order(vm));
    }

    [Fact]
    public void The_drag_preview_moves_a_whole_group_by_its_summed_width()
    {
        var rig = NewRig();
        var (owner, child, b, c) = Group(rig);
        using var vm = rig.Create();
        // The group (owner + child) is 200 wide, so its mid-point is 100; b spans 200-300 with its mid-point at 250.
        double[] widths = [100, 100, 100, 100];

        vm.BeginDrag(vm.Tabs[0]);

        Assert.False(vm.ReorderTo(90, widths, 186));
        Assert.True(vm.ReorderTo(200, widths, 186));
        Assert.Equal([b, owner, child, c], Order(vm));
    }

    [Fact]
    public void A_zero_width_counts_as_the_fallback_width()
    {
        var rig = NewRig();
        var a = OpenRepo();
        var b = OpenRepo();
        rig.Workspace.Set(a, a, b);
        using var vm = rig.Create();

        vm.BeginDrag(vm.Tabs[0]);

        // Unmeasured tabs are 100 wide each, so 160 is past the first mid-point (50) and the second's (150).
        Assert.True(vm.ReorderTo(160, [0, 0], 100));
        Assert.Equal([b, a], Order(vm));
    }

    [Fact]
    public void A_linked_tab_drag_stays_inside_its_group()
    {
        var rig = NewRig();
        var owner = OpenRepo();
        var first = OpenRepo();
        var second = OpenRepo();
        var b = OpenRepo();
        first.LinkOwner = owner;
        second.LinkOwner = owner;
        rig.Workspace.Set(owner, owner, first, second, b);
        using var vm = rig.Create();
        double[] widths = [100, 100, 100, 100];

        vm.BeginDrag(vm.Tabs[1]);

        Assert.True(vm.ReorderTo(380, widths, 186));
        Assert.Equal([owner, second, first, b], Order(vm));
        Assert.True(vm.ReorderTo(5, widths, 186));
        Assert.Equal([owner, first, second, b], Order(vm));
    }

    [Fact]
    public void A_pending_clone_tab_cannot_be_dragged()
    {
        var rig = NewRig();
        var a = OpenRepo();
        rig.Workspace.Set(a, a);
        rig.Clone.Current = new CloneStatus("https://example.com/r.git", "/tmp/elsewhere", null, false);
        using var vm = rig.Create();

        Assert.False(vm.BeginDrag(vm.Tabs[^1]));
        Assert.False(vm.IsDragging);
        Assert.False(vm.ReorderTo(0, [100, 100], 100));
    }

    [Fact]
    public void A_tab_from_an_older_strip_cannot_start_a_drag()
    {
        var rig = NewRig();
        var a = OpenRepo();
        rig.Workspace.Set(a, a);
        using var vm = rig.Create();
        var stale = vm.Tabs[0];
        rig.Workspace.RaiseOpenChanged();

        Assert.False(vm.BeginDrag(stale));
        Assert.True(vm.BeginDrag(vm.Tabs[0]));
        Assert.False(vm.BeginDrag(vm.Tabs[0]));
    }

    [Fact]
    public void The_strip_is_not_rebuilt_while_a_tab_is_dragged()
    {
        var rig = NewRig();
        var (owner, child, b, c) = Group(rig);
        using var vm = rig.Create();
        vm.BeginDrag(vm.Tabs[2]);
        var during = vm.Tabs;

        rig.Workspace.Set(owner, c, b);
        rig.Workspace.RaiseOpenChanged();
        rig.Workspace.Focus(b);

        Assert.Same(during, vm.Tabs);
        Assert.Equal([owner, child, b, c], Order(vm));
    }

    [Fact]
    public void Completing_a_drag_saves_the_new_position_through_the_workspace()
    {
        var rig = NewRig();
        var (owner, child, b, c) = Group(rig);
        using var vm = rig.Create();
        vm.BeginDrag(vm.Tabs[0]);
        vm.MoveTab(0, 2);

        vm.CompleteDrag();

        var move = Assert.Single(rig.Workspace.Moves);
        Assert.Same(owner, move.Context);
        Assert.Equal(2, move.Index);
        Assert.False(vm.IsDragging);
        Assert.All(vm.Tabs, t => Assert.False(t.IsDragging));
        Assert.Equal([b, c, owner, child], Order(vm));
    }

    [Fact]
    public void Dropping_a_tab_back_in_its_slot_rebuilds_without_moving_anything()
    {
        var rig = NewRig();
        var (owner, child, b, c) = Group(rig);
        using var vm = rig.Create();
        vm.BeginDrag(vm.Tabs[2]);
        vm.MoveTab(2, 3);
        vm.MoveTab(3, 2);
        var during = vm.Tabs;

        vm.CompleteDrag();

        Assert.Empty(rig.Workspace.Moves);
        Assert.NotSame(during, vm.Tabs);
        Assert.Equal([owner, child, b, c], Order(vm));
        Assert.False(vm.IsDragging);
    }

    [Fact]
    public void Cancelling_a_drag_restores_the_workspace_order()
    {
        var rig = NewRig();
        var (owner, child, b, c) = Group(rig);
        using var vm = rig.Create();
        vm.BeginDrag(vm.Tabs[0]);
        vm.MoveTab(0, 2);
        Assert.Equal([b, c, owner, child], Order(vm));

        vm.CancelDrag();

        Assert.Empty(rig.Workspace.Moves);
        Assert.False(vm.IsDragging);
        Assert.Equal([owner, child, b, c], Order(vm));
        Assert.All(vm.Tabs, t => Assert.False(t.IsDragging));
    }

    [Fact]
    public void A_press_that_never_becomes_a_drag_can_be_cancelled_and_rebuilds_the_strip()
    {
        var rig = NewRig();
        var a = OpenRepo();
        rig.Workspace.Set(a, a);
        using var vm = rig.Create();
        var before = vm.Tabs;

        vm.CancelDrag();

        Assert.NotSame(before, vm.Tabs);
    }

    // ── clone tab ────────────────────────────────────────────────────────────

    [Fact]
    public void A_running_clone_adds_a_pending_tab_after_the_repositories()
    {
        var rig = NewRig();
        var a = OpenRepo();
        rig.Workspace.Set(a, a);
        using var vm = rig.Create();
        Assert.Single(vm.Tabs);

        rig.Clone.Current = new CloneStatus("https://example.com/r.git", "/tmp/my-repo/", null, false);
        rig.Clone.RaiseChanged();

        Assert.Equal(2, vm.Tabs.Count);
        var pending = Assert.IsType<CloneTabViewModel>(vm.Tabs[1]);
        Assert.Equal("my-repo", pending.Name);
        Assert.Equal("/tmp/my-repo/", pending.TargetDirectory);
        Assert.Equal("Starting clone…", pending.StatusText);
        Assert.True(pending.CanCancel);
        Assert.True(pending.ShowLeadingSeparator);
    }

    [Fact]
    public void A_progress_tick_updates_the_pending_tab_in_place_without_rebuilding_the_strip()
    {
        var rig = NewRig();
        var a = OpenRepo();
        rig.Workspace.Set(a, a);
        rig.Clone.Current = new CloneStatus("https://example.com/r.git", "/tmp/my-repo", null, false);
        using var vm = rig.Create();
        var tabs = vm.Tabs;
        var pending = (CloneTabViewModel)tabs[1];
        var rebuilds = 0;
        vm.TabsRebuilt += (_, _) => rebuilds++;
        var changed = new List<string?>();
        pending.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        rig.Clone.Current = rig.Clone.Current with { Progress = new GitProgress { Phase = "Receiving objects", Percent = 42 } };
        rig.Clone.RaiseChanged();

        Assert.Same(tabs, vm.Tabs);
        Assert.Same(pending, vm.Tabs[1]);
        Assert.Equal(0, rebuilds);
        Assert.Equal(rig.Clone.Current.Describe(), pending.StatusText);
        Assert.Contains(nameof(CloneTabViewModel.StatusText), changed);
    }

    [Fact]
    public void A_cancel_request_disables_the_pending_tabs_button_and_the_button_cancels_the_clone()
    {
        var rig = NewRig();
        var a = OpenRepo();
        rig.Workspace.Set(a, a);
        rig.Clone.Current = new CloneStatus("https://example.com/r.git", "/tmp/my-repo", null, false);
        using var vm = rig.Create();
        var pending = (CloneTabViewModel)vm.Tabs[1];

        pending.CancelCommand.Execute(null);
        Assert.Equal(1, rig.Clone.Cancels);

        rig.Clone.Current = rig.Clone.Current with { CancelRequested = true };
        rig.Clone.RaiseChanged();

        Assert.False(pending.CanCancel);
        Assert.Same(pending, vm.Tabs[1]);
    }

    [Fact]
    public void Ending_the_clone_removes_the_pending_tab()
    {
        var rig = NewRig();
        var a = OpenRepo();
        rig.Workspace.Set(a, a);
        rig.Clone.Current = new CloneStatus("https://example.com/r.git", "/tmp/my-repo", null, false);
        using var vm = rig.Create();
        Assert.Equal(2, vm.Tabs.Count);

        rig.Clone.Current = null;
        rig.Clone.RaiseChanged();

        Assert.Single(vm.Tabs);
        Assert.IsType<RepoTabViewModel>(vm.Tabs[0]);
    }

    [Fact]
    public void The_pending_tab_is_hidden_once_its_target_folder_is_already_open()
    {
        var rig = NewRig();
        var a = OpenRepo();
        rig.Workspace.Set(a, a);
        rig.Clone.Current = new CloneStatus("https://example.com/r.git", a.RepositoryPath + "/", null, false);
        using var vm = rig.Create();

        Assert.Single(vm.Tabs);
        Assert.IsType<RepoTabViewModel>(vm.Tabs[0]);
    }

    [Fact]
    public void A_clone_that_starts_while_a_tab_is_dragged_shows_after_the_drag()
    {
        var rig = NewRig();
        var a = OpenRepo();
        var b = OpenRepo();
        rig.Workspace.Set(a, a, b);
        using var vm = rig.Create();
        vm.BeginDrag(vm.Tabs[0]);

        rig.Clone.Current = new CloneStatus("https://example.com/r.git", "/tmp/my-repo", null, false);
        rig.Clone.RaiseChanged();
        Assert.Equal(2, vm.Tabs.Count);

        vm.CancelDrag();

        Assert.Equal(3, vm.Tabs.Count);
        Assert.IsType<CloneTabViewModel>(vm.Tabs[2]);
    }

    // ── tab commands ─────────────────────────────────────────────────────────

    [Fact]
    public void Activating_a_tab_focuses_the_repository_and_shows_its_view()
    {
        var rig = NewRig();
        var a = OpenRepo();
        var b = OpenRepo();
        rig.Workspace.Set(a, a, b);
        using var vm = rig.Create();

        RepoTabs(vm)[1].ActivateCommand.Execute(null);

        Assert.Same(b, rig.Workspace.Focused);
        Assert.Equal(["repo"], rig.Routes);
        Assert.Equal([false, true], RepoTabs(vm).Select(t => t.IsActive));
    }

    [Fact]
    public async Task Closing_a_tab_closes_the_repository_and_goes_home_when_none_is_left()
    {
        var rig = NewRig();
        var a = OpenRepo();
        var b = OpenRepo();
        rig.Workspace.Set(a, a, b);
        using var vm = rig.Create();

        await RepoTabs(vm)[0].CloseCommand.ExecuteAsync(null);
        Assert.Equal([a], rig.Workspace.Closed);
        Assert.Empty(rig.Routes);

        await RepoTabs(vm)[0].CloseCommand.ExecuteAsync(null);
        Assert.Equal([a, b], rig.Workspace.Closed);
        Assert.Equal([""], rig.Routes);
        Assert.Empty(vm.Tabs);
    }

    [Fact]
    public async Task Closing_the_last_tab_goes_home_before_a_running_write_releases_the_close()
    {
        var rig = NewRig();
        var a = OpenRepo();
        rig.Workspace.Set(a, a);
        var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Workspace.DisposeGate = write.Task;
        using var vm = rig.Create();

        var closing = RepoTabs(vm)[0].CloseCommand.ExecuteAsync(null);

        Assert.False(closing.IsCompleted);
        Assert.Equal([""], rig.Routes);
        Assert.Empty(vm.Tabs);

        write.SetResult();
        await closing;
        Assert.Equal([""], rig.Routes);
    }

    [Fact]
    public async Task Closing_the_focused_tab_while_others_remain_does_not_leave_the_repository_view()
    {
        var rig = NewRig();
        var a = OpenRepo();
        var b = OpenRepo();
        rig.Workspace.Set(a, a, b);
        using var vm = rig.Create();

        await RepoTabs(vm)[0].CloseCommand.ExecuteAsync(null);

        Assert.Same(b, rig.Workspace.Focused);
        Assert.Empty(rig.Routes);
    }

    [Fact]
    public async Task Closing_a_tab_with_unsaved_work_stops_when_the_user_cancels()
    {
        var rig = NewRig();
        var a = OpenRepo();
        rig.Workspace.Set(a, a);
        var work = new FakeWork(a, saves: true);
        rig.Registry.Register(work);
        rig.Dialogs.EnqueueChoice(-1);
        using var vm = rig.Create();

        await RepoTabs(vm)[0].CloseCommand.ExecuteAsync(null);

        Assert.Empty(rig.Workspace.Closed);
        Assert.False(work.Saved);
        Assert.Contains(rig.Dialogs.Calls, call => call.Detail?.Contains("notes.txt") == true);
    }

    [Fact]
    public async Task Closing_a_tab_with_unsaved_work_saves_it_first_when_the_user_chooses_save()
    {
        var rig = NewRig();
        var a = OpenRepo();
        rig.Workspace.Set(a, a);
        var work = new FakeWork(a, saves: true);
        rig.Registry.Register(work);
        rig.Dialogs.EnqueueChoice(0);
        using var vm = rig.Create();

        await RepoTabs(vm)[0].CloseCommand.ExecuteAsync(null);

        Assert.True(work.Saved);
        Assert.Equal([a], rig.Workspace.Closed);
    }

    [Fact]
    public async Task Closing_a_tab_can_discard_its_unsaved_work()
    {
        var rig = NewRig();
        var a = OpenRepo();
        rig.Workspace.Set(a, a);
        var work = new FakeWork(a, saves: true);
        rig.Registry.Register(work);
        rig.Dialogs.EnqueueChoice(1);
        using var vm = rig.Create();

        await RepoTabs(vm)[0].CloseCommand.ExecuteAsync(null);

        Assert.True(work.Discarded);
        Assert.Equal([a], rig.Workspace.Closed);
    }

    [Fact]
    public async Task Opening_a_tabs_terminal_focuses_it_and_starts_the_repositorys_session()
    {
        var rig = NewRig();
        var a = OpenRepo();
        var b = OpenRepo();
        rig.Workspace.Set(a, a, b);
        using var vm = rig.Create();

        await RepoTabs(vm)[1].OpenTerminalCommand.ExecuteAsync(null);

        Assert.Same(b, rig.Workspace.Focused);
        var session = Assert.Single(rig.Terminals.Sessions);
        Assert.Equal(RepoTerminalOrigin.For(b).TabId, session.Origin.TabId);
        Assert.Single(rig.Backend.Starts);
    }

    [Fact]
    public async Task The_context_menu_commands_reveal_the_folder_and_copy_the_path()
    {
        var rig = NewRig();
        var a = OpenRepo();
        rig.Workspace.Set(a, a);
        using var vm = rig.Create();
        var tab = RepoTabs(vm)[0];

        Assert.True(tab.HasLocation);
        tab.RevealCommand.Execute(null);
        await tab.CopyPathCommand.ExecuteAsync(null);

        Assert.Equal([a.RepositoryPath], rig.Launcher.Folders);
        Assert.Equal([a.RepositoryPath], rig.Clipboard.Copied);
    }

    // ── auto-fetch bars ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(AutoFetchTabState.NotStarted, "ColorDanger")]
    [InlineData(AutoFetchTabState.PendingThisCycle, "ColorWarning")]
    [InlineData(AutoFetchTabState.FetchedThisCycle, "ColorSuccess")]
    public void The_auto_fetch_bar_token_names_the_state_colour(AutoFetchTabState state, string expected)
    {
        Assert.Equal(expected, AppBandViewModel.AutoFetchBarToken(state));
    }

    [Fact]
    public void Tabs_start_with_their_current_auto_fetch_state_and_the_bar_hidden()
    {
        var rig = NewRig();
        var a = OpenRepo();
        rig.Workspace.Set(a, a);
        rig.AutoFetch.States[a] = AutoFetchTabState.PendingThisCycle;
        using var vm = rig.Create();

        var tab = RepoTabs(vm)[0];
        Assert.Equal("ColorWarning", tab.AutoFetchBarToken);
        Assert.True(tab.IsPendingThisCycle);
        Assert.False(tab.IsFetchedThisCycle);
        Assert.False(tab.ShowAutoFetchBar);
    }

    [Fact]
    public void The_bars_recolour_in_place_only_while_the_developer_switch_is_on()
    {
        var ui = new RecordingUiDispatcher();
        var rig = NewRig(ui);
        var a = OpenRepo();
        rig.Workspace.Set(a, a);
        using var vm = rig.Create();
        var tab = RepoTabs(vm)[0];

        rig.AutoFetch.States[a] = AutoFetchTabState.FetchedThisCycle;
        rig.AutoFetch.RaiseChanged();
        ui.DrainPosted();
        Assert.Equal("ColorDanger", tab.AutoFetchBarToken);

        rig.Platform.AutoFetchBarsVisible = true;
        rig.Platform.RaiseVisibleChanged();
        ui.DrainPosted();

        Assert.True(tab.ShowAutoFetchBar);
        Assert.Equal("ColorSuccess", tab.AutoFetchBarToken);
        Assert.True(tab.IsFetchedThisCycle);

        rig.AutoFetch.States[a] = AutoFetchTabState.PendingThisCycle;
        rig.AutoFetch.RaiseChanged();
        ui.DrainPosted();
        Assert.Equal("ColorWarning", tab.AutoFetchBarToken);
        Assert.Same(tab, RepoTabs(vm)[0]);

        rig.Platform.AutoFetchBarsVisible = false;
        rig.Platform.RaiseVisibleChanged();
        ui.DrainPosted();
        Assert.False(tab.ShowAutoFetchBar);
    }

    // ── Historia menu ────────────────────────────────────────────────────────

    private static AppMenuEntry Entry(AppBandViewModel vm, string header) => vm.AppMenu.Single(e => e.Header == header);

    [Fact]
    public void The_menu_lists_the_fixed_entries_and_hides_Developer_unless_the_switch_is_on()
    {
        var rig = NewRig();
        using var vm = rig.Create();

        Assert.Equal(
            ["Open Repository…", "Clone Repository…", "Initialize Repository…", "", "Default profile", "Open Terminal", "Settings", "", "About Historia", "Exit"],
            vm.AppMenu.Select(e => e.Header));
        Assert.Equal([3, 7], vm.AppMenu.Select((e, i) => (e, i)).Where(x => x.e.IsSeparator).Select(x => x.i));
        Assert.DoesNotContain(vm.AppMenu, e => e.Header == "Developer");

        var devRig = NewRig();
        devRig.Platform.DeveloperMenuEnabled = true;
        using var withDev = devRig.Create();

        Assert.Equal("Developer", withDev.AppMenu[7].Header);
    }

    [Fact]
    public void The_navigation_entries_request_their_routes()
    {
        var rig = NewRig();
        using var vm = rig.Create();

        Entry(vm, "Open Repository…").Command!.Execute(null);
        Entry(vm, "Settings").Command!.Execute(null);
        Entry(vm, "Default profile").Children.Single(e => e.Header == "Manage profiles…").Command!.Execute(null);

        Assert.Equal(["", "settings", "profiles"], rig.Routes);
    }

    [Fact]
    public void The_navigate_command_requests_the_route_it_is_given()
    {
        var rig = NewRig();
        using var vm = rig.Create();

        vm.NavigateCommand.Execute("?intent=clone");

        Assert.Equal(["?intent=clone"], rig.Routes);
    }

    [Fact]
    public void About_and_Exit_go_to_the_host()
    {
        var rig = NewRig();
        using var vm = rig.Create();

        Entry(vm, "About Historia").Command!.Execute(null);
        Entry(vm, "Exit").Command!.Execute(null);

        Assert.Equal(1, rig.Platform.AboutCalls);
        Assert.Equal(1, rig.Platform.Exits);
    }

    [Fact]
    public async Task Open_Terminal_always_adds_another_Home_terminal()
    {
        var rig = NewRig();
        using var vm = rig.Create();
        var command = (IAsyncRelayCommand)Entry(vm, "Open Terminal").Command!;

        await command.ExecuteAsync(null);
        await command.ExecuteAsync(null);

        Assert.Equal(2, rig.Terminals.Sessions.Count);
        Assert.All(rig.Backend.Starts, start => Assert.Equal("/home/me", start.WorkingDirectory));
    }

    [Fact]
    public async Task The_default_profile_submenu_lists_the_profiles_checks_the_default_and_sets_a_new_one()
    {
        var rig = NewRig();
        var work = await rig.Profiles.CreateProfileAsync(new GitProfileCreateRequest { DisplayName = "Work", GitUserName = "W", GitUserEmail = "w@example.com" });
        using var vm = rig.Create();

        var submenu = Entry(vm, "Default profile").Children;
        Assert.Equal(["Global (~/.gitconfig)", "Work", "", "Manage profiles…"], submenu.Select(e => e.Header));
        Assert.Equal(["Global (~/.gitconfig)"], submenu.Where(e => e.IsChecked).Select(e => e.Header));

        rig.Dialogs.EnqueueChoice(0);
        await ((IAsyncRelayCommand)submenu.Single(e => e.Header == "Work").Command!).ExecuteAsync(null);

        Assert.Equal(work.Id, rig.Profiles.GetDefaultProfile()?.Id);
        Assert.Contains(rig.Notifications.Published, n => n.Message.Contains("'Work' is now the default profile."));
        var rebuilt = Entry(vm, "Default profile").Children;
        Assert.Equal(["Work"], rebuilt.Where(e => e.IsChecked).Select(e => e.Header));
    }

    [Fact]
    public async Task A_declined_default_profile_choice_changes_nothing()
    {
        var rig = NewRig();
        await rig.Profiles.CreateProfileAsync(new GitProfileCreateRequest { DisplayName = "Work", GitUserName = "W", GitUserEmail = "w@example.com" });
        using var vm = rig.Create();
        rig.Dialogs.EnqueueChoice(-1);

        await ((IAsyncRelayCommand)Entry(vm, "Default profile").Children.Single(e => e.Header == "Work").Command!).ExecuteAsync(null);

        Assert.True(rig.Profiles.GetDefaultProfile()?.IsGlobal);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("settings", false)]
    [InlineData("dev", false)]
    [InlineData("profiles", false)]
    [InlineData("/settings?x=1", false)]
    [InlineData("repo", true)]
    [InlineData("conflicts", true)]
    public void Opening_the_menu_from_a_repository_view_switches_the_detail_pane_to_Home(string current, bool navigates)
    {
        var rig = NewRig();
        using var vm = rig.Create();
        rig.Navigation.ReportRoute(current);

        vm.PrepareAppMenu();

        Assert.Equal(navigates ? [""] : [], rig.Routes);
    }
}
