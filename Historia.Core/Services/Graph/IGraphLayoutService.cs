namespace Historia.Services.Graph;

/// <summary>Result of a layout call: the layout, the opaque allocator state, and the ref fingerprint.</summary>
public sealed record GraphLayoutResult(GraphLayout Layout, GraphLayoutState State, long Fingerprint = 0);

/// <summary>
/// Computes the visual commit-graph layout for a repository. Runs entirely on a
/// background thread and accesses the repository only through the owning
/// <see cref="RepositoryContext"/>'s gate. Registered as a singleton.
/// </summary>
public interface IGraphLayoutService
{
    /// <summary>Computes the layout for the first <paramref name="maxRows"/> rows.</summary>
    Task<GraphLayoutResult> ComputeLayoutAsync(
        RepositoryContext context, GraphQuery query, int maxRows = 10_000, CancellationToken ct = default);

    /// <summary>
    /// An optimistic first paint from the last on-disk walk cache for this repo, skipping
    /// the ref-fingerprint check (and the <c>for-each-ref</c> scan it may need) that
    /// <see cref="ComputeLayoutAsync"/> must otherwise pay before returning anything — a plain
    /// file read + JSON deserialize instead. The result may be momentarily stale (a branch
    /// moved, a new commit landed, or history was rewritten since the file was written); the
    /// caller must still run <see cref="ComputeLayoutAsync"/> and replace this layout once that
    /// completes. Returns null on any miss: no cached file, an unsupported query, or this
    /// context already has an in-memory walk (nothing stale left to shortcut).
    /// </summary>
    GraphLayoutResult? TryOptimisticSnapshot(RepositoryContext context, GraphQuery query, int maxRows);

    /// <summary>
    /// Continues the walk from where <paramref name="existing"/> left off, appending up to
    /// <paramref name="additionalRows"/> rows. Lane indices already assigned are never
    /// renumbered. <paramref name="background"/> routes the walk through the low-priority repo
    /// gate (the silent pre-load crawler); scroll-driven extends leave it false.
    /// </summary>
    Task<GraphLayoutResult> ExtendLayoutAsync(
        RepositoryContext context, GraphLayoutResult existing, int additionalRows, CancellationToken ct = default,
        bool background = false);

    /// <summary>
    /// Recomputes the layout after a history change (invalidation table). Appended commits
    /// recompute from row 0 to the first unchanged row; every other change recomputes the whole
    /// window.
    /// </summary>
    Task<GraphLayoutResult> RecomputeAsync(
        RepositoryContext context, GraphQuery query, GraphLayoutResult previous,
        InvalidationReason reason, int maxRows = 10_000, CancellationToken ct = default);

    /// <summary>Drops any cached walk for a context (called when its tab closes).</summary>
    void Forget(RepositoryContext context);

    /// <summary>
    /// Releases everything a tab's graph holds in
    /// memory when the user switches away from it — cancels its background disk-cache spill,
    /// disposes its live walk (killing the CLI engine's <c>git.exe</c> if one is running), drops
    /// its resident commit list and decorations, and evicts its <c>.gz</c> read-cache entry.
    /// Unlike <see cref="Forget"/> this is NOT a full teardown: a partial on-disk cache is left in
    /// place for the next focus to resume from, and returning to the tab is a normal
    /// <see cref="ComputeLayoutAsync"/>. Safe to call for a context with no cache (no-op).
    /// </summary>
    Task Hibernate(RepositoryContext context);

    /// <summary>
    /// Deletes the on-disk persisted graph walk and drops the in-memory decoration caches.
    /// The next graph open re-walks from scratch. Returns the number of files removed.
    /// </summary>
    int ClearPersistentCache();

    /// <summary>
    /// Headlessly brings ONE inactive tab's on-disk
    /// graph cache up to date — no live layout, no lasting entry in this service's own cache
    /// table, holding at most one walk batch resident at a time. A no-op if disk-backed metadata
    /// is off, this query shape isn't supported by it, or the store already matches the repo's
    /// current walk key and is exhausted. Also writes/refreshes the commit-graph when the repo's
    /// size already qualifies. Driven by <c>GraphBackgroundWriter</c>'s inactive-tab queue —
    /// never called for the focused tab. <paramref name="ct"/> is checked at batch boundaries: a
    /// cancellation (the tab was focused mid-job) leaves whatever was already durably written in
    /// place and discards only the in-flight batch.
    /// </summary>
    Task RunHeadlessCacheAsync(RepositoryContext context, GraphQuery query, CancellationToken ct);

    /// <summary>
    /// Queues <paramref name="context"/> for the headless pipeline
    /// (<see cref="RunHeadlessCacheAsync"/>) on this service's own background-job queue, so it
    /// runs one at a time with — and serialized against — every other inactive tab and the active
    /// tab's own commit-graph write. A no-op if this service has no background writer configured
    /// (a test-only ctor). The caller is expected to call this only for tabs that are NOT
    /// currently focused.
    /// </summary>
    void EnqueueInactiveTabCaching(RepositoryContext context, GraphQuery query);

    /// <summary>
    /// Cancels <paramref name="context"/>'s
    /// inactive-tab headless job — queued or already running — and returns a task that completes
    /// once it has actually exited. Called when a tab gains focus, before its own foreground
    /// spill can start writing, so the headless pipeline and the newly focused tab's spill never
    /// both append to the same on-disk store. A no-op (a completed task) if there is no such job,
    /// or this service has no background writer configured (a test-only ctor).
    /// </summary>
    Task CancelInactiveTabCachingAsync(RepositoryContext context);

    /// <summary>
    /// Fire-and-forget refresh of
    /// <paramref name="context"/>'s commit-graph after a successful auto-fetch, through
    /// <c>GraphBackgroundWriter</c>'s serial/gated/below-normal-priority/never-killed write path.
    /// A no-op if this service has no background writer configured (a test-only ctor) or the
    /// "Write a commit-graph file for large repositories" setting is off.
    /// </summary>
    void RefreshCommitGraphAfterFetch(RepositoryContext context);

    /// <summary>
    /// Bounds the graph files in the cache directory, once per launch: deletes orphaned temp and
    /// retired files and the files of the metadata mode that is off, and evicts the least recently
    /// used <c>.meta</c> stores past the store-count and total-size caps. Never evicts the stores
    /// of <paramref name="openRepositoryPaths"/>. Runs off the caller's thread behind the
    /// background-work gate; later calls are no-ops.
    /// </summary>
    void SweepDiskCache(IReadOnlyCollection<string> openRepositoryPaths);

    /// <summary>
    /// Pre-caches a newly cloned repository's commit-graph and layout cache before its tab is
    /// opened. Runs ahead of the background-work gate. Writes the commit-graph first,
    /// then spools the full history walk to disk. Progress reports the commit-graph phase and then
    /// each spilled batch. Cancellation stops caching while keeping whatever was already durably
    /// written.
    /// </summary>
    Task PrecacheClonedRepositoryAsync(
        RepositoryContext context, GraphQuery query, IProgress<GitProgress>? progress, CancellationToken ct);
}
