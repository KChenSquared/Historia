
namespace Historia.Services.Git;

/// <summary>
/// A Git operation failure already classified into a <see cref="GitErrorKind"/>, with
/// an optional verbatim detail string for a "Details" expander. Higher layers map
/// <see cref="Kind"/> to an actionable message; raw stderr is never surfaced directly.
/// </summary>
public sealed class HistoriaGitException : Exception
{
    public HistoriaGitException(GitErrorKind kind, string message, string? details = null)
        : this(kind, message, details, log: true)
    {
    }

    private HistoriaGitException(GitErrorKind kind, string message, string? details, bool log)
        : base(message)
    {
        Kind = kind;
        Details = details;
        if (!log)
            return;

        // every classified git failure lands in the developer log the moment it is
        // created, whether or not a caller turns it into a notification. No-op unless enabled.
        var summary = GitStderr.SummaryLine(details);
        var detail = summary.Length == 0 ? "" : " — " + summary;
        AppLog.Write(AppLogLevel.Warn, "git-error", $"{kind}: {message}{detail}");
    }

    public GitErrorKind Kind { get; }
    public string? Details { get; }

    /// <summary>
    /// set when a user-initiated fetch/push/pull failed on every transport while a
    /// non-<i>Local config</i> profile was the active selection. The app layer offers to switch
    /// the repository to <i>Local config</i> and retry once.
    /// </summary>
    public bool FallbackToLocalConfigAvailable { get; init; }

    /// <summary>The same failure with other Details (e.g. every transport attempt's text), not
    /// logged again — this one was logged, with its own summary line, when it was created.</summary>
    internal HistoriaGitException WithDetails(string? details, bool fallbackToLocalConfigAvailable) =>
        new(Kind, Message, details, log: false) { FallbackToLocalConfigAvailable = fallbackToLocalConfigAvailable };

    /// <summary>
    /// Builds the exception for a failed CLI call: a Historia-authored message
    /// (<c>"Push failed: couldn't reach the remote."</c>) and Details that lead with the
    /// server's own <c>remote:</c> messages, followed by the stderr with progress noise removed.
    /// </summary>
    public static HistoriaGitException FromCli(Models.GitCliResult result, string operation) =>
        new(result.ErrorKind, $"{operation} failed: {Describe(result.ErrorKind)}.", CliDetails(result));

    /// <summary>A short lower-case phrase for a failure kind, written to follow
    /// <c>"&lt;Operation&gt; failed: "</c>.</summary>
    public static string Describe(GitErrorKind kind) => kind switch
    {
        GitErrorKind.None => "git reported a failure without saying why",
        GitErrorKind.AuthenticationFailed => "the remote didn't accept your credentials",
        GitErrorKind.HostKeyVerificationFailed => "the server's SSH host key couldn't be verified",
        GitErrorKind.NetworkUnreachable => "couldn't reach the remote",
        GitErrorKind.RemoteRejected => "the remote has commits this branch doesn't",
        GitErrorKind.RemoteDeclined => "the remote declined it — its reason is in Details",
        GitErrorKind.TagConflict => "a local tag points to a different commit than the remote's",
        GitErrorKind.MergeConflict => "it stopped on merge conflicts",
        GitErrorKind.UncommittedChanges => "local changes are in the way",
        GitErrorKind.LockedIndex => "another git process is using the repository",
        GitErrorKind.NotAGitRepository => "the folder is not a git repository",
        GitErrorKind.RefNotFound => "the branch, tag or commit wasn't found",
        GitErrorKind.InvalidRefName => "the name isn't a valid git ref",
        GitErrorKind.DetachedHeadOperation => "it needs a checked-out branch",
        GitErrorKind.SigningToolMissing => "commit signing needs GPG, which isn't installed. Install Gpg4win or Git for Windows, or turn off signing for this profile",
        GitErrorKind.HookFailed => "a git hook stopped it",
        GitErrorKind.LfsMissing => "the repository needs Git LFS, which wasn't found",
        GitErrorKind.UnrelatedHistories => "the histories are unrelated",
        GitErrorKind.Cancelled => "it was cancelled",
        GitErrorKind.TimedOut => "git took too long and was stopped",
        GitErrorKind.UnsafeRepository => "git doesn't trust this repository's ownership",
        GitErrorKind.Unknown => "git reported an error — see Details",
        _ => "git reported an error — see Details",
    };

    private static string CliDetails(Models.GitCliResult result)
    {
        if (string.IsNullOrWhiteSpace(result.StandardError))
            return result.StandardOutput;

        var cleaned = GitStderr.Clean(result.StandardError);
        var remote = GitStderr.RemoteMessages(result.StandardError);
        return remote.Count == 0 ? cleaned : string.Join('\n', remote) + "\n\n" + cleaned;
    }
}

/// <summary>Shared by every error-notification call site so none of them has to remember the
/// HistoriaGitException special case itself.</summary>
public static class ExceptionNotificationExtensions
{
    /// <summary>The right "Details" text for an error toast: an <see cref="HistoriaGitException"/>'s
    /// verbatim git stderr when it has one, else the .NET stack trace as a last resort for
    /// anything unexpected. Using <c>ex.ToString()</c> directly here is the recurring bug — it
    /// silently throws away the actual git error message (e.g. "refusing to merge unrelated
    /// histories") behind an opaque stack trace.</summary>
    public static string? NotificationDetails(this Exception ex) =>
        ex is HistoriaGitException git ? git.Details : ex.ToString();

    /// <summary>The toast message for a failed <paramref name="what"/>: a
    /// <see cref="HistoriaGitException"/>'s message as-is, since it already names the operation,
    /// otherwise <c>"&lt;what&gt; failed: &lt;message&gt;"</c>.</summary>
    public static string NotificationMessage(this Exception ex, string what) =>
        ex is HistoriaGitException git ? git.Message : $"{what} failed: {ex.Message}";
}
