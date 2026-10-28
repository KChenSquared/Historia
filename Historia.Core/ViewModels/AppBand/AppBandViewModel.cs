namespace Historia.ViewModels;

/// <summary>
/// The window's app band: the Historia menu and the strip of repository tabs. The view model owns
/// the tabs (one per open repository, plus a pending tab while a clone runs), which one is active,
/// the developer auto-fetch bars, the menu entries and the reordering of tabs. The view owns the
/// pointer gesture, the edge-scroll timer and the drag ghost, and calls <see cref="BeginDrag"/>,
/// <see cref="ReorderTo"/> and <see cref="CompleteDrag"/> as the gesture goes.
/// </summary>
public sealed partial class AppBandViewModel : ViewModelBase
{
    /// <summary>The most characters of a repository name a dynamic-width tab shows.</summary>
    public const int TabNameMaxChars = 25;

    /// <summary>The average width of one character, used to cap a dynamic-width tab's name.</summary>
    public const double ApproxCharPx = 7.2;

    private readonly IUiDispatcher _ui;
    private readonly IWorkspaceService _workspace;
    private readonly IAutoFetchService _autoFetch;
    private readonly ITerminalSessionService _terminals;
    private readonly INavigationBus _navigation;
    private readonly IProfileService _profiles;
    private readonly ISettingsService _settings;
    private readonly IExternalLauncher _launcher;
    private readonly IScrollDirectionProbe _scrollProbe;
    private readonly ICloneService _clone;
    private readonly IServiceProvider _services;
    private readonly IUnsavedWorkRegistry? _unsavedWork;
    private readonly IDialogService? _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly IAppBandPlatform _platform;
    private readonly string _homeDirectory;

    // The pending clone tab is on screen. A progress tick only rebuilds the strip when this flips.
    private bool _cloneTabWasPresent;

    private AppBandItemViewModel? _dragTab;
    private int _dragFromIndex = -1;

    // Reused across the per-pointer-move reorders so the drag path allocates only when a tab moves.
    private readonly List<double> _groupWidths = [];
    private readonly List<int> _groupStarts = [];
    private readonly List<int> _groupSizes = [];

    public AppBandViewModel(
        IUiDispatcher ui,
        IWorkspaceService workspace,
        IAutoFetchService autoFetch,
        ITerminalSessionService terminals,
        INavigationBus navigation,
        IProfileService profiles,
        ISettingsService settings,
        IExternalLauncher launcher,
        IScrollDirectionProbe scrollProbe,
        ICloneService clone,
        IServiceProvider services,
        IUnsavedWorkRegistry? unsavedWork = null,
        IDialogService? dialogs = null,
        IClipboardService? clipboard = null,
        IAppBandPlatform? platform = null,
        string? homeDirectory = null)
    {
        _ui = ui;
        _workspace = workspace;
        _autoFetch = autoFetch;
        _terminals = terminals;
        _navigation = navigation;
        _profiles = profiles;
        _settings = settings;
        _launcher = launcher;
        _scrollProbe = scrollProbe;
        _clone = clone;
        _services = services;
        _unsavedWork = unsavedWork;
        _dialogs = dialogs;
        _clipboard = clipboard ?? NullClipboardService.Instance;
        _platform = platform ?? NullAppBandPlatform.Instance;
        _homeDirectory = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddTab = new AddTabViewModel(this);

        ReadSettings();

        // Each of these services raises on the thread that changed it; the handlers touch bound state.
        Subscribe(() => _workspace.OpenRepositoriesChanged += OnOpenRepositoriesChanged, () => _workspace.OpenRepositoriesChanged -= OnOpenRepositoriesChanged);
        Subscribe(() => _workspace.FocusedRepositoryChanged += OnFocusedRepositoryChanged, () => _workspace.FocusedRepositoryChanged -= OnFocusedRepositoryChanged);
        Subscribe(() => _profiles.ProfilesChanged += OnProfilesChanged, () => _profiles.ProfilesChanged -= OnProfilesChanged);
        Subscribe(() => _settings.Changed += OnSettingsChanged, () => _settings.Changed -= OnSettingsChanged);
        Subscribe(() => _autoFetch.TabStatesChanged += OnAutoFetchStatesChanged, () => _autoFetch.TabStatesChanged -= OnAutoFetchStatesChanged);
        Subscribe(() => _platform.AutoFetchBarsVisibleChanged += OnAutoFetchBarsVisibleChanged, () => _platform.AutoFetchBarsVisibleChanged -= OnAutoFetchBarsVisibleChanged);
        Subscribe(() => _clone.Changed += OnCloneChanged, () => _clone.Changed -= OnCloneChanged);

        AppMenu = BuildAppMenu();
        _cloneTabWasPresent = _clone.Current is not null;
        RebuildTabs();
    }

    /// <summary>The tabs in strip order: one per open repository, then the pending clone tab when a clone runs.
    /// Replaced as a whole whenever the strip is rebuilt or a tab is moved.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AppBandItemViewModel> Tabs { get; private set; } = [];

    /// <summary>What the strip shows: <see cref="Tabs"/> followed by the <see cref="AddTab"/> button.
    /// The tabs keep their indexes.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<object> StripItems { get; private set; } = [];

    /// <summary>The "+" button at the end of the strip.</summary>
    public AddTabViewModel AddTab { get; }

    /// <summary>The Historia menu. Replaced as a whole when the profiles change.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AppMenuEntry> AppMenu { get; private set; } = [];

    /// <summary>True while a tab is being dragged. The strip is not rebuilt during a drag.</summary>
    public bool IsDragging { get; private set; }

    /// <summary>True when the tabs wrap onto several rows instead of scrolling along one.</summary>
    public bool IsMultiLine { get; private set; }

    /// <summary>The most rows the strip grows to when <see cref="IsMultiLine"/> is on.</summary>
    public int MaxRows { get; private set; } = 3;

    /// <summary>True when every tab has the same configured width.</summary>
    public bool IsFixedTabWidth { get; private set; }

    /// <summary>The width of a tab when <see cref="IsFixedTabWidth"/> is on.</summary>
    public double FixedTabWidth { get; private set; } = AppSettings.DefaultFixedTabWidth;

    /// <summary>Raised after the strip has been rebuilt, never for a drag move.</summary>
    public event EventHandler? TabsRebuilt;

    /// <summary>Raised when a settings change has been read, just before the strip is rebuilt for it.</summary>
    public event EventHandler? TabSettingsChanged;

    /// <summary>The palette key a tab's auto-fetch bar is coloured with.</summary>
    public static string AutoFetchBarToken(AutoFetchTabState state) => state switch
    {
        AutoFetchTabState.FetchedThisCycle => "ColorSuccess",
        AutoFetchTabState.PendingThisCycle => "ColorWarning",
        _ => "ColorDanger",
    };

    // ── navigation ──────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Navigate(string route) => RequestRoute(route);

    internal void RequestRoute(string route) => _navigation.RequestNavigate(route);

    /// <summary>Opening the menu from a repository view switches the detail pane to Home; the
    /// Settings, Developer and Profiles panels stay open so the menu can act on them.</summary>
    public void PrepareAppMenu()
    {
        var head = (_navigation.CurrentRoute ?? "").Split('?', 2)[0].TrimStart('/');
        if (head is not ("" or "settings" or "dev" or "profiles"))
            _navigation.RequestNavigate("");
    }

    /// <summary>The direction multiplier for a wheel delta over the strip, from the wheel-direction
    /// setting and the system's natural-scrolling preference.</summary>
    public double WheelSign()
        => WheelInput.Sign(_settings.Current.WheelScrollDirection, _scrollProbe.NaturalScrolling);

    // ── app menu ────────────────────────────────────────────────────────────────────────────

    private IReadOnlyList<AppMenuEntry> BuildAppMenu()
    {
        var items = new List<AppMenuEntry>
        {
            NavigateEntry("Open Repository…", ""),
            NavigateEntry("Clone Repository…", ""),
            NavigateEntry("Initialize Repository…", ""),
            AppMenuEntry.Separator(),
        };

        var profileItems = new List<AppMenuEntry>();
        var defaultProfile = _profiles.GetDefaultProfile();
        foreach (var profile in _profiles.GetAllProfiles())
        {
            var chosen = profile;
            profileItems.Add(AppMenuEntry.Item(
                chosen.IsGlobal ? "Global (~/.gitconfig)" : chosen.DisplayName,
                new AsyncRelayCommand(() => SetDefaultProfileAsync(chosen)),
                isChecked: defaultProfile is not null && defaultProfile.Id == chosen.Id));
        }
        if (profileItems.Count > 0)
            profileItems.Add(AppMenuEntry.Separator());
        profileItems.Add(NavigateEntry("Manage profiles…", "profiles"));
        items.Add(AppMenuEntry.Submenu("Default profile", profileItems));

        items.Add(AppMenuEntry.Item("Open Terminal", new AsyncRelayCommand(OpenHomeTerminalAsync)));
        items.Add(NavigateEntry("Settings", "settings"));
        if (_platform.DeveloperMenuEnabled)
            items.Add(NavigateEntry("Developer", "dev"));
        items.Add(AppMenuEntry.Separator());
        items.Add(AppMenuEntry.Item("About Historia", new RelayCommand(_platform.ShowAbout)));
        items.Add(AppMenuEntry.Item("Exit", new RelayCommand(_platform.Exit)));
        return items;
    }

    private AppMenuEntry NavigateEntry(string header, string route)
        => AppMenuEntry.Item(header, new RelayCommand(() => _navigation.RequestNavigate(route)));

    private async Task SetDefaultProfileAsync(GitProfile profile)
    {
        try
        {
            await DefaultProfileFlow.SetAsync(_services, profile);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "AppBand", "Setting the default profile failed.", ex);
        }
    }

    // The menu's sole terminal item always adds another Home shell and focuses it.
    private async Task OpenHomeTerminalAsync()
    {
        try
        {
            await _terminals.OpenNewAsync(TerminalOrigin.Home(_homeDirectory));
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "AppBand", "Opening a Home terminal failed.", ex);
        }
    }

    // ── service events ──────────────────────────────────────────────────────────────────────

    private void OnOpenRepositoriesChanged(object? sender, OpenRepositoriesChangedEventArgs e)
        => _ui.Post(() =>
        {
            if (!IsDisposed)
                RebuildTabs();
        });

    private void OnFocusedRepositoryChanged(object? sender, FocusedRepositoryChangedEventArgs e)
        => _ui.Post(() =>
        {
            if (IsDisposed)
                return;
            RebuildTabs();
            // The workspace raises this before it waits for a running write on the closed tab, so
            // the detail pane leaves the repository route as soon as the last tab is gone.
            if (e.New is null && _workspace.OpenRepositories.Count == 0)
                _navigation.RequestNavigate("");
        });

    private void OnProfilesChanged(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            if (!IsDisposed)
                AppMenu = BuildAppMenu();
        });

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
        => _ui.Post(() =>
        {
            if (IsDisposed)
                return;
            ReadSettings();
            // The default profile is a setting, so its check mark in the menu follows it.
            AppMenu = BuildAppMenu();
            TabSettingsChanged?.Invoke(this, EventArgs.Empty);
            // The tab-width mode and the fixed width are baked into each tab.
            RebuildTabs();
        });

    private void OnAutoFetchStatesChanged(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            if (!IsDisposed)
                RecolorAutoFetchBars();
        });

    private void OnAutoFetchBarsVisibleChanged()
        => _ui.Post(() =>
        {
            if (!IsDisposed)
                ApplyAutoFetchBarVisibility();
        });

    private void OnCloneChanged(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            if (!IsDisposed)
                OnCloneStatusChanged();
        });

    private void ReadSettings()
    {
        var settings = _settings.Current;
        IsMultiLine = settings.EnableMultiLineTabs;
        MaxRows = Math.Clamp(settings.MaxTabRows, 2, 5);
        IsFixedTabWidth = settings.TabWidthMode == TabWidthMode.Fixed;
        FixedTabWidth = AppSettings.ClampFixedTabWidth(settings.FixedTabWidth);
    }

    // The clone service raises Changed on every progress tick. Rebuilding the strip per tick would
    // rebuild every repository tab dozens of times a second, so the strip is rebuilt only when a
    // clone starts or ends; otherwise the pending tab updates in place.
    private void OnCloneStatusChanged()
    {
        var status = _clone.Current;
        var running = status is not null;
        if (running != _cloneTabWasPresent)
        {
            _cloneTabWasPresent = running;
            RebuildTabs();
            return;
        }
        if (status is null)
            return;
        foreach (var tab in Tabs)
        {
            if (tab is CloneTabViewModel pending)
                pending.Update(status);
        }
    }

    // ── auto-fetch bars ─────────────────────────────────────────────────────────────────────

    private void ApplyAutoFetchBarVisibility()
    {
        var visible = _platform.AutoFetchBarsVisible;
        foreach (var tab in Tabs)
        {
            if (tab is RepoTabViewModel repo)
                repo.SetAutoFetchBarVisible(visible);
        }
        RecolorAutoFetchBars();
    }

    private void RecolorAutoFetchBars()
    {
        if (!_platform.AutoFetchBarsVisible)
            return;
        foreach (var tab in Tabs)
        {
            if (tab is RepoTabViewModel repo)
                repo.SetAutoFetchState(_autoFetch.GetTabState(repo.Context));
        }
    }

    // ── tabs ────────────────────────────────────────────────────────────────────────────────

    private void RebuildTabs()
    {
        if (IsDragging)
            return;

        var open = _workspace.OpenRepositories;
        var focused = _workspace.Focused;
        var fixedWidth = IsFixedTabWidth;
        var bodyWidth = fixedWidth ? FixedTabWidth : double.NaN;
        var nameMaxWidth = fixedWidth ? double.PositiveInfinity : TabNameMaxChars * ApproxCharPx;
        var showBars = _platform.AutoFetchBarsVisible;

        var tabs = new List<AppBandItemViewModel>(open.Count + 1);
        foreach (var repository in open)
        {
            tabs.Add(new RepoTabViewModel(
                this,
                repository,
                ReferenceEquals(repository, focused),
                bodyWidth,
                nameMaxWidth,
                _autoFetch.GetTabState(repository),
                showBars));
        }

        // The clone service adds the real repository, raising OpenRepositoriesChanged, before it
        // clears Current, so for one rebuild both exist. The pending tab is suppressed once its
        // target folder is open, so that window never shows a duplicate.
        if (_clone.Current is { } status
            && !open.Any(repository => PathsEqual(repository.RepositoryPath, status.TargetDirectory)))
        {
            tabs.Add(new CloneTabViewModel(this, status));
        }

        SetTabs(tabs);
        TabsRebuilt?.Invoke(this, EventArgs.Empty);
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            a.TrimEnd('/', '\\'),
            b.TrimEnd('/', '\\'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // The first tab and every linked tab have no leading separator, so a group reads as one
    // continuous unit with only its owner's separator showing.
    private void SetTabs(List<AppBandItemViewModel> tabs)
    {
        for (var i = 0; i < tabs.Count; i++)
            tabs[i].SetLeadingSeparator(i > 0 && tabs[i] is not RepoTabViewModel { IsLinked: true });

        List<object> items = [.. tabs, AddTab];
        Tabs = tabs;
        StripItems = items;
    }

    internal void ActivateTab(RepositoryContext context)
    {
        _workspace.Focus(context);
        _navigation.RequestNavigate("repo");
    }

    internal async Task CloseTabAsync(RepositoryContext context)
    {
        try
        {
            if (_unsavedWork is not null && _dialogs is not null)
            {
                var work = _unsavedWork.For(context);
                if (work.Count > 0)
                {
                    var proceed = await UnsavedWorkPrompt.ResolveAsync(work, _dialogs);
                    if (!proceed)
                        return;
                }
            }

            await _workspace.CloseAsync(context);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "AppBand", "Closing a repository tab failed.", ex);
        }
    }

    internal async Task OpenTabTerminalAsync(RepositoryContext context)
    {
        try
        {
            _workspace.Focus(context);
            await _terminals.OpenOrFocusAsync(RepoTerminalOrigin.For(context));
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "AppBand", "Opening the repository terminal failed.", ex);
        }
    }

    internal void RevealTab(RepositoryContext context) => _launcher.OpenFolder(context.RepositoryPath);

    internal async Task CopyTabPathAsync(RepositoryContext context)
    {
        try
        {
            await _clipboard.SetTextAsync(context.RepositoryPath);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "AppBand", "Copying a repository path failed.", ex);
        }
    }

    internal void CancelClone() => _clone.Cancel();

    // ── reordering ──────────────────────────────────────────────────────────────────────────

    /// <summary>Starts a drag of <paramref name="tab"/>. The strip is frozen against rebuilds until
    /// <see cref="CompleteDrag"/> or <see cref="CancelDrag"/>. A pending clone tab cannot be dragged.</summary>
    public bool BeginDrag(AppBandItemViewModel tab)
    {
        if (IsDragging || tab is not RepoTabViewModel)
            return false;
        var index = IndexOf(Tabs, tab);
        if (index < 0)
            return false;

        _dragTab = tab;
        _dragFromIndex = index;
        IsDragging = true;
        tab.SetDragging(true);
        return true;
    }

    /// <summary>
    /// The live preview of a drag: moves the dragged tab to the slot under a cursor at
    /// <paramref name="panelX"/> (strip space), given the laid-out width of every tab in
    /// <see cref="Tabs"/> order. A width of zero or less (a tab not measured yet) counts as
    /// <paramref name="fallbackWidth"/>. Returns whether the order changed.
    /// </summary>
    public bool ReorderTo(double panelX, IReadOnlyList<double> tabWidths, double fallbackWidth)
    {
        if (_dragTab is not RepoTabViewModel dragged || tabWidths.Count == 0)
            return false;
        var tabs = Tabs;
        var current = IndexOf(tabs, dragged);
        if (current < 0)
            return false;

        if (dragged.IsLinked)
            return MoveTab(current, AppBandDragReorder.TargetIndex(panelX, tabWidths, fallbackWidth));

        // An owner or standalone tab moves its whole group as one block. Each group's member
        // widths sum into one width, the same slot maths runs over the groups, and the winning
        // group slot maps back onto a tab index.
        _groupWidths.Clear();
        _groupStarts.Clear();
        _groupSizes.Clear();
        var i = 0;
        while (i < tabs.Count && tabs[i] is RepoTabViewModel owner)
        {
            var start = i;
            var width = WidthOrFallback(tabWidths, i, fallbackWidth);
            i++;
            while (i < tabs.Count && tabs[i] is RepoTabViewModel sibling && ReferenceEquals(sibling.Context.LinkOwner, owner.Context))
            {
                width += WidthOrFallback(tabWidths, i, fallbackWidth);
                i++;
            }
            _groupWidths.Add(width);
            _groupStarts.Add(start);
            _groupSizes.Add(i - start);
        }

        var (groupStart, _) = GroupSpan(tabs, dragged.Context);
        var currentGroup = _groupStarts.IndexOf(groupStart);
        if (currentGroup < 0)
            return false;

        var targetGroup = AppBandDragReorder.TargetIndex(panelX, _groupWidths, fallbackWidth);
        if (targetGroup == currentGroup)
            return false;

        return MoveTab(current, AppBandDragReorder.BlockInsertIndex(_groupSizes, currentGroup, targetGroup));
    }

    private static double WidthOrFallback(IReadOnlyList<double> widths, int index, double fallback)
        => index < widths.Count && widths[index] > 0 ? widths[index] : fallback;

    /// <summary>
    /// Moves the repository tab at <paramref name="from"/> so it ends at index <paramref name="to"/>.
    /// An owner or standalone tab takes its whole group (itself and the tabs linked to it) along as
    /// one block, and <paramref name="to"/> is where the group starts after the move; it is clamped
    /// so a group never lands after the pending clone tab. A linked tab only moves among its own
    /// group's other members: <paramref name="to"/> is clamped into that range. The active tab stays
    /// active. Returns whether the order changed. Only the strip changes; the workspace order is
    /// updated when a drag completes.
    /// </summary>
    public bool MoveTab(int from, int to)
    {
        var tabs = Tabs;
        if ((uint)from >= (uint)tabs.Count || tabs[from] is not RepoTabViewModel moving)
            return false;

        var (start, count) = GroupSpan(tabs, moving.Context);
        if (start < 0)
            return false;

        var reordered = new List<AppBandItemViewModel>(tabs);
        if (moving.IsLinked)
        {
            var target = AppBandDragReorder.ClampChildTarget(to, start, count);
            if (target == from || target == start)
                return false;
            reordered.RemoveAt(from);
            reordered.Insert(Math.Clamp(target, 0, reordered.Count), moving);
        }
        else
        {
            var block = reordered.GetRange(start, count);
            reordered.RemoveRange(start, count);
            var repositoryTabs = reordered.Count(tab => tab is RepoTabViewModel);
            var insertAt = Math.Clamp(to, 0, repositoryTabs);
            if (insertAt == start)
                return false;
            reordered.InsertRange(insertAt, block);
        }

        SetTabs(reordered);
        return true;
    }

    /// <summary>Ends a drag. A tab that left its slot is moved in the workspace, which raises the
    /// open-repositories event that rebuilds the strip; a tab dropped back in its own slot
    /// triggers the rebuild itself.</summary>
    public void CompleteDrag()
    {
        var tab = _dragTab;
        var from = _dragFromIndex;
        EndDrag();

        var to = tab is null ? -1 : IndexOf(Tabs, tab);
        if (tab is RepoTabViewModel repo && to >= 0 && to != from)
            _workspace.Move(repo.Context, to);
        else
            RebuildTabs();
    }

    /// <summary>Abandons a drag (the pointer capture was lost) and restores the strip to the workspace order.</summary>
    public void CancelDrag()
    {
        EndDrag();
        RebuildTabs();
    }

    private void EndDrag()
    {
        _dragTab?.SetDragging(false);
        _dragTab = null;
        _dragFromIndex = -1;
        IsDragging = false;
    }

    private static int IndexOf(IReadOnlyList<AppBandItemViewModel> tabs, AppBandItemViewModel tab)
    {
        for (var i = 0; i < tabs.Count; i++)
        {
            if (ReferenceEquals(tabs[i], tab))
                return i;
        }
        return -1;
    }

    // The contiguous tab range of the group that holds `member`: the owner (or `member` itself
    // when it is not linked) followed by every tab linked to that owner.
    private static (int Start, int Count) GroupSpan(IReadOnlyList<AppBandItemViewModel> tabs, RepositoryContext member)
    {
        var owner = member.LinkOwner ?? member;
        var start = -1;
        for (var i = 0; i < tabs.Count; i++)
        {
            if (tabs[i] is RepoTabViewModel repo && ReferenceEquals(repo.Context, owner))
            {
                start = i;
                break;
            }
        }
        if (start < 0)
            return (-1, 0);

        var count = 1;
        while (start + count < tabs.Count
               && tabs[start + count] is RepoTabViewModel sibling
               && ReferenceEquals(sibling.Context.LinkOwner, owner))
            count++;
        return (start, count);
    }
}
