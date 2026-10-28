namespace Historia.Services.State;

/// <inheritdoc cref="IRepositoryStateService"/>
public sealed class RepositoryStateService : IRepositoryStateService, IAsyncDisposable
{
    private readonly RepositoryContext _context;
    private readonly IUiDispatcher? _ui;
    private readonly IRepoStateCache? _repoCache;
    private readonly object _sync = new();

    private readonly TimeProvider _time;

    // A status that arrives when nothing was raised in the last StatusRaiseWindow is raised at once;
    // later ones within the window wait on a trailing timer, restarted by each newer status but never
    // so long that the oldest waiting one passes StatusRaiseMaxWait.
    private static readonly TimeSpan StatusRaiseWindow = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan StatusRaiseMaxWait = TimeSpan.FromMilliseconds(150);

    // Touched only on the UI thread when there is a dispatcher.
    private readonly IUiTimer? _statusCoalesceTimer;
    private (RepositoryStatus Status, RepositoryInfo Info, OperationState Operation)? _pendingStatusRaise;
    private bool _statusTimerRunning;
    private long _pendingStatusSince;
    private long _lastStatusRaiseAt;
    private bool _statusRaisedOnce;
    private volatile bool _disposed;

    // The status, info and operation state of the last raise; guarded by _raiseSync.
    private readonly object _raiseSync = new();
    private (RepositoryStatus Status, RepositoryInfo Info, OperationState Operation)? _lastRaised;

    private TimeSpan _lastStatusDuration;
    private DateTimeOffset? _lastStatusCompletedAt;

    // A status slower than this means many index entries carry stale stat data; refreshing them
    // lets the next status skip the hashing. At most one refresh per interval, none while one runs,
    // and none after a tab's first status: that run is slow from a cold file cache whatever the
    // index holds, and the refresh would hold the write lock against the user's first stage or commit.
    // A status that is still slow with a warm cache is the signal.
    private static readonly TimeSpan SlowStatusThreshold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IndexStatRefreshInterval = TimeSpan.FromSeconds(60);

    // The refresh starts this long after the slow status, so a user operation that the status was
    // read for (Stage all, Commit) gets the write lock first.
    private static readonly TimeSpan IndexStatRefreshGrace = TimeSpan.FromSeconds(2);
    private long? _lastIndexStatRefreshAt;
    private Task _indexStatRefresh = Task.CompletedTask;

    private RepositoryInfo _repository;
    private RepositoryStatus _status = RepositoryStatus.Empty;
    private IReadOnlyList<BranchInfo> _branches = [];
    private IReadOnlyList<TagInfo> _tags = [];
    private IReadOnlyList<RemoteInfo> _remotes = [];
    private long _stashStamp;
    // The HEAD identity the last refs raise was computed against. The status refresh also writes
    // _repository, so comparing against that would hide a HEAD move it saw first.
    private (bool Detached, bool Unborn, GitObjectId? Id) _refsHead;
    private GitObjectId? _selectedCommitId;
    private IReadOnlyList<string> _selectedPaths = [];
    private GitProfile? _activeProfile;
    private OperationState _pendingOperation;
    private DiffRequest? _openDiff;
    private bool _keepConflictResolverOpen;
    private string? _selectedConflictPath;
    private bool _conflictResolverClosed;
    private string? _pendingCommitMessage;

    public event EventHandler<StatusChangedEventArgs>? StatusChanged;
    public event EventHandler<SelectionChangedEventArgs>? SelectedCommitChanged;
    public event EventHandler<RevealCommitEventArgs>? RevealCommitRequested;
    public event EventHandler<CommitMessageProposedEventArgs>? CommitMessageProposed;
    public event EventHandler<RefsChangedEventArgs>? RefsChanged;
    public event EventHandler<ProfileChangedEventArgs>? ActiveProfileChanged;
    public event EventHandler<OperationStateChangedEventArgs>? OperationStateChanged;
    public event EventHandler<OpenDiffChangedEventArgs>? OpenDiffChanged;
    public event EventHandler? ConflictSessionChanged;
    public event EventHandler<SelectedConflictPathChangedEventArgs>? SelectedConflictPathChanged;
    public event EventHandler? ConflictResolverClosedChanged;

    private readonly IGitGateway _gateway;
    private readonly SingleFlightCoalescer _refsCoalescer;
    private readonly SingleFlightCoalescer _statusCoalescer;

    public RepositoryStateService(
        RepositoryContext context,
        IUiDispatcher? ui = null,
        IRepoStateCache? repoCache = null,
        IGitGateway? gateway = null,
        TimeProvider? time = null)
    {
        _context = context;
        _time = time ?? TimeProvider.System;
        _gateway = gateway ?? context.Gateway;
        _refsCoalescer = new(() => _context.Lifetime, () => _context.CompletedOperations);
        _statusCoalescer = new(() => _context.Lifetime, () => _context.CompletedOperations);
        _ui = ui;
        _repoCache = repoCache;
        _stashStamp = RefStateToken.StashStamp(context.GitDirectory);
        _repository = new RepositoryInfo
        {
            WorkingDirectory = context.RepositoryPath,
            GitDirectory = context.GitDirectory,
            DisplayName = context.DisplayName,
            IsBare = context.IsBare,
            IsHeadDetached = false,
            IsHeadUnborn = false,
            CurrentBranchName = null,
        };

        if (_ui is not null)
            _statusCoalesceTimer = _ui.CreateTimer(StatusRaiseWindow, OnStatusCoalesceTick, UiPriority.Normal);
    }

    public RepositoryInfo Repository { get { lock (_sync) return _repository; } }
    public RepositoryStatus Status { get { lock (_sync) return _status; } }
    public IReadOnlyList<BranchInfo> Branches { get { lock (_sync) return _branches; } }
    public IReadOnlyList<TagInfo> Tags { get { lock (_sync) return _tags; } }
    public IReadOnlyList<RemoteInfo> Remotes { get { lock (_sync) return _remotes; } }
    public long StashStamp { get { lock (_sync) return _stashStamp; } }
    public GitObjectId? SelectedCommitId { get { lock (_sync) return _selectedCommitId; } }
    public IReadOnlyList<string> SelectedPaths { get { lock (_sync) return _selectedPaths; } }
    public DiffRequest? OpenDiff { get { lock (_sync) return _openDiff; } }
    public GitProfile? ActiveProfile { get { lock (_sync) return _activeProfile; } }
    public OperationState PendingOperation { get { lock (_sync) return _pendingOperation; } }
    public TimeSpan LastStatusDuration { get { lock (_sync) return _lastStatusDuration; } }
    public DateTimeOffset? LastStatusCompletedAt { get { lock (_sync) return _lastStatusCompletedAt; } }
    public bool KeepConflictResolverOpen { get { lock (_sync) return _keepConflictResolverOpen; } }
    public bool IsConflictSessionActive { get { lock (_sync) return ComputeIsConflictSessionActive(_status, _pendingOperation, _keepConflictResolverOpen); } }
    public string? SelectedConflictPath { get { lock (_sync) return _selectedConflictPath; } }
    public bool IsConflictResolverClosed { get { lock (_sync) return _conflictResolverClosed; } }

    private static bool ComputeIsConflictSessionActive(RepositoryStatus status, OperationState operation, bool keepOpen)
    {
        return status.Conflicted.Count > 0 ||
            (keepOpen && operation is OperationState.Merging or OperationState.Rebasing or OperationState.CherryPicking or OperationState.Reverting);
    }

    public void SetKeepConflictResolverOpen(bool keep)
    {
        bool flipped;
        bool nowActive;
        bool closedCleared = false;
        lock (_sync)
        {
            if (_keepConflictResolverOpen == keep)
                return;
            bool wasActive = ComputeIsConflictSessionActive(_status, _pendingOperation, _keepConflictResolverOpen);
            _keepConflictResolverOpen = keep;
            nowActive = ComputeIsConflictSessionActive(_status, _pendingOperation, _keepConflictResolverOpen);
            flipped = wasActive != nowActive;
            if (flipped && !nowActive)
            {
                _selectedConflictPath = null;
            }
            if (flipped)
            {
                closedCleared = _conflictResolverClosed;
                _conflictResolverClosed = false;
            }
        }

        if (flipped)
            HandleConflictSessionFlipped(nowActive, closedCleared);
    }

    public void SetConflictResolverClosed(bool closed)
    {
        lock (_sync)
        {
            if (_conflictResolverClosed == closed)
                return;
            _conflictResolverClosed = closed;
        }

        Raise(() => ConflictResolverClosedChanged?.Invoke(this, EventArgs.Empty));
    }

    public void SetSelectedConflictPath(string? path)
    {
        bool changed;
        lock (_sync)
        {
            if (string.Equals(_selectedConflictPath, path, StringComparison.Ordinal))
                return;
            _selectedConflictPath = path;
            changed = true;
        }

        if (changed)
            Raise(() => SelectedConflictPathChanged?.Invoke(this, new SelectedConflictPathChangedEventArgs(path)));
    }

    private void HandleConflictSessionFlipped(bool nowActive, bool closedCleared)
    {
        Raise(() => ConflictSessionChanged?.Invoke(this, EventArgs.Empty));
        if (closedCleared)
            Raise(() => ConflictResolverClosedChanged?.Invoke(this, EventArgs.Empty));

        if (nowActive)
        {
            SetSelectedCommit(null);
        }
        else
        {
            Raise(() => SelectedConflictPathChanged?.Invoke(this, new SelectedConflictPathChangedEventArgs(null)));
        }
    }

    public BranchInfo? CurrentBranch
    {
        get { lock (_sync) return _branches.FirstOrDefault(b => b.IsCurrent); }
    }

    public void SetSelectedCommit(GitObjectId? id)
    {
        bool diffCleared;
        lock (_sync)
        {
            if (_selectedCommitId == id)
                return;
            _selectedCommitId = id;
            // selecting a different commit must not leave the previous commit's file
            // diff filling the center panel — the single most likely stale state in the feature.
            diffCleared = _openDiff is not null;
            _openDiff = null;
        }

        Raise(() => SelectedCommitChanged?.Invoke(this, new SelectionChangedEventArgs(id)));
        if (diffCleared)
            Raise(() => OpenDiffChanged?.Invoke(this, new OpenDiffChangedEventArgs(null)));
    }

    public void SetOpenDiff(DiffRequest? request)
    {
        lock (_sync)
        {
            // Idempotent: DiffRequest is a record, so an equal request raises nothing — unless it
            // carries a focus line, which is raised again so the open viewer scrolls back to it.
            if (_openDiff == request && request?.FocusLine is null)
                return;
            _openDiff = request;
        }

        Raise(() => OpenDiffChanged?.Invoke(this, new OpenDiffChangedEventArgs(request)));
    }

    public void SetSelectedPaths(IReadOnlyList<string> paths)
    {
        lock (_sync)
            _selectedPaths = paths;
    }

    public void RequestRevealCommit(GitObjectId id) =>
        Raise(() => RevealCommitRequested?.Invoke(this, new RevealCommitEventArgs(id)));

    public string? PendingCommitMessage
    {
        get
        {
            lock (_sync)
                return _pendingCommitMessage;
        }
    }

    public void ProposeCommitMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_sync)
            _pendingCommitMessage = message;

        Raise(() => CommitMessageProposed?.Invoke(this, new CommitMessageProposedEventArgs(message)));
    }

    public void ClearPendingCommitMessage(string? onlyIf = null)
    {
        lock (_sync)
        {
            if (onlyIf is null || ReferenceEquals(_pendingCommitMessage, onlyIf))
                _pendingCommitMessage = null;
        }
    }

    public void SetActiveProfile(GitProfile? profile)
    {
        lock (_sync)
        {
            if (_activeProfile?.Id == profile?.Id)
                return;
            _activeProfile = profile;
        }

        Raise(() => ActiveProfileChanged?.Invoke(this, new ProfileChangedEventArgs(profile)));
    }

    public Task RefreshStatusAsync(CancellationToken ct = default)
        => _statusCoalescer.RunAsync(DoRefreshStatusAsync, joinFrom: null, ct);

    private async Task DoRefreshStatusAsync(CancellationToken ct)
    {
        using var _perf = Historia.Diagnostics.PerfLog.Scope($"RepositoryStateService.RefreshStatusAsync({_context.DisplayName})");
        var statusStarted = _time.GetTimestamp();
        var status = await _gateway.GetStatusAsync(ct).ConfigureAwait(false);
        var statusDuration = _time.GetElapsedTime(statusStarted);
        var info = await _gateway.GetRepositoryInfoAsync(ct).ConfigureAwait(false);
        var operation = DetectOperationState(_context.GitDirectory);

        bool operationChanged;
        bool openDiffCleared;
        bool conflictSessionFlipped;
        bool nowActive;
        bool closedCleared = false;
        bool followsEarlierStatus;
        lock (_sync)
        {
            bool wasActive = ComputeIsConflictSessionActive(_status, _pendingOperation, _keepConflictResolverOpen);

            bool isOperationInProgress = operation is OperationState.Merging or OperationState.Rebasing or OperationState.CherryPicking or OperationState.Reverting;
            if (status.Conflicted.Count > 0 && isOperationInProgress)
            {
                _keepConflictResolverOpen = true;
            }
            else if (!isOperationInProgress)
            {
                _keepConflictResolverOpen = false;
            }

            // A rebase or cherry-pick that stops on its next commit keeps the session active, so
            // conflicts reappearing after all were resolved reopen a closed resolver too.
            bool conflictsAppeared = _status.Conflicted.Count == 0 && status.Conflicted.Count > 0;

            _status = status;
            _repository = info;
            followsEarlierStatus = _lastStatusCompletedAt is not null;
            _lastStatusDuration = statusDuration;
            _lastStatusCompletedAt = _time.GetUtcNow();
            operationChanged = _pendingOperation != operation;
            _pendingOperation = operation;

            nowActive = ComputeIsConflictSessionActive(_status, _pendingOperation, _keepConflictResolverOpen);
            conflictSessionFlipped = wasActive != nowActive;
            if (conflictSessionFlipped && !nowActive)
            {
                _selectedConflictPath = null;
            }
            if (conflictSessionFlipped || conflictsAppeared)
            {
                closedCleared = _conflictResolverClosed;
                _conflictResolverClosed = false;
            }

            // the centre panel shows the open file diff in place of the commit graph.
            // A commit, an amend, a stage/unstage, or an external edit can drop the file a
            // working-tree diff was showing (or clean its whole side) with no explicit Close,
            // leaving a permanent "No changes" panel covering the graph — and switching repos
            // and back just re-shows that stale diff. Clear it here, on the path every refresh
            // takes, rather than in a staging panel that is only wired up while it is mounted.
            openDiffCleared = _openDiff is { } od && IsWorkingTreeDiffStale(od, status);
            if (openDiffCleared)
                _openDiff = null;
        }

        QueueStatusRaise(status, info, operation);
        RefreshIndexStatIfSlow(statusDuration, followsEarlierStatus);

        if (conflictSessionFlipped)
            HandleConflictSessionFlipped(nowActive, closedCleared);
        else if (closedCleared)
            Raise(() => ConflictResolverClosedChanged?.Invoke(this, EventArgs.Empty));

        if (openDiffCleared)
            Raise(() => OpenDiffChanged?.Invoke(this, new OpenDiffChangedEventArgs(null)));

        if (operationChanged)
            Raise(() => OperationStateChanged?.Invoke(this, new OperationStateChangedEventArgs(operation)));
    }

    /// <summary>The most recent index stat refresh this service started; completed when none is running.</summary>
    internal Task IndexStatRefreshTask { get { lock (_sync) return _indexStatRefresh; } }

    private void RefreshIndexStatIfSlow(TimeSpan statusDuration, bool followsEarlierStatus)
    {
        if (!followsEarlierStatus || statusDuration <= SlowStatusThreshold || _disposed)
            return;

        lock (_sync)
        {
            var now = _time.GetTimestamp();
            if (!_indexStatRefresh.IsCompleted ||
                (_lastIndexStatRefreshAt is { } last && _time.GetElapsedTime(last, now) < IndexStatRefreshInterval))
                return;

            _lastIndexStatRefreshAt = now;
            var operationsAtStart = _context.CompletedOperations;
            _indexStatRefresh = Task.Run(() => RunIndexStatRefreshAsync(operationsAtStart));
        }
    }

    // Best effort: the refresh only speeds up later statuses, so a failure is logged and nothing else.
    // It waits out a grace period and then gives way to user work: when an operation ended or one
    // holds the write lock meanwhile, it does not run, and the next slow status may schedule it again.
    private async Task RunIndexStatRefreshAsync(long operationsAtStart)
    {
        try
        {
            await Task.Delay(IndexStatRefreshGrace, _time, _context.Lifetime).ConfigureAwait(false);
            if (_disposed
                || _context.CompletedOperations != operationsAtStart
                || _context.CurrentWriteHolder is not null)
            {
                lock (_sync)
                    _lastIndexStatRefreshAt = null;
                return;
            }

            await _gateway.RefreshIndexStatAsync(_context.Lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Tab closed before the refresh started.
        }
        catch (HistoriaGitException)
        {
            // The logging gateway has already recorded the failure.
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "git", $"{_context.DisplayName}: index stat refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// True when <paramref name="request"/> is an index/working-tree diff whose subject is no
    /// longer part of that view — the file left the list (committed, staged, unstaged, reverted)
    /// or, for a whole-source diff, the list is now empty. Commit/Comparison diffs are never
    /// stale this way: they are pinned to explicit object ids, not to working-tree state.
    /// </summary>
    private static bool IsWorkingTreeDiffStale(DiffRequest request, RepositoryStatus status)
    {
        IReadOnlyList<FileStatusEntry> list;
        switch (request.Source)
        {
            case DiffSource.StagedChanges:
                list = status.Staged;
                break;
            case DiffSource.UnstagedChanges:
                list = [.. status.Unstaged, .. status.Untracked, .. status.Conflicted];
                break;
            default:
                return false; // Commit / Comparison diffs are pinned to object ids, never stale here
        }

        if (request.Path is null)
            return list.Count == 0;

        return !list.Any(f => f.Path == request.Path
            || (f.OldPath is not null && f.OldPath == request.Path));
    }

    public Task RefreshRefsAsync(CancellationToken ct = default)
        => _refsCoalescer.RunAsync(DoRefreshRefsAsync, joinFrom: null, ct);

    private async Task DoRefreshRefsAsync(CancellationToken ct)
    {
        using var _perf = Historia.Diagnostics.PerfLog.Scope($"RepositoryStateService.RefreshRefsAsync({_context.DisplayName})");
        var stamp = RefStateToken.StashStamp(_context.GitDirectory);
        var branches = await _gateway.GetBranchesAsync(ct).ConfigureAwait(false);
        var remotes = await _gateway.GetRemotesAsync(ct).ConfigureAwait(false);
        var info = await _gateway.GetRepositoryInfoAsync(ct).ConfigureAwait(false);

        bool branchesOrRemotesChanged;
        bool stashChanged;
        bool headChanged;
        IReadOnlyList<TagInfo> existingTags;
        lock (_sync)
        {
            // BranchInfo / RemoteInfo are records — structural comparison.
            branchesOrRemotesChanged = !branches.SequenceEqual(_branches)
                                      || !remotes.SequenceEqual(_remotes);
            stashChanged = stamp != _stashStamp;
            _stashStamp = stamp;
            _branches = branches;
            _remotes = remotes;

            var head = (info.IsHeadDetached, info.IsHeadUnborn, info.HeadCommitId);
            headChanged = head != _refsHead;
            _refsHead = head;
            _repository = info;

            existingTags = _tags;
        }

        // publish branches and remotes first so the sidebar populates in ~57ms rather than waiting
        // seconds on thousands of tags to project.
        if (branchesOrRemotesChanged || stashChanged || headChanged)
            Raise(() => RefsChanged?.Invoke(this, new RefsChangedEventArgs(branches, existingTags, remotes) { StashStamp = stamp }));

        var tags = await _gateway.GetTagsAsync(ct).ConfigureAwait(false);

        bool tagsChanged;
        lock (_sync)
        {
            // TagInfo is a record — structural comparison.
            tagsChanged = !tags.SequenceEqual(_tags);
            _tags = tags;
        }

        // One write after tags complete, so a half-populated ref snapshot is never stored, and only
        // when a ref list changed: an unchanged refresh would rewrite the same snapshot.
        if (branchesOrRemotesChanged || tagsChanged)
            _repoCache?.SaveRefs(_context.RepositoryPath, branches, tags, remotes);

        // Raise second time if tags changed.
        if (tagsChanged)
            Raise(() => RefsChanged?.Invoke(this, new RefsChangedEventArgs(branches, tags, remotes) { StashStamp = stamp }));
    }

    public void SeedFromCache() => SeedFrom(_repoCache?.TryLoad(_context.RepositoryPath));

    /// <summary>Seeds from a snapshot the caller already read (<see cref="RepositoryContext.OpenAsync"/>
    /// reads it alongside discovery). No event: nothing has bound to this state yet.</summary>
    internal void SeedFrom(RepoStateSnapshot? snap)
    {
        if (snap is null)
            return;
        lock (_sync)
        {
            if (snap.Branches is { Count: > 0 } b) _branches = b;
            if (snap.Tags is { } t) _tags = t;
            if (snap.Remotes is { } r) _remotes = r;
        }
    }

    public Task RefreshAllAsync(CancellationToken ct = default)
        => RefreshAllCoreAsync(joinFrom: null, ct);

    /// <summary>
    /// Refreshes refs then status after one of Historia's own operations ended at
    /// <paramref name="operationEnd"/> (see <see cref="RepositoryContext.CompletedOperations"/>).
    /// Joins a run that started at or after that count: it began after the operation finished, so
    /// it reads the operation's result.
    /// </summary>
    internal Task RefreshAfterOperationAsync(long operationEnd, CancellationToken ct)
        => RefreshAllCoreAsync(operationEnd, ct);

    private async Task RefreshAllCoreAsync(long? joinFrom, CancellationToken ct)
    {
        await _refsCoalescer.RunAsync(DoRefreshRefsAsync, joinFrom, ct).ConfigureAwait(false);
        await _statusCoalescer.RunAsync(DoRefreshStatusAsync, joinFrom, ct).ConfigureAwait(false);
    }

    // ── Status raise: leading edge, trailing coalescing, no raise for an unchanged status ──

    private void QueueStatusRaise(RepositoryStatus status, RepositoryInfo info, OperationState operation)
    {
        if (_ui is null || _statusCoalesceTimer is null)
        {
            RaiseStatus(status, info, operation);
            return;
        }

        if (_ui.CheckAccess())
            OfferStatus(status, info, operation);
        else
            _ui.Post(() => OfferStatus(status, info, operation), UiPriority.Normal);
    }

    private void OfferStatus(RepositoryStatus status, RepositoryInfo info, OperationState operation)
    {
        if (_disposed)
            return;

        var now = _time.GetTimestamp();
        _pendingStatusRaise = (status, info, operation);

        if (_statusTimerRunning)
        {
            // Restarting waits one more window; skip it when the oldest waiting status would then
            // pass the maximum wait. The running timer fires within one window of its last restart,
            // which is within the maximum wait of that status's arrival.
            if (_time.GetElapsedTime(_pendingStatusSince, now) + StatusRaiseWindow <= StatusRaiseMaxWait)
            {
                _statusCoalesceTimer!.Stop();
                _statusCoalesceTimer.Start();
            }
            return;
        }

        if (!_statusRaisedOnce || _time.GetElapsedTime(_lastStatusRaiseAt, now) >= StatusRaiseWindow)
        {
            _pendingStatusRaise = null;
            if (RaiseStatus(status, info, operation))
            {
                _statusRaisedOnce = true;
                _lastStatusRaiseAt = now;
            }
            return;
        }

        _pendingStatusSince = now;
        _statusTimerRunning = true;
        _statusCoalesceTimer!.Start();
    }

    private void OnStatusCoalesceTick()
    {
        _statusCoalesceTimer!.Stop();
        _statusTimerRunning = false;
        var pending = _pendingStatusRaise;
        _pendingStatusRaise = null;
        if (_disposed || pending is not { } p)
            return;

        if (RaiseStatus(p.Status, p.Info, p.Operation))
        {
            _statusRaisedOnce = true;
            _lastStatusRaiseAt = _time.GetTimestamp();
        }
    }

    /// <summary>Raises <see cref="StatusChanged"/> unless the status and operation state match the last raise.
    /// A status with conflicted files always raises: the conflict resolver compares a conflicted file
    /// with the disk on every raise, and an edit that keeps the file's size changes nothing the
    /// comparison sees. Returns whether it raised.</summary>
    private bool RaiseStatus(RepositoryStatus status, RepositoryInfo info, OperationState operation)
    {
        lock (_raiseSync)
        {
            if (_lastRaised is { } last
                && last.Operation == operation
                && RepositoryStatusEquivalence.Equivalent(last.Status, last.Info, status, info)
                && status.Conflicted.Count == 0)
                return false;
            _lastRaised = (status, info, operation);
        }

        StatusChanged?.Invoke(this, new StatusChangedEventArgs(status));
        return true;
    }

    private void Raise(Action action)
    {
        if (_ui is null || _ui.CheckAccess())
            action();
        else
            _ui.Post(action);
    }

    /// <summary>Derives <see cref="OperationState"/> from on-disk sequencer markers.</summary>
    internal static OperationState DetectOperationState(string gitDir) => OperationMarkers.Detect(gitDir);

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _statusCoalesceTimer?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// One refresh at a time per kind. A caller arriving while a refresh executes does not join it:
    /// that run may have read the working tree before the caller's change landed. Instead, the caller
    /// waits for the single trailing run queued behind it, and callers arriving before that run starts
    /// share it. The one exception is the refresh that follows one of Historia's own operations
    /// (<c>joinFrom</c>): it joins an executing run that started at or after the operation count its
    /// operation ended at, since that run began after the operation finished. Without it, the
    /// operation's caller and the operation's own refresh ask at once and a stage or commit often runs
    /// <c>git status</c> twice. A run's work gets a token that cancels when the tab closes or when every
    /// caller waiting on that run has given up (their tokens cancelled) — a tab the user switched away
    /// from stops paying for a refresh nobody is waiting for. A caller with a non-cancellable token
    /// keeps its run alive.
    /// <para>
    /// <b>Trap:</b> Two wider join rules served stale status. Joining an executing run until its git
    /// reads returned fails because git fixes its answer when the read starts, so a change landing
    /// mid-read is missed. Joining while no change had been observed since the run started fails for
    /// any change not yet observed when the caller asks: the watcher reports an edit only after it
    /// lands, and nothing reports a write the caller made itself. Only a caller that can show its
    /// change predates the run may join it.
    /// </para>
    /// </summary>
    private sealed class SingleFlightCoalescer(Func<CancellationToken> lifetime, Func<long> completedOperations)
    {
        private readonly object _gate = new();
        private Run? _current;  // running
        private Run? _trailing; // queued behind _current

        private sealed class Run
        {
            public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly CancellationTokenSource Abandoned = new();
            public int Waiters;      // guarded by the coalescer's _gate
            public long StartCount;  // operation count when its work started; guarded by _gate
        }

        public Task RunAsync(Func<CancellationToken, Task> work, long? joinFrom, CancellationToken ct)
        {
            // A caller that already gave up never starts (or joins) work.
            if (ct.IsCancellationRequested)
                return Task.FromCanceled(ct);

            Run run;
            var start = false;
            lock (_gate)
            {
                if (_current is null)
                {
                    _current = run = new Run { StartCount = completedOperations() };
                    start = true;
                }
                else if (joinFrom is { } from && _current.StartCount >= from
                    && !_current.Abandoned.IsCancellationRequested)
                {
                    run = _current;
                }
                else
                {
                    if (_trailing is { Abandoned.IsCancellationRequested: true })
                        _trailing = null; // everyone waiting on it left; it will never start
                    run = _trailing ??= new Run();
                }
                run.Waiters++;
            }

            // Off the caller's thread: a run started inline would execute the work's synchronous
            // prefix (process start, file probes) on the UI thread of a UI-originated refresh.
            if (start)
                _ = Task.Run(() => PumpAsync(work));
            return WaitAsync(run, ct);
        }

        private async Task WaitAsync(Run run, CancellationToken ct)
        {
            try
            {
                await TaskGate.WaitWithCancellationAsync(run.Done.Task, ct).ConfigureAwait(false);
            }
            finally
            {
                bool abandon;
                lock (_gate)
                {
                    run.Waiters--;
                    abandon = run.Waiters == 0 && !run.Done.Task.IsCompleted;
                }
                // Outside the lock: cancelling runs the work's cancellation callbacks.
                if (abandon)
                {
                    try { run.Abandoned.Cancel(); }
                    catch (ObjectDisposedException) { }
                }
            }
        }

        private async Task PumpAsync(Func<CancellationToken, Task> work)
        {
            while (true)
            {
                Run run;
                lock (_gate)
                    run = _current!;

                var (canceled, fault) = await ExecuteAsync(run, work).ConfigureAwait(false);

                lock (_gate)
                {
                    if (canceled)
                        run.Done.TrySetCanceled();
                    else if (fault is not null)
                        run.Done.TrySetException(fault);
                    else
                        run.Done.TrySetResult();

                    _current = _trailing;
                    _trailing = null;
                    if (_current is null)
                        return;
                    _current.StartCount = completedOperations();
                }
            }
        }

        private async Task<(bool Canceled, Exception? Fault)> ExecuteAsync(Run run, Func<CancellationToken, Task> work)
        {
            CancellationTokenSource? linked = null;
            try
            {
                var tabLifetime = lifetime();
                if (run.Abandoned.IsCancellationRequested || tabLifetime.IsCancellationRequested)
                    return (true, null);
                linked = CancellationTokenSource.CreateLinkedTokenSource(tabLifetime, run.Abandoned.Token);
                await work(linked.Token).ConfigureAwait(false);
                return (false, null);
            }
            catch (OperationCanceledException)
            {
                return (true, null);
            }
            catch (ObjectDisposedException) when (lifetime().IsCancellationRequested)
            {
                // The tab's context was disposed under the run.
                return (true, null);
            }
            catch (Exception) when (linked?.IsCancellationRequested == true)
            {
                // A git call killed by the cancellation can surface as a failure; it is still a
                // cancellation, not a fault to report.
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex);
            }
            finally
            {
                linked?.Dispose();
            }
        }
    }
}
