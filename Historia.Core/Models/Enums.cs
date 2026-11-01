namespace Historia.Models;

/// <summary>What the user chose in Settings &gt; General.</summary>
public enum ThemeMode
{
    Dark,
    Light,
    System,
}

/// <summary>
/// What is actually rendered, after resolving <see cref="ThemeMode.System"/> against the OS
/// and applying the High Contrast override. Deliberately a different type from
/// <see cref="ThemeMode"/>. Resolution order: HighContrast &gt; System-resolved OS
/// theme &gt; explicit Dark/Light.
/// </summary>
public enum EffectiveTheme
{
    Dark,
    Light,
    HighContrast,
}

public enum DiffViewMode
{
    Unified,
    SideBySide,
}

/// <summary>
/// How wide each repository tab in the app band is.
/// <see cref="Dynamic"/> — the default — sizes every tab to its own label, so a short repo
/// name gets a short tab; a long name grows the tab only up to a character cap, then
/// ellipsises. <see cref="Fixed"/> gives every tab the same <c>FixedTabWidth</c> pixels.
/// </summary>
public enum TabWidthMode
{
    Dynamic,
    Fixed,
}

public enum WhitespaceMode
{
    Show,
    IgnoreTrailing,
    IgnoreAll,
}

/// <summary>How the selected-commit detail panel lists changed files.</summary>
public enum CommitFilesView
{
    /// <summary>Flat list of full paths (the default).</summary>
    Path,

    /// <summary>Collapsible folder tree.</summary>
    Tree,
}

/// <summary>How the custom wheel handlers (graph body, tab strip) read scroll input.
/// <see cref="Auto"/> follows the OS "natural / inverted" setting via
/// <c>IScrollDirectionProbe</c> (macOS &amp; Linux; Windows is always standard).</summary>
public enum WheelScrollDirection
{
    Auto,
    Standard,
    Inverted,
}

/// <summary>Scope for writing git configuration values.</summary>
public enum GitConfigScope
{
    /// <summary>Writes to the repository's local <c>.git/config</c>.</summary>
    Local,

    /// <summary>Writes to the user's global <c>~/.gitconfig</c>.</summary>
    Global,

    /// <summary>Writes to the system-wide git config (rarely used).</summary>
    System,
}

/// <summary>Result of a profile deletion operation.</summary>
public enum ProfileDeleteResult
{
    /// <summary>A non-global profile was removed; the default profile was unaffected.</summary>
    Deleted,

    /// <summary>A non-global profile that was the current default was removed;
    /// <see cref="AppSettings.DefaultProfileId"/> is now null (global is the automatic default).</summary>
    DeletedClearedDefault,

    /// <summary>The reserved global profile cannot be deleted; nothing changed.</summary>
    CannotDeleteGlobal,

    NotFound,
}

/// <summary>
/// Closed set of Git CLI failure classifications. Raw stderr is never surfaced
/// unmodified; every kind maps to a user-facing message and, where applicable, a remedy.
/// <see cref="Unknown"/> is deliberately part of the set — implementing code must not invent
/// silent fallbacks for unrecognized failures.
/// </summary>
public enum GitErrorKind
{
    None,
    AuthenticationFailed,
    HostKeyVerificationFailed,
    NetworkUnreachable,
    RemoteRejected,

    /// <summary>The remote was reached and refused the update by its own policy (a server hook,
    /// a protected branch, a size limit). Integrating the remote's commits or force-pushing does
    /// not help; the server's reason is the remedy.</summary>
    RemoteDeclined,

    /// <summary>A fetch refused to move a local tag the remote now points elsewhere ("would
    /// clobber existing tag"). Git treats tags as fixed, so neither fetch nor pull updates it
    /// until the user replaces the local tag with the remote's.</summary>
    TagConflict,

    MergeConflict,
    UncommittedChanges,
    LockedIndex,
    NotAGitRepository,
    RefNotFound,
    InvalidRefName,
    DetachedHeadOperation,
    HookFailed,
    LfsMissing,
    UnrelatedHistories,
    Cancelled,

    /// <summary>The invocation's own <see cref="Historia.Models.GitCliRequest.Timeout"/> elapsed
    /// before git exited — distinct from <see cref="Cancelled"/> (the caller's token tripped) so
    /// a stalled credential helper or a hung network op is never reported as an opaque
    /// <see cref="Unknown"/> failure with no stderr to classify.</summary>
    TimedOut,

    /// <summary>
    /// Git refused the repository due to safe.directory ownership checks ("detected dubious ownership").
    /// Surfaces an actionable message with the command to allow the repository.
    /// </summary>
    UnsafeRepository,

    /// <summary>
    /// GPG was not found on PATH when creating a signed commit.
    /// </summary>
    SigningToolMissing,

    /// <summary>
    /// <c>ssh-keygen</c> was not found on PATH when creating an SSH-signed commit. The bundled Git
    /// ships an ssh client but not <c>ssh-keygen</c>.
    /// </summary>
    SshSigningToolMissing,

    Unknown,
}

/// <summary>Kind of change for a working-tree file entry.</summary>
public enum FileChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Untracked,
    Ignored,
    Conflicted,
}

/// <summary>
/// In-progress multi-step Git operation, derived from on-disk markers on every status
/// refresh.
/// </summary>
public enum OperationState
{
    None,
    Merging,
    Rebasing,
    CherryPicking,
    Reverting,
    Bisecting,
}

/// <summary>Kind of change for a diffed file.</summary>
public enum DiffKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
}

/// <summary>Kind of decoration (ref pointer) attached to a commit.</summary>
public enum DecorationType
{
    LocalBranch,
    RemoteBranch,
    Tag,
    Head,
}

/// <summary><c>git reset</c> mode (context menu). <see cref="Hard"/> discards working-tree changes.</summary>
public enum GitResetMode
{
    Soft,
    Mixed,
    Hard,
}
