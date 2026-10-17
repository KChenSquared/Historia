namespace Historia.Tests;

/// <summary>
/// The developer panel's view model: the custom-theme row reads the stored settings and drives the
/// theme service, the sample notices and cache buttons report what they did, and the wipe of all
/// Historia data asks first and leaves everything alone when declined.
/// </summary>
[Trait("tier", "unit")]
public sealed class DeveloperViewModelTests
{
    private static readonly CustomTheme Alpha = new() { Id = "a", Name = "Alpha", Base = "Dark" };
    private static readonly CustomTheme Beta = new() { Id = "b", Name = "Beta", Base = "Light" };

    private sealed class Harness
    {
        public required DeveloperViewModel Vm { get; init; }
        public required RecordingTheme Theme { get; init; }
        public required FakeDialogService Dialogs { get; init; }
        public required FakeNotificationService Notifications { get; init; }
        public required RecordingLauncher Launcher { get; init; }
        public required StubGraphLayout Graph { get; init; }
        public required StubRepoStateCache RepoCache { get; init; }
        public required List<string> Navigations { get; init; }
        public required List<int> Wipes { get; init; }
        public required List<bool> AutoFetchWrites { get; init; }
    }

    private static Harness Create(AppSettings? settings = null, CustomTheme[]? themes = null, bool autoFetchStatus = false, int wipeResult = 7)
    {
        var cfg = new FakeSettingsService(settings);
        var theme = new RecordingTheme();
        var dialogs = new FakeDialogService();
        var notifications = new FakeNotificationService();
        var launcher = new RecordingLauncher();
        var graph = new StubGraphLayout();
        var repoCache = new StubRepoStateCache();
        var nav = new NavigationBus();
        var navigations = new List<string>();
        nav.NavigationRequested += navigations.Add;
        var wipes = new List<int>();
        var autoFetchWrites = new List<bool>();

        var vm = new DeveloperViewModel(
            cfg, theme, new StubCustomThemes(themes ?? []), nav, new FakeWorkspace(), notifications, dialogs, launcher,
            graph, repoCache,
            getAutoFetchStatus: () => autoFetchStatus,
            setAutoFetchStatus: autoFetchWrites.Add,
            wipeLocalData: () => { wipes.Add(wipeResult); return wipeResult; });

        return new Harness
        {
            Vm = vm,
            Theme = theme,
            Dialogs = dialogs,
            Notifications = notifications,
            Launcher = launcher,
            Graph = graph,
            RepoCache = repoCache,
            Navigations = navigations,
            Wipes = wipes,
            AutoFetchWrites = autoFetchWrites,
        };
    }

    [Fact]
    public void Custom_theme_row_reflects_the_stored_settings()
    {
        var h = Create(new AppSettings { UseCustomTheme = true, CustomThemeId = "b" }, [Beta, Alpha]);

        Assert.True(h.Vm.UseCustomTheme);
        Assert.True(h.Vm.HasSavedThemes);
        Assert.Equal(["Beta", "Alpha"], h.Vm.ThemeNames);
        Assert.Equal(0, h.Vm.SelectedThemeIndex);

        var stale = Create(new AppSettings { CustomThemeId = "gone" }, [Alpha, Beta]);
        Assert.False(stale.Vm.UseCustomTheme);
        Assert.Equal(0, stale.Vm.SelectedThemeIndex);

        var stored = Create(new AppSettings { CustomThemeId = "b" }, [Alpha, Beta]);
        Assert.Equal(1, stored.Vm.SelectedThemeIndex);

        var none = Create();
        Assert.False(none.Vm.HasSavedThemes);
        Assert.Empty(none.Vm.ThemeNames);
    }

    [Fact]
    public void Building_the_view_model_changes_nothing()
    {
        var h = Create(new AppSettings { UseCustomTheme = true, CustomThemeId = "b" }, [Alpha, Beta], autoFetchStatus: true);

        Assert.Empty(h.Theme.EnabledCalls);
        Assert.Empty(h.Theme.SelectedIds);
        Assert.Empty(h.Navigations);
        Assert.Empty(h.AutoFetchWrites);
        Assert.True(h.Vm.ShowAutoFetchStatus);
    }

    [Fact]
    public void Ticking_the_override_with_no_saved_themes_enables_it_and_opens_the_Themes_panel()
    {
        var h = Create();

        h.Vm.UseCustomTheme = true;

        Assert.Equal([true], h.Theme.EnabledCalls);
        Assert.Equal(["themes"], h.Navigations);
    }

    [Fact]
    public void Ticking_and_clearing_the_override_with_a_saved_theme_stays_on_the_panel()
    {
        var h = Create(themes: [Alpha]);

        h.Vm.UseCustomTheme = true;
        h.Vm.UseCustomTheme = false;

        Assert.Equal([true, false], h.Theme.EnabledCalls);
        Assert.Empty(h.Navigations);
    }

    [Fact]
    public void Picking_another_theme_selects_it_and_picking_the_stored_one_does_not()
    {
        var h = Create(new AppSettings { CustomThemeId = "a" }, [Alpha, Beta]);

        h.Vm.SelectedThemeIndex = 0;
        Assert.Empty(h.Theme.SelectedIds);

        h.Vm.SelectedThemeIndex = 1;
        Assert.Equal(["b"], h.Theme.SelectedIds);
        Assert.Equal(1, h.Vm.SelectedThemeIndex);
    }

    [Fact]
    public void An_index_outside_the_list_is_ignored()
    {
        var h = Create(new AppSettings { CustomThemeId = "b" }, [Alpha, Beta]);

        h.Vm.SelectedThemeIndex = -1;
        h.Vm.SelectedThemeIndex = 5;

        Assert.Equal(1, h.Vm.SelectedThemeIndex);
        Assert.Empty(h.Theme.SelectedIds);
    }

    [Fact]
    public void A_theme_service_failure_is_reported_instead_of_escaping()
    {
        var h = Create(themes: [Alpha]);
        h.Theme.Failure = new InvalidOperationException("disk full");

        h.Vm.UseCustomTheme = true;

        Assert.Equal(NotificationSeverity.Error, Assert.Single(h.Notifications.Published).Severity);
    }

    [Fact]
    public void Manage_themes_and_close_navigate()
    {
        var h = Create();

        h.Vm.ManageThemesCommand.Execute(null);
        h.Vm.CloseCommand.Execute(null);

        Assert.Equal(["themes", ""], h.Navigations);
    }

    [Fact]
    public void Auto_fetch_status_switch_writes_through()
    {
        var h = Create();

        h.Vm.ShowAutoFetchStatus = true;
        h.Vm.ShowAutoFetchStatus = false;

        Assert.Equal([true, false], h.AutoFetchWrites);
    }

    [Fact]
    public void Auto_fetch_status_switch_is_kept_in_the_view_model_without_a_backing_switch()
    {
        var vm = new DeveloperViewModel(
            new FakeSettingsService(), new RecordingTheme(), new StubCustomThemes([]), new NavigationBus(), new FakeWorkspace(),
            new FakeNotificationService(), new FakeDialogService(), new RecordingLauncher(), new StubGraphLayout(), new StubRepoStateCache());

        Assert.False(vm.ShowAutoFetchStatus);
        vm.ShowAutoFetchStatus = true;
        Assert.True(vm.ShowAutoFetchStatus);
    }

    [Fact]
    public void Throw_test_exception_throws_the_deliberate_exception()
    {
        var h = Create();

        var ex = Assert.Throws<InvalidOperationException>(() => h.Vm.ThrowTestExceptionCommand.Execute(null));

        Assert.Equal("Developer panel: deliberate test exception.", ex.Message);
    }

    [Fact]
    public void Sample_toast_publishes_a_success_notice()
    {
        var h = Create();

        h.Vm.ShowSampleToastCommand.Execute(null);

        var published = Assert.Single(h.Notifications.Published);
        Assert.Equal(NotificationSeverity.Success, published.Severity);
        Assert.Equal("Commit created.", published.Message);
    }

    [Fact]
    public async Task Sample_alert_opens_the_delete_branch_confirm_and_changes_nothing()
    {
        var h = Create();
        h.Dialogs.EnqueueConfirmDestructive(true);

        await h.Vm.ShowSampleAlertCommand.ExecuteAsync(null);

        var call = Assert.Single(h.Dialogs.ConfirmDestructiveCalls);
        Assert.Equal(new FakeDialogService.DestructiveCall("Delete branch", "Delete 'sample-branch'?", "Delete"), call);
        Assert.Empty(h.Notifications.Published);
        Assert.Empty(h.Wipes);
    }

    [Fact]
    public void Open_logs_folder_opens_the_logs_directory()
    {
        var h = Create();

        h.Vm.OpenLogsFolderCommand.Execute(null);

        Assert.Equal([AppPaths.LogsDirectory], h.Launcher.OpenedFolders);
        Assert.Equal($"Opens {AppPaths.LogsDirectory}.", h.Vm.LogsFolderHint);
    }

    [Theory]
    [InlineData(0, "No persisted graph cache to remove.")]
    [InlineData(3, "Removed 3 cached graph file(s).")]
    public void Clear_graph_cache_reports_how_many_files_went(int removed, string message)
    {
        var h = Create();
        h.Graph.Removed = removed;

        h.Vm.ClearGraphCacheCommand.Execute(null);

        var published = Assert.Single(h.Notifications.Published);
        Assert.Equal(NotificationSeverity.Success, published.Severity);
        Assert.Equal(message, published.Message);
    }

    [Theory]
    [InlineData(0, "No persisted repo cache to remove.")]
    [InlineData(2, "Removed 2 cached repo file(s).")]
    public void Clear_sidebar_cache_reports_how_many_files_went(int removed, string message)
    {
        var h = Create();
        h.RepoCache.Removed = removed;

        h.Vm.ClearSidebarCacheCommand.Execute(null);

        var published = Assert.Single(h.Notifications.Published);
        Assert.Equal(NotificationSeverity.Success, published.Severity);
        Assert.Equal(message, published.Message);
    }

    [Fact]
    public async Task Delete_all_names_the_effect_and_keeps_the_data_when_declined()
    {
        var h = Create();

        await h.Vm.DeleteAllCommand.ExecuteAsync(null);

        var call = Assert.Single(h.Dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Delete all Historia data?", call.Title);
        Assert.Equal("Delete", call.ConfirmLabel);
        Assert.Equal(
            "This permanently deletes every Historia setting, profile, repo binding, theme, saved "
            + "session, cached repository/graph file and log from Historia's data and cache folders. "
            + "It cannot be undone. Restart Historia afterwards.",
            call.Effect);
        Assert.Empty(h.Wipes);
        Assert.Empty(h.Notifications.Published);
    }

    [Fact]
    public async Task Delete_all_wipes_and_reports_only_after_it_is_confirmed()
    {
        var h = Create(wipeResult: 12);
        h.Dialogs.EnqueueConfirmDestructive(true);

        await h.Vm.DeleteAllCommand.ExecuteAsync(null);

        Assert.Equal([12], h.Wipes);
        var published = Assert.Single(h.Notifications.Published);
        Assert.Equal(NotificationSeverity.Success, published.Severity);
        Assert.Equal("Removed 12 file(s). Restart Historia for a clean state.", published.Message);
    }

    private sealed class RecordingTheme : IThemeService
    {
        public Exception? Failure { get; set; }
        public List<bool> EnabledCalls { get; } = [];
        public List<string> SelectedIds { get; } = [];

        public Task SetCustomThemeEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            EnabledCalls.Add(enabled);
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }

        public Task SelectCustomThemeAsync(string themeId, CancellationToken ct = default)
        {
            SelectedIds.Add(themeId);
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }

        public ThemeMode CurrentMode => throw new NotSupportedException();
        public bool CustomThemeActive => throw new NotSupportedException();
        public bool HighContrastEnabled => throw new NotSupportedException();
        public EffectiveTheme Effective => throw new NotSupportedException();
        public string EffectiveCssThemeName => throw new NotSupportedException();
        public IReadOnlyDictionary<string, Rgb> Palette => throw new NotSupportedException();
        public IReadOnlyList<Rgb> LaneColors => throw new NotSupportedException();
        public Task ApplyModeAsync(ThemeMode mode, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetHighContrastAsync(bool enabled, CancellationToken ct = default) => throw new NotSupportedException();

#pragma warning disable CS0067
        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
#pragma warning restore CS0067
    }

    private sealed class StubCustomThemes(IReadOnlyList<CustomTheme> themes) : ICustomThemeService
    {
        public IReadOnlyList<CustomTheme> List() => themes;
        public CustomTheme? Get(string id) => themes.FirstOrDefault(t => t.Id == id);
        public Task<CustomTheme> SaveAsync(CustomTheme theme, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public CustomTheme NewFrom(string baseName) => throw new NotSupportedException();
        public ResolvedPalette Compose(CustomTheme theme) => throw new NotSupportedException();

#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067
    }

    private sealed class RecordingLauncher : IExternalLauncher
    {
        public List<string> OpenedFolders { get; } = [];

        public void OpenUrl(string url) => throw new NotSupportedException();
        public void RevealInFileManager(string path) => throw new NotSupportedException();
        public void OpenFolder(string path) => OpenedFolders.Add(path);
    }

    private sealed class StubRepoStateCache : IRepoStateCache
    {
        public int Removed { get; set; }

        public int WipeAll() => Removed;
        public RepoStateSnapshot? TryLoad(string repoPath) => throw new NotSupportedException();
        public void SaveRefs(string repoPath, IReadOnlyList<BranchInfo> branches, IReadOnlyList<TagInfo> tags, IReadOnlyList<RemoteInfo> remotes) => throw new NotSupportedException();
        public void SavePowerTools(string repoPath, IReadOnlyList<StashEntry>? stashes, IReadOnlyList<SubmoduleInfo>? submodules, IReadOnlyList<WorktreeInfo>? worktrees) => throw new NotSupportedException();
    }

    private sealed class StubGraphLayout : IGraphLayoutService
    {
        public int Removed { get; set; }

        public int ClearPersistentCache() => Removed;

        public Task<GraphLayoutResult> ComputeLayoutAsync(RepositoryContext context, GraphQuery query, int maxRows = 10_000, CancellationToken ct = default) => throw new NotSupportedException();
        public GraphLayoutResult? TryOptimisticSnapshot(RepositoryContext context, GraphQuery query, int maxRows) => throw new NotSupportedException();
        public Task<GraphLayoutResult> ExtendLayoutAsync(RepositoryContext context, GraphLayoutResult existing, int additionalRows, CancellationToken ct = default, bool background = false) => throw new NotSupportedException();
        public Task<GraphLayoutResult> RecomputeAsync(RepositoryContext context, GraphQuery query, GraphLayoutResult previous, InvalidationReason reason, int maxRows = 10_000, CancellationToken ct = default) => throw new NotSupportedException();
        public void Forget(RepositoryContext context) => throw new NotSupportedException();
        public Task Hibernate(RepositoryContext context) => throw new NotSupportedException();
        public Task RunHeadlessCacheAsync(RepositoryContext context, GraphQuery query, CancellationToken ct) => throw new NotSupportedException();
        public void EnqueueInactiveTabCaching(RepositoryContext context, GraphQuery query) => throw new NotSupportedException();
        public Task CancelInactiveTabCachingAsync(RepositoryContext context) => throw new NotSupportedException();
        public void RefreshCommitGraphAfterFetch(RepositoryContext context) => throw new NotSupportedException();
        public void SweepDiskCache(IReadOnlyCollection<string> openRepositoryPaths) { }
        public Task PrecacheClonedRepositoryAsync(RepositoryContext context, GraphQuery query, IProgress<GitProgress>? progress, CancellationToken ct) => throw new NotSupportedException();
    }
}
