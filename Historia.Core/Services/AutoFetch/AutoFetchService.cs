namespace Historia.Services.AutoFetch;

/// <inheritdoc cref="IAutoFetchService"/>
public sealed class AutoFetchService : IAutoFetchService
{
    /// <summary>the quiet period that opens every <c>[AutoFetch]</c> cycle.</summary>
    internal static readonly TimeSpan InitialIdle = TimeSpan.FromMinutes(5);

    /// <summary>the gap between one open repo's fetch and the next (also the trailing
    /// wait before the cycle restarts).</summary>
    internal static readonly TimeSpan PerRepoStep = TimeSpan.FromMinutes(1);

    private readonly IWorkspaceService _workspace;
    private readonly IAutoFetchHost _host;
    private readonly ISettingsService _settings;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly IGraphLayoutService? _graphLayoutService;
    private readonly ILogTrimmer? _logTrimmer;

    private readonly object _sync = new();
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _cycleCts;
    private Task? _loop;
    private TaskCompletionSource? _firstRepo;
    private int _lastCount;
    private bool _started;

    // dev visuals: per-tab state in the current [AutoFetch] cycle.
    private volatile bool _loopRunning;
    private readonly HashSet<RepositoryContext> _fetchedThisCycle = [];

    public event EventHandler? TabStatesChanged;

    public AutoFetchService(
        IWorkspaceService workspace,
        IAutoFetchHost host,
        ISettingsService settings,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        IGraphLayoutService? graphLayoutService = null,
        ILogTrimmer? logTrimmer = null)
    {
        _workspace = workspace;
        _host = host;
        _settings = settings;
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
        _graphLayoutService = graphLayoutService;
        _logTrimmer = logTrimmer;

        _settings.Changed += OnSettingsChanged;
        _workspace.OpenRepositoriesChanged += OnOpenRepositoriesChanged;
    }

    public bool IsEnabled => _settings.Current.AutoFetchEnabled;

    public AutoFetchTabState GetTabState(RepositoryContext context)
    {
        if (!_loopRunning)
            return AutoFetchTabState.NotStarted;
        lock (_sync)
            return _fetchedThisCycle.Contains(context)
                ? AutoFetchTabState.FetchedThisCycle
                : AutoFetchTabState.PendingThisCycle;
    }

    private void RaiseTabStates() => TabStatesChanged?.Invoke(this, EventArgs.Empty);

    public void Start()
    {
        lock (_sync)
        {
            if (_started || !IsEnabled)
                return;
            _started = true;
            _runCts = new CancellationTokenSource();
            var ct = _runCts.Token;
            _loop = Task.Run(() => RunAsync(ct));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? run;
        lock (_sync)
        {
            if (!_started)
                return;
            _started = false;
            run = _runCts;
            _runCts = null;
            _cycleCts?.Cancel();
            _firstRepo?.TrySetCanceled();
        }

        try { run?.Cancel(); }
        catch { /* already disposed */ }

        _loopRunning = false;
        lock (_sync)
            _fetchedThisCycle.Clear();
        RaiseTabStates();
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.OldSettings.AutoFetchEnabled == e.NewSettings.AutoFetchEnabled)
            return;
        if (e.NewSettings.AutoFetchEnabled)
            Start();
        else
            Stop();
    }

    private void OnOpenRepositoriesChanged(object? sender, OpenRepositoriesChangedEventArgs e)
    {
        lock (_sync)
        {
            if (e.OpenRepositories.Count > 0)
                _firstRepo?.TrySetResult();

            if (!_started)
                return;

            // a net add/close restarts an in-progress cycle; a pure reorder (same
            // count) does not.
            if (e.OpenRepositories.Count != _lastCount)
            {
                _lastCount = e.OpenRepositories.Count;
                _cycleCts?.Cancel();
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await WaitForStartConditionAsync(ct).ConfigureAwait(false);
            await AutoFetchLoopAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stop() / disable — expected.
        }
        catch
        {
            // the loop must never take the app down.
        }
        finally
        {
            _loopRunning = false;
            RaiseTabStates();
        }
    }

    private async Task WaitForStartConditionAsync(CancellationToken ct)
    {
        var open = _workspace.OpenRepositories;
        if (open.Count == 0)
        {
            var first = await WaitForFirstRepoAsync(ct).ConfigureAwait(false);
            await _host.WhenFullyLoadedAsync(first, ct).ConfigureAwait(false);
        }
        else
        {
            // Wait for *every* open repo — active or not — to fully load.
            await Task.WhenAll(open.Select(r => _host.WhenFullyLoadedAsync(r, ct))).ConfigureAwait(false);
        }

        lock (_sync)
            _lastCount = _workspace.OpenRepositories.Count;
    }

    private async Task<RepositoryContext> WaitForFirstRepoAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var open = _workspace.OpenRepositories;
            if (open.Count > 0)
                return open[0];

            TaskCompletionSource tcs;
            lock (_sync)
            {
                _firstRepo ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                tcs = _firstRepo;
            }

            await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
            lock (_sync)
                _firstRepo = null;
        }
    }

    private async Task AutoFetchLoopAsync(CancellationToken ct)
    {
        _loopRunning = true;

        while (!ct.IsCancellationRequested)
        {
            CancellationTokenSource cycle;
            lock (_sync)
            {
                _cycleCts?.Dispose();
                _cycleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cycle = _cycleCts;
                _fetchedThisCycle.Clear(); // a fresh cycle (incl. a restart) resets every tab to yellow
            }
            RaiseTabStates();

            var cct = cycle.Token;
            try
            {
                await _delay(InitialIdle, cct).ConfigureAwait(false);

                await TrimLogsAsync(ct).ConfigureAwait(false);
                cct.ThrowIfCancellationRequested();

                // Order: idle, trim, fetch per repo.
                // Positional, not per-tab: the repo at slot `pos` when this step fires is the one
                // fetched — reordering tabs is deliberately not re-targeted. The count
                // is read once per cycle; a net add/close cancels `cct` and restarts.
                // The fetch itself runs on the run token, never `cct`: cancelling kills git, and a
                // kill during its ref update leaves *.lock files that break every later fetch. A
                // restart requested mid-fetch therefore takes effect once the fetch finishes.
                var count = _workspace.OpenRepositories.Count;
                for (var pos = 0; pos < count; pos++)
                {
                    if (pos > 0)
                        await _delay(PerRepoStep, cct).ConfigureAwait(false);

                    var repos = _workspace.OpenRepositories;
                    if (pos < repos.Count && await SafeFetchAsync(repos[pos], ct).ConfigureAwait(false))
                    {
                        lock (_sync)
                            _fetchedThisCycle.Add(repos[pos]);
                        RaiseTabStates();
                    }
                    cct.ThrowIfCancellationRequested();
                }

                await _delay(PerRepoStep, cct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Add/close restart — rebuild the cycle from the top.
            }
        }
    }

    private async Task TrimLogsAsync(CancellationToken ct)
    {
        if (_logTrimmer is null)
            return;

        try
        {
            await _logTrimmer.TrimAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Trimming failures are handled inside the trimmer. The loop continues.
        }
    }

    /// <summary>
    /// runs one fetch for <paramref name="context"/>. On success, also refreshes that repo's state
    /// and commit-graph (after the network gate is released) and, for an inactive tab, queues its
    /// headless disk cache. A repository whose network gate is held — a fetch, pull or push the user started, or a lock cleanup — is
    /// skipped until the next cycle. Failures stop quietly here; the host reports them. A refused
    /// tag still refreshes, since git fetched every other ref first.
    /// </summary>
    /// <returns><c>true</c> if the fetch completed without error.</returns>
    private async Task<bool> SafeFetchAsync(RepositoryContext context, CancellationToken ct)
    {
        if (!context.TryBeginNetworkOperation(new NetworkOperationInfo(NetworkOperationOwner.AutoFetch, "Auto-fetch"), out var lease))
        {
            Diagnostics.PerfLog.Mark($"auto-fetch: skipped, {context.CurrentNetworkOperation?.Label ?? "another operation"} running ({context.DisplayName})");
            return false;
        }

        try
        {
            // The gate covers the network part only; the refresh below can take seconds on a large
            // repo and a user pull or push must not queue behind it.
            using (lease)
                await _host.FetchAsync(context, ct).ConfigureAwait(false);

            await _host.RefreshAfterFetchAsync(context, ct).ConfigureAwait(false);

            // Refresh this repo's commit-graph after every successful fetch the loop runs.
            // Itself fire-and-forget and best-effort — never allowed to fail this fetch or the loop.
            try { _graphLayoutService?.RefreshCommitGraphAfterFetch(context); }
            catch { /* commit-graph maintenance must never take the loop down */ }

            // Bring an inactive tab's on-disk graph cache up to date headlessly.
            // Never queued for the focused tab (its own RefsChanged re-spill covers it).
            if (context != _workspace.Focused)
            {
                try
                {
                    _graphLayoutService?.EnqueueInactiveTabCaching(context, WorkspaceService.TabGraphQuery);
                    Diagnostics.PerfLog.Mark($"auto-fetch: queued headless disk cache ({context.DisplayName})");
                }
                catch { /* headless disk caching must never take the loop down */ }
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HistoriaGitException ex) when (ex.Kind == GitErrorKind.TagConflict)
        {
            // Git applies the other refs before it reports the refused tag, so they still need to
            // land. The gate lease was released with the fetch.
            try { await _host.RefreshAfterFetchAsync(context, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception refreshEx)
            {
                AppLog.Write(AppLogLevel.Warn, "auto-fetch", $"{context.DisplayName}: {refreshEx.GetType().Name}: {refreshEx.Message}");
            }
            return false;
        }
        catch (HistoriaGitException)
        {
            // Already in the developer log; the host decides whether the user hears about it.
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "auto-fetch", $"{context.DisplayName}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _settings.Changed -= OnSettingsChanged;
        _workspace.OpenRepositoriesChanged -= OnOpenRepositoriesChanged;
        Stop();

        Task? loop;
        lock (_sync)
            loop = _loop;

        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch { /* teardown */ }
        }
    }
}
