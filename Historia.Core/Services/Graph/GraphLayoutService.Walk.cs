namespace Historia.Services.Graph;

public sealed partial class GraphLayoutService
{
    private RepoCache GetOrResetCache(RepositoryContext context, GraphQuery query, bool force)
    {
        lock (_sync)
        {
            if (!force && _caches.TryGetValue(context, out var existing) && SameWalkShape(existing.Query, query))
                return existing;

            // A forced reset (stale fingerprint) discards a cache that may have a background
            // spill task and an open on-disk store still live — tear those down explicitly,
            // the same as Forget(), or the old task keeps writing to a now-orphaned RepoCache
            // and its DiskCommitStore file handle never closes.
            if (_caches.TryGetValue(context, out var stale))
            {
                try { stale.Cts.Cancel(); } catch (ObjectDisposedException) { }
                stale.Cts.Dispose();
                stale.MetaStore?.Dispose();
                stale.DisposeLiveWalkWhenFree(); // disposed once no pull holds WalkLock
            }

            _caches.Remove(context);
            var fresh = new RepoCache { Query = query };
            _caches.Add(context, fresh);
            return fresh;
        }
    }

    /// <summary>True when two queries walk the same history — every knob that changes the tip
    /// set or the rows (scope, remotes, path filter, selected refs). <see cref="GraphQuery"/>'s
    /// own record equality can't be used: it compares <see cref="GraphQuery.SelectedRefs"/> by
    /// reference, and includes <see cref="GraphQuery.MaxLanes"/>, which only affects layout.</summary>
    private static bool SameWalkShape(GraphQuery a, GraphQuery b) =>
        a.RefScope == b.RefScope
        && a.IncludeRemoteBranches == b.IncludeRemoteBranches
        && string.Equals(a.PathFilter ?? "", b.PathFilter ?? "", StringComparison.Ordinal)
        && (a.SelectedRefs ?? []).SequenceEqual(b.SelectedRefs ?? [], StringComparer.Ordinal);

    private async Task EnsureWalkedAsync(
        RepositoryContext context, GraphQuery query, RepoCache cache, int wantCount, CancellationToken ct,
        bool background = false, bool lowestPriority = false)
    {
        if (cache.Exhausted || cache.WalkHalted || TotalWalked(cache) >= wantCount)
            return;

        DecoCache? deco;
        lock (_sync)
            _decoByPath.TryGetValue(context.RepositoryPath, out deco);

        Diagnostics.PerfLog.Mark($"EnsureWalked need {wantCount}, have {TotalWalked(cache)}, decoFp={deco?.Fingerprint ?? 0}");
        var repoPath = context.RepositoryPath;
        // three gate tiers, not two — a genuine foreground caller always wins; the
        // commit-graph's own silent crawl (background, but user-visible — it grows the
        // scrollbar) comes next; the full-history disk spill (background, no urgency of its
        // own) is lowestPriority and stands aside for either of the other two so it can never
        // starve the crawl, as it would if both shared a tier.
        var gate = GetGateFor(context);
        Task<GatePrep> WithGate(Func<Task<GatePrep>> work) => (background, lowestPriority) switch
        {
            (true, true) => gate.ExecuteSpillAsync(work, ct),
            (true, false) => gate.ExecuteBackgroundAsync(work, ct),
            _ => gate.ExecuteAsync(work, ct),
        };

        GatePrep prep = default;
        for (var pass = 0; pass < 2; pass++)
        {
            // Step 0 — the repo's refs. A file-state check (ms) unless a ref moved
            // since the last read, in which case one `git for-each-ref`.
            RefSnapshot? snapshot = null;
            string failure = "git.exe was not found";
            if (_locator.Current.Found)
            {
                try { snapshot = await context.RefSnapshots.GetAsync(ct).ConfigureAwait(false); }
                catch (HistoriaGitException ex) { failure = ex.Message; }
            }
            if (snapshot is null)
            {
                // Nothing can walk without the refs — the same outcome as `git log` failing to
                // start (no fallback): the graph keeps what it has and this cache stops
                // trying.
                await cache.WalkLock.WaitAsync(ct).ConfigureAwait(false);
                try { MarkWalkFailed(context, cache, $"reading refs failed: {failure}"); }
                finally { cache.WalkLock.Release(); }
                return;
            }

            long liveToken = cache.LiveWalkSnapshotToken;
            if (cache.LiveWalk is not null && liveToken != 0 && liveToken == snapshot.Token)
            {
                // No ref file changed since this walk's tip set was confirmed: the tips,
                // fingerprint and decorations are exactly the cache's — resume directly.
                // (The token covers HEAD and the stash reflog too, so this holds
                // with stashes on.) A resume never writes the cache's ref state, so nothing it
                // reads here can be merged back stale.
                prep = GatePrep.ForResumeCli(cache.Fingerprint, cache.LiveWalkFingerprint, TotalWalked(cache),
                    wantCount, cache.Decorations, snapshot.Token);
                break;
            }

            // Step 1 — on the graph prepare gate: probe on-disk cache, derive ref state, and
            // resolve decorations. It deliberately stops
            // short of spawning or pulling `git log` — the walk itself runs concurrently.
            prep = await WithGate(() =>
                PrepareWalk(repoPath, query, wantCount, deco, cache, snapshot, _metaFromDisk, _walkCache, context.RemoteConfig, ct))
                .ConfigureAwait(false);

            // The snapshot was read before the gate wait, which for the spill can be long. A prime
            // from tips that have since moved would only be superseded on the next step, so when a
            // ref file changed meanwhile, prepare once more from fresh refs — but only while this
            // cache holds nothing yet. Once PrepareWalk adopted a partial on-disk store, the
            // prime must walk that store's own tips (its rows are the walk's first rows); a real
            // tip change then supersedes the walk on the next step.
            if (pass > 0 || prep.Kind != GatePrepKind.PrimeCli || prep.Have != 0
                || RefStateToken.Compute(context.GitDirectory).Token == prep.SnapshotToken)
                break;
            Diagnostics.PerfLog.Mark("refs moved during the gate wait — re-preparing");
        }

        if (prep.Kind == GatePrepKind.Superseded)
        {
            await cache.WalkLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                cache.DisposeLiveWalk();
                cache.WalkSuperseded = true;
            }
            finally { cache.WalkLock.Release(); }
            Diagnostics.PerfLog.Mark(
                $"graph walk superseded: tip set changed (walk key {cache.WalkKey:X} → {prep.WalkKey:X}) — waiting for reset");
            return;
        }

        // Step 2 — off the gate: prime or resume the walk.
        WalkResult walked = prep.Kind switch
        {
            GatePrepKind.PrimeCli => await PrimeCliWalkAsync(context, cache, query, prep, allowPrefix: !background, ct).ConfigureAwait(false),
            GatePrepKind.ResumeCli => await ResumeCliWalkAsync(context, cache, prep, ct).ConfigureAwait(false),
            _ => prep.Result,
        };

        lock (cache)
        {
            cache.Commits.AddRange(walked.Commits);
            // Never back to false: a concurrent step that exhausted the walk may merge before one
            // that saw the walk already gone and reported the flag it read before that merge.
            cache.Exhausted |= walked.Exhausted;
            // The fingerprint, walk key and decorations describe one ref state and change
            // together, written only by the step that derived them: a prime publishes them under
            // WalkLock before pulling a row (PublishRefState), and only a Done from PrepareWalk
            // carries them here. A resume carries none, so it can never write back values it read
            // before a newer prime published.
            if (walked.CarriesRefState)
            {
                cache.Decorations = walked.Decorations;
                if (walked.Fingerprint != 0)
                    cache.Fingerprint = walked.Fingerprint; // ref-based: RecomputeAsync's short-circuit + badges
                if (walked.WalkKey != 0)
                    cache.WalkKey = walked.WalkKey; // the disk/resume identity key
            }
            if (walked.MetaStore is not null && cache.MetaStore is null)
            {
                cache.MetaStore = walked.MetaStore;
                // Adopting a pre-existing store the walk didn't write itself this
                // session (a warm launch) — AdoptDurableStore, not TrimConfirmedSpilled, since
                // the store's row count can exceed anything this cache has walked so far.
                AdoptDurableStore(cache, walked.MetaStore.Count);
                cache.MetaComplete = walked.MetaExhausted;
                cache.MetaWriter ??= new GraphMetaWriter(repoPath, cache.WalkKey, cache.IncludeRemotes);
            }
        }

        if (walked.DecosFresh && walked.Fingerprint != 0)
        {
            lock (_sync)
            {
                if (_decoByPath.Count >= DecoCacheCap && !_decoByPath.ContainsKey(repoPath))
                    _decoByPath.Remove(_decoByPath.Keys.First());
                _decoByPath[repoPath] = new DecoCache(walked.Fingerprint, walked.Decorations);
            }
        }

        // Persist a freshly-walked window for the next launch (best-effort, off the gate). Only
        // the bounded foreground walks do this — a background spill chunk's rows already reach
        // disk via the MetaStore, and snapshotting cache.Commits per chunk would be O(n²).
        // Debounced: the silent crawler drives many small extends in a row, and re-snapshotting
        // + re-gzipping the whole window on every one is pure waste (and a kill mid-write can
        // leave a bad .gz). Save on exhaustion, the first fill, or every WalkCacheSaveStride rows.
        // Retired once disk-backed metadata is on — the MetaStore (with its walk
        // key, exhausted flag, and ReadRawRange) is a strictly better warm-start source, and
        // this avoids ever building `[.. cache.Commits]` (an O(n) copy of the pending buffer)
        // for a file nothing reads in disk mode (see PrepareWalk / TryOptimisticSnapshot).
        if (!background && !_metaFromDisk && !walked.FromDisk && !cache.WalkHalted && walked.Fingerprint != 0
            && cache.ProvenPrefixIds is null && GraphWalkCache.Supports(query))
        {
            List<RawCommit> commits;
            Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>> decos;
            bool exhausted;
            long walkKeyToSave;
            var save = false;
            lock (cache)
            {
                exhausted = cache.Exhausted;
                var rows = cache.Commits.Count;
                if (exhausted || cache.WalkCacheSavedRows == 0 || rows - cache.WalkCacheSavedRows >= WalkCacheSaveStride)
                {
                    cache.WalkCacheSavedRows = rows;
                    save = true;
                }
                commits = save ? [.. cache.Commits] : [];
                decos = cache.Decorations;
                walkKeyToSave = cache.WalkKey; // key the .gz on the walk, not every ref
            }
            if (save)
                // Background-write gate: queued until the gate opens rather than fired
                // immediately, so it never runs inside the gate-closed first-paint window.
                _ = Task.Run(async () =>
                {
                    await WaitForGateAsync(CancellationToken.None).ConfigureAwait(false);
                    _walkCache.Save(repoPath, query, walkKeyToSave, exhausted, commits, decos);
                });
        }
    }

    private enum GatePrepKind { Done, PrimeCli, ResumeCli, Superseded }

    /// <summary>Result of the on-gate half of a walk step. <see cref="GatePrepKind.Done"/> carries
    /// a finished <see cref="WalkResult"/> (a disk-cache hit, nothing left to walk, or an empty
    /// tip set); the prime and resume cases hand back just enough (fingerprint, row counts, tips,
    /// decorations) for the caller to finish the work OFF the gate;
    /// <see cref="GatePrepKind.Superseded"/> says the tip set moved under this cache's walk
    /// (<see cref="RepoCache.WalkSuperseded"/>), with the new walk key for the log.</summary>
    private readonly record struct GatePrep(
        GatePrepKind Kind,
        WalkResult Result,
        long Fingerprint,
        long WalkKey,
        int Have,
        int WantCount,
        string[]? Tips,
        Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>> Decorations,
        bool DecosFresh,
        long SnapshotToken)
    {
        public static GatePrep Done(WalkResult result) =>
            new(GatePrepKind.Done, result, result.Fingerprint, result.WalkKey, 0, 0, null, result.Decorations, result.DecosFresh, 0);

        public static GatePrep ForResumeCli(
            long fp, long walkKey, int have, int want,
            Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>> decorations, long snapshotToken) =>
            new(GatePrepKind.ResumeCli, default, fp, walkKey, have, want, null, decorations, false, snapshotToken);

        public static GatePrep ForPrimeCli(
            long fp, long walkKey, int have, int want, string[] tips,
            Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>> decorations, bool decosFresh, long snapshotToken) =>
            new(GatePrepKind.PrimeCli, default, fp, walkKey, have, want, tips, decorations, decosFresh, snapshotToken);
    }

    /// <summary>Derives the fingerprint and walk key from
    /// <paramref name="snapshot"/> (plus the stash list, when the query includes stashes) and tries
    /// the on-disk caches; otherwise hands back what the caller needs to resume the live
    /// <see cref="GitLogStream"/> or prime a fresh one. Never spawns or pulls
    /// the stream itself.</summary>
    private static async Task<GatePrep> PrepareWalk(
        string repoPath, GraphQuery query, int wantCount, DecoCache? decoCache,
        RepoCache cache, RefSnapshot snapshot, bool metaFromDisk, GraphWalkCache walkCache,
        RepoConfigReader remoteConfig, CancellationToken ct = default)
    {
        // Read fresh on the gate — a concurrent EnsureWalkedAsync (the silent crawler vs the
        // background spill) serialises here and has usually already walked what this caller
        // wanted. Checked before any ref work: on a repo with tens of thousands of refs that work
        // costs seconds, and a satisfied caller needs none of it. "Have" is TOTAL rows walked
        // (spilled + resident pending), not cache.Commits.Count alone — confirmed-spilled rows
        // don't stay resident. Carries no ref state: the cache's own stays untouched.
        var have = cache.SpilledRows + cache.Commits.Count;
        if (have >= wantCount || cache.Exhausted)
        {
            Diagnostics.PerfLog.Mark($"EnsureWalked satisfied on gate (have {have} >= want {wantCount})");
            return GatePrep.Done(new WalkResult([], cache.Decorations, cache.Exhausted, Fingerprint: 0, DecosFresh: false,
                WalkKey: 0, CarriesRefState: false));
        }

        // The disk cache, live-walk resume, and .gz identity all key on the sorted
        // TIP SET the walk actually reads (`walkKey`) instead of every ref + HEAD (`fp`,
        // used below for badges/decorations and by the caller for RecomputeAsync's short-circuit).
        // A checkout, a branch rename, or a ref outside this set changes `fp`
        // but leaves `walkKey` — and so the on-disk cache — untouched.
        var (fp, tips, walkKey) = DeriveRefState(snapshot, query, repoPath);

        // This cache already walked (or adopted from disk) rows of another tip set. A walk over
        // the new tips doesn't match those rows one for one — new commits land anywhere in its
        // first rows — so neither resuming nor skipping ahead in a fresh walk is correct, and the
        // rows must never reach the old walk's on-disk store. Stop; a reset rebuilds from row 0.
        // Keyed on the walk key, not `have`: the headless reorder empties cache.Commits after
        // every batch.
        if (cache.WalkKey != 0 && walkKey != cache.WalkKey)
            return new GatePrep(GatePrepKind.Superseded, default, fp, walkKey, have, wantCount, null, cache.Decorations, false, snapshot.Token);

        async Task<(Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>> Decorations, bool Fresh)> GetDecorationsAsync()
        {
            if (decoCache is not null && fp != 0 && decoCache.Fingerprint == fp)
                return (decoCache.Decos, false);

            var remotes = await remoteConfig.GetRemotesAsync(ct).ConfigureAwait(false);
            var remoteNames = remotes.Select(r => r.Name).OrderByDescending(n => n.Length).ToArray();
            return ResolveDecorations(query, snapshot, decoCache, fp, remoteNames);
        }

        // Fresh cache, nothing primed: try the on-disk walk before spawning anything. Only the
        // plain all-refs shape is ever cached on disk.
        if (have == 0 && cache.LiveWalk is null && GraphWalkCache.Supports(query))
        {
            // Prefer the meta store over the (in disk mode, retired) .gz walk
            // cache — it never needs a resident copy of rows already durable. An exhausted store
            // serves every row straight off disk (WalkResult.Commits stays empty: nothing to add
            // to the pending buffer); a partial store resumes the walk with skip = store.Count
            // instead of reloading any of its rows into memory.
            if (metaFromDisk)
            {
                var probe = GraphMetaStore.Probe(repoPath, walkKey, query.IncludeRemoteBranches);
                if (probe.Ok && probe.RowCount > 0
                    && GraphMetaStore.TryOpen(repoPath, walkKey, query.IncludeRemoteBranches, probe.RowCount) is { } store)
                {
                    if (probe.Exhausted)
                    {
                        // The .meta store has no decoration data (unlike the retired .gz DTO) —
                        // resolve it the same way a fresh walk would, so a warm reopen doesn't
                        // paint with empty ref badges (GraphSpillTrunkStabilityTests: the tip-
                        // priority pre-pass also depends on which commit is decorated).
                        var (decorations, decosFresh) = await GetDecorationsAsync().ConfigureAwait(false);
                        Diagnostics.PerfLog.Mark($"meta store HIT (exhausted): {store.Count} rows on disk, no resident copy");
                        return GatePrep.Done(new WalkResult([], decorations, true, fp, decosFresh, FromDisk: true, MetaStore: store, MetaExhausted: true, WalkKey: walkKey));
                    }

                    cache.MetaStore = store;
                    cache.MetaWriter ??= new GraphMetaWriter(repoPath, walkKey, query.IncludeRemoteBranches);
                    AdoptDurableStore(cache, store.Count);
                    // The adopted rows commit this cache to the store's walk now, not at the
                    // prime: a step that prepares before that prime and sees other tips must
                    // supersede, not prime its own walk over these rows.
                    lock (cache)
                        cache.WalkKey = walkKey;
                    have = store.Count;
                    Diagnostics.PerfLog.Mark($"meta store HIT (partial): resuming walk after {store.Count} durable rows");
                    if (have >= wantCount)
                    {
                        var (decorations, decosFresh) = await GetDecorationsAsync().ConfigureAwait(false);
                        return GatePrep.Done(new WalkResult([], decorations, false, fp, decosFresh, FromDisk: true, WalkKey: walkKey));
                    }
                    // fall through below to prime/continue a walk with skip = have
                }
            }
            else if (walkCache.TryLoad(repoPath, query, wantCount, walkKey, metadataOnDisk: false) is { } hit)
            {
                // The file is keyed on the tip set, which ignores HEAD — its saved badges can
                // predate a checkout. Badges come from the refs instead, as for the meta store.
                var (decorations, decosFresh) = await GetDecorationsAsync().ConfigureAwait(false);
                Diagnostics.PerfLog.Mark($"walk cache HIT: {hit.Commits.Count} commits, exhausted={hit.Exhausted}");
                return GatePrep.Done(new WalkResult(hit.Commits, decorations, hit.Exhausted, fp, decosFresh, FromDisk: true, WalkKey: walkKey));
            }
        }

        // Continue the ONE primed walk for this walk key — no tip reseed, no BuildDecorations.
        // This is what makes an Extend / silent-crawl / spill chunk cheap on a big repo after the
        // first (unavoidably expensive) prime, and what lets it survive a checkout/stash/rename
        // that leaves the tip set — and so the walk key — unchanged.
        // Pulling from the git process is what must stay off the gate — hand it back.
        if (cache.LiveWalk is not null && cache.LiveWalkFingerprint == walkKey)
            return GatePrep.ForResumeCli(fp, walkKey, have, wantCount, cache.Decorations, snapshot.Token);

        // No live walk for this tip set: prime one. The tip set is this cache's own (or the cache
        // is fresh, possibly holding a partial on-disk store of this same walk key), so the `have`
        // rows the prime skips are exactly the walk's first rows. A prime publishes its walk key
        // with LiveWalk (PublishRefState), so no other live walk can be left here; the dispose is
        // a guard, not a path.
        cache.DisposeLiveWalk();
        var (primeDecorations, primeDecosFresh) = await GetDecorationsAsync().ConfigureAwait(false);
        if (tips.Length == 0)
            return GatePrep.Done(new WalkResult([], primeDecorations, true, fp, primeDecosFresh, WalkKey: walkKey));
        return GatePrep.ForPrimeCli(fp, walkKey, have, wantCount, tips, primeDecorations, primeDecosFresh, snapshot.Token);
    }

    /// <summary>The disk-cache / live-walk-resume identity key — a hash of the
    /// SORTED tip SHAs the walk actually reads (stash tips included when the query includes
    /// stashes), the remote-branch flag and, only when set, the path filter and the repository's
    /// history shape (<see cref="RefSnapshot.HistoryShape"/>: its shallow boundary and grafts).
    /// Unlike <see cref="ComputeFingerprint"/> it does not depend on HEAD or on any ref outside
    /// the walk's tips, so a checkout, a stash outside the walk, a branch rename, or
    /// `pack-refs`/`gc` leaves it unchanged as long as the tip set is the same. The same tips walk
    /// a different history once a shallow clone is deepened, so the shape is part of the key. The
    /// path and the shape are fed only when non-empty, so a filtered walk can never be resumed as
    /// the unfiltered one (or another path's) while every unfiltered key of a full clone — and so
    /// every existing on-disk store — stays as it is.
    /// <paramref name="sortedTips"/> must already be ordinally sorted (<see cref="ResolveTipShas"/>
    /// guarantees this) — the hash itself doesn't re-sort, so a caller feeding an unsorted list
    /// would silently defeat the point.</summary>
    private static long ComputeWalkKey(IReadOnlyList<string> sortedTips, GraphQuery query, string historyShape)
    {
        var h = StableHash.Fnv64OffsetBasis;
        void Feed(string s) => h = StableHash.Fnv1a64Step(h, s);
        foreach (var sha in sortedTips)
            Feed(sha);
        Feed(query.IncludeRemoteBranches ? "R1" : "R0");
        if (!string.IsNullOrEmpty(query.PathFilter))
            Feed("P:" + query.PathFilter);
        if (historyShape.Length > 0)
            Feed("H:" + historyShape);
        return unchecked((long)h);
    }

    /// <summary>Spawns <see cref="GitLogStream"/> and pulls the first batch. When git is missing,
    /// the process can't start, or it fails, the walk stops there
    /// (<see cref="RepoCache.WalkFailed"/>): the rows already pulled are kept and returned
    /// non-exhausted, so an empty or partial history is never recorded as complete.
    /// <para>The prime's ref state (fingerprint, walk key, decorations) reaches the cache here,
    /// under <see cref="RepoCache.WalkLock"/> and before any row is pulled, so a resume that takes
    /// the lock next already sees it. The result carries none for the caller to merge.</para>
    /// <para>A foreground first prime may paint from a proven prefix instead
    /// (<see cref="TryPrefixPrime"/>), leaving no live walk; the next prime is then the full
    /// walk, skipping and checking the proven rows.</para></summary>
    private async Task<WalkResult> PrimeCliWalkAsync(
        RepositoryContext context, RepoCache cache, GraphQuery query, GatePrep prep, bool allowPrefix, CancellationToken ct)
    {
        await cache.WalkLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another step primed (or superseded the walk) while this one waited for the lock — its
            // outcome stands; the next step resumes it, or supersedes it when the tips differ. A
            // walk that already ran to the end leaves no LiveWalk behind, so the row count tells
            // too: priming now would skip only the rows this step saw and pull the rest again.
            if (cache.WalkHalted || cache.LiveWalk is not null
                || (cache.WalkKey != 0 && cache.WalkKey != prep.WalkKey)
                || TotalWalked(cache) != prep.Have)
                return new WalkResult([], prep.Decorations, Exhausted: false, prep.Fingerprint, DecosFresh: false,
                    WalkKey: prep.WalkKey, CarriesRefState: false);

            using var walkTimer = Diagnostics.PerfLog.Scope("Walk loop (cli)");
            PublishRefState(cache, prep);

            if (allowPrefix && TryPrefixPrime(context, cache, query, prep, ct))
                return new WalkResult([], prep.Decorations, Exhausted: false, prep.Fingerprint, prep.DecosFresh,
                    WalkKey: prep.WalkKey, CarriesRefState: false);

            var stream = GitLogStream.TryStart(
                _locator, context.RepositoryPath, prep.Tips!, context.ActiveProfile, query.PathFilter);

            if (stream is null)
            {
                MarkWalkFailed(context, cache,
                    _locator.Current.Found ? "git log could not be started" : "git.exe was not found");
                return new WalkResult([], prep.Decorations, Exhausted: false, prep.Fingerprint, prep.DecosFresh,
                    WalkKey: prep.WalkKey, CarriesRefState: false);
            }

            cache.LiveWalk = stream;
            cache.LiveWalkFingerprint = prep.WalkKey;
            cache.LiveWalkPos = 0;
            cache.LiveWalkSnapshotToken = prep.SnapshotToken;

            var need = prep.WantCount - prep.Have;
            var delta = new List<RawCommit>(Math.Min(need, 8192));
            // prep.Have rows may already be resident from a partial on-disk cache (a
            // non-exhausted .gz or .meta store), so the fresh walk re-skips them or its rows
            // [0, need) would land duplicated after the cached ones.
            var exhausted = PumpCommits(stream, delta, skip: prep.Have, want: need, cache, ct);

            if (cache.PrefixRejected)
            {
                RejectProvenPrefix(context, cache);
                return new WalkResult([], prep.Decorations, Exhausted: false, prep.Fingerprint, prep.DecosFresh,
                    WalkKey: prep.WalkKey, CarriesRefState: false);
            }
            if (stream.Failed)
            {
                MarkWalkFailed(context, cache, stream.FailureReason ?? "git log failed");
                exhausted = false;
            }
            else if (exhausted)
                cache.DisposeLiveWalk();

            AppendWalked(cache, delta, prep.Have);
            Diagnostics.PerfLog.Mark($"Walk loop (cli) read {delta.Count} commits (pos {cache.LiveWalkPos}, exhausted={exhausted})");
            return new WalkResult([], prep.Decorations, exhausted, prep.Fingerprint, prep.DecosFresh,
                WalkKey: prep.WalkKey, CarriesRefState: false);
        }
        finally
        {
            cache.WalkLock.Release();
        }
    }

    /// <summary>Writes a prime's fingerprint, walk key and decorations to the cache together.
    /// Caller holds <see cref="RepoCache.WalkLock"/>.</summary>
    private static void PublishRefState(RepoCache cache, GatePrep prep)
    {
        lock (cache)
        {
            cache.Fingerprint = prep.Fingerprint;
            cache.WalkKey = prep.WalkKey;
            cache.Decorations = prep.Decorations;
        }
    }

    /// <summary>Stops the walk for good — see <see cref="RepoCache.WalkFailed"/> — and records
    /// why. Caller holds <see cref="RepoCache.WalkLock"/>.</summary>
    private static void MarkWalkFailed(RepositoryContext context, RepoCache cache, string reason)
    {
        cache.DisposeLiveWalk();
        cache.WalkFailed = true;
        Diagnostics.PerfLog.Mark($"graph walk failed: {reason}");
        AppLog.Write(AppLogLevel.Warn, "graph", $"Commit graph walk failed for {context.RepositoryPath}: {reason}");
    }

    /// <summary>Resumes an already-primed <see cref="GitLogStream"/>, so a
    /// scroll Extend or the background spill's next chunk never queues behind status/diff
    /// work on this repo.</summary>
    private static async Task<WalkResult> ResumeCliWalkAsync(
        RepositoryContext context, RepoCache cache, GatePrep prep, CancellationToken ct)
    {
        await cache.WalkLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The walk may have been disposed/replaced between PrepareWalk's read and here (a
            // concurrent Forget/reset) — nothing to add this round; the caller's next attempt
            // re-evaluates from scratch.
            if (cache.LiveWalk is not { } live || cache.LiveWalkFingerprint != prep.WalkKey)
                return new WalkResult([], prep.Decorations, cache.Exhausted, prep.Fingerprint, DecosFresh: false, WalkKey: prep.WalkKey, CarriesRefState: false);

            // PrepareWalk just matched this walk's key against a tip set derived from this
            // snapshot, so later steps may skip the gate until a ref file changes again.
            if (prep.SnapshotToken != 0)
                cache.LiveWalkSnapshotToken = prep.SnapshotToken;

            // Sized from the rows walked now, not prep.Have: a concurrent resume may have pulled a
            // whole chunk while this one waited on WalkLock, after PrepareWalk read it. Exact
            // here because every pull appends its rows before releasing WalkLock (AppendWalked).
            // Not LiveWalkPos: the reorder pass clears cache.Commits after every batch and asks
            // for batchRows each time, i.e. relative to what the cache holds, not to the stream.
            var have = TotalWalked(cache);
            var need = prep.WantCount - have;
            if (need <= 0)
                return new WalkResult([], cache.Decorations, Exhausted: false, prep.Fingerprint, DecosFresh: false, WalkKey: prep.WalkKey, CarriesRefState: false);

            // Trap: resuming with skip: 0 after another writer's rows were adopted (SpilledRows
            // jumped past LiveWalkPos) re-appended rows the store already held, and the next spill
            // wrote them to disk a second time. Rows below `have` are already in the cache, so the
            // stream discards them. The reorder pass clears cache.Commits per batch, so its `have`
            // is 0 and nothing is skipped there.
            var delta = new List<RawCommit>(Math.Min(need, 8192));
            var exhausted = PumpCommits(live, delta, skip: have, want: need, cache, ct);
            if (cache.PrefixRejected)
            {
                RejectProvenPrefix(context, cache);
                return new WalkResult([], cache.Decorations, Exhausted: false, prep.Fingerprint, DecosFresh: false, WalkKey: prep.WalkKey, CarriesRefState: false);
            }
            if (live is GitLogStream { Failed: true } failed)
            {
                MarkWalkFailed(context, cache, failed.FailureReason ?? "git log failed");
                exhausted = false;
            }
            else if (exhausted)
                cache.DisposeLiveWalk();
            AppendWalked(cache, delta, have);
            Diagnostics.PerfLog.Mark($"walk resumed (cli) +{delta.Count} (pos {cache.LiveWalkPos}, exhausted={exhausted})");
            return new WalkResult([], cache.Decorations, exhausted, prep.Fingerprint, DecosFresh: false, WalkKey: prep.WalkKey, CarriesRefState: false);
        }
        finally
        {
            cache.WalkLock.Release();
        }
    }

    /// <summary>Appends a pull's rows to the pending buffer while the caller still holds
    /// <see cref="RepoCache.WalkLock"/>, so two pulls in a row can never land in the buffer in the
    /// opposite order to the one they were read from the stream in.</summary>
    private static void AppendWalked(RepoCache cache, List<RawCommit> delta, int firstRow)
    {
        if (delta.Count == 0)
            return;
        lock (cache)
        {
            // Trap: the pull ran off every lock, sized from the rows held when it began. Another
            // writer's rows adopted meanwhile (SpilledRows jumped) already cover the front of the
            // pull; appending it whole put those rows in the buffer twice.
            var held = WalkedRowsAlreadyHeld(cache.SpilledRows + cache.Commits.Count, firstRow, delta.Count);
            cache.Commits.AddRange(held == 0 ? delta : delta.Skip(held));
        }
    }

    /// <summary>How many leading rows of a pull that starts at absolute row <paramref name="firstRow"/>
    /// the cache already holds when <paramref name="rowsHeld"/> rows are held by now.</summary>
    internal static int WalkedRowsAlreadyHeld(int rowsHeld, int firstRow, int pulled)
        => Math.Clamp(rowsHeld - firstRow, 0, pulled);

    private const int RetireWalkLockTimeoutMs = 5_000;

    /// <summary>
    /// Safely retires and disposes the cache's live walk off the spill lock, under
    /// <see cref="RepoCache.WalkLock"/> so it never races an in-flight pull. Gives up after
    /// <see cref="RetireWalkLockTimeoutMs"/> rather than stall a layout.
    /// </summary>
    private static async Task RetireLiveWalkAsync(RepoCache cache)
    {
        if (cache.LiveWalk is null)
            return;

        try
        {
            if (await cache.WalkLock.WaitAsync(RetireWalkLockTimeoutMs).ConfigureAwait(false))
            {
                try
                {
                    cache.DisposeLiveWalk();
                }
                finally
                {
                    cache.WalkLock.Release();
                }
            }
        }
        catch
        {
            // Retirement is best-effort; a failed retirement must never fail a layout.
        }
    }

    /// <summary>Pull up to <paramref name="want"/> commits from an already-positioned walk
    /// enumerator into <paramref name="into"/>, discarding the first <paramref name="skip"/>
    /// still-unconsumed rows (used once, when a partial on-disk cache seeded
    /// <c>cache.Commits</c> and the fresh walk has to re-skip them). Returns true when the walk
    /// ran out — which, for a <see cref="GitLogStream"/>, is also how a failure surfaces, so
    /// callers check <see cref="GitLogStream.Failed"/> before treating it as exhaustion.
    /// Cooperative: a cancelled <paramref name="ct"/> stops early and leaves the enumerator valid
    /// for the next call. Caller holds <see cref="RepoCache.WalkLock"/>.</summary>
    private static bool PumpCommits(
        IEnumerator<RawCommit> walk, List<RawCommit> into, int skip, int want, RepoCache cache, CancellationToken ct)
    {
        var collected = 0;
        while (collected < want)
        {
            if (ct.IsCancellationRequested)
                return false;
            if (!walk.MoveNext())
                return true;
            var pos = cache.LiveWalkPos++;
            if (cache.ProvenPrefixIds is { } proven && pos < proven.Length)
            {
                // The full walk's row `pos` must be the proven prefix's row `pos`.
                if (walk.Current.Id != proven[pos])
                {
                    cache.PrefixRejected = true;
                    return false;
                }
                if (pos == proven.Length - 1)
                {
                    cache.ProvenPrefixIds = null;
                    Diagnostics.PerfLog.Mark($"graph prefix walk: full walk confirmed all {cache.ProvenPrefix} proven rows");
                }
            }
            if (pos < skip)
                continue;
            into.Add(walk.Current);
            collected++;
        }
        return false;
    }

    /// <summary>Repositories where the full walk once disagreed with a proven prefix: their primes
    /// walk every tip from the start for the rest of the session.</summary>
    private static readonly ConcurrentDictionary<string, byte> _prefixDistrusted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The first prime of an all-refs walk: walks only the recent tips and keeps those rows
    /// only when they are provably the full walk's first rows, so the first window doesn't wait
    /// for <c>git log --date-order</c> to sort every tip's history. Returns false — nothing pulled
    /// kept, nothing in the cache changed — when the prime isn't eligible or the proof fails
    /// twice; the caller then primes the full walk as usual.
    /// <para><b>Eligible</b> when nothing has been walked, loaded or adopted yet (no rows, no live
    /// store), the scope is all refs with no path filter, there are at least two tips (one tip
    /// can never leave an older one outside the window), the repository isn't distrusted after a
    /// failed proof, it has no <c>info/grafts</c>, and the commit-graph is usable. The tip count
    /// sets no other bound: the tips are always looked up, and the dates decide — when every tip
    /// is recent there is nothing to leave out, and the normal prime runs.</para>
    /// <para><b>The proof.</b> The commit-graph's corrected commit date (cd) of a commit is greater
    /// than each parent's and at least its own commit date, so a tip's cd bounds the commit date of
    /// everything it reaches. R = tips with cd within <see cref="PrefixRecentWindowSeconds"/> of
    /// the newest, plus any tip the graph doesn't list; O = the rest, maxO their largest cd. Walk R
    /// with the same <c>git log</c> and pull a window. If maxO is below every pulled row's commit
    /// date, then in the full walk no commit reachable only from O can come out before any of them
    /// (its date is lower, and the walk emits the newest ready commit first), and none of them has
    /// such a commit as a child (that child's cd would exceed maxO): the full walk emits exactly
    /// these rows first, in this order — R keeps the full tip list's order, so ties break the
    /// same way. On failure, R widens once to every tip whose cd reaches the oldest pulled row.</para>
    /// <para>On success the rows become rows <c>[0, n)</c> with no live walk; the next step primes
    /// the full walk, which skips those rows and checks each against them
    /// (<see cref="RepoCache.ProvenPrefixIds"/>). Caller holds <see cref="RepoCache.WalkLock"/>
    /// and has published the prime's ref state; the prefix stream never outlives this call.</para>
    /// </summary>
    private bool TryPrefixPrime(RepositoryContext context, RepoCache cache, GraphQuery query, GatePrep prep, CancellationToken ct)
    {
        var tips = prep.Tips!;
        if (PrefixWalkDisabledForTesting || prep.Have != 0 || TotalWalked(cache) != 0 || cache.MetaStore is not null
            || query.RefScope != GraphRefScope.AllRefs || !string.IsNullOrEmpty(query.PathFilter)
            || tips.Length < 2 || _prefixDistrusted.ContainsKey(context.RepositoryPath))
            return false;

        var sw = Stopwatch.StartNew();
        void Report(PrefixWalkOutcome outcome)
        {
            if (outcome.Proven)
                Diagnostics.PerfLog.Mark($"graph prefix walk: {outcome.RecentTips} of {outcome.TotalTips} tips, proof ok, {outcome.Rows} rows in {sw.Elapsed.TotalMilliseconds:F0} ms");
            else
                Diagnostics.PerfLog.Mark($"graph prefix walk: {outcome.RecentTips} of {outcome.TotalTips} tips, full walk instead ({outcome.FallbackReason}) after {sw.Elapsed.TotalMilliseconds:F0} ms");
            TestPrefixWalkObserved?.Invoke(outcome);
        }

        // Grafts rewrite parents the commit-graph knows nothing about; its dates bound nothing then.
        if (File.Exists(Path.Combine(RefStateToken.ResolveCommonDir(context.GitDirectory), "info", "grafts")))
        {
            Report(new(0, tips.Length, false, 0, "info/grafts"));
            return false;
        }

        var ids = new GitObjectId[tips.Length];
        for (var i = 0; i < tips.Length; i++)
            if (!GitObjectId.TryParse(tips[i], out ids[i]))
                return false;
        if (context.CommitGraph.Lookup(ids) is not { } graph)
        {
            Report(new(0, tips.Length, false, 0, "no commit-graph with corrected dates"));
            return false;
        }

        var dates = new long?[tips.Length];
        long newest = long.MinValue;
        for (var i = 0; i < tips.Length; i++)
        {
            dates[i] = graph.CorrectedDate(ids[i]);
            if (dates[i] is { } d && d > newest)
                newest = d;
        }
        if (newest == long.MinValue)
        {
            Report(new(0, tips.Length, false, 0, "no tip in the commit-graph"));
            return false;
        }

        var cutoff = newest - PrefixRecentWindowSeconds;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var recent = new List<string>();
            long maxOld = long.MinValue;
            for (var i = 0; i < tips.Length; i++)
            {
                if (dates[i] is not { } d || d >= cutoff)
                    recent.Add(tips[i]);
                else if (d > maxOld)
                    maxOld = d;
            }
            if (maxOld == long.MinValue)
            {
                Report(new(recent.Count, tips.Length, false, 0, attempt == 0 ? "every tip is recent" : "the widened window holds every tip"));
                return false;
            }

            var (rows, failure) = PullPrefix(context, query, recent, prep.WantCount, ct);
            if (rows is null)
            {
                Report(new(recent.Count, tips.Length, false, 0, failure));
                return false;
            }

            var minDate = long.MaxValue;
            foreach (var r in rows)
                minDate = Math.Min(minDate, r.Metadata.CommitWhen.ToUnixTimeSeconds());
            if (maxOld < minDate)
            {
                if (TestRewriteProvenPrefix is { } rewrite)
                    rows = rewrite(rows);
                cache.ProvenPrefixIds = [.. rows.Select(r => r.Id)];
                cache.ProvenPrefix = rows.Count;
                AppendWalked(cache, rows, 0);
                Report(new(recent.Count, tips.Length, true, rows.Count, null));
                return true;
            }

            Diagnostics.PerfLog.Mark(
                $"graph prefix walk: proof failed for {recent.Count} of {tips.Length} tips (oldest tip outside {maxOld} >= oldest row {minDate})");
            cutoff = minDate;
            if (attempt == 1)
                Report(new(recent.Count, tips.Length, false, 0, "the proof failed twice"));
        }
        return false;
    }

    /// <summary>Walks <paramref name="tips"/> for up to <paramref name="want"/> rows and disposes
    /// the stream. Null rows (with the reason) when it couldn't start, failed, was cancelled, or
    /// produced nothing.</summary>
    private (List<RawCommit>? Rows, string Failure) PullPrefix(
        RepositoryContext context, GraphQuery query, List<string> tips, int want, CancellationToken ct)
    {
        using var stream = GitLogStream.TryStart(_locator, context.RepositoryPath, tips, context.ActiveProfile, query.PathFilter);
        if (stream is null)
            return (null, "git log could not be started");
        var rows = new List<RawCommit>(Math.Min(want, 8192));
        while (rows.Count < want)
        {
            if (ct.IsCancellationRequested)
                return (null, "cancelled");
            if (!stream.MoveNext())
                break;
            rows.Add(stream.Current);
        }
        if (stream.Failed)
            return (null, stream.FailureReason ?? "git log failed");
        return rows.Count == 0 ? (null, "the recent tips walked no rows") : (rows, "");
    }

    /// <summary>The full walk disagreed with a proven prefix row: halt this cache like a stale
    /// walk key (the next compute rebuilds from a full walk), and never prefix-walk this
    /// repository again this session. Caller holds <see cref="RepoCache.WalkLock"/>.</summary>
    private static void RejectProvenPrefix(RepositoryContext context, RepoCache cache)
    {
        cache.DisposeLiveWalk();
        cache.ProvenPrefixIds = null;
        cache.WalkSuperseded = true;
        _prefixDistrusted[context.RepositoryPath] = 0;
        Diagnostics.PerfLog.Mark("graph prefix walk: full walk disagrees with a proven row — rebuilding from the full walk");
        AppLog.Write(AppLogLevel.Warn, "graph",
            $"Commit graph for {context.RepositoryPath}: the full walk disagrees with a row of the first window; rebuilding the graph from the full walk.");
    }

    /// <summary>Resolves the walk's tips from <paramref name="snapshot"/> — local / remote / tag
    /// refs filtered by <see cref="GraphQuery.RefScope"/> — as
    /// commit SHAs for <c>git log --stdin</c>.
    /// <para>An annotated tag reaches the walk through its peeled commit (a release tagged on a
    /// branch later deleted is only reachable that way); a ref that reaches a tree or blob, such
    /// as linux's <c>v2.6.11-tree</c>, is dropped (<see cref="RefEntry.PeeledCommit"/>). A dangling
    /// symbolic ref never appears in the snapshot at all. The scope filters are the plain
    /// <c>refs/heads/</c> / <c>refs/remotes/</c> / <c>refs/tags/</c> prefix tests, and
    /// <see cref="GraphRefScope.Selected"/> names are full ref names or <c>HEAD</c>.</para>
    /// The returned array is sorted ordinally — git's topological sort depends on the order tips
    /// are fed to it (measured), and the fingerprint already ignores ref enumeration order, so an
    /// unsorted list would let `git gc`/`pack-refs` silently reorder the walk under an unchanged
    /// fingerprint.</summary>
    private static string[] ResolveTipShas(RefSnapshot snapshot, GraphQuery query)
    {
        var shas = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? sha) { if (sha is not null && seen.Add(sha)) shas.Add(sha); }

        switch (query.RefScope)
        {
            case GraphRefScope.CurrentBranch:
                Add(snapshot.HeadTarget?.ToString());
                break;

            case GraphRefScope.Selected:
                foreach (var name in query.SelectedRefs ?? [])
                {
                    if (string.Equals(name, "HEAD", StringComparison.Ordinal))
                        Add(snapshot.HeadTarget?.ToString());
                    else
                        Add(snapshot.Find(name)?.PeeledCommit?.ToString());
                }
                break;

            default:
                foreach (var r in snapshot.Refs)
                {
                    var name = r.CanonicalName;
                    if (name.StartsWith("refs/heads/", StringComparison.Ordinal)
                        || (query.IncludeRemoteBranches && name.StartsWith("refs/remotes/", StringComparison.Ordinal))
                        || name.StartsWith("refs/tags/", StringComparison.Ordinal))
                        Add(r.PeeledCommit?.ToString());
                }
                break;
        }

        shas.Sort(StringComparer.Ordinal);
        return [.. shas];
    }

    private readonly record struct WalkResult(
        List<RawCommit> Commits,
        Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>> Decorations,
        bool Exhausted,
        long Fingerprint,
        bool DecosFresh,
        bool FromDisk = false,
        DiskCommitStore? MetaStore = null,
        bool MetaExhausted = false,
        long WalkKey = 0,
        // Whether the merge writes Fingerprint / WalkKey / Decorations to the cache: only a Done
        // that derived them on the gate. A prime publishes its own under WalkLock; a resume
        // derives none.
        bool CarriesRefState = true);

    /// <summary>The fingerprint, sorted tip set and walk key for one query over one snapshot —
    /// pure computation, milliseconds even at chromium's 39k refs.</summary>
    private static (long Fingerprint, string[] Tips, long WalkKey) DeriveRefState(
        RefSnapshot snapshot, GraphQuery query, string repositoryPath)
    {
        CountRefScan(repositoryPath);
        var fp = ComputeFingerprint(snapshot, query);
        var tips = ResolveTipShas(snapshot, query);
        return (fp, tips, ComputeWalkKey(tips, query, snapshot.HistoryShape));
    }

    /// <summary>The current ref state for <paramref name="query"/> (the
    /// snapshot is a token check or one separate process). Null when the refs can't be read — the
    /// <see cref="HistoriaGitException"/> has already logged why, and each caller decides what an
    /// unknown ref state means for it; none lets the failure escape the service.</summary>
    private static async Task<(RefSnapshot Snapshot, long Fingerprint, string[] Tips, long WalkKey)?> TryGetRefStateAsync(
        RepositoryContext context, GraphQuery query, CancellationToken ct)
    {
        RefSnapshot snapshot;
        try { snapshot = await context.RefSnapshots.GetAsync(ct).ConfigureAwait(false); }
        catch (HistoriaGitException ex)
        {
            Diagnostics.PerfLog.Mark($"ref state unreadable: {ex.Message}");
            return null;
        }
        var (fp, tips, walkKey) = DeriveRefState(snapshot, query, context.RepositoryPath);
        return (snapshot, fp, tips, walkKey);
    }

    /// <summary>
    /// Stable (cross-process) fingerprint of everything that changes the walk output or its
    /// badges: every ref's name + target (a symbolic ref's target ref name), where HEAD points
    /// and what it resolves to, the history shape when there is one, and the query knobs (scope, remote-branch inclusion,
    /// path filter, and selected refs).
    /// FNV-1a over ordinally sorted input, so it does not depend on ref enumeration order or
    /// per-process <see cref="string.GetHashCode()"/> randomisation. In-memory only — the disk
    /// stores key on <see cref="ComputeWalkKey"/>.
    /// </summary>
    private static long ComputeFingerprint(RefSnapshot snapshot, GraphQuery query)
    {
        var h = StableHash.Fnv64OffsetBasis;
        void Feed(string s) => h = StableHash.Fnv1a64Step(h, s);

        foreach (var r in snapshot.Refs) // already ordinal by name
        {
            Feed(r.CanonicalName);
            Feed(r.SymrefTarget ?? r.Target?.ToString() ?? "");
        }

        Feed("HEAD");
        Feed(snapshot.HeadSymref ?? "");
        Feed(snapshot.HeadTarget?.ToString() ?? "");
        if (snapshot.HistoryShape.Length > 0)
            Feed("history:" + snapshot.HistoryShape);

        Feed("query");
        Feed(query.RefScope.ToString());
        Feed(query.IncludeRemoteBranches ? "R1" : "R0");
        Feed(query.PathFilter ?? "");
        if (query.RefScope == GraphRefScope.Selected && query.SelectedRefs is { } sel)
            foreach (var s in sel.OrderBy(x => x, StringComparer.Ordinal))
                Feed(s);

        return unchecked((long)h);
    }
}
