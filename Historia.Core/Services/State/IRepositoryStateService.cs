namespace Historia.Services.State;

public sealed record StatusChangedEventArgs(RepositoryStatus Status);
public sealed record SelectionChangedEventArgs(GitObjectId? CommitId);
public sealed record RevealCommitEventArgs(GitObjectId CommitId);
public sealed record CommitMessageProposedEventArgs(string Message);
public sealed record RefsChangedEventArgs(
    IReadOnlyList<BranchInfo> Branches,
    IReadOnlyList<TagInfo> Tags,
    IReadOnlyList<RemoteInfo> Remotes)
{
    public long StashStamp { get; init; }
}
public sealed record ProfileChangedEventArgs(GitProfile? Profile);
public sealed record OperationStateChangedEventArgs(OperationState State);
public sealed record OpenDiffChangedEventArgs(DiffRequest? Request);
public sealed record SelectedConflictPathChangedEventArgs(string? Path);

/// <summary>
/// The contract between every producer (Git services, watcher, profile service) and every
/// consumer (views and controls) for a single repository. The
/// graph and power tools plug into this same surface unchanged.
/// <para>
/// Threading contract: all mutation happens on a background thread; every event is raised through
/// the <see cref="IUiDispatcher"/> supplied at construction (inline when there is none, as in tests). Consumers must unsubscribe on dispose.
/// </para>
/// </summary>
public interface IRepositoryStateService
{
    // ── Current state: always non-blocking reads of cached values ──
    RepositoryInfo Repository { get; }
    RepositoryStatus Status { get; }
    BranchInfo? CurrentBranch { get; }
    IReadOnlyList<BranchInfo> Branches { get; }
    IReadOnlyList<TagInfo> Tags { get; }
    IReadOnlyList<RemoteInfo> Remotes { get; }
    long StashStamp { get; }
    GitObjectId? SelectedCommitId { get; }
    IReadOnlyList<string> SelectedPaths { get; }
    GitProfile? ActiveProfile { get; }
    OperationState PendingOperation { get; }

    /// <summary>How long the last completed <c>git status</c> read took; zero before the first one.
    /// Callers pace background work by it.</summary>
    TimeSpan LastStatusDuration { get; }

    /// <summary>When the last status read completed; null before the first one.</summary>
    DateTimeOffset? LastStatusCompletedAt { get; }

    /// <summary>True when the conflict resolver should stay open despite 0 conflicts remaining until the operation completes.</summary>
    bool KeepConflictResolverOpen { get; }

    /// <summary>True when conflicted files exist or an in-progress operation has keep-open set.</summary>
    bool IsConflictSessionActive { get; }

    /// <summary>Which conflicted file the resolver shows; written by the staging panel on click and by the resolver when it switches on its own; raised on the UI dispatcher.</summary>
    string? SelectedConflictPath { get; }

    /// <summary>
    /// True while the user has closed the conflict resolver. Closing hides it without saving or
    /// discarding: the view and its unsaved edits stay alive. The flag resets to false whenever the
    /// conflict session starts or ends, and when conflicts reappear after all were resolved (a
    /// rebase or cherry-pick stopping on its next commit).
    /// </summary>
    bool IsConflictResolverClosed { get; }

    /// <summary>The diff currently occupying the center panel, or null when the graph is shown.</summary>
    DiffRequest? OpenDiff { get; }

    /// <summary>The latest commit message offered by an external tool and not yet taken or
    /// dismissed by the commit box, or null. Never persisted.</summary>
    string? PendingCommitMessage { get; }

    // ── Mutations: safe to call from any thread ──
    void SetKeepConflictResolverOpen(bool keep);
    void SetSelectedConflictPath(string? path);

    /// <summary>Closes (true) or reopens (false) the conflict resolver; a no-op when unchanged.</summary>
    void SetConflictResolverClosed(bool closed);
    void SetSelectedCommit(GitObjectId? id);
    void SetSelectedPaths(IReadOnlyList<string> paths);

    /// <summary>
    /// Asks the graph to bring <paramref name="id"/> into view and centre it vertically.
    /// Selection is a separate concern — call <see cref="SetSelectedCommit"/> too.
    /// A no-op consumer is fine; the graph is the only listener.
    /// </summary>
    void RequestRevealCommit(GitObjectId id);

    /// <summary>
    /// Offers <paramref name="message"/> to the commit-message draft; a newer proposal replaces an
    /// older one. It never commits: the commit box decides whether to fill itself or to offer the
    /// text, and the user still reviews the staged files and clicks Commit. This is the path from
    /// the external-control host to the staging panel.
    /// </summary>
    void ProposeCommitMessage(string message);

    /// <summary>Forgets the pending proposal. Only the commit box calls this, once it has taken
    /// the text or the user has dismissed it. With <paramref name="onlyIf"/>, it forgets the
    /// proposal only while that same proposal is still the pending one, so a newer one survives.
    /// Raises no event.</summary>
    void ClearPendingCommitMessage(string? onlyIf = null);

    /// <summary>Opens or closes the center diff. Idempotent, except that an equal request with a
    /// <see cref="DiffRequest.FocusLine"/> is raised again so the viewer re-scrolls to that line.
    /// Safe to call from any thread.</summary>
    void SetOpenDiff(DiffRequest? request);
    void SetActiveProfile(GitProfile? profile);

    /// <summary>
    /// Refreshes the working-tree and staging status. Completes once state has been read from git
    /// by a run that started after this call, ensuring any change made before calling is reflected.
    /// Concurrent callers arriving while a run executes share the single trailing run.
    /// </summary>
    Task RefreshStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Refreshes branches, tags, remotes, and repository info. Completes once state has been read
    /// from git by a run that started after this call, ensuring any ref change made before calling
    /// is reflected. Concurrent callers arriving while a run executes share the single trailing run.
    /// </summary>
    Task RefreshRefsAsync(CancellationToken ct = default);

    /// <summary>
    /// Refreshes refs and then status sequentially, with the same freshness guarantees as
    /// <see cref="RefreshRefsAsync"/> and <see cref="RefreshStatusAsync"/>.
    /// </summary>
    Task RefreshAllAsync(CancellationToken ct = default);

    /// <summary>Populate branches/tags/remotes synchronously from the on-disk repo-state cache
    /// (if enabled and present) so the sidebar paints before the first async refresh. No event
    /// is raised — consumers read the current lists when they bind.</summary>
    void SeedFromCache();

    // ── Events: raised on the UI dispatcher ──

    /// <summary>
    /// Raised on the UI dispatcher when the status differs from the last one raised (see
    /// <see cref="RepositoryStatusEquivalence"/>). The first change after a quiet moment is raised
    /// at once; changes arriving in a burst are coalesced into one raise, never delayed past
    /// 150 ms.
    /// </summary>
    event EventHandler<StatusChangedEventArgs>? StatusChanged;
    event EventHandler<SelectionChangedEventArgs>? SelectedCommitChanged;

    /// <summary>Raised on the UI dispatcher when something asks the graph to scroll a commit into view.</summary>
    event EventHandler<RevealCommitEventArgs>? RevealCommitRequested;

    /// <summary>Raised on the UI dispatcher when a commit message is proposed; see <see cref="ProposeCommitMessage"/>.</summary>
    event EventHandler<CommitMessageProposedEventArgs>? CommitMessageProposed;
    event EventHandler<RefsChangedEventArgs>? RefsChanged;
    event EventHandler<ProfileChangedEventArgs>? ActiveProfileChanged;
    event EventHandler<OperationStateChangedEventArgs>? OperationStateChanged;

    /// <summary>Raised on the UI dispatcher, like every other event on this service.</summary>
    event EventHandler<OpenDiffChangedEventArgs>? OpenDiffChanged;

    /// <summary>Raised on the UI dispatcher whenever IsConflictSessionActive flips value.</summary>
    event EventHandler? ConflictSessionChanged;

    /// <summary>Raised on the UI dispatcher whenever SelectedConflictPath changes.</summary>
    event EventHandler<SelectedConflictPathChangedEventArgs>? SelectedConflictPathChanged;

    /// <summary>Raised on the UI dispatcher whenever IsConflictResolverClosed changes, including its resets.</summary>
    event EventHandler? ConflictResolverClosedChanged;
}
