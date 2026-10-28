namespace Historia.Services.AutoFetch;

/// <summary>Per-tab position in the current <c>[AutoFetch]</c> cycle, for the developer-only
/// tab colour bars. Red / yellow / green.</summary>
public enum AutoFetchTabState
{
    /// <summary>The <c>[AutoFetch]</c> loop has not started yet (start-condition wait,
    /// or the feature is disabled). Red.</summary>
    NotStarted,

    /// <summary>The loop is running but this repo has not been fetched in the current cycle.
    /// Yellow.</summary>
    PendingThisCycle,

    /// <summary>The loop is running and this repo has been fetched in the current cycle. Green.</summary>
    FetchedThisCycle,
}

/// <summary>
/// App-supplied bridge the Core <see cref="IAutoFetchService"/> uses to (a) know when a
/// repository tab is <b>fully loaded</b> — branches panel, full graph paint, changes panel
/// — and (b) actually run a background <c>git fetch</c>. Kept out of Core so the
/// schedule logic can be tested with no real repository, watcher, or git process.
/// </summary>
public interface IAutoFetchHost
{
    /// <summary>Completes once <paramref name="context"/> has fully loaded. Must never fault on
    /// its own; a headless context completes it immediately.</summary>
    Task WhenFullyLoadedAsync(RepositoryContext context, CancellationToken ct);

    /// <summary>Runs one background fetch on <paramref name="context"/> (honours SSH
    /// Only, never prompts, quiet on failure). Network only: the caller holds the repository's
    /// network gate for exactly this call. Exceptions other than cancellation are swallowed
    /// by the caller.</summary>
    Task FetchAsync(RepositoryContext context, CancellationToken ct);

    /// <summary>Lands the refs a <see cref="FetchAsync"/> brought in — a successful one, or one that
    /// failed only on a refused tag — into the UI state.
    /// Runs after the network gate is released, so it must not assume the gate is held.</summary>
    Task RefreshAfterFetchAsync(RepositoryContext context, CancellationToken ct);
}

/// <summary>
/// keeps every open repository's remote-tracking refs warm without a manual Fetch. An
/// app-lifetime singleton. Once the app has fully loaded its repo(s) it runs an infinite
/// <c>[AutoFetch]</c> loop (5&#160;min idle, then trims logs older than 48 hours, then one fetch
/// per open repo 1&#160;min apart, then restart). A successful fetch also refreshes that repo's
/// commit-graph and, for an inactive tab, queues its headless disk cache. Gated by
/// <see cref="Historia.Models.AppSettings.AutoFetchEnabled"/> — turning it off stops the loop at
/// once; turning it back on re-enters at the "app start with open repos" path.
/// </summary>
public interface IAutoFetchService : IAsyncDisposable
{
    /// <summary>Mirrors <see cref="Historia.Models.AppSettings.AutoFetchEnabled"/>.</summary>
    bool IsEnabled { get; }

    /// <summary>Idempotent. Begins the start-condition wait, then the loop. A no-op while
    /// disabled or already running.</summary>
    void Start();

    /// <summary>Cancels the loop.</summary>
    void Stop();

    /// <summary>This repo's state in the current <c>[AutoFetch]</c> cycle — for the developer-only
    /// tab colour bars. Cheap; safe to call from the UI thread.</summary>
    AutoFetchTabState GetTabState(RepositoryContext context);

    /// <summary>Raised (on an arbitrary thread) whenever a <see cref="GetTabState"/> result may
    /// have changed — loop start/stop, a cycle restart, or a repo fetched this cycle.</summary>
    event EventHandler? TabStatesChanged;
}
