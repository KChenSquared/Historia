namespace Historia.ViewModels;

/// <summary>What the centre pane of the main window shows.</summary>
public enum CenterPaneMode
{
    /// <summary>The commit graph.</summary>
    Graph,

    /// <summary>The file diff of the repository's open diff request.</summary>
    Diff,

    /// <summary>The conflict resolver of the repository.</summary>
    Conflict,

    /// <summary>The AI integrations window.</summary>
    AiIntegrations,
}

/// <summary>Carries one centre pane update.</summary>
/// <param name="RefocusDiffLine">A repeated request for the diff that is already open: the shown
/// diff keeps its view and scrolls to the request's line again.</param>
public sealed class CenterPaneChangedEventArgs(bool refocusDiffLine) : EventArgs
{
    public bool RefocusDiffLine { get; } = refocusDiffLine;
}

/// <summary>
/// Decides what the centre pane shows and keeps that decision current. Precedence: the AI
/// integrations window while it is open; else the repository's open diff; else the conflict
/// resolver while a session the user has not closed is running, or while a closed resolver still
/// holds unsaved edits after the session ended; else the graph. A new diff or a selected conflict
/// path closes the AI window so the click is not lost behind it.
/// </summary>
public sealed partial class CenterPaneViewModel : ViewModelBase
{
    private readonly IWorkspaceService? _workspace;
    private readonly IAiIntegrationsPanel? _aiPanel;
    private readonly IUiDispatcher _ui;
    private readonly Func<RepositoryContext, bool> _hasUnsavedResolverWork;

    // Requests (by instance) whose focus line a view has already scrolled to. The state keeps the
    // same instance while the diff stays open, so a view rebuilt after the AI window closes or the
    // tab is revisited leaves the scroll alone; a new request for the same line is a new instance.
    private readonly ConditionalWeakTable<DiffRequest, object> _focusLineShown = new();
    private static readonly object FocusLineShownMarker = new();

    private RepositoryContext? _activeRepo;

    /// <param name="workspace">When given, the pane follows the focused repository.</param>
    /// <param name="aiPanel">When given, the pane shows the AI window while it is open.</param>
    /// <param name="hasUnsavedResolverWork">Whether a repository's resolver view holds unsaved
    /// edits; without it no resolver is kept past its session.</param>
    public CenterPaneViewModel(
        IWorkspaceService? workspace,
        IAiIntegrationsPanel? aiPanel,
        IUiDispatcher ui,
        Func<RepositoryContext, bool>? hasUnsavedResolverWork = null)
    {
        _workspace = workspace;
        _aiPanel = aiPanel;
        _ui = ui;
        _hasUnsavedResolverWork = hasUnsavedResolverWork ?? (_ => false);

        if (_aiPanel is not null)
            Subscribe(() => _aiPanel.OpenChanged += OnAiPanelChanged, () => _aiPanel.OpenChanged -= OnAiPanelChanged);

        if (_workspace is not null)
        {
            Subscribe(() => _workspace.FocusedRepositoryChanged += OnFocusedRepositoryChanged, () => _workspace.FocusedRepositoryChanged -= OnFocusedRepositoryChanged);
            AttachRepository(_workspace.Focused);
        }

        Recompute();
    }

    /// <summary>Raised after the pane's mode or its repository's state was recomputed.</summary>
    public event EventHandler<CenterPaneChangedEventArgs>? Changed;

    /// <summary>Raised when the pane stops following <paramref name="repository"/>, before the next
    /// one is attached, so the owner can let go of what it cached for it.</summary>
    public event EventHandler<RepositoryContext>? RepositoryLeft;

    /// <summary>The repository the pane follows, or null.</summary>
    public RepositoryContext? ActiveRepo => _activeRepo;

    [ObservableProperty]
    public partial CenterPaneMode Mode { get; private set; } = CenterPaneMode.Graph;

    /// <summary>The repository's open diff request while <see cref="Mode"/> is
    /// <see cref="CenterPaneMode.Diff"/>.</summary>
    [ObservableProperty]
    public partial DiffRequest? OpenDiff { get; private set; }

    /// <summary>Follows <paramref name="ctx"/> and recomputes. Following the repository already
    /// followed only recomputes.</summary>
    public void Bind(RepositoryContext? ctx)
    {
        if (IsDisposed)
            return;

        if (!ReferenceEquals(ctx, _activeRepo))
            AttachRepository(ctx);

        Recompute();
    }

    /// <summary>Recomputes the mode from the current state and raises <see cref="Changed"/>.</summary>
    public void Refresh()
    {
        if (IsDisposed)
            return;

        Recompute();
    }

    /// <summary>
    /// True when a view for <paramref name="request"/> should open at the top because its focus
    /// line was already shown; otherwise records that the line is being shown now and returns
    /// false. A request without a focus line always returns false.
    /// </summary>
    public bool ClaimFocusLine(DiffRequest request)
    {
        if (request.FocusLine is null)
            return false;

        if (_focusLineShown.TryGetValue(request, out _))
            return true;

        _focusLineShown.Add(request, FocusLineShownMarker);
        return false;
    }

    protected override void OnDisposed() => DetachRepository();

    private void AttachRepository(RepositoryContext? ctx)
    {
        if (_activeRepo is { } previous)
        {
            DetachRepository();
            RepositoryLeft?.Invoke(this, previous);
        }

        _activeRepo = ctx;
        if (ctx is null)
            return;

        ctx.State.OpenDiffChanged += OnOpenDiffChanged;
        ctx.State.StatusChanged += OnStatusChanged;
        ctx.State.ConflictSessionChanged += OnConflictSessionChanged;
        ctx.State.ConflictResolverClosedChanged += OnConflictResolverClosedChanged;
        ctx.State.SelectedConflictPathChanged += OnSelectedConflictPathChanged;
    }

    private void DetachRepository()
    {
        if (_activeRepo is not { } ctx)
            return;

        ctx.State.OpenDiffChanged -= OnOpenDiffChanged;
        ctx.State.StatusChanged -= OnStatusChanged;
        ctx.State.ConflictSessionChanged -= OnConflictSessionChanged;
        ctx.State.ConflictResolverClosedChanged -= OnConflictResolverClosedChanged;
        ctx.State.SelectedConflictPathChanged -= OnSelectedConflictPathChanged;
    }

    private void OnFocusedRepositoryChanged(object? sender, FocusedRepositoryChangedEventArgs e)
        => _ui.Post(() => Bind(e.New));

    private void OnAiPanelChanged(object? sender, EventArgs e) => _ui.Post(Refresh);

    private void OnStatusChanged(object? sender, StatusChangedEventArgs e)
    {
        if (_ui.CheckAccess())
            Refresh();
        else
            _ui.Post(Refresh);
    }

    private void OnConflictSessionChanged(object? sender, EventArgs e) => _ui.Post(Refresh);

    private void OnConflictResolverClosedChanged(object? sender, EventArgs e) => _ui.Post(Refresh);

    private void OnOpenDiffChanged(object? sender, OpenDiffChangedEventArgs e)
        => _ui.Post(() =>
        {
            if (IsDisposed)
                return;

            if (_activeRepo?.State.OpenDiff is not null)
                _aiPanel?.Close();

            // A repeated request for the open diff keeps its view; only the line focus is replayed.
            var shown = Mode == CenterPaneMode.Diff ? OpenDiff : null;
            Recompute(e.Request is { FocusLine: not null } request ? request : null, shown);
        });

    private void OnSelectedConflictPathChanged(object? sender, SelectedConflictPathChangedEventArgs e)
        => _ui.Post(() =>
        {
            if (IsDisposed)
                return;

            if (e.Path is not null)
                _aiPanel?.Close();
            Recompute();
        });

    // replay is the request whose focus line should be shown again when shown, the diff on screen
    // before this update, is still the open one.
    private void Recompute(DiffRequest? replay = null, DiffRequest? shown = null)
    {
        var mode = ComputeMode(out var openDiff);
        Mode = mode;
        OpenDiff = openDiff;

        var refocus = false;
        if (replay is not null && shown is not null && mode == CenterPaneMode.Diff && openDiff == shown)
        {
            _focusLineShown.AddOrUpdate(replay, FocusLineShownMarker);
            refocus = true;
        }

        Changed?.Invoke(this, new CenterPaneChangedEventArgs(refocus));
    }

    private CenterPaneMode ComputeMode(out DiffRequest? openDiff)
    {
        openDiff = null;

        // A diff is re-created from the open request when the window closes; a resolver keeps its
        // own cached view, so its edits are untouched.
        if (_aiPanel?.IsOpen == true)
            return CenterPaneMode.AiIntegrations;

        if (_activeRepo is not { } ctx)
            return CenterPaneMode.Graph;

        if (ctx.State.OpenDiff is { } diff)
        {
            openDiff = diff;
            return CenterPaneMode.Diff;
        }

        // Only a live session can be closed: with no conflicts or operation left, nothing would
        // bring back a hidden resolver holding edits.
        var sessionActive = ctx.State.IsConflictSessionActive;
        if (sessionActive && ctx.State.IsConflictResolverClosed)
            return CenterPaneMode.Graph;

        return sessionActive || _hasUnsavedResolverWork(ctx) ? CenterPaneMode.Conflict : CenterPaneMode.Graph;
    }
}
