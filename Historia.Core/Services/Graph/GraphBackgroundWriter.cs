namespace Historia.Services.Graph;

/// <summary>
/// The one app-wide queue for heavy background graph jobs. Two kinds of work go through it —
/// see the second <c>&lt;para&gt;</c> below for how each is serialized so a multi-tab session
/// never fans out several concurrent <c>git commit-graph write</c> invocations, nor several
/// concurrent headless walks, at once:
/// <list type="bullet">
/// <item><b>Keeping a repository's commit-graph current</b> (<see cref="EnsureCurrent"/>) — every
/// repository, whatever its size, always. A missing graph (no <c>commit-graph</c> file and no
/// <c>commit-graphs/</c> chain in the <i>common</i> directory's <c>objects/info</c>, so a linked
/// worktree sees the shared one) gets a full <c>--reachable</c> write. A stale graph (a ref tip —
/// a branch, remote-tracking branch, tag peeled to a commit, or <c>HEAD</c> — that
/// <see cref="CommitGraphIndex"/> doesn't find) or an unusable one (no generation data, damaged)
/// gets an incremental <c>--split</c> refresh, whoever wrote it: git documents the split write as
/// adding only the commits the graph lacks in a new layer, merging layers by its own size rule,
/// and expiring replaced layers no sooner than <c>--expire-time</c>, so it never rewrites another
/// writer's graph from scratch. At most one check runs per repository (keyed by common directory)
/// per ref state (<see cref="RefStateToken"/>): an empty repository (git writes no file), a graph
/// that stays unusable, a write that failed, or back-to-back triggers do nothing more until a ref
/// moves.</item>
/// <item><b>Inactive tabs, one at a time</b> (<see cref="EnqueueInactiveTab"/>) — a headless
/// per-tab pipeline that <c>GraphLayoutService</c> drives; this class only owns the
/// queue/serialization/gate discipline, not what the work actually does.</item>
/// </list>
/// Every job awaits <see cref="IBackgroundWorkGate"/> before it starts and runs at
/// <see cref="ProcessPriorityClass.BelowNormal"/> for the git process itself
/// (<see cref="GitCliRequest.LowerPriority"/>). Every commit-graph write sets
/// <c>GIT_NO_LAZY_FETCH=1</c>: in a partial clone, <c>commit-graph write --reachable</c> otherwise
/// fetches a promisor-only object a ref points at — a network call the user never asked for.
/// Never calls <c>git maintenance start</c>/<c>register</c> or changes git config.
/// <para>
/// A commit-graph write must not be killed once it has started: git only removes
/// <c>objects/info/commit-graph.lock</c> / <c>commit-graphs/commit-graph-chain.lock</c> from its own
/// exit/signal handlers, never when the process tree is torn down with <c>TerminateProcess</c>
/// (<see cref="GitCliProcessSetup.TryKill"/> calls <c>Process.Kill(entireProcessTree: true)</c>,
/// which on Windows is exactly that). <see cref="GitCliRequest.LowerPriority"/> does not protect a
/// process: the runner kills a lower-priority request on cancellation or timeout like any other,
/// unless the request is <see cref="GitCliCancelPolicy.NeverAfterStart"/> or runs inside a
/// <see cref="GitWriteScope"/>. So <see cref="RunWriteAsync"/> runs the git process with
/// <see cref="CancellationToken.None"/> and an infinite timeout, leaving nothing that could cancel
/// it; a context's <see cref="RepositoryContext.Lifetime"/> only gates whether a write is allowed
/// to <i>start</i> (the gate wait, and the wait for <see cref="_serial"/> below) — a closed tab
/// still skips a write that hasn't begun, but never kills one already running.
/// </para>
/// <para>
/// The inactive-tab queue (<see cref="EnqueueInactiveTab"/>) de-dupes by on-disk store: a
/// store already queued and not yet running is not queued a second time, and linked worktrees
/// that share a store share its job. It also runs behind
/// its own serializer, <see cref="_headlessSerial"/>, separate from <see cref="_serial"/> — a long
/// headless walk (pure C#, no git process) never makes a below-normal commit-graph write wait
/// behind it. <see cref="_serial"/> is reserved for the commit-graph check and its
/// <c>git commit-graph write</c> process, wherever it's invoked from, so a below-normal
/// commit-graph write may briefly overlap a lowest-priority headless walk's non-git work —
/// an accepted trade-off, since only one process-level write can ever run at a time either way.
/// </para>
/// <para>
/// A tab gaining focus stops its own queued or running inactive-tab job outright
/// (<see cref="CancelInactiveTabAsync"/>, called by <c>WorkspaceService.FocusInternal</c>) rather
/// than letting it keep running to completion — the headless pipeline and that tab's own
/// foreground spill both write to the same on-disk store, and only cancelling the headless side
/// (never mid-write; see the docs on the write path above) keeps the two from ever appending at
/// once. <see cref="GraphMetaWriter.AppendSpillBatch"/> and
/// <see cref="GraphMetaWriter.TryCommitReorder"/> are the second line of defence, re-checking the
/// on-disk header under a per-repo lock before every write.
/// </para>
/// </summary>
internal sealed class GraphBackgroundWriter(IGitCliService cli, IBackgroundWorkGate? gate = null)
{
    private static readonly IReadOnlyDictionary<string, string> NoLazyFetch =
        new Dictionary<string, string> { ["GIT_NO_LAZY_FETCH"] = "1" };

    // How long a layer that a split write replaces is kept before git may delete it, so a reader
    // that opened the chain just before the write can still finish.
    private static readonly TimeSpan SplitExpiry = TimeSpan.FromHours(1);

    // Serializes the commit-graph check and its `git commit-graph write` process — active tab,
    // headless or clone — against every other one. Waiting on it is cancellable (a not-yet-started
    // write can still be skipped when a tab closes); the process run itself, once it starts, is
    // not (see the class doc comment).
    private readonly SemaphoreSlim _serial = new(1, 1);

    // Serializes the inactive-tab headless pipeline's own steps (one at a time, in enqueue order)
    // separately from _serial, so a long headless walk never blocks a commit-graph write behind it.
    private readonly SemaphoreSlim _headlessSerial = new(1, 1);

    // Per common directory: the ref-state token the last commit-graph check acted on. Read and
    // written only while _serial is held.
    private readonly Dictionary<string, long> _checkedToken = new(StringComparer.OrdinalIgnoreCase);

    private int _pendingChecks;

    private readonly object _sync = new();

    private readonly Queue<(RepositoryContext Context, Func<CancellationToken, Task> Work, CancellationTokenSource Cts, TaskCompletionSource Done)> _inactiveQueue = new();

    // Inactive jobs are tracked by the on-disk store they write, not by tab: linked worktrees of
    // one repository share a store (GraphCachePaths.StoreRoot), so one job serves them all, and
    // any of those tabs is a second writer to a sibling's job.
    private static string StoreKey(RepositoryContext context) => GraphCachePaths.Stem(context.RepositoryPath);

    private static bool SameStore(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // Stores currently queued (enqueued but not yet dequeued for running), each with the tab and
    // cancellation source of its queued job — backs the de-duplication in EnqueueInactiveTab. A
    // store is removed from this map the moment its job is dequeued, so a fresh
    // EnqueueInactiveTab call for the same store while its previous job is actually running
    // queues a new run rather than being dropped.
    private readonly Dictionary<string, (RepositoryContext Context, CancellationTokenSource Cts)> _queuedStores =
        new(StringComparer.OrdinalIgnoreCase);

    // Sibling worktrees whose call was de-duplicated into another tab's queued job, keyed by that
    // job's cancellation source. The job is cancelled with its own tab's Lifetime, so when it ends
    // that way the first sibling still open is queued in its place. Dropped when the job ends any
    // other way, and by CancelInactiveTabAsync (the focused tab then writes the store).
    private readonly Dictionary<CancellationTokenSource, List<(RepositoryContext Context, Func<CancellationToken, Task> Work)>> _standbys = new();

    // Every queued or currently-running inactive job's own
    // cancellation source and completion signal, keyed by store. A store can briefly have
    // two entries at once — one finishing its run while a fresh call (the "queued" dedupe doesn't
    // apply to a running job) queues another — so each store maps to a list; a job removes
    // its own entry when it exits (cancelled, failed, or finished), never anyone else's.
    private readonly Dictionary<string, List<(CancellationTokenSource Cts, TaskCompletionSource Done)>> _inactiveJobs =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _pumping;

    /// <summary>
    /// Queues a headless job for an inactive tab. Jobs run one at a time, in the order
    /// enqueued, each behind the gate and <see cref="_headlessSerial"/>. A job's own cancellation
    /// (typically <paramref name="context"/>'s <c>Lifetime</c>, or a linked token the caller ties
    /// to "focus arrived") stops it at its own next checkpoint; one job failing or being
    /// cancelled never stalls the rest of the queue.
    /// <para>
    /// De-duplicated by store: a store that is already queued and not yet running is not
    /// queued again — a repeated focus-change churn that re-enqueues every inactive tab on every
    /// switch does not pile up redundant jobs, and sibling worktrees share one. A store whose job
    /// is currently <i>running</i> is not "queued", so a fresh call for it queues a new run. A tab
    /// whose store the focused tab writes is not queued at all: the focused tab's own spill is
    /// that store's writer. A sibling folded into another tab's job stands by: if that tab closes
    /// before its job finishes, the sibling is queued in its place.
    /// </para>
    /// </summary>
    public void EnqueueInactiveTab(RepositoryContext context, Func<CancellationToken, Task> headlessWork)
    {
        var store = StoreKey(context);
        if (gate?.Focused is { } focused && SameStore(StoreKey(focused), store))
            return;
        lock (_sync)
        {
            if (_queuedStores.TryGetValue(store, out var queued))
            {
                // Already queued and not yet running — de-dupe, keeping a sibling on standby.
                if (!ReferenceEquals(queued.Context, context))
                {
                    if (!_standbys.TryGetValue(queued.Cts, out var standbys))
                        _standbys[queued.Cts] = standbys = [];
                    if (!standbys.Exists(s => ReferenceEquals(s.Context, context)))
                        standbys.Add((context, headlessWork));
                }
                return;
            }

            // This job's own cancellation is linked to the context's Lifetime (a tab
            // close still stops it) AND to CancelInactiveTabAsync below (focus arriving stops it
            // too) — neither owns the other; either can fire independently.
            var cts = CancellationTokenSource.CreateLinkedTokenSource(context.Lifetime);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_inactiveJobs.TryGetValue(store, out var jobs))
                _inactiveJobs[store] = jobs = [];
            jobs.Add((cts, done));

            _queuedStores[store] = (context, cts);
            _inactiveQueue.Enqueue((context, headlessWork, cts, done));
            if (!_pumping)
            {
                _pumping = true;
                _ = Task.Run(DrainInactiveQueueAsync);
            }
        }
    }

    /// <summary>True while an inactive-tab job for <paramref name="context"/>'s store is queued or
    /// running — its own, or a sibling worktree's.</summary>
    internal bool HasInactiveJob(RepositoryContext context)
    {
        var store = StoreKey(context);
        lock (_sync)
            return _inactiveJobs.TryGetValue(store, out var jobs) && jobs.Count > 0;
    }

    /// <summary>Cancels the queued or running inactive job(s) for <paramref name="context"/>'s
    /// store, a sibling worktree's included, and returns a task that completes once every one of
    /// them has actually exited. A job that has not started yet is dropped from the queue at once.
    /// A no-op (a completed task) if there is none.</summary>
    public Task CancelInactiveTabAsync(RepositoryContext context)
    {
        // Trap: a cancelled job left in the queue stays there until the drain loop reaches it,
        // which is only after the job running ahead of it (another tab's, possibly minutes long)
        // finishes. Until then the tab counts as having a headless job, so its own foreground
        // spill stands aside and its background spill waits, and its rows pile up in memory.
        // Dropping it here is safe: it never started, so it holds nothing.
        List<(RepositoryContext Context, Func<CancellationToken, Task> Work, CancellationTokenSource Cts, TaskCompletionSource Done)> dropped = [];
        (CancellationTokenSource Cts, TaskCompletionSource Done)[] running;
        var store = StoreKey(context);
        lock (_sync)
        {
            if (!_inactiveJobs.TryGetValue(store, out var jobs) || jobs.Count == 0)
                return Task.CompletedTask;
            foreach (var job in jobs)
                _standbys.Remove(job.Cts);

            if (_queuedStores.Remove(store))
            {
                var queued = _inactiveQueue.ToArray();
                _inactiveQueue.Clear();
                foreach (var item in queued)
                {
                    if (SameStore(StoreKey(item.Context), store))
                        dropped.Add(item);
                    else
                        _inactiveQueue.Enqueue(item);
                }
                foreach (var item in dropped)
                    jobs.RemoveAll(j => ReferenceEquals(j.Cts, item.Cts));
            }

            // Snapshot before cancelling: a running job's finally block removes its own entry
            // from the live list under _sync as soon as it exits.
            running = [.. jobs];
            if (jobs.Count == 0)
                _inactiveJobs.Remove(store);
        }

        foreach (var item in dropped)
        {
            item.Done.TrySetResult();
            try { item.Cts.Dispose(); } catch { /* best effort */ }
        }
        foreach (var (cts, _) in running)
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        return running.Length == 0 ? Task.CompletedTask : Task.WhenAll(running.Select(j => j.Done.Task));
    }

    private async Task DrainInactiveQueueAsync()
    {
        while (true)
        {
            (RepositoryContext Context, Func<CancellationToken, Task> Work, CancellationTokenSource Cts, TaskCompletionSource Done)? item;
            lock (_sync)
            {
                if (_inactiveQueue.Count == 0) { _pumping = false; return; }
                item = _inactiveQueue.Dequeue();
                _queuedStores.Remove(StoreKey(item.Value.Context));
            }
            var (context, work, cts, done) = item.Value;
            var store = StoreKey(context);
            try
            {
                if (gate is not null)
                    await gate.WaitUntilOpenAsync(cts.Token).ConfigureAwait(false);
                // Its own serializer, separate from _serial — see the class doc comment. A long
                // headless walk here never makes a commit-graph write (which only ever needs
                // _serial) wait behind it.
                await _headlessSerial.WaitAsync(cts.Token).ConfigureAwait(false);
                try
                {
                    // Focus only requests this job's cancellation, and a job can be queued after
                    // that request ran, so the tab (or a sibling worktree sharing its store) may
                    // have gained focus while the job waited. The focused tab's own foreground
                    // spill is the one writer for its store.
                    if (gate?.Focused is { } focused && SameStore(StoreKey(focused), store))
                        Diagnostics.PerfLog.Mark($"headless cache skipped: tab is focused ({context.DisplayName})");
                    else
                        await work(cts.Token).ConfigureAwait(false);
                }
                finally { _headlessSerial.Release(); }
            }
            catch (OperationCanceledException)
            {
                // This job's own context closed, or focus arrived and cancelled it — move on.
            }
            catch
            {
                // A headless job failing must not stall the rest of the queue.
            }
            finally
            {
                List<(RepositoryContext Context, Func<CancellationToken, Task> Work)>? standbys = null;
                lock (_sync)
                {
                    if (_inactiveJobs.TryGetValue(store, out var jobs))
                    {
                        jobs.RemoveAll(j => ReferenceEquals(j.Cts, cts));
                        if (jobs.Count == 0)
                            _inactiveJobs.Remove(store);
                    }
                    if (_standbys.Remove(cts, out var waiting) && context.Lifetime.IsCancellationRequested)
                        standbys = waiting;
                }
                done.TrySetResult();
                try { cts.Dispose(); } catch { /* best effort */ }

                // This job's tab closed before it finished: hand the store to a sibling still open.
                foreach (var (sibling, siblingWork) in standbys ?? [])
                {
                    if (!sibling.Lifetime.IsCancellationRequested)
                        EnqueueInactiveTab(sibling, siblingWork);
                }
            }
        }
    }

    /// <summary>
    /// Fire-and-forget: brings <paramref name="context"/>'s commit-graph up to date once the gate
    /// opens — writes a missing graph, refreshes a stale or unusable one with <c>--split</c>, or
    /// does nothing when it already holds every ref tip or this ref state was already checked.
    /// Safe to call as often as triggers fire.
    /// </summary>
    public void EnsureCurrent(RepositoryContext context)
    {
        if (!cli.IsGitAvailable)
            return;
        Interlocked.Increment(ref _pendingChecks);
        _ = Task.Run(async () =>
        {
            try { await EnsureCurrentAsync(context).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref _pendingChecks); }
        });
    }

    /// <summary>Test seam: checks queued by <see cref="EnsureCurrent"/> that haven't finished.</summary>
    internal int PendingChecksForTesting => Volatile.Read(ref _pendingChecks);

    /// <summary>The work behind <see cref="EnsureCurrent"/>; completes when the check (and any
    /// write it started) is done, or when the tab closed before its turn. Never throws.</summary>
    internal async Task EnsureCurrentAsync(RepositoryContext context)
    {
        try
        {
            if (gate is not null)
                await gate.WaitUntilOpenAsync(context.Lifetime).ConfigureAwait(false);
            await _serial.WaitAsync(context.Lifetime).ConfigureAwait(false);
            try
            {
                await CheckAndWriteAsync(context).ConfigureAwait(false);
            }
            finally
            {
                _serial.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Tab closed while waiting for the gate, _serial or its refs — no write started, so
            // there's nothing to clean up (a write already running is never cancelled).
        }
        catch
        {
            // Commit-graph maintenance is an optimisation, never a correctness dependency.
        }
    }

    /// <summary>One check, under <see cref="_serial"/>. The ref-state token is recorded whatever
    /// the outcome — a write that ran, failed, or wasn't needed — so one ref state is never acted
    /// on twice.</summary>
    private async Task CheckAndWriteAsync(RepositoryContext context)
    {
        var gitDir = context.GitDirectory;
        var commonDir = RefStateToken.ResolveCommonDir(gitDir);
        var token = RefStateToken.Compute(gitDir).Token;
        if (_checkedToken.TryGetValue(commonDir, out var last) && last == token)
            return;

        if (!HasCommitGraph(gitDir))
        {
            _checkedToken[commonDir] = token;
            await RunWriteAsync(context, split: false, "written (missing)").ConfigureAwait(false);
            return;
        }

        RefSnapshot snapshot;
        try
        {
            snapshot = await context.RefSnapshots.GetAsync(context.Lifetime).ConfigureAwait(false);
        }
        catch (HistoriaGitException ex)
        {
            _checkedToken[commonDir] = token;
            Diagnostics.PerfLog.Mark($"commit-graph: not checked, refs unreadable ({ex.Kind}) ({context.DisplayName})");
            return;
        }
        _checkedToken[commonDir] = token;

        var tips = CollectTips(snapshot);
        var lookup = context.CommitGraph.Lookup(tips);
        if (lookup is null)
        {
            await RunWriteAsync(context, split: true, "refreshed (unusable graph)").ConfigureAwait(false);
            return;
        }

        var missing = tips.Count(t => !lookup.Contains(t));
        if (missing > 0)
            await RunWriteAsync(context, split: true, $"refreshed ({missing} of {tips.Count} tips not in the graph)").ConfigureAwait(false);
        else
            Diagnostics.PerfLog.Mark($"commit-graph: current ({tips.Count} tips) ({context.DisplayName})");
    }

    /// <summary>The commits the graph must hold to be current: every branch, remote-tracking
    /// branch and tag peeled to a commit (a tag of a tree or blob has none), plus <c>HEAD</c>.</summary>
    private static List<GitObjectId> CollectTips(RefSnapshot snapshot)
    {
        var tips = new HashSet<GitObjectId>();
        foreach (var r in snapshot.Refs)
        {
            if (r.PeeledCommit is { } id
                && (r.CanonicalName.StartsWith("refs/heads/", StringComparison.Ordinal)
                    || r.CanonicalName.StartsWith("refs/remotes/", StringComparison.Ordinal)
                    || r.CanonicalName.StartsWith("refs/tags/", StringComparison.Ordinal)))
                tips.Add(id);
        }
        if (snapshot.HeadTarget is { } head)
            tips.Add(head);
        return [.. tips];
    }

    /// <summary>Runs one <c>git commit-graph write --reachable</c> — with <c>--split</c> and an
    /// expiry when <paramref name="split"/> — and marks the outcome. The caller holds
    /// <see cref="_serial"/>. The process runs with <see cref="CancellationToken.None"/>: git
    /// only clears its lock files from its own exit/signal handlers, never when the process tree
    /// is torn down with <c>TerminateProcess</c> (see the class doc comment), so a write already
    /// in flight is left to finish rather than killed when the tab closes. A failure is logged
    /// and not retried.</summary>
    private async Task RunWriteAsync(RepositoryContext context, bool split, string outcome)
    {
        string[] args = split
            ? ["commit-graph", "write", "--reachable", "--split", $"--expire-time={DateTimeOffset.UtcNow - SplitExpiry:O}"]
            : ["commit-graph", "write", "--reachable"];

        var sw = Stopwatch.StartNew();
        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = args,
            WorkingDirectory = context.RepositoryPath,
            Timeout = Timeout.InfiniteTimeSpan,
            LowerPriority = true,
            ExtraEnvironment = NoLazyFetch,
        }, CancellationToken.None).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            Diagnostics.PerfLog.Mark($"commit-graph: {outcome} in {sw.ElapsedMilliseconds} ms ({context.DisplayName})");
            return;
        }
        Diagnostics.PerfLog.Mark($"commit-graph: write failed (exit {result.ExitCode}) ({context.DisplayName})");
        AppLog.Write(AppLogLevel.Info, "git",
            $"Commit-graph write failed for {context.RepositoryPath} (exit {result.ExitCode}); not retried until a ref changes");
    }

    /// <summary>
    /// Writes a commit-graph for a newly cloned repository ahead of the background gate. If
    /// cancelled before acquiring <see cref="_serial"/>, the write is skipped; once the git
    /// process starts it runs to the end. Records the clone's ref state, so the first
    /// <see cref="EnsureCurrent"/> after it does not write again.
    /// </summary>
    public async Task WriteForCloneAsync(RepositoryContext context, CancellationToken ct)
    {
        if (!cli.IsGitAvailable)
            return;

        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var gitDir = context.GitDirectory;
            _checkedToken[RefStateToken.ResolveCommonDir(gitDir)] = RefStateToken.Compute(gitDir).Token;
            await RunWriteAsync(context, split: false, "written (clone)").ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>Fire-and-forget commit-graph check after a successful fetch, pull or push — the
    /// same check as <see cref="EnsureCurrent"/>.</summary>
    public void RefreshAfterFetch(RepositoryContext context) => EnsureCurrent(context);

    /// <summary>True when the repository's common directory holds a commit-graph — the single
    /// <c>objects/info/commit-graph</c> file or a <c>commit-graphs/commit-graph-chain</c> — so a
    /// linked worktree sees the graph it shares with its main worktree.</summary>
    internal static bool HasCommitGraph(string gitDirectory)
    {
        try
        {
            var info = Path.Combine(RefStateToken.ResolveCommonDir(gitDirectory), "objects", "info");
            return File.Exists(Path.Combine(info, "commit-graph"))
                || File.Exists(Path.Combine(info, "commit-graphs", "commit-graph-chain"));
        }
        catch
        {
            return false;
        }
    }
}
