namespace Historia.Services.Workspace;

/// <inheritdoc cref="IWorkspaceService"/>
public sealed partial class WorkspaceService : IWorkspaceService, IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly IGitCliService _cli;
    private readonly IRecentRepositoryService _recents;
    private readonly IProfileService _profiles;
    private readonly IRepoIdentityService _identity;

    private readonly List<RepositoryContext> _open = [];
    private readonly object _sync = new();
    private RepositoryContext? _focused;

    // restored-but-not-yet-focused tabs whose InitializeContextAsync has been deferred.
    // They get the ordinary open path on first focus, exactly once.
    private readonly HashSet<RepositoryContext> _deferredInit = [];

    private sealed class BackgroundQueueItem
    {
        public RepositoryContext Context { get; }
        public bool IsInit { get; }
        public bool Full { get; set; }
        public bool IsInterruptedPriority { get; set; }
        public List<TaskCompletionSource> Awaiters { get; } = [];

        public BackgroundQueueItem(RepositoryContext context, bool isInit, bool full = false, bool isInterruptedPriority = false)
        {
            Context = context;
            IsInit = isInit;
            Full = full;
            IsInterruptedPriority = isInterruptedPriority;
        }
    }

    // Serial background-init/refresh pump. Drains in app-band tab order,
    // one repo at a time, behind the focused tab's FirstFullLoad.
    private readonly List<BackgroundQueueItem> _bgQueue = [];
    private bool _bgInitPumpIdle = true;
    private RepositoryContext? _foregroundInit; // the focused/opened tab currently initialising

    // Per-attempt cancellation for whichever context is currently running
    // InitializeContextCoreAsync, linked to (but distinct from) RepositoryContext.Lifetime —
    // Lifetime only ever cancels on tab close, but a focus switch away from a still-loading tab
    // needs to interrupt its identity/refresh work without tearing the tab down. The entry is the
    // tab's claim to being mid-load: InitializeContextAsync removes it when its load settles (or
    // its attempt ends), FocusInternal when it interrupts the attempt — whichever comes first
    // under _sync decides whether the load counts as completed or interrupted.
    private readonly Dictionary<RepositoryContext, CancellationTokenSource> _attemptCts = [];

    // Signalled (and replaced) every time _foregroundInit changes, so
    // DrainBackgroundInitQueueAsync's wait on the current foreground tab's FirstFullLoad can wake
    // immediately when that tab gets interrupted instead of waiting on a signal that, per the
    // interrupt contract below, will never come on its own.
    private TaskCompletionSource _foregroundChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Cancelled (and replaced) whenever focus changes — the focused tab's own token for work that
    // only matters while the user is looking at it. Never disposed: tokens taken from it may still
    // be linked after it is cancelled.
    private CancellationTokenSource _focusCts = new();

    // What a context this workspace opens is built from: the app's services, with this workspace
    // as IWorkspaceService. A context then knows its workspace from construction, before its file
    // watcher can publish — a watcher with no workspace refreshes on the tab's Lifetime, a token
    // focus loss can't cancel.
    private readonly IServiceProvider _contextServices;

    private sealed class WorkspaceScopedServices(IServiceProvider inner, IWorkspaceService workspace) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IWorkspaceService) ? workspace : inner.GetService(serviceType);
    }

    private AtomicFileWriter? _sessionWriter;
    private AtomicFileWriter SessionWriter => _sessionWriter ??= new AtomicFileWriter(AppPaths.SessionFile);
    private long _sessionPersistSequence;

    // After focusing the restored session's active repo, RestoreSessionAsync pauses
    // briefly before opening the rest of the session's tabs -- see RestoreSessionAsync for why.
    private static readonly TimeSpan FocusPriorityYield = TimeSpan.FromMilliseconds(60);

    // A background (force-initialised, no graph control attached) tab waits at most this
    // long for a first-paint signal that will never come before its identity/refresh runs.
    private static readonly TimeSpan GraphPriorityTimeout = TimeSpan.FromSeconds(8);

    // the FOCUSED tab's graph control WILL paint — wait for that, not a short guess,
    // before its identity resolution + RefreshAllAsync run. A short ceiling like the 8s
    // above is far too short on a big repo (linux first paint alone is tens of seconds):
    // identity/refresh would run mid-first-walk and stall it for ~60s. This ceiling is only a stuck-guard.
    private static readonly TimeSpan GraphFirstPaintCeiling = TimeSpan.FromSeconds(120);

    // A large working tree's first status waits for the graph's crawl to warm up; this is only a
    // stuck-guard for a tab with no graph attached or a crawl that never reports.
    private static readonly TimeSpan StatusAfterGraphCeiling = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Default query for repository tabs. Must match <c>CommitGraphControl._query</c>'s default.
    /// Used by clone precache and inactive-tab background caching so the store matches the tab's first walk.
    /// </summary>
    internal static readonly GraphQuery TabGraphQuery = new()
    {
        RefScope = GraphRefScope.AllRefs,
        IncludeRemoteBranches = true,
    };

    public WorkspaceService(
        IServiceProvider services,
        IGitCliService cli,
        IRecentRepositoryService recents,
        IProfileService profiles,
        IRepoIdentityService identity)
    {
        _services = services;
        _contextServices = new WorkspaceScopedServices(services, this);
        _cli = cli;
        _recents = recents;
        _profiles = profiles;
        _identity = identity;
    }

    /// <summary>Sets <see cref="_foregroundInit"/> and wakes anything awaiting
    /// <see cref="_foregroundChanged"/> (the background pump). Caller must hold <see cref="_sync"/>.</summary>
    private void SetForegroundInit(RepositoryContext? context)
    {
        _foregroundInit = context;
        var prev = _foregroundChanged;
        _foregroundChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        prev.TrySetResult();
    }

    public event EventHandler<FocusedRepositoryChangedEventArgs>? FocusedRepositoryChanged;
    public event EventHandler<OpenRepositoriesChangedEventArgs>? OpenRepositoriesChanged;

    public IReadOnlyList<RepositoryContext> OpenRepositories
    {
        get { lock (_sync) return _open.ToList(); }
    }

    public RepositoryContext? Focused
    {
        get { lock (_sync) return _focused; }
    }

    public async Task CloseAsync(RepositoryContext context)
    {
        bool removed;
        bool wasFocused = false;
        RepositoryContext? nextFocus = null;
        lock (_sync)
        {
            removed = _open.Remove(context);
            if (removed)
            {
                // (Decisions table): closing a group's owner detaches its children rather than
                // closing them — no uncommitted work in a submodule tab is ever silently discarded.
                foreach (var t in _open)
                    if (ReferenceEquals(t.LinkOwner, context))
                        t.LinkOwner = null;
                _deferredInit.Remove(context);
                foreach (var item in _bgQueue.Where(x => ReferenceEquals(x.Context, context)))
                {
                    foreach (var a in item.Awaiters)
                        a.TrySetCanceled();
                }
                _bgQueue.RemoveAll(x => ReferenceEquals(x.Context, context));
                wasFocused = ReferenceEquals(_focused, context);
                if (wasFocused)
                {
                    _focused = null;
                    nextFocus = _open.LastOrDefault();
                }
            }
        }

        if (!removed)
        {
            await context.DisposeAsync().ConfigureAwait(false);
            return;
        }

        (_services.GetService(typeof(Historia.Services.Graph.IGraphLayoutService)) as Historia.Services.Graph.IGraphLayoutService)
            ?.Forget(context);

        // close the terminal session bound to this tab so its shell releases the
        // working-directory handle before the context is disposed. Keyed by the tab's own id,
        // so a second tab open on the same repository keeps its shell running.
        if (_services.GetService(typeof(Historia.Services.Terminal.ITerminalSessionService))
               is Historia.Services.Terminal.ITerminalSessionService terminals)
        {
            try { await terminals.CloseForTabAsync(context.Id).ConfigureAwait(false); }
            catch { /* teardown must not be blocked by a terminal close */ }
        }

        // The tab is already out of every collection, so the strip and the focus update first;
        // disposing waits for a running write to finish and nothing the user sees may wait on it.
        RaiseOpenChanged();

        if (wasFocused)
        {
            if (nextFocus is not null)
                FocusInternal(nextFocus);
            else
                FocusedRepositoryChanged?.Invoke(this, new FocusedRepositoryChangedEventArgs(context, null));
        }

        await context.DisposeAsync().ConfigureAwait(false);
    }

    public void Focus(RepositoryContext context) => FocusInternal(context);

    public void Move(RepositoryContext context, int newIndex)
    {
        bool moved;
        lock (_sync)
        {
            moved = context.LinkOwner is { } owner
                ? MoveLinkedChild(context, owner, newIndex)
                : MoveGroup(context, newIndex);
        }

        if (!moved)
            return;

        // Raise outside the lock. RaiseOpenChanged re-persists the session, so
        // the dragged order is what a later RestoreSessionAsync replays.
        RaiseOpenChanged();
    }

    /// <summary>Under <see cref="_sync"/>: a linked child can only reorder among its own
    /// siblings — <paramref name="newIndex"/> is clamped into its owner group's span
    /// (<c>[ownerStart + 1, ownerStart + groupCount - 1]</c>). Returns whether anything moved.</summary>
    private bool MoveLinkedChild(RepositoryContext context, RepositoryContext owner, int newIndex)
    {
        var (ownerStart, groupCount) = GroupSpan(owner);
        if (groupCount <= 1)
            return false; // no siblings — nothing legal to move to
        var from = _open.IndexOf(context);
        if (from < 0)
            return false;
        var clamped = Math.Clamp(newIndex, ownerStart + 1, ownerStart + groupCount - 1);
        if (clamped == from)
            return false;
        _open.RemoveAt(from);
        _open.Insert(Math.Clamp(clamped, 0, _open.Count), context);
        return true;
    }

    /// <summary>Under <see cref="_sync"/>: an owner or standalone tab's whole contiguous group
    /// moves as one block. The block is removed from the list, boundaries are collected from the
    /// remaining list, and the block is inserted at the boundary nearest <paramref name="newIndex"/>
    /// (snapping in the direction of travel on ties). This ensures groups stay contiguous and
    /// never split. Returns whether anything moved.</summary>
    private bool MoveGroup(RepositoryContext context, int newIndex)
    {
        var (start, count) = GroupSpan(context);
        if (start < 0 || count == 0)
            return false;

        var block = _open.GetRange(start, count);
        _open.RemoveRange(start, count);

        var boundaries = new List<int>();
        for (var i = 0; i < _open.Count; i++)
        {
            if (_open[i].LinkOwner is null)
                boundaries.Add(i);
        }
        boundaries.Add(_open.Count); // trailing sentinel: "move to the very end"

        var target = boundaries.MinBy(b =>
        {
            var dist = Math.Abs(b - newIndex);
            var tieBreaker = newIndex > start ? -b : b;
            return (dist, tieBreaker);
        });

        if (target == start)
        {
            _open.InsertRange(start, block);
            return false; // already at the nearest boundary — no-op
        }

        _open.InsertRange(Math.Clamp(target, 0, _open.Count), block);
        return true;
    }

    private void RaiseOpenChanged()
    {
        PersistSession();
        OpenRepositoriesChanged?.Invoke(this, new OpenRepositoriesChangedEventArgs(OpenRepositories));
    }

    /// <summary>Rejects a pasted clone URL that <see cref="CloneValidation.DescribeUrlProblem"/>
    /// flags — see its doc comment for the exact checks.</summary>
    private static void ValidateCloneUrl(string url)
    {
        var problem = CloneValidation.DescribeUrlProblem(url);
        if (problem is not null)
            throw new HistoriaGitException(GitErrorKind.Unknown, problem);
    }

    private static string NormalizeDir(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return path; }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    public async ValueTask DisposeAsync()
    {
        Historia.Diagnostics.PerfLog.Mark("WorkspaceService.DisposeAsync start");

        if (_sessionWriter is not null)
            await _sessionWriter.DisposeAsync().ConfigureAwait(false);
        Historia.Diagnostics.PerfLog.Mark("WorkspaceService.DisposeAsync session writer flushed");

        List<RepositoryContext> toDispose;
        lock (_sync)
        {
            toDispose = _open.ToList();
            _open.Clear();
            _focused = null;
        }

        // Concurrently: each context waits for its own running write, and one long write must not
        // use up the whole exit wait for the others.
        await Task.WhenAll(toDispose.Select(c => c.DisposeAsync().AsTask())).ConfigureAwait(false);
        Historia.Diagnostics.PerfLog.Mark($"WorkspaceService.DisposeAsync done ({toDispose.Count} repos)");
    }
}
