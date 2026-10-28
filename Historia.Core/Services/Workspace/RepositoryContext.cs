namespace Historia.Services.Workspace;

/// <summary>Who holds a repository's network gate.</summary>
public enum NetworkOperationOwner
{
    /// <summary>A fetch, pull, push or prune the user started.</summary>
    User,

    /// <summary>The background auto-fetch.</summary>
    AutoFetch,

    /// <summary>Repository upkeep, such as removing stale lock files.</summary>
    Maintenance,
}

/// <summary>The operation holding a repository's network gate; <paramref name="Label"/> is the
/// user-facing name ("Fetch", "Auto-fetch", "Lock cleanup").</summary>
public sealed record NetworkOperationInfo(NetworkOperationOwner Owner, string Label);

/// <summary>What the file watcher drops while one of Historia's own Git operations runs.</summary>
public enum SelfOperationKind
{
    /// <summary>Every watcher event: the operation rewrites the working tree, the index and refs.</summary>
    Full,

    /// <summary>Only events under the git directory: the operation writes refs and objects there
    /// and leaves the working tree alone, so edits made while it runs still show.</summary>
    GitDirectoryOnly,
}

/// <summary>
/// Per-repository-tab handle. Owns the reactive state service, the gateway, the file watcher,
/// write lock, and a lifetime cancellation scope.
/// </summary>
public sealed class RepositoryContext : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly AsyncLocal<bool> _isHoldingWriteLock = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _networkGate = new(1, 1);
    private NetworkOperationInfo? _networkOp;
    private int _selfOperationDepth;
    private int _gitDirectoryOperationDepth;
    private int _anySelfOperationDepth;
    private long _completedOperations;

    // Signaled once the graph control's first paint for this context lands (success, failure,
    // or cancellation -- whichever comes first). WorkspaceService's InitializeContextAsync waits
    // on this (bounded by its own timeout) before it starts touching the repo itself, so the
    // graph's first walk -- what the user is actually staring at during cold open -- runs first
    // instead of queueing behind identity/branches/status work.
    private readonly TaskCompletionSource _firstGraphPaint =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once this context's first graph paint (or its failure) lands. Never
    /// faults or gets stuck on its own -- a caller with no graph control ever attaching (a
    /// headless open, a test) must apply its own timeout rather than await this alone.</summary>
    public Task FirstGraphPaint => _firstGraphPaint.Task;

    /// <summary>Idempotent -- safe to call once per attempt or not at all.</summary>
    public void MarkFirstGraphPaintDone() => _firstGraphPaint.TrySetResult();

    // Signalled once the graph's silent crawl has loaded its first stretch of history, or stopped
    // short of it. On a large working tree WorkspaceService holds the tab's first git status until
    // then: a cold status there reads every file, and each crawl step it overlapped ran several
    // times slower.
    private readonly TaskCompletionSource _graphWarmed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the graph's silent crawl has warmed up (or stopped). Never faults;
    /// a caller with no graph control attached applies its own timeout.</summary>
    public Task GraphWarmed => _graphWarmed.Task;

    /// <summary>Idempotent.</summary>
    public void MarkGraphWarmed() => _graphWarmed.TrySetResult();

    // signalled once this tab is "fully loaded" -- identity resolved and the first full
    // state refresh (branches, graph, changes) has completed. IAutoFetchService waits on this
    // before its [AutoFetch] loop touches the repo. Like FirstGraphPaint it never faults; a
    // headless open completes it as soon as InitializeContextAsync runs.
    private readonly TaskCompletionSource _firstFullLoad =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once this context has fully loaded. Never faults.</summary>
    public Task FirstFullLoad => _firstFullLoad.Task;

    /// <summary>Idempotent. Called by <c>WorkspaceService.InitializeContextAsync</c> when the
    /// first refresh settles (or the tab closes first).</summary>
    public void MarkFirstFullLoadDone() => _firstFullLoad.TrySetResult();

    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Working directory, or the git directory for a bare repo.</summary>
    public string RepositoryPath { get; }
    public string GitDirectory { get; }
    public bool IsBare { get; }
    public string DisplayName { get; }

    /// <summary>Cancelled when the tab closes; every in-flight operation observes it.</summary>
    public CancellationToken Lifetime { get; }

    public IGitGateway Gateway { get; }

    /// <summary>This repo's refs, read with one <c>git for-each-ref</c> and cached until a ref
    /// file changes — the commit graph and the tag list read refs here.</summary>
    public RefSnapshotProvider RefSnapshots { get; }

    /// <summary>Read-only access to this repo's commit-graph files, in the common directory's
    /// object store so linked worktrees share it.</summary>
    public CommitGraphIndex CommitGraph { get; }

    /// <summary>This repo's remotes, read via Git CLI and cached until the common config
    /// file timestamp changes.</summary>
    public RepoConfigReader RemoteConfig { get; }

    public IRepositoryStateService State { get; }
    private readonly RepositoryStateService _state;
    internal FileWatcherService Watcher { get; }
    internal IWorkspaceService? Workspace { get; set; }

    /// <summary>Null when this tab is standalone; otherwise the tab that opened it as one of its
    /// submodules. Invariant <see cref="WorkspaceService"/> maintains in its open-tab list:
    /// a linked tab always sits contiguously after its owner, and one owner's own children are
    /// always contiguous too — never interleaved with a different owner's group. Cleared (not
    /// closed) when the owner tab closes (see <c>WorkspaceService.CloseAsync</c>).</summary>
    public RepositoryContext? LinkOwner { get; internal set; }

    /// <summary>
    /// Stable repo identity, resolved once per open by <c>IRepoIdentityService</c>
    /// and cached here — it walks history, so it is never recomputed per status refresh.
    /// </summary>
    public RepoIdentityKey? IdentityKey { get; set; }

    /// <summary>The profile resolved for this tab. Null in zero-profile mode.</summary>
    public GitProfile? ActiveProfile => State.ActiveProfile;

    private RepositoryContext(DiscoveredRepository discovered, IServiceProvider services)
        : this(discovered, services, seed: null, seedFromCache: true)
    {
    }

    private RepositoryContext(DiscoveredRepository discovered, IServiceProvider services,
        RepoStateSnapshot? seed, bool seedFromCache)
    {
        Lifetime = _lifetimeCts.Token;
        IsBare = discovered.IsBare;
        GitDirectory = discovered.GitDirectory;
        RepositoryPath = discovered.RepositoryPath;
        DisplayName = DeriveDisplayName(RepositoryPath, GitDirectory);
        GraphCachePaths.Forget(RepositoryPath);

        CommitGraph = new CommitGraphIndex(Path.Combine(RefStateToken.ResolveCommonDir(GitDirectory), "objects"));

        RefSnapshots = new RefSnapshotProvider(
            services.GetRequiredService<IGitCliService>(), GitDirectory, RepositoryPath, Lifetime, CommitGraph);

        RemoteConfig = new RepoConfigReader(
            services.GetRequiredService<IGitCliService>(), GitDirectory, RepositoryPath);

        Gateway = new LoggingGitGateway(
            new GitGateway(
                this,
                services.GetRequiredService<IGitCliService>(),
                services.GetRequiredService<Historia.Services.Settings.IRepoLayoutService>(),
                services.GetRequiredService<Historia.Services.Profiles.ICredentialVaultService>()),
            RepositoryPath);

        var state = new RepositoryStateService(
            this,
            services.GetService<IUiDispatcher>(),
            services.GetService<IRepoStateCache>());
        State = _state = state;
        // Paint the sidebar from last-known state before the first refresh.
        if (seedFromCache)
            state.SeedFromCache();
        else
            state.SeedFrom(seed);

        Workspace = services.GetService<IWorkspaceService>();

        Watcher = new FileWatcherService(
            this,
            State,
            services.GetRequiredService<ISettingsService>());
    }

    /// <summary>
    /// Opens a repository discovered from <paramref name="path"/>, blocking the calling thread on
    /// discovery — never from the UI thread; <see cref="OpenAsync"/> is the app's path. Throws <see cref="HistoriaGitException"/> with
    /// <see cref="GitErrorKind.NotAGitRepository"/> when no repository is found.
    /// </summary>
    public static RepositoryContext Open(string path, IServiceProvider services)
    {
        using var _ = Historia.Diagnostics.PerfLog.Scope($"RepositoryContext.Open({Path.GetFileName(path)})");
        using var _log = AppLog.Operation("repo", $"Open({Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))})");
        var discovered = RepositoryDiscovery.Discover(path, services);
        try
        {
            var ctx = new RepositoryContext(discovered, services);
            _log.Outcome(ctx.DisplayName);
            return ctx;
        }
        catch (Exception ex)
        {
            _log.Fail(ex);
            throw;
        }
    }

    /// <summary>
    /// Opens a repository discovered from <paramref name="path"/> without blocking a thread on git,
    /// reading the cached sidebar state alongside discovery rather than after it. Pass
    /// <paramref name="discovered"/> when the caller already discovered the repository. Throws
    /// <see cref="HistoriaGitException"/> with <see cref="GitErrorKind.NotAGitRepository"/> when no
    /// repository is found.
    /// </summary>
    public static async Task<RepositoryContext> OpenAsync(
        string path, IServiceProvider services, CancellationToken ct = default, DiscoveredRepository? discovered = null)
    {
        using var _ = Historia.Diagnostics.PerfLog.Scope($"RepositoryContext.Open({Path.GetFileName(path)})");
        using var _log = AppLog.Operation("repo", $"Open({Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))})");

        // The cache is keyed on the repository root, which the path of a restored or recent tab
        // already is: start reading it now, and only read again if discovery lands elsewhere.
        var cache = services.GetService<IRepoStateCache>();
        var speculativeSeed = cache is null ? null : Task.Run(() => cache.TryLoad(path), CancellationToken.None);

        discovered ??= await RepositoryDiscovery.DiscoverAsync(path, services.GetRequiredService<IGitCliService>(), ct)
            .ConfigureAwait(false);

        RepoStateSnapshot? seed = null;
        if (cache is not null)
        {
            var root = discovered.RepositoryPath;
            seed = SameDirectory(path, root)
                ? await speculativeSeed!.ConfigureAwait(false)
                : await Task.Run(() => cache.TryLoad(root), CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            var ctx = new RepositoryContext(discovered, services, seed, seedFromCache: false);
            _log.Outcome(ctx.DisplayName);
            return ctx;
        }
        catch (Exception ex)
        {
            _log.Fail(ex);
            throw;
        }
    }

    private static bool SameDirectory(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                // As the repo-state cache keys its files.
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private const double WriteLockLogThresholdMs = 5.0;
    private const double CloseWaitLogThresholdMs = 50.0;

    /// <summary>The write-lock holder's label and acquisition time. Immutable and swapped whole, so
    /// a reader on another thread never sees a label from one holder and a time from another.</summary>
    private sealed record WriteHolder(string Label, long StartTimestamp);

    private WriteHolder? _writeHolder;

    /// <summary>The operation holding the write lock and how long it has held it, or null while the
    /// lock is free. Safe to read from any thread; the value is a snapshot.</summary>
    public (string Label, TimeSpan Held)? CurrentWriteHolder
    {
        get
        {
            var holder = Volatile.Read(ref _writeHolder);
            return holder is null
                ? null
                : (holder.Label, System.Diagnostics.Stopwatch.GetElapsedTime(holder.StartTimestamp));
        }
    }

    private static string ResolveCallerLabel(string? label, string callerMember, string callerFile)
    {
        if (!string.IsNullOrEmpty(label))
            return label;
        var fileName = string.IsNullOrEmpty(callerFile) ? "" : Path.GetFileNameWithoutExtension(callerFile);
        if (!string.IsNullOrEmpty(fileName) && !string.IsNullOrEmpty(callerMember))
            return $"{fileName}.{callerMember}";
        if (!string.IsNullOrEmpty(callerMember))
            return callerMember;
        return "unknown";
    }

    /// <summary>
    /// Serializes Git operations that mutate the index, HEAD, refs, or the work tree.
    /// Reads never take this lock. Re-entrant acquisition from within the work delegate throws.
    /// </summary>
    public async Task<T> WithWriteLockAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken ct = default,
        string? label = null,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "")
    {
        if (_isHoldingWriteLock.Value)
            throw new InvalidOperationException("Nested or re-entrant write lock acquisition is not permitted.");

        // The pre-checks and Process.Start in the work delegate must not run on a UI thread.
        // Everything below, including the wait timing and the holder bookkeeping, therefore runs
        // after the hop, and the AsyncLocal below is set after it.
        await ThreadHop.LeaveUiThreadAsync().ConfigureAwait(false);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime);
        var holderAtWaitStart = Volatile.Read(ref _writeHolder);
        var waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await _writeLock.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var cancelledMs = System.Diagnostics.Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
            if (cancelledMs > WriteLockLogThresholdMs && PerfLog.Enabled)
            {
                var holder = holderAtWaitStart is null ? "" : $" behind {holderAtWaitStart.Label}";
                PerfLog.Mark($"write lock wait {cancelledMs:F1}ms ({DisplayName}){holder}, cancelled");
            }
            throw;
        }
        var waitMs = System.Diagnostics.Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
        var resolvedLabel = ResolveCallerLabel(label, callerMember, callerFile);
        var holdStart = System.Diagnostics.Stopwatch.GetTimestamp();
        Volatile.Write(ref _writeHolder, new WriteHolder(resolvedLabel, holdStart));
        if (waitMs > WriteLockLogThresholdMs && PerfLog.Enabled)
        {
            var behind = holderAtWaitStart is null ? "" : $" behind {holderAtWaitStart.Label}";
            PerfLog.Mark($"write lock wait {waitMs:F1}ms ({DisplayName}){behind}");
        }

        _isHoldingWriteLock.Value = true;
        try
        {
            // A git process started by the work is never killed once it has started, so a
            // cancellation (tab close) cannot leave index.lock behind or a half-applied change.
            using var writeScope = GitWriteScope.Enter();
            return await work(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            var holdMs = System.Diagnostics.Stopwatch.GetElapsedTime(holdStart).TotalMilliseconds;
            if (holdMs > WriteLockLogThresholdMs && PerfLog.Enabled)
            {
                PerfLog.Mark($"write lock hold {resolvedLabel} {holdMs:F1}ms ({DisplayName})");
            }
            _isHoldingWriteLock.Value = false;
            Volatile.Write(ref _writeHolder, null);
            _writeLock.Release();
        }
    }

    public Task WithWriteLockAsync(
        Func<CancellationToken, Task> work,
        CancellationToken ct = default,
        string? label = null,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "")
        => WithWriteLockAsync(async innerCt => { await work(innerCt).ConfigureAwait(false); return true; }, ct, label, callerMember, callerFile);

    /// <summary>
    /// Marks a scope as one of Historia's own Git operations so the watcher suppresses the
    /// self-triggered refresh storm and issues one explicit refresh on completion.
    /// <see cref="SelfOperationKind.Full"/> drops every watcher event for the scope;
    /// <see cref="SelfOperationKind.GitDirectoryOnly"/>, for an operation that writes only inside
    /// the git directory and may run for minutes, drops only the git-directory events so edits to
    /// the working tree still show while it runs. The refresh fires when the last scope of either
    /// kind ends.
    /// </summary>
    public IDisposable BeginSelfOperation(SelfOperationKind kind = SelfOperationKind.Full)
    {
        Interlocked.Increment(ref kind == SelfOperationKind.Full ? ref _selfOperationDepth : ref _gitDirectoryOperationDepth);
        Interlocked.Increment(ref _anySelfOperationDepth);
        return new SelfOperationScope(this, kind);
    }

    /// <summary>A <see cref="SelfOperationKind.Full"/> scope is open.</summary>
    public bool IsSelfOperationInProgress => Volatile.Read(ref _selfOperationDepth) > 0;

    /// <summary>A <see cref="SelfOperationKind.GitDirectoryOnly"/> scope is open.</summary>
    public bool IsGitDirectoryOperationInProgress => Volatile.Read(ref _gitDirectoryOperationDepth) > 0;

    /// <summary>
    /// Counts Historia's own operations that have ended on this tab. A refresh whose work started
    /// at or after the count an operation ended at began after that operation finished.
    /// </summary>
    internal long CompletedOperations => Volatile.Read(ref _completedOperations);

    /// <summary>The fetch, pull, push, prune or lock cleanup running on this repository, or null.
    /// Lets a caller name what it is waiting on.</summary>
    public NetworkOperationInfo? CurrentNetworkOperation => Volatile.Read(ref _networkOp);

    /// <summary>
    /// Takes this repository's network gate without waiting, or returns false while another
    /// network operation holds it. The gate lets one fetch, pull, push, prune or stale-lock cleanup
    /// run per repository at a time, so two git processes never race on the same ref locks. It is
    /// not the write lock and may be held across a dialog; a holder that also needs the write lock
    /// takes the gate first, never the other way round. Dispose the lease to release it.
    /// </summary>
    public bool TryBeginNetworkOperation(NetworkOperationInfo info, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IDisposable? lease)
    {
        if (!_networkGate.Wait(0))
        {
            lease = null;
            return false;
        }
        lease = EnterNetworkOperation(info);
        return true;
    }

    /// <summary>Waits for this repository's network gate (see <see cref="TryBeginNetworkOperation"/>).
    /// Only <paramref name="ct"/> cancels the wait; the caller decides whether that includes
    /// <see cref="Lifetime"/>.</summary>
    public async Task<IDisposable> BeginNetworkOperationAsync(NetworkOperationInfo info, CancellationToken ct)
    {
        await _networkGate.WaitAsync(ct).ConfigureAwait(false);
        return EnterNetworkOperation(info);
    }

    private NetworkOperationLease EnterNetworkOperation(NetworkOperationInfo info)
    {
        Volatile.Write(ref _networkOp, info);
        return new NetworkOperationLease(this);
    }

    private sealed class NetworkOperationLease(RepositoryContext owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            Volatile.Write(ref owner._networkOp, null);
            owner._networkGate.Release();
        }
    }

    private sealed class SelfOperationScope(RepositoryContext owner, SelfOperationKind kind) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            var operationEnd = Interlocked.Increment(ref owner._completedOperations);
            if (kind == SelfOperationKind.Full)
                Interlocked.Decrement(ref owner._selfOperationDepth);
            else
                Interlocked.Decrement(ref owner._gitDirectoryOperationDepth);

            // One counter across both kinds, so exactly one ending scope sees zero and fires the
            // refresh even when scopes of different kinds end on different threads at once.
            if (Interlocked.Decrement(ref owner._anySelfOperationDepth) == 0)
                _ = RefreshQuietlyAsync(owner, operationEnd);
        }

        // Fire-and-forget, so a fault here must be observed and swallowed here — otherwise it
        // surfaces later as an unobserved TaskScheduler exception, which App.axaml.cs reports to
        // the user as a fatal error for what can be a routine tab close mid-operation.
        private static async Task RefreshQuietlyAsync(RepositoryContext owner, long operationEnd)
        {
            try
            {
                await owner._state.RefreshAfterOperationAsync(operationEnd, owner.Lifetime).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Tab closed during the operation.
            }
            catch
            {
                // A self-operation's trailing refresh failing must not surface as a fatal error.
            }
        }
    }

    private static string DeriveDisplayName(string workingDir, string gitDir)
    {
        var basis = workingDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(basis))
            basis = gitDir;
        var name = Path.GetFileName(basis);
        return string.IsNullOrEmpty(name) ? basis : name;
    }

    private Task? _disposeTask;

    public ValueTask DisposeAsync()
    {
        if (_disposeTask is { } existing)
            return new ValueTask(existing);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _disposeTask, tcs.Task, null) == null)
        {
            _ = RunDisposeCoreAsync(tcs);
        }

        return new ValueTask(_disposeTask);
    }

    private async Task RunDisposeCoreAsync(TaskCompletionSource tcs)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            tcs.TrySetResult();
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }

    private async Task DisposeCoreAsync()
    {
        using var perfScope = Historia.Diagnostics.PerfLog.Scope($"RepositoryContext.DisposeAsync({DisplayName})");
        AppLog.Write(AppLogLevel.Info, "repo", $"Close({DisplayName})");
        try { _lifetimeCts.Cancel(); } catch { /* ignore */ }

        // A write that already holds the lock finishes before the watcher and state go away; the
        // cancelled lifetime stops writes that are still waiting. A close requested from inside a
        // write would wait for itself, so that flow skips the wait.
        if (!_isHoldingWriteLock.Value)
        {
            var waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
            await _writeLock.WaitAsync().ConfigureAwait(false);
            _writeLock.Release();
            var waitedMs = System.Diagnostics.Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
            if (waitedMs > CloseWaitLogThresholdMs && PerfLog.Enabled)
                PerfLog.Mark($"close waited {waitedMs:F1}ms for the write lock ({DisplayName})");
        }

        await Watcher.DisposeAsync().ConfigureAwait(false);
        if (State is IAsyncDisposable stateDisposable)
            await stateDisposable.DisposeAsync().ConfigureAwait(false);
    }
}
