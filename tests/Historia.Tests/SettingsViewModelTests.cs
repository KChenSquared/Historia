namespace Historia.Tests;

/// <summary>
/// The settings page's view model: every row writes its setting, numeric fields clamp and commit
/// only on request, the destructive confirmations guard the row cap and the PATH change, and the
/// page follows the services through the dispatcher.
/// </summary>
[Trait("tier", "unit")]
public class SettingsViewModelTests
{
    private sealed class FakeThemeService(ISettingsService settings) : IThemeService
    {
        public ThemeMode CurrentMode => settings.Current.AppTheme;
        public bool HighContrast { get; set; }
        public bool HighContrastEnabled => HighContrast;
        public List<ThemeMode> AppliedModes { get; } = [];
        public List<bool> HighContrastCalls { get; } = [];
        public bool CustomThemeActive => false;
        public EffectiveTheme Effective => EffectiveTheme.Dark;
        public string EffectiveCssThemeName => "dark";
        public IReadOnlyDictionary<string, Rgb> Palette => HistoriaPalette.For(Effective);
        public IReadOnlyList<Rgb> LaneColors => [];

        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

        public void RaiseThemeChanged() => ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(EffectiveTheme.Dark, EffectiveTheme.Light));

        public Task ApplyModeAsync(ThemeMode mode, CancellationToken ct = default)
        {
            AppliedModes.Add(mode);
            return settings.UpdateAsync(s => s with { AppTheme = mode }, ct);
        }

        public Task SetHighContrastAsync(bool enabled, CancellationToken ct = default)
        {
            HighContrastCalls.Add(enabled);
            HighContrast = enabled;
            return Task.CompletedTask;
        }

        public Task SetCustomThemeEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task SelectCustomThemeAsync(string themeId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeRepoStateCache : IRepoStateCache
    {
        public int WipeCount { get; private set; }
        public RepoStateSnapshot? TryLoad(string repoPath) => null;
        public void SaveRefs(string repoPath, IReadOnlyList<BranchInfo> branches, IReadOnlyList<TagInfo> tags, IReadOnlyList<RemoteInfo> remotes) { }
        public void SavePowerTools(string repoPath, IReadOnlyList<StashEntry>? stashes, IReadOnlyList<SubmoduleInfo>? submodules, IReadOnlyList<WorktreeInfo>? worktrees) { }
        public int WipeAll() => ++WipeCount;
    }

    private sealed class RecordingCmdrPathService : ICmdrPathService
    {
        private int _statusCalls;
        public CmdrPathStatus StatusToReturn { get; set; } = new CmdrPathStatus.Current();
        public CmdrPathApplyResult ApplyResultToReturn { get; set; } = new CmdrPathApplyResult.Written(@"C:\Backups\path.reg");
        public List<PathChange> ApplyCalls { get; } = [];
        public bool ThrowFromGetStatus { get; set; }
        public bool ThrowFromApply { get; set; }
        public TaskCompletionSource<bool>? ApplyGate { get; set; }
        public int StatusCalls => _statusCalls;

        public CmdrPathStatus GetStatus()
        {
            Interlocked.Increment(ref _statusCalls);
            if (ThrowFromGetStatus)
                throw new InvalidOperationException("Status error");
            return StatusToReturn;
        }

        public CmdrPathApplyResult Apply(PathChange confirmed)
        {
            ApplyCalls.Add(confirmed);
            ApplyGate?.Task.GetAwaiter().GetResult();
            if (ThrowFromApply)
                throw new InvalidOperationException("Apply error");
            return ApplyResultToReturn;
        }
    }

    /// <summary>A dialog service whose destructive confirmations wait for the test to answer them.</summary>
    private sealed class GatedDialogService : IDialogService
    {
        public List<TaskCompletionSource<bool>> Pending { get; } = [];
        public List<(string Title, string Effect, string ConfirmLabel)> DestructiveCalls { get; } = [];

        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel)
        {
            DestructiveCalls.Add((title, effect, confirmLabel));
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending.Add(gate);
            return gate.Task;
        }

        public Task<bool> ConfirmAsync(string title, string message, bool destructive = false, string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None) => Task.FromResult(false);
        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options, int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None) => Task.FromResult(-1);
        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PromptAsync(string title, string message, string? defaultValue = null, string confirmLabel = "OK", bool primaryConfirm = false) => Task.FromResult<string?>(null);
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }

    private sealed class Rig : IDisposable
    {
        public Rig(
            AppSettings? settings = null,
            IUiDispatcher? ui = null,
            IAiIntegrationsPanel? panel = null,
            bool developerMode = false,
            CmdrPathStatus? cmdrStatus = null,
            IDialogService? dialogs = null,
            FakeSettingsService? service = null,
            bool cmdrThrows = false)
        {
            Settings = service ?? new FakeSettingsService(settings);
            Theme = new FakeThemeService(Settings);
            Dialogs = dialogs as FakeDialogService ?? new FakeDialogService();
            Ui = ui ?? new ImmediateUiDispatcher();
            if (cmdrStatus is not null)
                Cmdr.StatusToReturn = cmdrStatus;
            Cmdr.ThrowFromGetStatus = cmdrThrows;
            Routes = [];
            Nav.NavigationRequested += Routes.Add;
            Vm = new SettingsViewModel(Settings, Theme, dialogs ?? Dialogs, Cache, Cmdr, Ui, Nav, Workspace, developerMode, panel);
        }

        public FakeSettingsService Settings { get; }
        public FakeThemeService Theme { get; }
        public FakeDialogService Dialogs { get; }
        public FakeRepoStateCache Cache { get; } = new();
        public RecordingCmdrPathService Cmdr { get; } = new();
        public NavigationBus Nav { get; } = new();
        public FakeWorkspace Workspace { get; } = new();
        public IUiDispatcher Ui { get; }
        public List<string> Routes { get; }
        public SettingsViewModel Vm { get; }

        /// <summary>Waits for the PATH status check and lets its result land.</summary>
        public async Task StatusAsync()
        {
            await Vm.CmdrStatusTask;
            (Ui as RecordingUiDispatcher)?.DrainPosted();
        }

        public void Dispose() => Vm.Dispose();
    }

    private static CmdrPathStatus.NeedsUpdate NeedsUpdate(PathChange? preview = null, params string[] machineCopies)
        => new(preview ?? new PathChange(null, @"C:\NewCmdr", @"C:\NewCmdr", [], false), machineCopies, @"C:\NewCmdr", @"C:\Backups");

    // ── Toggles ──────────────────────────────────────────────────────────────

    public static TheoryData<string> ToggleNames =>
    [
        nameof(AppSettings.ShowSplashScreen),
        nameof(AppSettings.EnableMultiLineTabs),
        nameof(AppSettings.ShowAuthorDate),
        nameof(AppSettings.PersistRepoCache),
        nameof(AppSettings.AutoFetchEnabled),
        nameof(AppSettings.ExternalControlEnabled),
        nameof(AppSettings.DiffWordWrap),
        nameof(AppSettings.PruneOnFetch),
        nameof(AppSettings.PullWithRebase),
        nameof(AppSettings.FetchAfterPull),
        nameof(AppSettings.RestoreSessionTabs),
        nameof(AppSettings.UseCustomStashNames),
        nameof(AppSettings.TerminalKittyKeyboard),
        nameof(AppSettings.TerminalAllowClipboardWrite),
        nameof(AppSettings.TerminalCursorBlink),
        nameof(AppSettings.LoggingEnabled),
        nameof(AppSettings.AutoHideNotices),
        nameof(AppSettings.GraphMetadataFromDisk),
        nameof(AppSettings.EnableGraphSplice),
        nameof(AppSettings.LightweightMode),
        nameof(AppSettings.DeveloperPanelEnabled),
    ];

    [Theory]
    [MemberData(nameof(ToggleNames))]
    public void Every_toggle_writes_its_setting(string name)
    {
        using var rig = new Rig();
        var vmProperty = typeof(SettingsViewModel).GetProperty(name)!;
        var setting = typeof(AppSettings).GetProperty(name)!;

        var before = (bool)vmProperty.GetValue(rig.Vm)!;
        Assert.Equal(before, (bool)setting.GetValue(rig.Settings.Current)!);

        vmProperty.SetValue(rig.Vm, !before);

        Assert.Equal(!before, (bool)setting.GetValue(rig.Settings.Current)!);
    }

    [Fact]
    public void The_toggle_list_covers_every_boolean_row()
    {
        var rowNames = typeof(SettingsViewModel).GetProperties()
            .Where(p => p.PropertyType == typeof(bool) && p.CanWrite && p.SetMethod!.IsPublic)
            .Select(p => p.Name)
            .Except([nameof(SettingsViewModel.HighContrast), nameof(SettingsViewModel.UseFixedTabWidth)]);

        Assert.Equal(rowNames.Order(), ((IEnumerable<object[]>)ToggleNames).Select(d => (string)d[0]).Order());
    }

    [Fact]
    public void Fixed_tab_width_writes_the_tab_width_mode()
    {
        using var rig = new Rig();

        rig.Vm.UseFixedTabWidth = true;
        Assert.Equal(TabWidthMode.Fixed, rig.Settings.Current.TabWidthMode);

        rig.Vm.UseFixedTabWidth = false;
        Assert.Equal(TabWidthMode.Dynamic, rig.Settings.Current.TabWidthMode);
    }

    [Fact]
    public void High_contrast_goes_to_the_theme_service()
    {
        using var rig = new Rig();

        rig.Vm.HighContrast = true;

        Assert.Equal([true], rig.Theme.HighContrastCalls);
    }

    [Fact]
    public void Turning_the_repository_cache_off_clears_it_and_turning_it_on_does_not()
    {
        using var rig = new Rig(new AppSettings { PersistRepoCache = true });

        rig.Vm.PersistRepoCache = false;
        Assert.Equal(1, rig.Cache.WipeCount);
        Assert.False(rig.Settings.Current.PersistRepoCache);

        rig.Vm.PersistRepoCache = true;
        Assert.Equal(1, rig.Cache.WipeCount);
        Assert.True(rig.Settings.Current.PersistRepoCache);
    }

    [Fact]
    public void The_combo_boxes_write_their_enum_settings()
    {
        using var rig = new Rig();

        rig.Vm.WheelScrollDirectionIndex = 2;
        rig.Vm.DefaultDiffViewIndex = 1;
        rig.Vm.TerminalCursorStyleIndex = 2;

        Assert.Equal(WheelScrollDirection.Inverted, rig.Settings.Current.WheelScrollDirection);
        Assert.Equal(DiffViewMode.SideBySide, rig.Settings.Current.DefaultDiffView);
        Assert.Equal(TerminalCursorStyle.Bar, rig.Settings.Current.TerminalCursorStyle);
    }

    [Fact]
    public void A_combo_box_with_nothing_selected_changes_nothing()
    {
        using var rig = new Rig(new AppSettings { WheelScrollDirection = WheelScrollDirection.Standard });

        rig.Vm.WheelScrollDirectionIndex = -1;

        Assert.Equal(WheelScrollDirection.Standard, rig.Settings.Current.WheelScrollDirection);
    }

    // ── Following the services ───────────────────────────────────────────────

    [Fact]
    public void The_page_starts_from_the_saved_settings()
    {
        using var rig = new Rig(new AppSettings
        {
            ShowSplashScreen = false,
            EnableMultiLineTabs = true,
            MaxTabRows = 4,
            TabWidthMode = TabWidthMode.Fixed,
            FixedTabWidth = 200,
            WheelScrollDirection = WheelScrollDirection.Standard,
            DefaultDiffView = DiffViewMode.SideBySide,
            DiffContextLines = 9,
            TerminalShellPath = "/bin/zsh",
            TerminalShellArgs = ["-l", "-i"],
            TerminalFontFamily = "Mono",
            TerminalFontSize = 15,
            TerminalCursorStyle = TerminalCursorStyle.Underline,
            LightweightMode = true,
        });

        Assert.False(rig.Vm.ShowSplashScreen);
        Assert.True(rig.Vm.EnableMultiLineTabs);
        Assert.Equal(4, rig.Vm.MaxTabRows.Value);
        Assert.True(rig.Vm.UseFixedTabWidth);
        Assert.Equal(200, rig.Vm.FixedTabWidth.Value);
        Assert.Equal(1, rig.Vm.WheelScrollDirectionIndex);
        Assert.Equal(1, rig.Vm.DefaultDiffViewIndex);
        Assert.Equal(9, rig.Vm.DiffContextLines.Value);
        Assert.Equal("/bin/zsh", rig.Vm.TerminalShellPath);
        Assert.Equal("-l -i", rig.Vm.TerminalShellArgs);
        Assert.Equal("Mono", rig.Vm.TerminalFontFamily);
        Assert.Equal(15, rig.Vm.TerminalFontSize.Value);
        Assert.Equal(1, rig.Vm.TerminalCursorStyleIndex);
        Assert.True(rig.Vm.LightweightMode);
        Assert.False(rig.Vm.RowCapsEnabled);
    }

    [Fact]
    public void Opening_the_page_saves_nothing()
    {
        var service = new FakeSettingsService(new AppSettings
        {
            TerminalShellArgs = ["-l"],
            TerminalShellPath = "  padded  ",
            IssueAutolinks = [new IssueAutolinkRule { Pattern = "a", UrlTemplate = "u" }],
        });
        var saves = 0;
        service.Changed += (_, _) => saves++;

        using var rig = new Rig(service: service);

        Assert.Equal(0, saves);
    }

    [Fact]
    public async Task A_settings_change_made_elsewhere_reaches_the_page_through_the_dispatcher()
    {
        var ui = new RecordingUiDispatcher();
        using var rig = new Rig(ui: ui);
        await rig.StatusAsync();
        var posts = ui.PostCount;

        await rig.Settings.UpdateAsync(s => s with { LoggingEnabled = !s.LoggingEnabled, DiffContextLines = 12 });

        Assert.Equal(posts + 1, ui.PostCount);
        Assert.NotEqual(rig.Settings.Current.LoggingEnabled, rig.Vm.LoggingEnabled);
        ui.DrainPosted();
        Assert.Equal(rig.Settings.Current.LoggingEnabled, rig.Vm.LoggingEnabled);
        Assert.Equal(12, rig.Vm.DiffContextLines.Value);
    }

    [Fact]
    public void Fetch_after_pull_starts_on_and_follows_the_setting()
    {
        using var rig = new Rig();
        Assert.True(rig.Vm.FetchAfterPull);

        rig.Settings.UpdateAsync(s => s with { FetchAfterPull = false });

        Assert.False(rig.Vm.FetchAfterPull);
    }

    [Fact]
    public void Following_a_change_does_not_save_it_back()
    {
        using var rig = new Rig();
        var saves = 0;
        rig.Settings.Changed += (_, _) => saves++;

        rig.Settings.UpdateAsync(s => s with { PullWithRebase = true, TerminalShellPath = "sh" });

        Assert.True(rig.Vm.PullWithRebase);
        Assert.Equal("sh", rig.Vm.TerminalShellPath);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void A_theme_change_refreshes_the_high_contrast_row_and_the_mode_flags()
    {
        var ui = new RecordingUiDispatcher();
        using var rig = new Rig(ui: ui);
        ui.DrainPosted();
        var changed = new List<string?>();
        rig.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        rig.Theme.HighContrast = true;
        rig.Theme.RaiseThemeChanged();
        ui.DrainPosted();

        Assert.True(rig.Vm.HighContrast);
        Assert.Empty(rig.Theme.HighContrastCalls);
        Assert.Contains(nameof(SettingsViewModel.IsDarkMode), changed);
    }

    [Fact]
    public void Choosing_a_theme_mode_applies_it_and_moves_the_flags()
    {
        using var rig = new Rig(new AppSettings { AppTheme = ThemeMode.System });
        Assert.True(rig.Vm.IsSystemMode);

        rig.Vm.SetThemeModeCommand.Execute(ThemeMode.Light);

        Assert.Equal([ThemeMode.Light], rig.Theme.AppliedModes);
        Assert.True(rig.Vm.IsLightMode);
        Assert.False(rig.Vm.IsDarkMode);
        Assert.False(rig.Vm.IsSystemMode);
    }

    [Fact]
    public async Task A_disposed_page_stops_following_the_services()
    {
        var ui = new RecordingUiDispatcher();
        var rig = new Rig(ui: ui);
        await rig.StatusAsync();
        var posts = ui.PostCount;

        rig.Vm.Dispose();
        await rig.Settings.UpdateAsync(s => s with { LoggingEnabled = !s.LoggingEnabled });
        rig.Theme.RaiseThemeChanged();

        Assert.Equal(posts, ui.PostCount);
    }

    [Fact]
    public void A_post_that_lands_after_disposal_does_nothing()
    {
        var ui = new RecordingUiDispatcher();
        var rig = new Rig(ui: ui);
        ui.DrainPosted();
        rig.Settings.UpdateAsync(s => s with { LoggingEnabled = !s.LoggingEnabled });
        var shown = rig.Vm.LoggingEnabled;

        rig.Vm.Dispose();
        ui.DrainPosted();

        Assert.Equal(shown, rig.Vm.LoggingEnabled);
    }

    [Fact]
    public void Close_goes_home_when_no_repository_is_focused()
    {
        using var rig = new Rig();

        rig.Vm.CloseCommand.Execute(null);

        Assert.Equal([""], rig.Routes);
    }

    [Fact]
    public void The_general_hint_mentions_the_custom_theme_override_only_in_developer_mode()
    {
        using var plain = new Rig(developerMode: false);
        using var dev = new Rig(developerMode: true);

        Assert.DoesNotContain("developer mode", plain.Vm.GeneralHint);
        Assert.StartsWith(plain.Vm.GeneralHint, dev.Vm.GeneralHint);
        Assert.Contains("In developer mode, a custom theme from the Developer panel overrides Light, Dark and System.", dev.Vm.GeneralHint);
        Assert.Equal(dev.Vm.GeneralHint, dev.Vm.General.Hint);
    }

    // ── Numeric fields ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(9, 5)]
    [InlineData(1, 2)]
    [InlineData(4, 4)]
    public async Task Maximum_rows_clamps_to_its_range(int typed, int saved)
    {
        using var rig = new Rig(new AppSettings { MaxTabRows = 3 });

        rig.Vm.MaxTabRows.Value = typed;
        await rig.Vm.MaxTabRows.CommitAsync();

        Assert.Equal(saved, rig.Settings.Current.MaxTabRows);
        Assert.Equal(saved, rig.Vm.MaxTabRows.Value);
    }

    [Theory]
    [InlineData(10, AppSettings.MinFixedTabWidth)]
    [InlineData(1000, AppSettings.MaxFixedTabWidth)]
    [InlineData(250, 250)]
    public async Task Tab_width_clamps_to_its_range(int typed, int saved)
    {
        using var rig = new Rig();

        rig.Vm.FixedTabWidth.Value = typed;
        await rig.Vm.FixedTabWidth.CommitAsync();

        Assert.Equal(saved, rig.Settings.Current.FixedTabWidth);
        Assert.Equal(saved, rig.Vm.FixedTabWidth.Value);
    }

    [Fact]
    public async Task Scrollback_and_background_preload_clamp_to_their_ranges()
    {
        using var rig = new Rig();

        rig.Vm.TerminalScrollbackLines.Value = 1;
        await rig.Vm.TerminalScrollbackLines.CommitAsync();
        rig.Vm.SilentCrawlRowCap.Value = -5;
        await rig.Vm.SilentCrawlRowCap.CommitAsync();

        Assert.Equal(AppSettings.MinTerminalScrollback, rig.Settings.Current.TerminalScrollbackLines);
        Assert.Equal(AppSettings.MinSilentCrawlRowCap, rig.Settings.Current.SilentCrawlRowCap);
    }

    [Theory]
    [InlineData(0, AppSettings.MinCmdrQueueExpiryHours)]
    [InlineData(1_000, AppSettings.MaxCmdrQueueExpiryHours)]
    [InlineData(24, 24)]
    public async Task Agent_queue_expiry_clamps_and_saves(int typed, int saved)
    {
        using var rig = new Rig();
        Assert.Equal(5, rig.Vm.CmdrQueueExpiryHours.Value);

        rig.Vm.CmdrQueueExpiryHours.Value = typed;
        await rig.Vm.CmdrQueueExpiryHours.CommitAsync();

        Assert.Equal(saved, rig.Settings.Current.CmdrQueueExpiryHours);
        Assert.Equal(saved, rig.Vm.CmdrQueueExpiryHours.Value);
    }

    [Fact]
    public async Task Context_lines_and_font_size_commit_what_was_typed()
    {
        using var rig = new Rig();

        rig.Vm.DiffContextLines.Value = 8;
        await rig.Vm.DiffContextLines.CommitAsync();
        rig.Vm.TerminalFontSize.Value = 20;
        await rig.Vm.TerminalFontSize.CommitAsync();

        Assert.Equal(8, rig.Settings.Current.DiffContextLines);
        Assert.Equal(20, rig.Settings.Current.TerminalFontSize);
    }

    [Fact]
    public async Task Typing_alone_saves_nothing_and_a_commit_of_an_unchanged_value_saves_nothing()
    {
        using var rig = new Rig(new AppSettings { DiffContextLines = 3 });
        var saves = 0;
        rig.Settings.Changed += (_, _) => saves++;

        rig.Vm.DiffContextLines.Value = 7;
        Assert.Equal(0, saves);
        Assert.Equal(3, rig.Settings.Current.DiffContextLines);

        rig.Vm.DiffContextLines.Value = 3;
        await rig.Vm.DiffContextLines.CommitAsync();
        Assert.Equal(0, saves);
    }

    [Fact]
    public async Task A_refresh_that_brings_nothing_new_keeps_a_pending_edit()
    {
        using var rig = new Rig(new AppSettings { DiffContextLines = 3 });
        rig.Vm.DiffContextLines.Value = 7;

        await rig.Settings.UpdateAsync(s => s with { PullWithRebase = true });

        Assert.Equal(7, rig.Vm.DiffContextLines.Value);
    }

    [Fact]
    public async Task A_refresh_with_a_new_saved_value_replaces_a_pending_edit()
    {
        using var rig = new Rig(new AppSettings { DiffContextLines = 3 });
        rig.Vm.DiffContextLines.Value = 7;

        await rig.Settings.UpdateAsync(s => s with { DiffContextLines = 5 });

        Assert.Equal(5, rig.Vm.DiffContextLines.Value);
    }

    // ── Max rows in memory ───────────────────────────────────────────────────

    private const int StartCap = 2_000_000;

    [Fact]
    public async Task A_changed_row_cap_asks_once_and_saves_on_confirm()
    {
        var dialogs = new FakeDialogService().EnqueueConfirmDestructive(true);
        using var rig = new Rig(new AppSettings { ResidentRowCap = StartCap }, dialogs: dialogs);

        rig.Vm.ResidentRowCap.Value = 2_500_000;
        await rig.Vm.ResidentRowCap.CommitAsync();

        var call = Assert.Single(dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Change the resident row cap?", call.Title);
        Assert.Equal("Continue", call.ConfirmLabel);
        Assert.Equal(2_500_000, rig.Settings.Current.ResidentRowCap);
        Assert.Equal(2_500_000, rig.Vm.ResidentRowCap.Value);
    }

    [Fact]
    public async Task Declining_the_row_cap_confirmation_restores_the_saved_value()
    {
        var dialogs = new FakeDialogService().EnqueueConfirmDestructive(false);
        using var rig = new Rig(new AppSettings { ResidentRowCap = StartCap }, dialogs: dialogs);

        rig.Vm.ResidentRowCap.Value = 1_500_000;
        await rig.Vm.ResidentRowCap.CommitAsync();

        Assert.Single(dialogs.ConfirmDestructiveCalls);
        Assert.Equal(StartCap, rig.Settings.Current.ResidentRowCap);
        Assert.Equal(StartCap, rig.Vm.ResidentRowCap.Value);
    }

    [Fact]
    public async Task An_unchanged_row_cap_does_not_ask()
    {
        var dialogs = new FakeDialogService();
        using var rig = new Rig(new AppSettings { ResidentRowCap = StartCap }, dialogs: dialogs);

        rig.Vm.ResidentRowCap.Value = StartCap;
        await rig.Vm.ResidentRowCap.CommitAsync();

        Assert.Empty(dialogs.ConfirmDestructiveCalls);
    }

    [Fact]
    public async Task A_row_cap_above_the_warning_threshold_asks_twice()
    {
        var dialogs = new FakeDialogService().EnqueueConfirmDestructive(true).EnqueueConfirmDestructive(true);
        using var rig = new Rig(new AppSettings { ResidentRowCap = StartCap }, dialogs: dialogs);

        rig.Vm.ResidentRowCap.Value = 20_000_000;
        await rig.Vm.ResidentRowCap.CommitAsync();

        Assert.Equal(2, dialogs.ConfirmDestructiveCalls.Count);
        Assert.Equal("Keep this many rows in memory?", dialogs.ConfirmDestructiveCalls[1].Title);
        Assert.Equal("Save anyway", dialogs.ConfirmDestructiveCalls[1].ConfirmLabel);
        Assert.Contains("20,000,000", dialogs.ConfirmDestructiveCalls[1].Effect.Replace('\u00A0', ','));
        Assert.Equal(20_000_000, rig.Settings.Current.ResidentRowCap);
    }

    [Fact]
    public async Task Declining_the_second_warning_restores_the_saved_value()
    {
        var dialogs = new FakeDialogService().EnqueueConfirmDestructive(true).EnqueueConfirmDestructive(false);
        using var rig = new Rig(new AppSettings { ResidentRowCap = StartCap }, dialogs: dialogs);

        rig.Vm.ResidentRowCap.Value = 6_000_000;
        await rig.Vm.ResidentRowCap.CommitAsync();

        Assert.Equal(2, dialogs.ConfirmDestructiveCalls.Count);
        Assert.Equal(StartCap, rig.Settings.Current.ResidentRowCap);
        Assert.Equal(StartCap, rig.Vm.ResidentRowCap.Value);
    }

    [Fact]
    public async Task A_row_cap_at_the_threshold_asks_only_once()
    {
        var dialogs = new FakeDialogService().EnqueueConfirmDestructive(true);
        using var rig = new Rig(new AppSettings { ResidentRowCap = StartCap }, dialogs: dialogs);

        rig.Vm.ResidentRowCap.Value = AppSettings.ResidentRowCapWarnThreshold;
        await rig.Vm.ResidentRowCap.CommitAsync();

        Assert.Single(dialogs.ConfirmDestructiveCalls);
        Assert.Equal(AppSettings.ResidentRowCapWarnThreshold, rig.Settings.Current.ResidentRowCap);
    }

    [Fact]
    public async Task A_second_commit_while_the_confirmation_is_open_is_ignored()
    {
        var dialogs = new GatedDialogService();
        using var rig = new Rig(new AppSettings { ResidentRowCap = StartCap }, dialogs: dialogs);
        rig.Vm.ResidentRowCap.Value = 2_500_000;

        var first = rig.Vm.ResidentRowCap.CommitAsync();
        await rig.Vm.ResidentRowCap.CommitAsync();
        Assert.Single(dialogs.DestructiveCalls);

        dialogs.Pending[0].SetResult(true);
        await first;

        Assert.Equal(2_500_000, rig.Settings.Current.ResidentRowCap);
    }

    [Fact]
    public async Task The_value_is_read_again_after_the_wait_before_the_confirmation()
    {
        var dialogs = new FakeDialogService().EnqueueConfirmDestructive(true);
        using var rig = new Rig(new AppSettings { ResidentRowCap = StartCap }, dialogs: dialogs);
        rig.Vm.ResidentRowCap.Value = 2_500_000;

        await rig.Vm.ResidentRowCap.CommitAsync(() =>
        {
            rig.Vm.ResidentRowCap.Value = 3_000_000;
            return Task.CompletedTask;
        });

        Assert.Equal(3_000_000, rig.Settings.Current.ResidentRowCap);
    }

    [Fact]
    public async Task Typing_the_saved_value_back_during_the_wait_cancels_the_confirmation()
    {
        var dialogs = new FakeDialogService();
        using var rig = new Rig(new AppSettings { ResidentRowCap = StartCap }, dialogs: dialogs);
        rig.Vm.ResidentRowCap.Value = 2_500_000;

        await rig.Vm.ResidentRowCap.CommitAsync(() =>
        {
            rig.Vm.ResidentRowCap.Value = StartCap;
            return Task.CompletedTask;
        });

        Assert.Empty(dialogs.ConfirmDestructiveCalls);
        Assert.Equal(StartCap, rig.Settings.Current.ResidentRowCap);
    }

    [Fact]
    public void Lightweight_mode_disables_both_row_cap_fields_but_keeps_their_values()
    {
        using var rig = new Rig(new AppSettings { LightweightMode = false, SilentCrawlRowCap = 250_000, ResidentRowCap = 4_000_000 });
        Assert.True(rig.Vm.RowCapsEnabled);

        rig.Vm.LightweightMode = true;

        Assert.False(rig.Vm.RowCapsEnabled);
        Assert.Equal(250_000, rig.Vm.SilentCrawlRowCap.Value);
        Assert.Equal(4_000_000, rig.Vm.ResidentRowCap.Value);
        Assert.Equal(250_000, rig.Settings.Current.SilentCrawlRowCap);
        Assert.Equal(4_000_000, rig.Settings.Current.ResidentRowCap);
    }

    // ── Text boxes ───────────────────────────────────────────────────────────

    [Fact]
    public void The_shell_path_is_trimmed_and_an_empty_one_means_the_platform_default()
    {
        using var rig = new Rig();

        rig.Vm.TerminalShellPath = "  /usr/bin/fish  ";
        Assert.Equal("/usr/bin/fish", rig.Settings.Current.TerminalShellPath);
        Assert.Equal("/usr/bin/fish", rig.Vm.TerminalShellPath);

        rig.Vm.TerminalShellPath = "   ";
        Assert.Null(rig.Settings.Current.TerminalShellPath);
        Assert.Equal("", rig.Vm.TerminalShellPath);
    }

    [Fact]
    public void Shell_arguments_split_on_spaces()
    {
        using var rig = new Rig();

        rig.Vm.TerminalShellArgs = "  -l   --login  ";

        Assert.Equal(["-l", "--login"], rig.Settings.Current.TerminalShellArgs);
        Assert.Equal("-l --login", rig.Vm.TerminalShellArgs);
    }

    [Fact]
    public void The_font_family_is_trimmed_and_an_empty_one_means_the_platform_default()
    {
        using var rig = new Rig();

        rig.Vm.TerminalFontFamily = " Fira Code ";
        Assert.Equal("Fira Code", rig.Settings.Current.TerminalFontFamily);

        rig.Vm.TerminalFontFamily = "";
        Assert.Null(rig.Settings.Current.TerminalFontFamily);
    }

    [Fact]
    public async Task Browse_fills_in_the_picked_shell()
    {
        var dialogs = new FakeDialogService().EnqueueFile("/opt/shell/bash");
        using var rig = new Rig(dialogs: dialogs);

        await rig.Vm.BrowseShellCommand.ExecuteAsync(null);

        Assert.Equal("/opt/shell/bash", rig.Vm.TerminalShellPath);
        Assert.Equal("/opt/shell/bash", rig.Settings.Current.TerminalShellPath);
        Assert.Equal("Terminal shell", Assert.Single(dialogs.Calls).Title);
    }

    [Fact]
    public async Task Cancelling_browse_changes_nothing()
    {
        var dialogs = new FakeDialogService().EnqueueFile(null);
        using var rig = new Rig(new AppSettings { TerminalShellPath = "/bin/sh" }, dialogs: dialogs);

        await rig.Vm.BrowseShellCommand.ExecuteAsync(null);

        Assert.Equal("/bin/sh", rig.Settings.Current.TerminalShellPath);
    }

    // ── Issue autolinks ──────────────────────────────────────────────────────

    [Fact]
    public void Add_rule_appends_an_empty_rule()
    {
        using var rig = new Rig(new AppSettings { IssueAutolinks = [new IssueAutolinkRule { Pattern = "a", UrlTemplate = "u" }] });

        rig.Vm.AddRuleCommand.Execute(null);

        Assert.Equal(2, rig.Settings.Current.IssueAutolinks.Count);
        Assert.Equal("", rig.Settings.Current.IssueAutolinks[1].Pattern);
        Assert.Equal(2, rig.Vm.IssueAutolinks.Count);
    }

    [Fact]
    public void Editing_a_rule_changes_only_that_rule()
    {
        using var rig = new Rig(new AppSettings
        {
            IssueAutolinks =
            [
                new IssueAutolinkRule { Pattern = "a", UrlTemplate = "ua" },
                new IssueAutolinkRule { Pattern = "b", UrlTemplate = "ub" },
            ],
        });

        rig.Vm.IssueAutolinks[1].Pattern = "#(\\d+)";
        rig.Vm.IssueAutolinks[1].UrlTemplate = "https://tracker.example/$1";

        Assert.Equal("a", rig.Settings.Current.IssueAutolinks[0].Pattern);
        Assert.Equal("ua", rig.Settings.Current.IssueAutolinks[0].UrlTemplate);
        Assert.Equal("#(\\d+)", rig.Settings.Current.IssueAutolinks[1].Pattern);
        Assert.Equal("https://tracker.example/$1", rig.Settings.Current.IssueAutolinks[1].UrlTemplate);
    }

    [Fact]
    public void Removing_a_rule_drops_it_and_numbers_the_rest_again()
    {
        using var rig = new Rig(new AppSettings
        {
            IssueAutolinks =
            [
                new IssueAutolinkRule { Pattern = "a", UrlTemplate = "ua" },
                new IssueAutolinkRule { Pattern = "b", UrlTemplate = "ub" },
                new IssueAutolinkRule { Pattern = "c", UrlTemplate = "uc" },
            ],
        });

        rig.Vm.IssueAutolinks[0].RemoveCommand.Execute(null);
        Assert.Equal(["b", "c"], rig.Settings.Current.IssueAutolinks.Select(r => r.Pattern));

        rig.Vm.IssueAutolinks[1].Pattern = "z";
        Assert.Equal(["b", "z"], rig.Settings.Current.IssueAutolinks.Select(r => r.Pattern));
    }

    [Fact]
    public void The_rule_rows_are_replaced_only_when_the_saved_rules_differ()
    {
        using var rig = new Rig(new AppSettings { IssueAutolinks = [new IssueAutolinkRule { Pattern = "a", UrlTemplate = "ua" }] });
        var rows = rig.Vm.IssueAutolinks;

        rig.Settings.UpdateAsync(s => s with { PullWithRebase = true });
        Assert.Same(rows, rig.Vm.IssueAutolinks);

        rig.Vm.IssueAutolinks[0].Pattern = "b";
        Assert.Same(rows[0], rig.Vm.IssueAutolinks[0]);

        rig.Settings.UpdateAsync(s => s with { IssueAutolinks = [new IssueAutolinkRule { Pattern = "other", UrlTemplate = "x" }] });
        Assert.NotSame(rows, rig.Vm.IssueAutolinks);
        Assert.Equal("other", Assert.Single(rig.Vm.IssueAutolinks).Pattern);
    }

    [Fact]
    public void Rebuilding_the_rule_rows_for_a_change_made_elsewhere_saves_nothing_more()
    {
        var saves = 0;
        using var rig = new Rig(new AppSettings { IssueAutolinks = [new IssueAutolinkRule { Pattern = "a", UrlTemplate = "ua" }] });
        rig.Settings.Changed += (_, _) => saves++;

        rig.Settings.UpdateAsync(s => s with { IssueAutolinks = [new IssueAutolinkRule { Pattern = "x", UrlTemplate = "y" }] });

        Assert.Equal(1, saves);
        Assert.Equal("x", Assert.Single(rig.Vm.IssueAutolinks).Pattern);
    }

    // ── Sections ─────────────────────────────────────────────────────────────

    [Fact]
    public void Sections_start_collapsed_and_toggle()
    {
        using var rig = new Rig();
        SettingsSectionViewModel[] sections = [rig.Vm.General, rig.Vm.Network, rig.Vm.Diff, rig.Vm.Git, rig.Vm.Terminal, rig.Vm.Advanced];
        Assert.All(sections, s => Assert.False(s.IsExpanded));

        rig.Vm.Git.ToggleCommand.Execute(null);

        Assert.True(rig.Vm.Git.IsExpanded);
        Assert.Equal("▼", rig.Vm.Git.Glyph);
        Assert.All(sections.Where(s => s != rig.Vm.Git), s => Assert.False(s.IsExpanded));

        rig.Vm.Git.ToggleCommand.Execute(null);
        Assert.False(rig.Vm.Git.IsExpanded);
        Assert.Equal("▶", rig.Vm.Git.Glyph);
    }

    [Fact]
    public void Only_the_general_and_network_sections_carry_a_hint()
    {
        using var rig = new Rig();

        Assert.True(rig.Vm.General.HasHint);
        Assert.Equal("Identity and SSH-only are set per repository, in Repository settings.", rig.Vm.Network.Hint);
        Assert.False(rig.Vm.Diff.HasHint);
        Assert.False(rig.Vm.Git.HasHint);
        Assert.False(rig.Vm.Terminal.HasHint);
        Assert.False(rig.Vm.Advanced.HasHint);
    }

    [Fact]
    public void Without_the_AI_window_there_is_no_AI_section()
    {
        using var rig = new Rig();

        Assert.Null(rig.Vm.Ai);
    }

    [Fact]
    public void The_AI_section_starts_in_step_with_the_window()
    {
        var panel = new AiIntegrationsPanel();
        panel.Open();
        using var rig = new Rig(panel: panel);

        Assert.True(rig.Vm.Ai!.IsExpanded);
        Assert.Equal("AI Integration", rig.Vm.Ai.Title);
    }

    [Fact]
    public void Expanding_and_collapsing_the_AI_section_opens_and_closes_the_window()
    {
        var panel = new AiIntegrationsPanel();
        using var rig = new Rig(panel: panel);

        rig.Vm.Ai!.Toggle();
        Assert.True(panel.IsOpen);

        rig.Vm.Ai.Toggle();
        Assert.False(panel.IsOpen);
    }

    [Fact]
    public void The_AI_section_follows_the_window_without_opening_it_again()
    {
        var panel = new AiIntegrationsPanel();
        var opens = 0;
        panel.OpenChanged += (_, _) => opens++;
        using var rig = new Rig(panel: panel);

        panel.Open("profile-1");
        Assert.True(rig.Vm.Ai!.IsExpanded);
        Assert.Equal(1, opens);
        Assert.Equal("profile-1", panel.FocusProfileId);

        panel.Close();
        Assert.False(rig.Vm.Ai.IsExpanded);
        Assert.Equal(2, opens);
    }

    [Fact]
    public void The_AI_section_follows_the_window_through_the_dispatcher()
    {
        var ui = new RecordingUiDispatcher();
        var panel = new AiIntegrationsPanel();
        using var rig = new Rig(ui: ui, panel: panel);
        ui.DrainPosted();

        panel.Open();
        Assert.False(rig.Vm.Ai!.IsExpanded);

        ui.DrainPosted();
        Assert.True(rig.Vm.Ai.IsExpanded);
    }

    [Fact]
    public void A_disposed_page_stops_following_the_AI_window()
    {
        var panel = new AiIntegrationsPanel();
        var rig = new Rig(panel: panel);

        rig.Vm.Dispose();
        panel.Open();

        Assert.False(rig.Vm.Ai!.IsExpanded);
    }

    // ── historia-cmdr on the user PATH ───────────────────────────────────────

    [Fact]
    public async Task An_unsupported_status_hides_the_command_line_row()
    {
        using var rig = new Rig(cmdrStatus: new CmdrPathStatus.Unavailable());
        await rig.StatusAsync();

        Assert.False(rig.Vm.ShowCommandLine);
        Assert.False(rig.Vm.CanAddToPath);
    }

    [Fact]
    public async Task A_failing_status_check_hides_the_command_line_row()
    {
        using var rig = new Rig(cmdrThrows: true);
        await rig.StatusAsync();

        Assert.Equal(1, rig.Cmdr.StatusCalls);
        Assert.False(rig.Vm.ShowCommandLine);
        Assert.False(rig.Vm.CanAddToPath);
    }

    [Fact]
    public async Task A_current_status_shows_a_disabled_On_PATH_button()
    {
        using var rig = new Rig(cmdrStatus: new CmdrPathStatus.Current());
        await rig.StatusAsync();

        Assert.True(rig.Vm.ShowCommandLine);
        Assert.Equal("On PATH", rig.Vm.CommandLineButtonText);
        Assert.False(rig.Vm.CanAddToPath);
        Assert.False(rig.Vm.AddToPathCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_status_that_needs_an_update_shows_an_enabled_Add_to_PATH_button()
    {
        using var rig = new Rig(cmdrStatus: NeedsUpdate());
        await rig.StatusAsync();

        Assert.True(rig.Vm.ShowCommandLine);
        Assert.Equal("Add to PATH", rig.Vm.CommandLineButtonText);
        Assert.True(rig.Vm.CanAddToPath);
        Assert.True(rig.Vm.AddToPathCommand.CanExecute(null));
    }

    [Fact]
    public async Task Add_to_PATH_names_the_effect_and_cancelling_skips_the_apply()
    {
        var preview = new PathChange(null, @"C:\NewCmdr", @"C:\NewCmdr", [@"C:\OldCmdr1", @"C:\OldCmdr2"], false);
        var dialogs = new FakeDialogService().EnqueueConfirmDestructive(false);
        using var rig = new Rig(cmdrStatus: NeedsUpdate(preview, @"C:\System\cmdr"), dialogs: dialogs);
        await rig.StatusAsync();

        await rig.Vm.AddToPathCommand.ExecuteAsync(null);

        var call = Assert.Single(dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Update PATH", call.Title);
        Assert.Equal("Add to PATH", call.ConfirmLabel);
        Assert.Contains(@"Adds C:\NewCmdr to your user PATH.", call.Effect);
        Assert.Contains(@"C:\OldCmdr1", call.Effect);
        Assert.Contains(@"C:\OldCmdr2", call.Effect);
        Assert.Contains(@"C:\System\cmdr", call.Effect);
        Assert.Contains(@"backed up to C:\Backups", call.Effect);
        Assert.Empty(rig.Cmdr.ApplyCalls);
        Assert.True(rig.Vm.CanAddToPath);
    }

    [WindowsFact]
    public async Task The_effect_says_keeps_when_nothing_is_added_and_lists_duplicates_separately()
    {
        var preview = new PathChange(null, @"C:\NewCmdr", null, [@"C:\Old", @"C:\NewCmdr"], false);
        var dialogs = new FakeDialogService().EnqueueConfirmDestructive(false);
        using var rig = new Rig(cmdrStatus: NeedsUpdate(preview), dialogs: dialogs);
        await rig.StatusAsync();

        await rig.Vm.AddToPathCommand.ExecuteAsync(null);

        var effect = Assert.Single(dialogs.ConfirmDestructiveCalls).Effect;
        Assert.DoesNotContain("Adds", effect);
        Assert.Contains(@"Keeps C:\NewCmdr on your user PATH.", effect);
        Assert.Contains("Removes from your user PATH:", effect);
        Assert.Contains(@"C:\Old", effect);
        Assert.Contains("Removes duplicates of that folder:", effect);
    }

    [Fact]
    public async Task Confirming_applies_the_previewed_change_once_and_checks_the_status_again()
    {
        var preview = new PathChange(null, @"C:\NewCmdr", @"C:\NewCmdr", [], false);
        var dialogs = new FakeDialogService().EnqueueConfirmDestructive(true);
        using var rig = new Rig(cmdrStatus: NeedsUpdate(preview), dialogs: dialogs);
        await rig.StatusAsync();

        rig.Cmdr.StatusToReturn = new CmdrPathStatus.Current();
        await rig.Vm.AddToPathCommand.ExecuteAsync(null);
        await rig.StatusAsync();

        Assert.Equal(preview, Assert.Single(rig.Cmdr.ApplyCalls));
        Assert.DoesNotContain(dialogs.Calls, c => c.Method == nameof(IDialogService.ShowMessageAsync));
        Assert.Equal(2, rig.Cmdr.StatusCalls);
        Assert.Equal("On PATH", rig.Vm.CommandLineButtonText);
    }

    [Theory]
    [InlineData("changed", "PATH changed while the dialog was open; try again.")]
    [InlineData("failed", "Disk full")]
    public async Task A_result_that_did_not_write_shows_a_message(string result, string message)
    {
        var dialogs = new FakeDialogService().EnqueueConfirmDestructive(true);
        using var rig = new Rig(cmdrStatus: NeedsUpdate(), dialogs: dialogs);
        rig.Cmdr.ApplyResultToReturn = result == "changed" ? new CmdrPathApplyResult.Changed() : new CmdrPathApplyResult.Failed("Disk full");
        await rig.StatusAsync();

        await rig.Vm.AddToPathCommand.ExecuteAsync(null);

        var shown = Assert.Single(dialogs.Calls, c => c.Method == nameof(IDialogService.ShowMessageAsync));
        Assert.Equal("Update PATH", shown.Title);
        Assert.Equal(message, shown.Detail);
    }

    [Fact]
    public async Task A_throwing_apply_shows_a_message_and_checks_the_status_again()
    {
        var dialogs = new FakeDialogService().EnqueueConfirmDestructive(true);
        using var rig = new Rig(cmdrStatus: NeedsUpdate(), dialogs: dialogs);
        rig.Cmdr.ThrowFromApply = true;
        await rig.StatusAsync();

        await rig.Vm.AddToPathCommand.ExecuteAsync(null);
        await rig.StatusAsync();

        var shown = Assert.Single(dialogs.Calls, c => c.Method == nameof(IDialogService.ShowMessageAsync));
        Assert.Equal("Apply error", shown.Detail);
        Assert.Equal(2, rig.Cmdr.StatusCalls);
    }

    [Fact]
    public async Task The_button_stays_disabled_while_the_apply_runs_and_until_the_new_status_arrives()
    {
        var ui = new RecordingUiDispatcher();
        var dialogs = new FakeDialogService().EnqueueConfirmDestructive(true);
        using var rig = new Rig(ui: ui, cmdrStatus: NeedsUpdate(), dialogs: dialogs);
        rig.Cmdr.ApplyGate = new TaskCompletionSource<bool>();
        await rig.StatusAsync();
        Assert.True(rig.Vm.CanAddToPath);

        var run = rig.Vm.AddToPathCommand.ExecuteAsync(null);
        await Task.Yield();
        Assert.False(rig.Vm.CanAddToPath);
        Assert.False(rig.Vm.AddToPathCommand.CanExecute(null));

        // The status check that follows the write sees the new state; its result waits in the
        // dispatcher until the test lets it land.
        rig.Cmdr.StatusToReturn = new CmdrPathStatus.Current();
        rig.Cmdr.ApplyGate.SetResult(true);
        await run;
        Assert.False(rig.Vm.CanAddToPath);
        Assert.Equal("Add to PATH", rig.Vm.CommandLineButtonText);

        await rig.StatusAsync();
        Assert.Equal("On PATH", rig.Vm.CommandLineButtonText);
        Assert.False(rig.Vm.CanAddToPath);
    }

    [Fact]
    public async Task The_status_reaches_the_page_through_the_dispatcher()
    {
        var ui = new RecordingUiDispatcher();
        using var rig = new Rig(ui: ui, cmdrStatus: NeedsUpdate());
        await rig.Vm.CmdrStatusTask;

        Assert.False(rig.Vm.ShowCommandLine);
        ui.DrainPosted();
        Assert.True(rig.Vm.ShowCommandLine);
    }

    [Fact]
    public async Task A_status_that_lands_after_disposal_is_ignored()
    {
        var ui = new RecordingUiDispatcher();
        var rig = new Rig(ui: ui, cmdrStatus: NeedsUpdate());
        await rig.Vm.CmdrStatusTask;

        rig.Vm.Dispose();
        ui.DrainPosted();

        Assert.False(rig.Vm.ShowCommandLine);
    }
}
