namespace Historia.Services.Graph;

public sealed partial class GraphLayoutService
{
    /// <summary>
    /// One repository tab's walk, resident rows and on-disk store handles. Each field names the one
    /// lock that guards its writes:
    /// <list type="bullet">
    /// <item><c>lock (cache)</c> — <see cref="Commits"/>, <see cref="SpilledRows"/>,
    /// <see cref="Exhausted"/>, <see cref="MetaComplete"/>, <see cref="Decorations"/>,
    /// <see cref="Fingerprint"/>, <see cref="WalkKey"/>, <see cref="WalkCacheSavedRows"/>.</item>
    /// <item><see cref="SpillSync"/> — <see cref="MetaStore"/>, <see cref="MetaWriter"/>,
    /// <see cref="BackgroundSpillRunning"/>.</item>
    /// <item><see cref="WalkLock"/> — <see cref="LiveWalk"/>, <see cref="LiveWalkPos"/>,
    /// <see cref="LiveWalkFingerprint"/>, <see cref="LiveWalkSnapshotToken"/>,
    /// <see cref="WalkFailed"/>, <see cref="WalkSuperseded"/>, <see cref="ProvenPrefixIds"/>,
    /// <see cref="ProvenPrefix"/>, <see cref="PrefixRejected"/>.</item>
    /// </list>
    /// Lock order: the service's <c>_sync</c>, then <see cref="SpillSync"/>, then <c>lock (cache)</c>;
    /// <see cref="WalkLock"/> is taken before <c>lock (cache)</c>. <c>lock (cache)</c> and a store's
    /// own lock are leaves: nothing is acquired under them, and <see cref="SpillSync"/> is never
    /// taken while holding <c>lock (cache)</c>. A <see cref="bool"/> may be read without its lock
    /// only where a stale value is harmless. Reads that must agree with each other (a row count
    /// and the buffer it indexes) take the lock.
    /// </summary>
    private sealed class RepoCache
    {
        /// <summary>The whole query this cache walks — scope, remotes, stashes, path filter and
        /// selected refs — so an extend continues exactly this walk. <see cref="GetOrResetCache"/>
        /// reuses the cache only for a query of the same shape (<see cref="SameWalkShape"/>).
        /// Immutable.</summary>
        public required GraphQuery Query { get; init; }
        public bool IncludeRemotes => Query.IncludeRemoteBranches;

        /// <summary>Guarded by <c>lock (cache)</c>.</summary>
        public List<RawCommit> Commits { get; } = [];

        /// <summary>Guarded by <c>lock (cache)</c>.</summary>
        public Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>> Decorations { get; set; } = [];

        /// <summary>The walk reached its end. Guarded by <c>lock (cache)</c>; only ever set, never
        /// cleared, so an unlocked read can only be late, not wrong.</summary>
        public bool Exhausted { get; set; }

        /// <summary>The walk could not run or continue — git.exe missing, the process failed to
        /// start, or it failed before exhausting (no fallback). The graph keeps whatever
        /// rows were already walked (none, for a failure before the first row) and nothing more
        /// is attempted for this cache: no respawn from the crawler or the spill, no disk write.
        /// <see cref="DropCache"/> or a forced reset drops the cache, so a ref change or a reopen
        /// tries again. Written under <see cref="WalkLock"/>.</summary>
        public bool WalkFailed { get; set; }

        /// <summary>The tip set moved after this cache committed to a walk (<see cref="WalkKey"/>
        /// set): its rows still picture the old refs correctly, but a walk over the new tips
        /// doesn't line up with them row for row, so nothing more is walked or written for this
        /// cache — no resume, no re-prime, no spill, no disk save. The next
        /// <see cref="ComputeLayoutAsync"/> (or the RefsChanged recompute's <see cref="DropCache"/>)
        /// replaces it with a fresh walk from row 0. Normal, not a failure. Written under
        /// <see cref="WalkLock"/>.</summary>
        public bool WalkSuperseded { get; set; }

        /// <summary>Nothing more is walked or written for this cache.</summary>
        public bool WalkHalted => WalkFailed || WalkSuperseded;

        // ── disk-backed metadata ──

        /// <summary>Guarded by <c>lock (cache)</c>; published together with <see cref="WalkKey"/>
        /// and <see cref="Decorations"/> by the step that derived them.</summary>
        public long Fingerprint { get; set; }

        /// <summary>The sorted-tip-set key the on-disk store/`.gz`/live-walk
        /// resume are keyed on, computed by <see cref="ComputeWalkKey"/> — NOT <see
        /// cref="Fingerprint"/>, which covers every ref + HEAD and is the key
        /// <c>RecomputeAsync</c> and the ref-badge cache use. A checkout, a stash, a branch
        /// rename, or a ref outside the walk's scope changes <see cref="Fingerprint"/> (so badges
        /// refresh) but leaves <see cref="WalkKey"/> — and so the disk cache — unchanged.
        /// <para>Once non-zero it names the one walk this cache is committed to; a step that
        /// derives another key supersedes the walk (<see cref="WalkSuperseded"/>). Normally a
        /// prime or a <c>Done</c> sets it with <see cref="Fingerprint"/> and the decorations. It is
        /// set ahead of them — before any row is pulled — in two cases: when the cache adopts a
        /// partial on-disk store (whose rows are that key's), and when a headless job starts
        /// (keyed on the tips it probed, which its writes use). Guarded by <c>lock (cache)</c>.</para></summary>
        public long WalkKey { get; set; }

        /// <summary>Rows [0, SpilledRows) are on disk and no longer in <see cref="Commits"/>.
        /// Guarded by <c>lock (cache)</c>.</summary>
        public int SpilledRows { get; set; }

        /// <summary>The whole history is on disk. Guarded by <c>lock (cache)</c>.</summary>
        public bool MetaComplete { get; set; }

        /// <summary>A background spill task owns the spill loop. Guarded by <see cref="SpillSync"/>.</summary>
        public bool BackgroundSpillRunning { get; set; }

        /// <summary>Guarded by <see cref="SpillSync"/>. The one exception is a fresh cache's first
        /// walk step (a warm-store hit while preparing, and the merge of its result): no spill or
        /// teardown can be running for a cache whose first step has not returned.</summary>
        public GraphMetaWriter? MetaWriter { get; set; }

        /// <summary>Guarded by <see cref="SpillSync"/>, with the same first-step exception as
        /// <see cref="MetaWriter"/>. A store whose <see cref="DiskCommitStore.Generation"/> differs
        /// from <see cref="MetaWriter"/>'s belongs to files another writer has replaced and is
        /// reopened.</summary>
        public DiskCommitStore? MetaStore { get; set; }

        public readonly object SpillSync = new();
        public readonly CancellationTokenSource Cts = new();

        /// <summary><see cref="Cts"/>'s token, read once here. Teardown (Forget, a forced reset,
        /// Hibernate) cancels and disposes <see cref="Cts"/> without waiting for a compute or extend
        /// that is still running on this cache, and reading <c>Cts.Token</c> after that throws.</summary>
        public CancellationToken Cancellation { get; }

        public RepoCache() => Cancellation = Cts.Token;

        /// <summary>The background spill's own Task, so
        /// <see cref="IGraphLayoutService.Hibernate"/> can cancel <see cref="Cts"/> and then await
        /// this before disposing <see cref="LiveWalk"/> — never racing a Dispose against a spill
        /// chunk that may still be mid-<c>MoveNext</c>. Set by the caller that claimed
        /// <see cref="BackgroundSpillRunning"/>; a late read only skips the wait.</summary>
        public Task? BackgroundSpillTask { get; set; }

        // row count as of the last queued GraphWalkCache.Save — debounces the .gz
        // rewrite so a long run of small foreground extends (the silent crawler) doesn't
        // re-snapshot + re-gzip the whole window on every one. Guarded by lock (cache).
        public int WalkCacheSavedRows { get; set; }

        // ── resumable walk ──
        // A live, primed `git log` process (GitLogStream) pulled forward in chunks by
        // EnsureWalkedAsync — the quick phase, each scroll Extend, the silent crawler and the
        // background spill all continue this ONE walk instead of respawning from the tips every
        // time. Pulled under WalkLock. Disposed on reset / Forget / exhaustion /
        // failure, and when a tip-set change supersedes the walk (WalkSuperseded) — a walk is
        // never re-primed over another tip set within one cache. LiveWalk and the three fields
        // below it are written under WalkLock; Forget and a forced reset call DisposeLiveWalkWhenFree
        // without it, deliberately (see DisposeLiveWalkWhenFree).
        public IEnumerator<RawCommit>? LiveWalk { get; set; }

        /// <summary>Despite the name (reflection-based tests poke this field by name), this
        /// holds the WALK KEY (<see cref="WalkKey"/>'s
        /// value at prime time), not the full ref fingerprint — a live walk resumes across a
        /// checkout/stash/rename, since none of those change the tip set it's reading.</summary>
        public long LiveWalkFingerprint { get; set; }
        public int LiveWalkPos { get; set; } // rows already pulled from LiveWalk

        /// <summary>The <see cref="RefSnapshot.Token"/> the live walk's tip set was last
        /// confirmed against. While the repo's ref files still hash to it, a walk step resumes
        /// directly — no ref read, no tip resolution. 0 = unconfirmed.</summary>
        public long LiveWalkSnapshotToken { get; set; }

        // Serializes a prime/resume pull against a concurrent one
        // — e.g. a foreground Extend racing the background spill's next chunk.
        public readonly SemaphoreSlim WalkLock = new(1, 1);

        /// <summary>Dispose the walk (killing its git process) and forget it. Never while another
        /// thread may be mid-MoveNext: callers hold <see cref="WalkLock"/>, or have cancelled
        /// <see cref="Cts"/> and let the spill task observe it.</summary>
        public void DisposeLiveWalk()
        {
            var w = LiveWalk;
            DropLiveWalk();
            try { w?.Dispose(); } catch { /* best effort */ }
        }

        /// <summary>Forget the walk now and dispose it (killing its git process) as soon as
        /// <see cref="WalkLock"/> is free — for callers that can't take the lock themselves
        /// (Forget, a forced reset), where disposing on the spot could race a pull still
        /// mid-MoveNext. Such a pull finishes its chunk with the stream it holds; the dispose
        /// waits for it.
        /// <para>Trap: only dropping the reference left the git process alive until finalization
        /// closed its pipe, still running beside the walk that replaced it.</para></summary>
        public void DisposeLiveWalkWhenFree()
        {
            var walk = LiveWalk;
            DropLiveWalk();
            if (walk is null)
                return;
            _ = Task.Run(async () =>
            {
                await WalkLock.WaitAsync().ConfigureAwait(false);
                try { walk.Dispose(); }
                catch { /* best effort */ }
                finally { WalkLock.Release(); }
            });
        }

        /// <summary>The ids of rows <c>[0, n)</c> when they came from a proven prefix walk (see
        /// <see cref="TryPrefixPrime"/>) and the full walk has not yet confirmed them. The hand-off
        /// prime skips those rows of the full walk and compares each with its id here; once every
        /// one matched this is cleared. While it is set, no row reaches the disk caches. Written
        /// under <see cref="WalkLock"/>; a stale read elsewhere only delays a disk write.</summary>
        public GitObjectId[]? ProvenPrefixIds { get; set; }

        /// <summary>Rows <c>[0, n)</c> came from a proven prefix walk — the count, for logs.</summary>
        public int ProvenPrefix { get; set; }

        /// <summary>The full walk disagreed with a proven prefix row: the walk is halted (with
        /// <see cref="WalkSuperseded"/>) and the next extend rebuilds the layout from a fresh full
        /// walk. Written under <see cref="WalkLock"/>.</summary>
        public bool PrefixRejected { get; set; }

        /// <summary>Forget the walk without disposing it. Callers either dispose it themselves
        /// (<see cref="DisposeLiveWalk"/>) or hand it to <see cref="DisposeLiveWalkWhenFree"/>.</summary>
        public void DropLiveWalk()
        {
            LiveWalk = null;
            LiveWalkFingerprint = 0;
            LiveWalkPos = 0;
            LiveWalkSnapshotToken = 0;
        }
    }

    /// <summary>
    /// Per-repo decoration cache keyed by a cheap fingerprint of every ref's name+target.
    /// Survives <see cref="Forget"/> (which the RefsChanged path calls) so an unchanged
    /// ref set never re-pays the multi-second <see cref="BuildDecorations"/> scan.
    /// </summary>
    private sealed record DecoCache(long Fingerprint, Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>> Decos);
}
