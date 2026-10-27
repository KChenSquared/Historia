
namespace Historia.Services.Workspace;

public sealed record FocusedRepositoryChangedEventArgs(RepositoryContext? Old, RepositoryContext? New);
public sealed record OpenRepositoriesChangedEventArgs(IReadOnlyList<RepositoryContext> OpenRepositories);

public sealed record CloneOptions
{
    public bool Recursive { get; init; }
    public int? Depth { get; init; }

    /// <summary>Pass <c>--sparse</c> so the working tree starts as a cone-mode sparse checkout.</summary>
    public bool Sparse { get; init; }
}

/// <summary>
/// Owns the set of open repository tabs. Singleton. Each tab is fully independent:
/// its own <c>Repository</c>, watcher, cancellation scope, resolved profile, and selection
/// state. Focusing a tab republishes its cached state; it never re-reads from disk.
/// </summary>
public interface IWorkspaceService
{
    IReadOnlyList<RepositoryContext> OpenRepositories { get; }
    RepositoryContext? Focused { get; }

    /// <summary>Opens an existing repository discovered from <paramref name="path"/>.</summary>
    Task<RepositoryContext> OpenAsync(string path, CancellationToken ct = default);

    /// <summary>Initializes a new repository and opens it.</summary>
    Task<RepositoryContext> InitAsync(string path, string? initialBranch, CancellationToken ct = default);

    /// <summary>Clones <paramref name="url"/> into <paramref name="targetDirectory"/>, then opens it.</summary>
    Task<RepositoryContext> CloneAsync(
        string url,
        string targetDirectory,
        CloneOptions options,
        IProgress<GitProgress>? progress,
        CancellationToken ct = default);

    /// <summary>Opens <paramref name="submodule"/> (relative to <paramref name="owner"/>'s
    /// working directory) as a tab linked to <paramref name="owner"/>'s root group owner — inserted
    /// immediately after the root owner's current group (its own tab plus any already-
    /// linked children), focused once opened. Groups are flat: if <paramref name="owner"/> is itself
    /// linked, the new tab links to <paramref name="owner"/>'s root owner. If the submodule's on-disk
    /// <c>.git</c> entry is missing (uninitialized submodule), throws
    /// <see cref="HistoriaGitException"/> with <see cref="GitErrorKind.NotAGitRepository"/>.
    /// If the submodule's working copy is already open under a standalone tab that owns no children,
    /// that tab is adopted into the group and moved to the end of the group. If it is already linked
    /// to an owner or owns children, it is focused without relinking.</summary>
    Task<RepositoryContext> OpenSubmoduleAsync(RepositoryContext owner, SubmoduleInfo submodule, CancellationToken ct = default);

    Task CloseAsync(RepositoryContext context);
    void Focus(RepositoryContext context);

    /// <summary>force a deferred session-restore tab through its initialize path now
    /// rather than on first focus. No-op if it is already initializing or fully loaded.</summary>
    void EnsureInitialized(RepositoryContext context);

    /// <summary>
    /// Enqueues a refresh for a background tab, draining on the serial background pump behind the
    /// focused tab's first full load in app-band tab order. If <paramref name="context"/>
    /// is currently focused, it refreshes itself instead, paced by the cost of its last status
    /// (see <see cref="IsAppActive"/>); explicit user operations call the state service directly
    /// and are never paced.
    /// </summary>
    Task RequestBackgroundRefreshAsync(RepositoryContext context, bool full = false, CancellationToken ct = default)
        => full ? context.State.RefreshAllAsync(ct) : context.State.RefreshStatusAsync(ct);

    /// <summary>True while the Historia window is the active window. Watcher-driven refreshes of the
    /// focused tab are paced further apart while it is not.</summary>
    bool IsAppActive => true;

    /// <summary>Records whether the Historia window is active. Activation refreshes the focused
    /// tab's status at once, regardless of pacing; a tab whose watcher is degraded gets a full
    /// refresh, since that watcher misses file events. Safe to call from any thread.</summary>
    void SetAppActive(bool active)
    {
    }

    /// <summary>
    /// Moves an open tab so its first tab ends at <paramref name="newIndex"/> in the strip
    /// (drag-and-drop reorder). No-op when the context is not open or is already there.
    /// Raises <see cref="OpenRepositoriesChanged"/> and re-persists the session so the order survives a
    /// restart. Focus is unaffected.
    /// <para>group-aware: when <paramref name="context"/> is a group owner (or a standalone
    /// tab), its whole contiguous group (itself plus any linked children) moves together as one
    /// block, snapping to the nearest group boundary in the direction of travel — it never
    /// splits another group. When <paramref name="context"/> is itself a linked child,
    /// <paramref name="newIndex"/> is clamped to its own group's span, so it can only reorder
    /// among its own siblings.</para>
    /// </summary>
    void Move(RepositoryContext context, int newIndex);

    /// <summary>Reopens the repositories that were open when the app last closed (opt-in).</summary>
    Task RestoreSessionAsync(CancellationToken ct = default);

    event EventHandler<FocusedRepositoryChangedEventArgs>? FocusedRepositoryChanged;
    event EventHandler<OpenRepositoriesChangedEventArgs>? OpenRepositoriesChanged;
}
