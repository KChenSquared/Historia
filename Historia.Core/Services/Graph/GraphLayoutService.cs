namespace Historia.Services.Graph;

/// <inheritdoc cref="IGraphLayoutService"/>
public sealed partial class GraphLayoutService : IGraphLayoutService
{
    private const int WalkOverscan = 2_000;

    /// <summary>Rows per chunk of the background full-history walk. Each chunk *resumes*
    /// the primed <c>git log</c> walk (cheap — no respawn) while no ref file has changed
    /// and otherwise after one low-priority gate turn, so this bounds how long a
    /// foreground extend can wait behind the spill to roughly one chunk's pull.</summary>
    private const int SpillWalkChunkRows = 25_000;

    /// <summary>Rows per batch of the disk-spill write, both for the active tab's background
    /// spill and the headless per-tab pipeline. See <see cref="TestSpillBatchRowsOverride"/>.</summary>
    private const int DefaultSpillBatchRows = 25_000;

    /// <summary>Test seam: when &gt; 0, overrides <see cref="DefaultSpillBatchRows"/>
    /// for both the active-tab background spill and the headless spool — so a fixture repo with
    /// only a few hundred commits can still exercise multi-batch write races that would otherwise
    /// need a 25,000-row history to trigger. Left at 0 (production default) outside tests; a test
    /// that sets it must reset it to 0 when done, since it's process-wide state.</summary>
    private static readonly AsyncLocal<int> _testSpillBatchRowsOverride = new();
    internal static int TestSpillBatchRowsOverride
    {
        get => _testSpillBatchRowsOverride.Value;
        set => _testSpillBatchRowsOverride.Value = value;
    }

    /// <summary>Test seam: invoked once per streamed batch during
    /// <see cref="RunHeadlessReorderAsync"/> with that batch's own row count (never a running
    /// total) — lets a test assert resident memory during a reorder never exceeds one batch. No-op
    /// (left null) outside tests.</summary>
    private static readonly AsyncLocal<Action<int>?> _testReorderBatchObserved = new();
    internal static Action<int>? TestReorderBatchObserved
    {
        get => _testReorderBatchObserved.Value;
        set => _testReorderBatchObserved.Value = value;
    }

    /// <summary>Test seams: override <see cref="GraphMetaSplice.MinRowsToConsider"/>
    /// / <see cref="GraphMetaSplice.MaxOldParentRow"/> for <see cref="TryHeadlessSpliceAsync"/> —
    /// a real 200,000-row history isn't practical to build in a fixture test, so a test lowers
    /// these to drive the live splice trigger at small scale. <c>null</c> (production default)
    /// outside tests; a test that sets either must reset it to <c>null</c> when done, since both
    /// are process-wide state.</summary>
    private static readonly AsyncLocal<int?> _testSpliceMinRowsOverride = new();
    internal static int? TestSpliceMinRowsOverride
    {
        get => _testSpliceMinRowsOverride.Value;
        set => _testSpliceMinRowsOverride.Value = value;
    }
    private static readonly AsyncLocal<int?> _testSpliceMaxOldParentRowOverride = new();
    internal static int? TestSpliceMaxOldParentRowOverride
    {
        get => _testSpliceMaxOldParentRowOverride.Value;
        set => _testSpliceMaxOldParentRowOverride.Value = value;
    }

    /// <summary>Test seam: substitutes for the header's real <c>LastFullWalkMs</c>
    /// (guard 4's baseline) in <see cref="TryHeadlessSpliceAsync"/>. A tiny fixture repo's own
    /// walk-from-row-0 time is itself only milliseconds, so the 25%-of-it budget guard 4 computes
    /// from it is too tight to reliably clear real process-spawn variance on a test machine — this
    /// lets a wiring test supply a realistic (large) baseline instead. <c>null</c> outside tests.</summary>
    private static readonly AsyncLocal<TimeSpan?> _testSpliceFullWalkElapsedOverride = new();
    internal static TimeSpan? TestSpliceFullWalkElapsedOverride
    {
        get => _testSpliceFullWalkElapsedOverride.Value;
        set => _testSpliceFullWalkElapsedOverride.Value = value;
    }

    /// <summary>Test seam: invoked with <c>(addedRecords, removedRecords)</c> right
    /// after a splice actually commits — since a spliced header looks identical to a reorder's
    /// (both produce <c>OrderVersion &gt; 0</c> with the right row/record counts), a test needs
    /// this to tell "the splice path ran" apart from "it fell back to the ordinary reorder". Null
    /// outside tests.</summary>
    private static readonly AsyncLocal<Action<int, int>?> _testSpliceCommitted = new();
    internal static Action<int, int>? TestSpliceCommitted
    {
        get => _testSpliceCommitted.Value;
        set => _testSpliceCommitted.Value = value;
    }

    /// <summary>Test seam: invoked as a compute or extend reaches
    /// <see cref="MaybeStartBackgroundSpill"/>, so a test can tear the cache down at exactly that
    /// point. Null outside tests.</summary>
    private static readonly AsyncLocal<Action?> _testBeforeBackgroundSpillStart = new();
    internal static Action? TestBeforeBackgroundSpillStart
    {
        get => _testBeforeBackgroundSpillStart.Value;
        set => _testBeforeBackgroundSpillStart.Value = value;
    }

    /// <summary>Test seam: primes walk every tip from the start, as if no prime were eligible
    /// for <see cref="TryPrefixPrime"/> — the A side of an A/B comparison. Flow-local.</summary>
    private static readonly AsyncLocal<bool> _prefixWalkDisabledForTesting = new();
    internal static bool PrefixWalkDisabledForTesting
    {
        get => _prefixWalkDisabledForTesting.Value;
        set => _prefixWalkDisabledForTesting.Value = value;
    }

    /// <summary>Test seam: told how each eligible prime's prefix walk went. Null outside tests.</summary>
    private static readonly AsyncLocal<Action<PrefixWalkOutcome>?> _testPrefixWalkObserved = new();
    internal static Action<PrefixWalkOutcome>? TestPrefixWalkObserved
    {
        get => _testPrefixWalkObserved.Value;
        set => _testPrefixWalkObserved.Value = value;
    }

    /// <summary>Test seam: rewrites a proven prefix's rows before they reach the cache, so a test
    /// can plant a row the full walk disagrees with. Null outside tests.</summary>
    private static readonly AsyncLocal<Func<List<RawCommit>, List<RawCommit>>?> _testRewriteProvenPrefix = new();
    internal static Func<List<RawCommit>, List<RawCommit>>? TestRewriteProvenPrefix
    {
        get => _testRewriteProvenPrefix.Value;
        set => _testRewriteProvenPrefix.Value = value;
    }

    /// <summary>What <see cref="TryPrefixPrime"/> did: how many of the tips the accepted (or last
    /// tried) prefix walked, whether the proof held, the rows it kept, and why it fell back when
    /// it did.</summary>
    internal readonly record struct PrefixWalkOutcome(int RecentTips, int TotalTips, bool Proven, int Rows, string? FallbackReason);

    /// <summary>Tips whose corrected commit date is within this of the newest go into the prefix
    /// walk.</summary>
    private static readonly long PrefixRecentWindowSeconds = (long)TimeSpan.FromDays(30).TotalSeconds;

    /// <summary>Test seam: how many full ref scans (fingerprint + tip resolution) have run per
    /// repository path. A scan costs seconds on a repo with tens of thousands of refs, so tests
    /// assert on the delta across a call to prove a satisfied or resumed walk step skipped it.
    /// Keyed by <see cref="RepositoryContext.RepositoryPath"/> so parallel test classes on other
    /// fixtures never disturb the count.</summary>
    private static readonly ConcurrentDictionary<string, int> _refScanCounts = new(StringComparer.OrdinalIgnoreCase);

    internal static int RefScanCountForTesting(string repositoryPath) =>
        _refScanCounts.TryGetValue(repositoryPath, out var n) ? n : 0;

    private static void CountRefScan(string repositoryPath) =>
        _refScanCounts.AddOrUpdate(repositoryPath, 1, (_, n) => n + 1);

    /// <summary>Minimum row-count growth between debounced <see cref="GraphWalkCache.Save"/>
    /// calls for the same cache (see <see cref="RepoCache.WalkCacheSavedRows"/>).</summary>
    private const int WalkCacheSaveStride = 5_000;

    private static readonly ConditionalWeakTable<RepositoryContext, GraphPrepareGate> _gates = new();

    internal static GraphPrepareGate GetGateFor(RepositoryContext context) =>
        _gates.GetValue(context, c => new GraphPrepareGate(c.DisplayName));

    private readonly ISettingsService _settings;
    private readonly IGitExecutableLocator _locator;
    private readonly GraphBackgroundWriter? _backgroundWriter;

    /// <summary>Test seam: the commit-graph and inactive-tab writer; null for the constructors
    /// without a CLI.</summary>
    internal GraphBackgroundWriter? BackgroundWriterForTesting => _backgroundWriter;

    /// <summary>Stored (not just forwarded to <see cref="GraphBackgroundWriter"/>)
    /// so <see cref="TryHeadlessSpliceAsync"/> can run its own <c>git rev-list --stdin</c> delta
    /// query directly.</summary>
    private readonly IGitCliService _cli;

    /// <summary>Null in every ctor except the 4-arg DI one below — callers that don't pass a gate
    /// (tests) run un-gated, as if the gate were always open.</summary>
    private readonly IBackgroundWorkGate? _gate;

    /// <summary>page per-commit metadata from a random-access file instead of keeping
    /// it resident. Read once — the setting takes effect on the next launch.</summary>
    private readonly bool _metaFromDisk;

    /// <summary>Every graph query is walked by <c>git log</c> (<see cref="GitLogStream"/>) through
    /// <paramref name="locator"/>. With git.exe missing the graph is empty rather than walked
    /// some other way (see <see cref="PrimeCliWalkAsync"/>); the startup notice tells the user
    /// why.</summary>
    public GraphLayoutService(ISettingsService settings, IGitExecutableLocator locator)
    {
        _settings = settings;
        _locator = locator;
        _cli = new GitCliService(locator);
        // Disk-backed metadata pages from the graph cache folder.
        _metaFromDisk = settings.Current.GraphMetadataFromDisk;
    }

    /// <summary>Additionally wires up
    /// <see cref="GraphBackgroundWriter"/>, scheduled once a repo's background history spill
    /// reaches completion. The 2-arg ctor above runs with no commit-graph
    /// maintenance — used by tests that don't care about it.</summary>
    public GraphLayoutService(ISettingsService settings, IGitExecutableLocator locator, IGitCliService cli)
        : this(settings, locator)
    {
        _cli = cli;
        _backgroundWriter = new GraphBackgroundWriter(cli);
    }

    /// <summary>DI ctor: additionally routes every
    /// background writer through <see cref="IBackgroundWorkGate"/> — no commit-graph write and no
    /// graph disk-cache write starts, for any tab, until the focused tab is done loading.</summary>
    public GraphLayoutService(ISettingsService settings, IGitExecutableLocator locator, IGitCliService cli, IBackgroundWorkGate gate)
        : this(settings, locator, cli)
    {
        _gate = gate;
        _backgroundWriter = new GraphBackgroundWriter(cli, gate);
    }

    /// <summary>Awaits the gate if one was supplied; a no-op ("always open") for the ctors above
    /// that never received one.</summary>
    private Task WaitForGateAsync(CancellationToken ct) =>
        _gate?.WaitUntilOpenAsync(ct) ?? Task.CompletedTask;

    private int _diskCacheSwept;

    /// <inheritdoc/>
    public void SweepDiskCache(IReadOnlyCollection<string> openRepositoryPaths)
    {
        if (Interlocked.Exchange(ref _diskCacheSwept, 1) != 0)
            return;
        string[] keep = [.. openRepositoryPaths];
        var metaFromDisk = _metaFromDisk;
        _ = Task.Run(async () =>
        {
            await WaitForGateAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var swept = GraphCacheJanitor.Sweep(keep, metaFromDisk, DateTime.UtcNow);
                Diagnostics.PerfLog.Mark(
                    $"graph cache sweep: {swept.StoresEvicted} stores evicted, {swept.FilesDeleted} files, {swept.BytesFreed / 1_000_000.0:0.0}MB freed");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Diagnostics.PerfLog.Mark($"graph cache sweep failed: {ex.Message}");
            }
        });
    }

    private readonly DagLayoutEngine _engine = new();
    private readonly GraphWalkCache _walkCache = new();
    private readonly ConditionalWeakTable<RepositoryContext, RepoCache> _caches = [];
    // The walk each layout's rows came from. Extend shares State in place, so one entry covers a
    // layout and every extension of it. A layout whose walk has since been reset (a RefsChanged
    // re-walk) must never be extended from the new walk — its row N is a different commit there.
    private readonly ConditionalWeakTable<GraphLayoutState, RepoCache> _layoutOwners = [];
    // Keyed by repository path, not context: survives tab close/reopen and context re-creation,
    // and decorations are a function of the repo's refs, not the context object.
    private readonly Dictionary<string, DecoCache> _decoByPath = new(StringComparer.OrdinalIgnoreCase);
    private const int DecoCacheCap = 24;
    private readonly object _sync = new();

    public async Task<GraphLayoutResult> ComputeLayoutAsync(
        RepositoryContext context, GraphQuery query, int maxRows = 10_000, CancellationToken ct = default)
    {
        Diagnostics.PerfLog.Mark($"ComputeLayout enter (maxRows={maxRows})");
        var cache = GetOrResetCache(context, query, force: false);
        // A superseded walk can't grow — its tips moved — so a compute always starts a fresh one.
        if (cache.WalkSuperseded)
            cache = GetOrResetCache(context, query, force: true);
        // Refs that can't be read right now are no evidence the painted rows are stale — keep the
        // cache; EnsureWalkedAsync records the failure if it needs refs to go further.
        if (TotalWalked(cache) > 0 && cache.Fingerprint != 0
            && await TryGetRefStateAsync(context, query, ct).ConfigureAwait(false) is { } state
            && state.Fingerprint != cache.Fingerprint)
            cache = GetOrResetCache(context, query, force: true);
        await EnsureWalkedAsync(context, query, cache, maxRows + WalkOverscan, ct).ConfigureAwait(false);
        Diagnostics.PerfLog.Mark($"ComputeLayout walked ({TotalWalked(cache)} commits cached)");

        var spill = SpillEnabled(query);

        // cache.Commits doesn't hold the whole walked history once rows are
        // confirmed spilled (see TrimConfirmedSpilled) — ReadWindow stitches the on-disk store
        // and the resident pending buffer back into one absolute-row-indexed list.
        // A short disk read (a store disposed mid-read, an IOException, a
        // concurrent from-row-0 re-spill) must never feed a misaligned window into the engine —
        // reset the cache (force: true) and re-walk once; if the retry is itself short (a fresh
        // cache has no disk store yet to fail, so this should not recur), proceed with whatever
        // came back rather than loop.
        GraphLayoutResult swapped;
        for (var attempt = 0; ; attempt++)
        {
            var read = ReadWindow(cache, 0, maxRows + WalkOverscan);
            if (!read.Complete)
            {
                cache = GetOrResetCache(context, query, force: true);
                await EnsureWalkedAsync(context, query, cache, maxRows + WalkOverscan, ct).ConfigureAwait(false);
                read = ReadWindow(cache, 0, maxRows + WalkOverscan);
            }
            var input = read.Rows;

            // The trunk pre-pass's ancestry check needs no repo handle: DagLayoutEngine.SeedTipPriority
            // answers it with an exact in-window walk over the already-fetched commit prefix
            // (isAncestor: null below), so Compute runs fully in memory.
            GraphLayoutResult result;
            using (Diagnostics.PerfLog.Scope("engine.Compute"))
                result = _engine.Compute(input, cache.Decorations, maxRows, query.MaxLanes, ct, retainCommits: !spill);

            result = result with { Fingerprint = cache.Fingerprint };
            _layoutOwners.AddOrUpdate(result.State, cache);

            if (!spill)
            {
                _backgroundWriter?.EnsureCurrent(context);
                return result;
            }
            swapped = SpillAndSwap(context, cache, query.IncludeRemoteBranches, result);

            // Trap: the store can be dropped between the read above and the swap (another writer
            // reset it), leaving rows that live only on disk. The layout then names rows it has no
            // commits for, and painting it reads past the end of the store. Build it again from a
            // fresh walk; the second pass has no store to lose.
            if (swapped.Layout.Commits.Count >= swapped.Layout.RowCount || attempt > 0)
                break;
            cache = GetOrResetCache(context, query, force: true);
            await EnsureWalkedAsync(context, query, cache, maxRows + WalkOverscan, ct).ConfigureAwait(false);
        }
        if (cache.Exhausted && cache.MetaComplete)
            await RetireLiveWalkAsync(cache).ConfigureAwait(false);
        MaybeStartBackgroundSpill(context, cache, query);
        // Queued only: the check waits for the background-work gate, so it never competes with
        // the first paint this layout is for.
        _backgroundWriter?.EnsureCurrent(context);
        return swapped with { Fingerprint = cache.Fingerprint };
    }

    /// <inheritdoc/>
    public GraphLayoutResult? TryOptimisticSnapshot(RepositoryContext context, GraphQuery query, int maxRows)
    {
        // Only the very first paint for this context can benefit — once an in-memory walk
        // exists (this session's own earlier walk, or the real ComputeLayoutAsync already
        // finished), it is already at least as fresh as a stale on-disk snapshot.
        lock (_sync)
            if (_caches.TryGetValue(context, out var existing) && TotalWalked(existing) > 0)
                return null;

        // try the metadata store first. Unlike the .gz walk cache below, it is
        // written incrementally to the FULL history by the background spill, isn't bounded by
        // whatever a single foreground walk reached, and carries real parent SHAs — enough to lay out a genuine DAG frame, not just a row list.
        // Stale-tolerant like TryLoadStale: any fingerprint is fine, this frame is always
        // replaced by the verified walk a moment later. Ref badges are not in this store, so the
        // frame paints commits/lanes with no decorations for that one moment.
        if (GraphWalkCache.Supports(query))
        {
            var probe = GraphMetaStore.ProbeExisting(context.RepositoryPath, query.IncludeRemoteBranches);
            if (probe is { Ok: true, RowCount: > 0 })
            {
                DiskCommitStore? store = null;
                try
                {
                    // Open through the header's record count and order version: a headless reorder
                    // appends new records and writes an order file, and an identity-order open
                    // would put those records last and lose their edge to the old tip.
                    store = DiskCommitStore.Open(context.RepositoryPath, probe.RowCount, probe.RecordCount, probe.OrderVersion);
                    var take = Math.Min(maxRows, store.Count);
                    if (take > 0)
                    {
                        var commits = new List<RawCommit>(take);
                        for (var i = 0; i < take; i++)
                            commits.Add(store.ReadRaw(i));
                        Diagnostics.PerfLog.Mark($"optimistic snapshot HIT (meta): {commits.Count} commits (stale, unverified)");
                        return _engine.Compute(commits, EmptyDecorations, maxRows, query.MaxLanes, CancellationToken.None, retainCommits: true);
                    }
                }
                catch { /* fall through to the .gz path — the meta store is an optimisation */ }
                finally { store?.Dispose(); }
            }
        }

        // The .gz walk cache is retired once disk-backed metadata is on (its save is
        // skipped in EnsureWalkedAsync below) — no point reading a file nothing writes in that mode.
        if (_metaFromDisk)
            return null;

        var loaded = _walkCache.TryLoadStale(context.RepositoryPath, query, metadataOnDisk: false);
        if (loaded is null)
            return null;

        Diagnostics.PerfLog.Mark($"optimistic snapshot HIT: {loaded.Commits.Count} commits (stale, unverified)");
        return _engine.Compute(loaded.Commits, loaded.Decorations, maxRows, query.MaxLanes, CancellationToken.None, retainCommits: true);
    }

    private static readonly Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>> EmptyDecorations = [];

    public async Task<GraphLayoutResult> ExtendLayoutAsync(
        RepositoryContext context, GraphLayoutResult existing, int additionalRows, CancellationToken ct = default,
        bool background = false)
    {
        RepoCache? cache;
        lock (_sync)
            _caches.TryGetValue(context, out cache);
        if (cache is null)
            return existing;

        // `existing` was laid out from an earlier walk (a RefsChanged re-walk reset the cache
        // while this layout was still on screen): the current walk's rows don't line up with its
        // rows, so splicing them on would duplicate commits and SpillAndSwap would shift every
        // row's metadata. Its replacement is already being computed — leave it as it is.
        if (!_layoutOwners.TryGetValue(existing.State, out var owner) || !ReferenceEquals(owner, cache))
            return existing;

        var want = existing.State.Rows.Count + additionalRows + WalkOverscan;
        await EnsureWalkedAsync(context, cache.Query, cache, want, ct, background).ConfigureAwait(false);

        // The full walk disagreed with a row of the proven prefix this layout was built on: no row
        // of it can be trusted. Hand back a layout of the full walk instead; its caller replaces
        // the one it extended.
        if (cache.PrefixRejected)
            return await ComputeLayoutAsync(context, cache.Query, existing.State.Rows.Count + additionalRows, ct).ConfigureAwait(false);

        // Extend mutates existing.State in place; run it to completion (a handful of ms for
        // <=5k rows) rather than risk a cancellation leaving that shared state half-updated.
        // Read the consumed-row count under lock(existing.State), then
        // RELEASE it before the (potentially disk-IO-bound) ReadWindow call — holding the lock
        // across disk IO blocks the render thread's own lock(existing.State) in DeriveWindow for
        // the duration of a full disk read. Re-take the lock afterwards and proceed only if
        // State.Rows.Count is STILL `consumed`: a concurrent ExtendLayoutAsync on this layout
        // (e.g. the initial compute's phase-2 extend racing a reveal-driven one) may have advanced
        // it while ReadWindow ran off-lock, and feeding _engine.Extend a slice read against a
        // stale `consumed` would re-place commits it already consumed — duplicate rows and a
        // corrupted RowByCommitId. The per-context extend pump is the only extender, so a mismatch
        // here means exactly that race, and returning `existing` unchanged lets the pump retry.
        int consumed;
        lock (existing.State)
            consumed = existing.State.Rows.Count;

        if (TotalWalked(cache) <= consumed)
            return existing;

        // Only what this extend consumes (+ overscan) — the background spill can have walked
        // (and spilled) far past this, and reading all of it here would be pointless.
        // ReadWindow reads confirmed-spilled rows from disk and the rest from the
        // resident pending buffer, since cache.Commits doesn't hold the whole history.
        var read = ReadWindow(cache, consumed, additionalRows + WalkOverscan);
        if (!read.Complete)
            return existing; // A short disk read — never splice; the pump retries

        GraphLayoutResult result;
        lock (existing.State)
        {
            if (existing.State.Rows.Count != consumed)
                return existing; // Someone else extended this layout meanwhile
            result = _engine.Extend(existing, read.Rows, cache.Decorations, additionalRows, CancellationToken.None);
        }

        if (!SpillEnabled(cache.Query))
            return result with { Fingerprint = cache.Fingerprint };
        var swapped = SpillAndSwap(context, cache, cache.IncludeRemotes, result);
        if (cache.Exhausted && cache.MetaComplete)
            await RetireLiveWalkAsync(cache).ConfigureAwait(false);
        MaybeStartBackgroundSpill(context, cache, cache.Query); // resume if it stalled/hadn't started
        return swapped with { Fingerprint = cache.Fingerprint };
    }

    public async Task<GraphLayoutResult> RecomputeAsync(
        RepositoryContext context, GraphQuery query, GraphLayoutResult previous,
        InvalidationReason reason, int maxRows = 10_000, CancellationToken ct = default)
    {
        if (reason == InvalidationReason.RefsChanged && previous.Fingerprint != 0)
        {
            // Unreadable refs: keep what is painted; the next RefsChanged tries again.
            if (await TryGetRefStateAsync(context, query, ct).ConfigureAwait(false) is not { } state
                || state.Fingerprint == previous.Fingerprint)
                return previous;
        }

        // Every invalidation reason recomputes the whole window: lane assignment is a
        // left-to-right dependency chain, so a change at row k can alter every later row.
        Forget(context);
        return await ComputeLayoutAsync(context, query, maxRows, ct).ConfigureAwait(false);
    }

    public void Forget(RepositoryContext context)
    {
        lock (_sync)
        {
            if (_caches.TryGetValue(context, out var cache))
            {
                try { cache.Cts.Cancel(); } catch (ObjectDisposedException) { }
                cache.Cts.Dispose();
                cache.MetaStore?.Dispose();
                cache.DisposeLiveWalkWhenFree(); // disposed once no pull holds WalkLock
            }
            _caches.Remove(context);
        }
    }

    /// <summary>What <see cref="GetCacheStateForTesting"/> reports: the counters that must agree with
    /// each other (a foreground cache with a live walk has <c>TotalWalked == LiveWalkPos</c>) and
    /// the identity of the store the cache holds.</summary>
    internal sealed record CacheStateForTesting(
        int TotalWalked, int SpilledRows, int LiveWalkPos, bool HasLiveWalk, long WalkKey,
        bool Exhausted, bool MetaComplete, bool WalkHalted, string? MetaStoreGeneration, int? MetaStoreCount);

    /// <summary>A read-only view of one context's cache for the concurrency harness. Every lock is
    /// tried with a zero timeout, in the documented lock order, and any that is busy makes the
    /// whole read return null — it never blocks a walk, a spill or a teardown and never mutates.
    /// Null too when the context has no cache.</summary>
    internal CacheStateForTesting? GetCacheStateForTesting(RepositoryContext context)
    {
        RepoCache? cache;
        if (!Monitor.TryEnter(_sync, 0))
            return null;
        try { _caches.TryGetValue(context, out cache); }
        finally { Monitor.Exit(_sync); }
        if (cache is null)
            return null;

        if (!cache.WalkLock.Wait(0))
            return null;
        try
        {
            if (!Monitor.TryEnter(cache.SpillSync, 0))
                return null;
            try
            {
                if (!Monitor.TryEnter(cache, 0))
                    return null;
                try
                {
                    var store = cache.MetaStore;
                    return new CacheStateForTesting(
                        TotalWalked: cache.SpilledRows + cache.Commits.Count,
                        SpilledRows: cache.SpilledRows,
                        LiveWalkPos: cache.LiveWalkPos,
                        HasLiveWalk: cache.LiveWalk is not null,
                        WalkKey: cache.WalkKey,
                        Exhausted: cache.Exhausted,
                        MetaComplete: cache.MetaComplete,
                        WalkHalted: cache.WalkHalted,
                        MetaStoreGeneration: store?.Generation,
                        MetaStoreCount: store?.Count);
                }
                finally { Monitor.Exit(cache); }
            }
            finally { Monitor.Exit(cache.SpillSync); }
        }
        finally { cache.WalkLock.Release(); }
    }

    /// <inheritdoc/>
    public async Task Hibernate(RepositoryContext context)
    {
        RepoCache? cache;
        lock (_sync)
        {
            if (!_caches.TryGetValue(context, out cache))
                return;
            _caches.Remove(context);
        }

        try { cache.Cts.Cancel(); } catch (ObjectDisposedException) { }

        // Let the background spill observe the cancellation and finish its own turn before
        // this touches LiveWalk — disposing it while a chunk may still be mid-MoveNext is exactly
        // the race DropLiveWalk exists to avoid. A stuck spill falls back
        // to the same drop Forget already uses, rather than hanging tab-switch forever.
        if (cache.BackgroundSpillTask is { } spillTask)
        {
            try { await spillTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch { /* timed out or faulted — best effort, DropLiveWalk below is the fallback */ }
        }

        // Trap: tearing the store and writer down without SpillSync let a foreground extend that was
        // already inside SpillRange see them go null mid-call; the resulting NullReferenceException
        // was swallowed and that frame fell back to the in-memory rebuild. Under SpillSync the
        // extend finishes with the store and writer it took first.
        lock (cache.SpillSync)
        {
            cache.MetaStore?.Dispose();
            cache.MetaStore = null;
            cache.MetaWriter = null;
        }

        if (cache.LiveWalk is { } walk)
        {
            try
            {
                // GitLogStream is a separate process — only serialization against a concurrent pull
                // is needed.
                await cache.WalkLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(cache.LiveWalk, walk))
                        cache.DisposeLiveWalk();
                }
                finally { cache.WalkLock.Release(); }
            }
            catch { cache.DisposeLiveWalkWhenFree(); } // best effort
        }

        lock (cache)
        {
            cache.Commits.Clear();
            cache.Decorations = [];
        }
        try { cache.Cts.Dispose(); } catch { /* best effort */ }

        // Drop this repo's .gz read-cache entry too — kept resident it would outlive the tab
        // switch for the rest of the session.
        _walkCache.Evict(context.RepositoryPath);
    }

    public int ClearPersistentCache()
    {
        lock (_sync)
            _decoByPath.Clear();
        // Any DiskCommitStore still open for a focused tab keeps its file handle; WipeAll may
        // not remove those until the tab closes, but the next open re-walks regardless.
        return _walkCache.WipeAll();
    }

}
