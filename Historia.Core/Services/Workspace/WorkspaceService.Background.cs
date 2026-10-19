namespace Historia.Services.Workspace;

public sealed partial class WorkspaceService
{
    /// <summary>Force a still-deferred session-restore tab through the ordinary
    /// initialize path, instead of waiting for its first focus — auto-fetch needs every open
    /// repo fully loaded before its loop starts. enqueued onto the serial background
    /// pump rather than fired immediately, so a restore does not fan every tab's identity walk +
    /// status refresh out at once. No-op for a tab that is already initialising.</summary>
    public void EnsureInitialized(RepositoryContext context)
    {
        bool startPump = false;
        lock (_sync)
        {
            if (!_deferredInit.Remove(context))
                return;
            var existing = _bgQueue.FirstOrDefault(x => ReferenceEquals(x.Context, context));
            if (existing is null)
            {
                _bgQueue.Add(new BackgroundQueueItem(context, isInit: true));
            }
            if (_bgInitPumpIdle)
            {
                _bgInitPumpIdle = false;
                startPump = true;
            }
        }
        if (startPump)
            _ = Task.Run(DrainBackgroundInitQueueAsync);
    }

    /// <inheritdoc/>
    public Task RequestBackgroundRefreshAsync(RepositoryContext context, bool full = false, CancellationToken ct = default)
    {
        Task? taskToAwait = null;
        bool startPump = false;
        bool isFocused;
        var focus = CancellationToken.None;
        lock (_sync)
        {
            // The focused tab must keep refreshing immediately; this queue is for background tabs only.
            isFocused = ReferenceEquals(_focused, context);
            if (isFocused)
                focus = _focusCts.Token;
            if (!isFocused && !_open.Contains(context))
            {
                taskToAwait = Task.CompletedTask;
            }
            else if (!isFocused)
            {
                var existing = _bgQueue.FirstOrDefault(x => ReferenceEquals(x.Context, context));
                if (existing is not null)
                {
                    if (full) existing.Full = true;
                    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    existing.Awaiters.Add(tcs);
                    taskToAwait = tcs.Task;
                }
                else
                {
                    _deferredInit.Remove(context);
                    var item = new BackgroundQueueItem(context, isInit: !context.FirstFullLoad.IsCompleted, full: full);
                    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    item.Awaiters.Add(tcs);
                    _bgQueue.Add(item);
                    taskToAwait = tcs.Task;
                }

                if (_bgInitPumpIdle)
                {
                    _bgInitPumpIdle = false;
                    startPump = true;
                }
            }
        }

        if (startPump)
            _ = Task.Run(DrainBackgroundInitQueueAsync);

        // Start the focused tab's refresh outside the lock — RefreshAllAsync/RefreshStatusAsync
        // run synchronously up to their first await, and _sync must be held only across queue
        // mutation.
        if (isFocused)
            return RefreshFocusedAsync(context, full, focus, ct);

        return TaskGate.WaitWithCancellationAsync(taskToAwait!, ct);
    }

    /// <summary>A focused tab's refresh runs at once, but only while the tab keeps focus: a cold
    /// status on a huge working tree can take tens of seconds and must not compete with the tab
    /// the user moved on to. When focus leaves mid-refresh, the refresh is abandoned and finished
    /// from the background queue instead — the caller still waits for it.
    /// <para>On a large working tree, status also waits until the graph's crawl has warmed up
    /// (<see cref="RepositoryContext.GraphWarmed"/>, at most <see cref="StatusAfterGraphCeiling"/>):
    /// a cold status there read every file for ~40 s and slowed each crawl step 5–9×. Refs refresh
    /// at once.</para></summary>
    private async Task RefreshFocusedAsync(RepositoryContext context, bool full, CancellationToken focus, CancellationToken ct)
    {
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, focus))
        {
            try
            {
                if (context.Watcher.IsLargeWorkingTree && !context.GraphWarmed.IsCompleted)
                {
                    if (full)
                        await context.State.RefreshRefsAsync(linked.Token).ConfigureAwait(false);
                    // Task.WhenAny never throws; a focus change or close ends the delay.
                    await Task.WhenAny(context.GraphWarmed, Task.Delay(StatusAfterGraphCeiling, linked.Token))
                        .ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                    await context.State.RefreshStatusAsync(linked.Token).ConfigureAwait(false);
                    return;
                }

                await (full ? context.State.RefreshAllAsync(linked.Token) : context.State.RefreshStatusAsync(linked.Token))
                    .ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (focus.IsCancellationRequested
                && !ct.IsCancellationRequested && !context.Lifetime.IsCancellationRequested)
            {
                // Focus moved on — requeued below.
            }
        }

        await RequestBackgroundRefreshAsync(context, full, ct).ConfigureAwait(false);
    }

    /// <summary>Initialises and refreshes the queued background tabs one at a time in app-band tab order,
    /// and never before the focused tab has fully loaded — that tab is what the user is waiting on.</summary>
    private async Task DrainBackgroundInitQueueAsync()
    {
        while (true)
        {
            RepositoryContext? foreground;
            BackgroundQueueItem? next;
            Task foregroundChanged;
            lock (_sync)
            {
                if (_bgQueue.Count == 0) { _bgInitPumpIdle = true; return; }
                foreground = _foregroundInit;
                // A closed foreground tab (or every tab, after DisposeAsync) will never finish
                // loading, and its cancelled Lifetime makes the wait below return instantly —
                // waiting on it would spin this loop forever. Treat it as no foreground. The same
                // goes for one whose load already completed: a tab the queue was loading when it
                // took focus (or when the open path found it loading) stays _foregroundInit after
                // that background attempt ends, since only a foreground attempt clears it.
                if (foreground is not null && (!_open.Contains(foreground) || foreground.FirstFullLoad.IsCompleted))
                    foreground = null;
                next = SelectNextBackgroundItemLocked();
                foregroundChanged = _foregroundChanged.Task;
            }

            // Let the focused tab finish loading first (its own graph paint + identity +
            // RefreshAllAsync). If it is itself the next queued item, don't wait on it here.
            // Also race against _foregroundChanged — if `foreground` gets interrupted
            // (FocusInternal), its FirstFullLoad will never complete on its own anymore, so
            // without this the pump would wait forever on exactly the tab it just displaced.
            if (foreground is not null && next is not null && !ReferenceEquals(foreground, next.Context))
            {
                try { await Task.WhenAny(foreground.FirstFullLoad.WaitAsync(foreground.Lifetime), foregroundChanged).ConfigureAwait(false); }
                catch { /* focused tab closed before it finished — carry on */ }
                continue; // re-snapshot: foreground/queue-front may have changed while we waited
            }

            lock (_sync)
            {
                if (_bgQueue.Count == 0) { _bgInitPumpIdle = true; return; }
                next = SelectNextBackgroundItemLocked();
                if (next is null) { _bgInitPumpIdle = true; return; }
                _bgQueue.Remove(next);
                if (!_open.Contains(next.Context))
                {
                    foreach (var a in next.Awaiters) a.TrySetCanceled();
                    continue; // closed while queued
                }
            }

            try
            {
                // Decided now, not when queued: a tab whose load completed since then needs only
                // the refresh.
                if (next.IsInit && !next.Context.FirstFullLoad.IsCompleted)
                {
                    await InitializeContextAsync(next.Context, foreground: false).ConfigureAwait(false);
                }
                else if (next.Full)
                {
                    await next.Context.State.RefreshAllAsync(next.Context.Lifetime).ConfigureAwait(false);
                }
                else
                {
                    await next.Context.State.RefreshStatusAsync(next.Context.Lifetime).ConfigureAwait(false);
                }

                foreach (var a in next.Awaiters) a.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                foreach (var a in next.Awaiters) a.TrySetCanceled();
            }
            catch (Exception ex)
            {
                foreach (var a in next.Awaiters) a.TrySetException(ex);
            }
        }
    }

    private BackgroundQueueItem? SelectNextBackgroundItemLocked()
    {
        if (_bgQueue.Count == 0)
            return null;

        // 1. Interrupted tab has priority ahead of tab-order selection.
        var interrupted = _bgQueue.FirstOrDefault(x => x.IsInterruptedPriority);
        if (interrupted is not null)
            return interrupted;

        // 2. Tab with the lowest index in _open (app-band tab order).
        BackgroundQueueItem? best = null;
        int bestIdx = int.MaxValue;
        foreach (var item in _bgQueue)
        {
            var idx = _open.IndexOf(item.Context);
            if (idx >= 0 && idx < bestIdx)
            {
                bestIdx = idx;
                best = item;
            }
        }

        return best ?? _bgQueue[0];
    }

    private void FocusInternal(RepositoryContext context)
    {
        RepositoryContext? old;
        bool needsInit;
        CancellationTokenSource? toCancel = null;
        CancellationTokenSource? leftFocus;
        bool startPump = false;
        lock (_sync)
        {
            if (ReferenceEquals(_focused, context))
                return;
            old = _focused;
            _focused = context;
            leftFocus = _focusCts;
            _focusCts = new CancellationTokenSource();

            // Interrupt the outgoing tab's in-flight identity/refresh work, if any, so
            // the tab the user is now looking at doesn't queue behind it. A tab whose FirstFullLoad
            // already completed has nothing to interrupt. The interrupted attempt is requeued at
            // the front of the background pump (ahead of never-started tabs) to resume once
            // priority frees up — RefreshAllAsync is read-only, and identity resolution's only
            // write (seeding git config from the active profile) checks the attempt token
            // before starting and then runs uncancelled, so re-running from scratch
            // is fine: either the write never started, or it already completed. Taking the
            // attempt's entry is what tells its InitializeContextAsync the load was interrupted.
            if (old is not null && !old.FirstFullLoad.IsCompleted && _attemptCts.Remove(old, out var oldCts))
            {
                toCancel = oldCts;
                var existing = _bgQueue.FirstOrDefault(x => ReferenceEquals(x.Context, old));
                if (existing is null)
                {
                    _bgQueue.Insert(0, new BackgroundQueueItem(old, isInit: true, isInterruptedPriority: true));
                }
                else
                {
                    existing.IsInterruptedPriority = true;
                }
                if (_bgInitPumpIdle)
                {
                    _bgInitPumpIdle = false;
                    startPump = true;
                }
            }

            // Promote the newly focused context to immediate init if it still needs one — a
            // never-started deferred placeholder, a previously interrupted tab sitting in
            // _bgQueue, or one the pump is already actively running (relabel it as the
            // foreground target rather than starting a second concurrent attempt against it).
            needsInit = false;
            if (!context.FirstFullLoad.IsCompleted)
            {
                var wasQueued = _deferredInit.Remove(context) | (_bgQueue.RemoveAll(x => ReferenceEquals(x.Context, context)) > 0);
                if (wasQueued)
                {
                    needsInit = true;
                    SetForegroundInit(context);
                }
                else if (_attemptCts.ContainsKey(context))
                {
                    SetForegroundInit(context); // already running — just becomes the priority tab
                }
            }
        }

        // Never run cancellation callbacks while holding _sync. The attempt may have ended (and
        // disposed its source) since it was taken above.
        try { toCancel?.Cancel(); } catch (ObjectDisposedException) { }
        leftFocus.Cancel();
        if (startPump)
            _ = Task.Run(DrainBackgroundInitQueueAsync);

        // Stop the newly focused context's own inactive-tab headless job (queued or
        // running) before its foreground graph work can start — that job and this tab's own
        // background spill both write to the same on-disk store, so the focused tab must never
        // start writing while its headless pass might still be running. Fire-and-forget: the
        // graph service's own writer serializes against it internally regardless.
        if (_services.GetService(typeof(Historia.Services.Graph.IGraphLayoutService))
            is Historia.Services.Graph.IGraphLayoutService graphSvcForFocus)
            _ = graphSvcForFocus.CancelInactiveTabCachingAsync(context);

        // a placeholder (or previously interrupted) tab initialises through the ordinary
        // open path on first/renewed focus.
        if (needsInit)
            _ = InitializeContextAsync(context, foreground: true);

        PersistSession(); // focus is recorded, not only tab-set changes.
        FocusedRepositoryChanged?.Invoke(this, new FocusedRepositoryChangedEventArgs(old, context));

        // Release the outgoing tab's graph memory and, once the newly focused tab is done loading,
        // hand the freed heap back to the OS.
        if (old is not null)
            _ = HibernateAndReclaimAsync(old, context);
        else
            _ = QueueInitialInactiveTabPipelineAsync(context);
    }

    // An aggressive, blocking, compacting GC.Collect after every tab switch pays a
    // full-heap pause and (LargeObjectHeapCompactionMode.CompactOnce) a LOH compaction even when
    // the outgoing tab held only a few MB, and Aggressive also decommits, so the next tab pays to
    // re-commit. Only worth it once the heap has actually grown enough to matter.
    private const long CompactHeapThresholdBytes = 1L * 1024 * 1024 * 1024; // 1 GiB
    private const long CompactLohThresholdBytes = 256L * 1024 * 1024; // 256 MiB

    /// <summary>Whether the managed heap (or the large-object heap alone) is big
    /// enough to justify the blocking, compacting GC in <see cref="HibernateAndReclaimAsync"/>.
    /// A small heap gains nothing from the pause and just pays the decommit-then-recommit cost on
    /// the next tab switch.</summary>
    internal static bool ShouldCompactAfterHibernate(long heapBytes, long lohBytes) =>
        heapBytes >= CompactHeapThresholdBytes || lohBytes >= CompactLohThresholdBytes;

    /// <summary>Releases everything the outgoing tab's graph held in memory (cancels its
    /// background spill, disposes its live walk, drops its resident commit list). Once
    /// the newly focused tab's background-write gate opens (so the pause never lands inside its
    /// own first paint) and focus has been stable for 2s (so a burst of switches triggers one
    /// collection, not several), runs a compacting GC to actually shrink the working set — below
    /// ~90% memory load the GC prefers background collections that free live objects without
    /// returning the heap size to the OS. Skipped when neither the managed heap nor
    /// the LOH has grown past its threshold — see <see cref="ShouldCompactAfterHibernate"/>.</summary>
    private async Task HibernateAndReclaimAsync(RepositoryContext old, RepositoryContext newContext)
    {
        if (_services.GetService(typeof(Historia.Services.Graph.IGraphLayoutService))
            is Historia.Services.Graph.IGraphLayoutService graphSvc)
        {
            try { await graphSvc.Hibernate(old).ConfigureAwait(false); }
            catch { /* best effort — memory release must never surface as a fatal error */ }
        }

        if (_services.GetService(typeof(IBackgroundWorkGate)) is IBackgroundWorkGate gate)
        {
            try { await gate.WaitUntilOpenAsync(newContext.Lifetime).ConfigureAwait(false); }
            catch { return; } // the new tab closed before it finished loading
        }

        try { await Task.Delay(TimeSpan.FromSeconds(2), newContext.Lifetime).ConfigureAwait(false); }
        catch { return; } // focus moved again (or the tab closed) during the debounce

        lock (_sync)
        {
            if (!ReferenceEquals(_focused, newContext))
                return; // focus moved again right at the debounce boundary
        }

        // Gate the blocking, compacting collection on actual heap growth. Verified
        // against this repo's own PerfLog.Memory (Historia.Core/Diagnostics/PerfLog.cs): .NET's
        // GCMemoryInfo.GenerationInfo is a GCGenerationInfo[] indexed by generation, where index 3
        // is the large object heap (index 4 is the pinned object heap; absent on runtimes too old
        // to report it, hence the length check) and HeapSizeBytes is the managed heap's total size
        // as of the end of the last GC.
        var info = GC.GetGCMemoryInfo();
        var gen = info.GenerationInfo;
        var lohBytes = gen.Length > 3 ? gen[3].SizeAfterBytes : 0L;
        if (!ShouldCompactAfterHibernate(info.HeapSizeBytes, lohBytes))
        {
            static long Mb(long bytes) => bytes / (1024 * 1024);
            Historia.Diagnostics.PerfLog.Mark(
                $"tab-switch GC compaction skipped (heap={Mb(info.HeapSizeBytes)}MB, loh={Mb(lohBytes)}MB)");
        }
        else
        {
            Historia.Diagnostics.PerfLog.Memory("before tab-switch GC compaction");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            sw.Stop();
            Historia.Diagnostics.PerfLog.Memory("after tab-switch GC compaction");
            Historia.Diagnostics.PerfLog.Mark($"tab-switch GC compaction: {sw.Elapsed.TotalMilliseconds:F1}ms");
        }

        // Once the active tab is settled (gate open, focus stable, its own reclaim done),
        // queue every OTHER open tab for the headless commit-graph + disk-cache pipeline.
        // GraphBackgroundWriter runs them one at a time, behind the same gate, so this never
        // contends with the active tab's own work.
        _ = RunInactiveTabPipelineAsync(newContext);
    }

    /// <summary>Queues every open tab except <paramref name="activeContext"/> for
    /// <c>IGraphLayoutService.RunHeadlessCacheAsync</c>. Waits for the background-init pump to be
    /// idle first — a session restore's own identity/status work takes priority over this purely
    /// optional caching. A job is stopped both by its own tab closing
    /// (<c>RepositoryContext.Lifetime</c>) AND by that tab gaining focus
    /// (<see cref="FocusInternal"/> calls <c>IGraphLayoutService.CancelInactiveTabCachingAsync</c>
    /// for the newly focused context before its own foreground spill can start) — the headless
    /// pipeline and a focused tab's own background spill both write to the same on-disk store, so
    /// letting a headless pass for the tab the user just switched to keep running would race it.
    /// <c>GraphMetaWriter.AppendSpillBatch</c>/<c>TryCommitReorder</c> re-check the on-disk header
    /// under a per-repo lock as a second line of defence regardless.</summary>
    private async Task RunInactiveTabPipelineAsync(RepositoryContext activeContext)
    {
        try
        {
            if (_services.GetService(typeof(Historia.Services.Graph.IGraphLayoutService))
                is not Historia.Services.Graph.IGraphLayoutService graphSvc)
                return;

            if (!await WaitForBackgroundInitIdleAsync(activeContext.Lifetime).ConfigureAwait(false))
                return;

            List<RepositoryContext> targets;
            string[] openPaths;
            lock (_sync)
            {
                if (!ReferenceEquals(_focused, activeContext))
                    return; // focus moved again while this waited
                targets = [.. _open.Where(c => !ReferenceEquals(c, activeContext))];
                openPaths = [.. _open.Select(c => c.RepositoryPath)];
            }

            foreach (var context in targets)
                graphSvc.EnqueueInactiveTabCaching(context, TabGraphQuery);

            // Once per launch, after the first settled tab: the open tabs' stores are never evicted.
            graphSvc.SweepDiskCache(openPaths);
        }
        catch (OperationCanceledException)
        {
            // The active tab closed while this waited — nothing left to queue for.
        }
        catch (Exception ex)
        {
            // Headless caching is a pure optimisation; never let it surface as a fatal error.
            Historia.Diagnostics.PerfLog.Mark($"RunInactiveTabPipelineAsync failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Cold-launch path: the first focus has no outgoing tab to hibernate, but the
    /// inactive tabs still need their headless disk-cache + commit-graph pipeline. Waits for
    /// the same preconditions <see cref="HibernateAndReclaimAsync"/> does (gate open, bg-init
    /// idle, focus stable) — minus the GC compaction (nothing to reclaim on a fresh start)
    /// and minus the 2s debounce (no focus churn to absorb on the initial tab).</summary>
    private async Task QueueInitialInactiveTabPipelineAsync(RepositoryContext context)
    {
        try
        {
            if (_services.GetService(typeof(IBackgroundWorkGate)) is IBackgroundWorkGate gate)
            {
                try { await gate.WaitUntilOpenAsync(context.Lifetime).ConfigureAwait(false); }
                catch { return; }
            }

            if (!await WaitForBackgroundInitIdleAsync(context.Lifetime).ConfigureAwait(false))
                return;

            lock (_sync)
            {
                if (!ReferenceEquals(_focused, context))
                    return;
            }

            _ = RunInactiveTabPipelineAsync(context);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Historia.Diagnostics.PerfLog.Mark(
                $"QueueInitialInactiveTabPipelineAsync failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Waits until <see cref="_bgInitPumpIdle"/> is true, <paramref name="ct"/> is
    /// cancelled, or <see cref="BackgroundInitIdleWaitCap"/> elapses. Polling rather than a
    /// signal: this is a low-urgency background optimisation, not on any user-visible
    /// latency path, so a short delay loop is simpler and no less correct than threading a new
    /// completion source through every one of the pump's several mutation points. Capped rather
    /// than unconditional: this is a best-effort caching pass, not a correctness dependency, and
    /// must never wait forever on an internal signal — the next focus change tries again.</summary>
    private async Task<bool> WaitForBackgroundInitIdleAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + BackgroundInitIdleWaitCap;
        while (true)
        {
            bool idle;
            lock (_sync) idle = _bgInitPumpIdle;
            if (idle)
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            try { await Task.Delay(250, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
    }

    private static readonly TimeSpan BackgroundInitIdleWaitCap = TimeSpan.FromSeconds(30);
}
