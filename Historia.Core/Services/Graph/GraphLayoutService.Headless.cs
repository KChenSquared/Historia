namespace Historia.Services.Graph;

public sealed partial class GraphLayoutService
{
    /// <inheritdoc/>
    public void EnqueueInactiveTabCaching(RepositoryContext context, GraphQuery query) =>
        _backgroundWriter?.EnqueueInactiveTab(context, ct => RunHeadlessCacheAsync(context, query, ct));

    /// <inheritdoc/>
    public Task CancelInactiveTabCachingAsync(RepositoryContext context) =>
        _backgroundWriter?.CancelInactiveTabAsync(context) ?? Task.CompletedTask;

    /// <inheritdoc/>
    public void RefreshCommitGraphAfterFetch(RepositoryContext context) =>
        _backgroundWriter?.RefreshAfterFetch(context);

    /// <inheritdoc/>
    public async Task PrecacheClonedRepositoryAsync(
        RepositoryContext context, GraphQuery query, IProgress<GitProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new GitProgress { Phase = CloneStatus.CommitGraphPhase });
        var writeTask = _backgroundWriter is not null
            ? _backgroundWriter.WriteForCloneAsync(context, ct)
            : Task.CompletedTask;

        if (!SpillEnabled(query))
        {
            await writeTask.ConfigureAwait(false);
            return;
        }

        progress?.Report(new GitProgress { Phase = CloneStatus.CachingPhase, ObjectsDone = 0 });

        var spoolTask = Task.Run(async () =>
        {
            if (await ProbeStoreAsync(context, query, ct).ConfigureAwait(false) is not { } probe)
                return 0; // refs unreadable — nothing to walk

            var rowsProgress = progress is null
                ? null
                : new SynchronousProgress<int>(rows =>
                    progress.Report(new GitProgress { Phase = CloneStatus.CachingPhase, ObjectsDone = rows }));

            return await RunHeadlessSpoolAsync(context, query, probe.Tips, probe.WalkKey, ct, ignoreGate: true, rowsSpilled: rowsProgress)
                .ConfigureAwait(false);
        }, ct);

        await Task.WhenAll(writeTask, spoolTask).ConfigureAwait(false);
    }

    /// <summary>Null when the repo's refs can't be read — there is nothing to walk or key a store
    /// on.</summary>
    private static async Task<(
        long WalkKey,
        string[] Tips,
        string HistoryShape,
        (bool Ok, int RowCount, bool Exhausted) Current,
        (bool Ok, long WalkKey, int RowCount, int RecordCount, int OrderVersion, bool Exhausted, long LastFullWalkMs) Existing)?> ProbeStoreAsync(
        RepositoryContext context, GraphQuery query, CancellationToken ct)
    {
        // The tips come from the ref snapshot, and the probes are header reads
        // of Historia's own files.
        var repoPath = context.RepositoryPath;
        if (await TryGetRefStateAsync(context, query, ct).ConfigureAwait(false) is not { } state)
            return null;
        var (snapshot, _, tips, walkKey) = state;
        return (
            WalkKey: walkKey,
            Tips: tips,
            HistoryShape: snapshot.HistoryShape,
            Current: GraphMetaStore.Probe(repoPath, walkKey, query.IncludeRemoteBranches),
            Existing: GraphMetaStore.ProbeExisting(repoPath, query.IncludeRemoteBranches));
    }

    /// <inheritdoc/>
    public async Task RunHeadlessCacheAsync(RepositoryContext context, GraphQuery query, CancellationToken ct)
    {
        await RunHeadlessCacheCoreAsync(context, query, ct).ConfigureAwait(false);
        // The job ran to its end (not cancelled by focus or close): check the commit-graph too.
        _backgroundWriter?.EnsureCurrent(context);
    }

    private async Task RunHeadlessCacheCoreAsync(RepositoryContext context, GraphQuery query, CancellationToken ct)
    {
        if (!SpillEnabled(query))
            return; // disk-backed metadata off, or an unsupported query shape — nothing to do headlessly
        if (!_locator.Current.Found)
            return; // no git.exe — nothing can walk; the startup notice already says so

        var repoPath = context.RepositoryPath;

        // Step 1: tip resolution + walk key from the ref snapshot, and a
        // header-only probe both at the current key and at whatever key the store was last
        // written under, regardless of match.
        if (await ProbeStoreAsync(context, query, ct).ConfigureAwait(false) is not { } probe)
            return; // refs unreadable — nothing to walk, nothing written

        if (probe.Current is { Ok: true, Exhausted: true })
            return; // already fully cached at the current walk key

        ct.ThrowIfCancellationRequested();

        // A store whose dead records (superseded by earlier reorders, never
        // referenced by the current order) have grown past 25% of its live row count, or whose
        // unreachable dead prefix is >= 25% of .meta.dat, is compacted by a from-row-0 re-spill
        // instead of yet another reorder — RunHeadlessSpoolAsync resets the store to identity order
        // (RecordCount == RowCount) under the new key, which RunHeadlessReorderAsync's incremental
        // append-only shape can never do on its own.
        var deadRecordsTooHigh = probe.Existing is { Ok: true } existing
            && (long)existing.RecordCount * 3 > (long)existing.RowCount * 4;
        var prefix = GraphMetaStore.ProbeDeadPrefix(repoPath);
        var deadPrefixTooHigh = prefix.DatLength > 0 && prefix.DeadBytes * 4 >= prefix.DatLength;
        if (deadPrefixTooHigh)
            Diagnostics.PerfLog.Mark($"graph cache compaction: dead prefix {prefix.DeadBytes / 1_000_000.0:0.0}MB of {prefix.DatLength / 1_000_000.0:0.0}MB ({repoPath})");

        // Step 2 — headless spool (store missing, partial at the CURRENT key, or over either
        // compaction threshold above) or reorder (an exhausted store exists at a DIFFERENT
        // key — a fetch, a new branch — and isn't due for compaction). Reordering needs no
        // concurrent foreground reader to protect, which is exactly why this headless pipeline —
        // not the active tab's gate/spill loop — is where the live reorder trigger lands.
        if (!probe.Current.Ok && probe.Existing is { Ok: true, Exhausted: true } && !deadRecordsTooHigh && !deadPrefixTooHigh)
        {
            // Try the guarded splice first when the setting is on —
            // it falls back to false on ANY guard rejection or query/write failure, in which case
            // the ordinary reorder below is always correct, just potentially slower.
            if (_settings.Current.EnableGraphSplice)
            {
                var splicedRows = await TryHeadlessSpliceAsync(
                    context, query, repoPath, probe.Tips, probe.WalkKey, probe.HistoryShape, probe.Existing, ct).ConfigureAwait(false);
                if (splicedRows >= 0)
                    return;
            }
            await RunHeadlessReorderAsync(context, query, repoPath, probe.Tips, probe.WalkKey, ct).ConfigureAwait(false);
        }
        else
            await RunHeadlessSpoolAsync(context, query, probe.Tips, probe.WalkKey, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The live trigger for <see cref="GraphMetaSplice"/> — brings an
    /// exhausted store whose walk key has moved (a fetch, a new branch) up to date by splicing in
    /// just the added/removed commits instead of a full re-walk, when every guard passes. Returns
    /// the new row count on success, or <c>-1</c> on any guard rejection, missing git/CLI
    /// availability, or query/write failure — the caller falls back to
    /// <see cref="RunHeadlessReorderAsync"/> in every <c>-1</c> case, so a failure here is never a
    /// correctness problem, only a missed optimization.
    /// </summary>
    private async Task<int> TryHeadlessSpliceAsync(
        RepositoryContext context, GraphQuery query, string repoPath, string[] newSortedTips, long newWalkKey, string historyShape,
        (bool Ok, long WalkKey, int RowCount, int RecordCount, int OrderVersion, bool Exhausted, long LastFullWalkMs) oldProbe,
        CancellationToken ct)
    {
        if (_cli is null || !_cli.IsGitAvailable)
            return -1;
        // Guard 3's threshold, checked up front — no point running two git processes on a history
        // too small for splicing to ever pay off.
        var minRowsToConsider = TestSpliceMinRowsOverride ?? GraphMetaSplice.MinRowsToConsider;
        if (oldProbe.RowCount < minRowsToConsider)
            return -1;

        // Guard 1: a store with no `.meta.tips` (never reordered or spooled to exhaustion with
        // its tips recorded) has nothing reliable to splice from.
        var oldTips = GraphMetaStore.ReadTips(repoPath);
        if (oldTips is not { Count: > 0 })
            return -1;
        var oldTipShas = oldTips.Select(t => t.ToString()).ToArray();

        // Guard 1 continued: `.meta.tips` is a separate file from the header, so it must hash to
        // the key the old order was committed under. A tips write that failed while its header
        // write succeeded would otherwise make the deltas below run from the wrong base and add
        // commits the order already holds.
        var oldTipsMatch = ComputeWalkKey(oldTipShas, query, historyShape) == oldProbe.WalkKey;
        if (!oldTipsMatch)
        {
            Diagnostics.PerfLog.Mark($"graph splice rejected: .meta.tips does not match the store's walk key ({repoPath})");
            return -1;
        }

        // Guard 2 (simplified, same scope cut as GraphMetaSplice's own doc comment): only the
        // CURRENT shallow state is checked, not "unchanged since the old order was built".
        // `shallow` lives in the common directory, which a linked worktree's git dir is not.
        var repoIsShallow = File.Exists(Path.Combine(RefStateToken.ResolveCommonDir(context.GitDirectory), "shallow"));

        try
        {
            // "Added": GitLogStream fed the new tips plus the old tips as exclusions (`^sha`) —
            // git's own stdin revision syntax for `git log --stdin`, same as an argv `^rev`.
            var addedArgs = newSortedTips.Concat(oldTipShas.Select(sha => "^" + sha)).ToArray();
            var addedCommits = new List<RawCommit>();
            var addedSw = Stopwatch.StartNew();
            using (var stream = GitLogStream.TryStart(_locator, repoPath, addedArgs, context.ActiveProfile))
            {
                if (stream is null)
                    return -1;
                // A background walk: it yields the CPU to interactive work.
                stream.SetLowPriority(true);
                while (stream.MoveNext())
                {
                    ct.ThrowIfCancellationRequested();
                    addedCommits.Add(stream.Current);
                }
                if (stream.Failed)
                    return -1;
            }
            addedSw.Stop();

            // "Removed": plain `git rev-list --stdin` fed the old tips plus the new tips as
            // exclusions — commits reachable from the old tips but not the new ones.
            var removedStdin = string.Join('\n', oldTipShas.Concat(newSortedTips.Select(sha => "^" + sha))) + "\n";
            var removedSw = Stopwatch.StartNew();
            var removedResult = await _cli.RunAsync(new GitCliRequest
            {
                Arguments = ["rev-list", "--stdin"],
                WorkingDirectory = repoPath,
                StandardInput = removedStdin,
                Timeout = TimeSpan.FromSeconds(30),
            }, ct).ConfigureAwait(false);
            removedSw.Stop();
            if (!removedResult.IsSuccess)
                return -1;
            var removedShas = removedResult.StandardOutput.Split(
                '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            using var oldStore = DiskCommitStore.Open(repoPath, oldProbe.RowCount, oldProbe.RecordCount, oldProbe.OrderVersion);
            var shaIndex = oldStore.BuildShaIndex();

            var removedRecords = new HashSet<int>();
            foreach (var sha in removedShas)
                if (shaIndex.TryGetValue(GitObjectId.Parse(sha), out var rec))
                    removedRecords.Add(rec);

            // Guard 5 only ever cares about an added commit's OLD parent sitting within the top
            // MaxOldParentRow rows — no need to index the whole (possibly multi-million-row) store.
            // Guard 6 tracks the max CommitWhen over these same top rows.
            var maxOldParentRow = TestSpliceMaxOldParentRowOverride ?? GraphMetaSplice.MaxOldParentRow;
            var capRows = Math.Min(oldProbe.RowCount, maxOldParentRow);
            var existingRecordRow = new Dictionary<GitObjectId, int>(capRows);
            var newestOldTopCommitWhen = DateTimeOffset.MinValue;
            for (var row = 0; row < capRows; row++)
            {
                var record = oldStore[row];
                existingRecordRow[record.Id] = row;
                if (record.CommitWhen > newestOldTopCommitWhen)
                    newestOldTopCommitWhen = record.CommitWhen;
            }

            var guard = GraphMetaSplice.Evaluate(
                storeExhausted: oldProbe.Exhausted,
                oldTipsMatch: oldTipsMatch,
                repoIsShallow: repoIsShallow,
                oldRowCount: oldProbe.RowCount,
                addedQueryElapsed: addedSw.Elapsed,
                removedQueryElapsed: removedSw.Elapsed,
                lastFullWalkElapsed: TestSpliceFullWalkElapsedOverride ?? TimeSpan.FromMilliseconds(oldProbe.LastFullWalkMs),
                addedCommits: addedCommits,
                existingRecordRow: existingRecordRow,
                newestOldTopCommitWhen: newestOldTopCommitWhen,
                minRowsToConsider: TestSpliceMinRowsOverride,
                maxOldParentRow: TestSpliceMaxOldParentRowOverride);

            if (!guard.Allowed)
            {
                Diagnostics.PerfLog.Mark($"graph splice rejected: {guard.Reason} ({repoPath})");
                return -1;
            }

            var plan = GraphMetaSplice.Build(oldStore.ToOrderArray(), removedRecords, addedCommits, oldProbe.RecordCount);

            var writer = new GraphMetaWriter(repoPath, newWalkKey, query.IncludeRemoteBranches);
            if (plan.NewRecords.Count > 0 && !writer.AppendReorderBatch(plan.NewRecords, oldProbe.WalkKey))
                return -1;

            if (!writer.TryCommitReorder(
                    oldProbe.WalkKey, oldProbe.Exhausted, oldProbe.OrderVersion, oldProbe.RecordCount,
                    plan.NewRecords.Count, plan.Order, [.. newSortedTips.Select(sha => GitObjectId.Parse(sha))]))
                return -1;

            Diagnostics.PerfLog.Mark(
                $"graph splice committed: +{plan.NewRecords.Count} new, -{removedRecords.Count}, {plan.Order.Length} rows ({repoPath})");
            GraphMetaStore.DeleteSupersededOrderFiles(repoPath, currentVersion: oldProbe.OrderVersion + 1);
            TestSpliceCommitted?.Invoke(plan.NewRecords.Count, removedRecords.Count);
            return plan.Order.Length;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A splice is an optimization over the ordinary reorder, never a correctness
            // dependency — any unexpected failure just falls back.
            return -1;
        }
    }

    /// <summary>Streams a walk into the on-disk store for an inactive tab, one batch at
    /// a time, holding no more than one batch resident between spills — the same interleaved
    /// walk-then-spill shape as the active tab's <see cref="MaybeStartBackgroundSpill"/>, but with
    /// no <see cref="RepoCache"/> ever added to <see cref="_caches"/> and no layout produced.
    /// Cancelling <paramref name="ct"/> stops at the next batch boundary; whatever was already
    /// spilled stays on disk.</summary>
    private async Task<int> RunHeadlessSpoolAsync(
        RepositoryContext context, GraphQuery query, string[] sortedTips, long walkKey, CancellationToken ct,
        bool ignoreGate = false, IProgress<int>? rowsSpilled = null)
    {
        if (!_locator.Current.Found)
            return 0; // no git.exe — nothing can walk; the startup notice already says so

        var batchRows = TestSpillBatchRowsOverride > 0 ? TestSpillBatchRowsOverride : DefaultSpillBatchRows;
        // Committed to the probed walk from the start: the store's key and the tips recorded with
        // it are the probe's, so tips that moved before the first step supersede this spool
        // instead of walking other rows under them.
        var cache = new RepoCache { Query = query, WalkKey = walkKey };
        // Times this spool from the moment it starts pulling rows so a walk that
        // reaches exhaustion having started at row 0 (a genuine full walk, not a resumed partial
        // one — the ONLY case that stamp is meaningful for) can record GraphMetaSplice's guard-4
        // baseline. Only the headless spool times itself this way (not the active tab's own
        // foreground background spill or the extend-spill fallback) — a deliberate scope narrowing:
        // the splice trigger itself only ever runs from this headless pipeline,
        // so only this path's timing is ever read back.
        var walkStopwatch = Stopwatch.StartNew();
        bool? fullWalkFromZero = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!ignoreGate)
                {
                    await WaitForGateAsync(ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested)
                        break;
                }

                int target;
                lock (cache) target = cache.SpilledRows + cache.Commits.Count + batchRows;
                await EnsureWalkedAsync(context, query, cache, target, ct, background: true, lowestPriority: true)
                    .ConfigureAwait(false);
                if (cache.WalkHalted)
                    break;

                bool walkDone;
                lock (cache) walkDone = cache.Exhausted;

                if (!ignoreGate)
                {
                    await WaitForGateAsync(ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested)
                        break;
                }

                int from, to;
                var conflicted = false;
                lock (cache.SpillSync)
                {
                    lock (cache) { from = cache.SpilledRows; to = cache.SpilledRows + cache.Commits.Count; }
                    fullWalkFromZero ??= from == 0;
                    var fullWalkElapsed = walkDone && fullWalkFromZero == true ? walkStopwatch.Elapsed : (TimeSpan?)null;
                    var tipsForCommit = walkDone ? sortedTips.Select(sha => GitObjectId.Parse(sha)).ToArray() : null;
                    if (to > from)
                    {
                        var outcome = SpillRange(cache, context.RepositoryPath, cache.IncludeRemotes, from, to, exhausted: walkDone, fullWalkElapsed, tipsForCommit);
                        if (outcome == SpillOutcome.Conflict)
                            conflicted = true;
                        else
                            rowsSpilled?.Report(to);
                    }
                }
                // A conflict here means the active tab (or another headless job)
                // already moved this repo's store out from under this spool — stop; the next
                // RunHeadlessCacheAsync call re-probes and resyncs.
                if (conflicted)
                    break;
                if (walkDone || cache.Exhausted || cache.MetaComplete)
                    break;
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
            if (!cache.Exhausted)
                ct.ThrowIfCancellationRequested();
            return TotalWalked(cache);
        }
        finally
        {
            cache.DisposeLiveWalk();
            cache.MetaStore?.Dispose();
            try { cache.Cts.Dispose(); } catch { /* best effort */ }
        }
    }

    /// <summary>Streams the new history in batches (never
    /// materializes the whole new walk resident at once), mapping each batch to a record — reusing the old store's by SHA where possible,
    /// appending only genuinely new ones — as it goes, and trims the walked batch out of
    /// <c>cache.Commits</c> immediately after mapping it. The row → record order isn't knowable
    /// until the walk is exhausted (a partial reorder has no valid order to write), so only the
    /// FINAL atomic <see cref="GraphMetaWriter.TryCommitReorder"/> switches the header over; every
    /// batch's new records were already durably appended via <see cref="GraphMetaWriter.AppendReorderBatch"/>,
    /// off that lock, so a mismatch there just means this whole attempt is abandoned — nothing
    /// references those records, and the store's next append cuts them off. Cancelling
    /// <paramref name="ct"/> at any point before that final commit discards the whole attempt;
    /// the old order stays authoritative until it.</summary>
    private async Task RunHeadlessReorderAsync(
        RepositoryContext context, GraphQuery query, string repoPath, string[] sortedTips, long newWalkKey,
        CancellationToken ct)
    {
        if (!_locator.Current.Found)
            return; // no git.exe — nothing can walk; the startup notice already says so

        // Open the old store and build its SHA index up front — everything the walk
        // below needs to map each batch as it streams in, rather than only after the whole new
        // history has been walked and copied resident.
        var oldProbe = GraphMetaStore.ProbeExisting(repoPath, query.IncludeRemoteBranches);
        if (!oldProbe.Ok || !oldProbe.Exhausted || oldProbe.WalkKey == newWalkKey)
            return; // stale, gone, or someone else already brought it up to date

        Diagnostics.PerfLog.Memory($"reorder start ({repoPath})");

        GraphMetaReorderBuilder builder;
        try
        {
            using var oldStore = DiskCommitStore.Open(repoPath, oldProbe.RowCount, oldProbe.RecordCount, oldProbe.OrderVersion);
            builder = new GraphMetaReorderBuilder(oldStore.BuildShaIndex(), oldStore.RecordCount);
        }
        catch (InvalidDataException)
        {
            // A torn old store has no records worth reusing: drop it and spool afresh.
            if (GraphMetaStore.DiscardIfTorn(repoPath, query.IncludeRemoteBranches))
                await RunHeadlessSpoolAsync(context, query, sortedTips, newWalkKey, ct).ConfigureAwait(false);
            return;
        }

        var writer = new GraphMetaWriter(repoPath, newWalkKey, query.IncludeRemoteBranches);
        // Committed to the probed walk: the order is written under newWalkKey with sortedTips, so
        // tips that moved before the first step supersede this reorder rather than feed it
        // another walk's rows.
        var cache = new RepoCache { Query = query, WalkKey = newWalkKey };
        try
        {
            var batchRows = TestSpillBatchRowsOverride > 0 ? TestSpillBatchRowsOverride : DefaultSpillBatchRows;
            while (!cache.Exhausted && !ct.IsCancellationRequested)
            {
                await WaitForGateAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                    return;
                // cache.Commits is cleared to empty at the end of every iteration below, so
                // TotalWalked(cache) (== cache.Commits.Count here — this cache never spills) is
                // always 0 at this point: EnsureWalkedAsync's own "have" bookkeeping treats
                // batchRows as "batchRows MORE from wherever the live walk enumerator currently
                // is" (tracked separately, by RepoCache.LiveWalkPos), not an absolute total.
                await EnsureWalkedAsync(context, query, cache, batchRows, ct, background: true, lowestPriority: true)
                    .ConfigureAwait(false);
                if (cache.WalkHalted)
                    return; // the new order can't be completed — the old one stays authoritative

                List<RawCommit> batch;
                lock (cache)
                {
                    batch = [.. cache.Commits];
                    cache.Commits.Clear(); // never accumulate past one batch
                }
                TestReorderBatchObserved?.Invoke(batch.Count);

                if (batch.Count > 0)
                {
                    var newRecords = builder.MapBatch(batch);
                    if (newRecords.Count > 0 && !writer.AppendReorderBatch(newRecords, oldProbe.WalkKey))
                        return; // the old store moved under this reorder — abandon
                }

                if (!cache.Exhausted)
                    await Task.Delay(150, ct).ConfigureAwait(false);
            }
            if (ct.IsCancellationRequested || !cache.Exhausted)
                return; // cancelled mid-walk — nothing durable is referenced by any order file yet

            // The atomic switch-over, re-checking under the writer lock that the
            // old store is still exactly what this reorder started from — another writer (a live
            // foreground open of this same repo) could have moved it during any of the streamed
            // batches above, all of which ran off this lock.
            if (!writer.TryCommitReorder(
                    oldProbe.WalkKey, oldProbe.Exhausted, oldProbe.OrderVersion, oldProbe.RecordCount,
                    builder.NewRecordCount, builder.Order, [.. sortedTips.Select(sha => GitObjectId.Parse(sha))]))
                return; // best-effort — the old order stays authoritative on any mismatch or write failure

            Diagnostics.PerfLog.Memory($"reorder committed ({repoPath})");
            Diagnostics.PerfLog.Mark(
                $"graph cache write: reorder {builder.NewRecordCount} new records, {builder.Order.Length} rows ({repoPath})");

            // The just-superseded order version's file (and anything older still
            // lingering) is no longer reachable — a reader loads the whole order file once, at
            // Open, so nothing can be holding a reference to it now that the header has switched.
            GraphMetaStore.DeleteSupersededOrderFiles(repoPath, currentVersion: oldProbe.OrderVersion + 1);
        }
        finally
        {
            cache.DisposeLiveWalk();
            cache.MetaStore?.Dispose();
            try { cache.Cts.Dispose(); } catch { /* best effort */ }
        }
    }
}
