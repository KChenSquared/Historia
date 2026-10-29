
namespace Historia.Services.Git;

/// <summary>
/// Classifies a failed Git CLI invocation into a <see cref="GitErrorKind"/> by exit code plus
/// stderr pattern matching. <c>LC_ALL=C</c> guarantees English message text.
/// Unrecognized failures return <see cref="GitErrorKind.Unknown"/> — never a silent fallback.
/// </summary>
public static class GitErrorClassifier
{
    /// <summary>
    /// The server was reached and refused the update by its own policy — a pre-receive hook, a
    /// protected branch, a size limit. git prints these refs as <c>! [remote rejected]</c>, with
    /// the server's reason in parentheses. Checked first, so a multi-ref push where one ref was
    /// declined and another was merely behind never offers a force push.
    /// </summary>
    private static readonly string[] DeclinedNeedles =
    [
        "! [remote rejected]",
        "pre-receive hook declined",
        "protected branch",
        "push declined",
    ];

    /// <summary>
    /// git's own client-side refusal: the remote has commits the pushed branch does not
    /// (<c>! [rejected] … (non-fast-forward)</c> / <c>(fetch first)</c>). These win over the
    /// ordered <see cref="Rules"/> below because a rejected push <i>also</i> prints the generic
    /// <c>error: failed to push some refs</c> line, and sometimes a trailing <c>fatal: unable to
    /// access …</c> — which would otherwise match <see cref="GitErrorKind.NetworkUnreachable"/>
    /// first and hide the real, recoverable cause. The generic push-failure lines alone
    /// (<c>failed to push some refs</c>, <c>the remote end hung up</c>) are not evidence of
    /// either and fall through to <see cref="GitErrorKind.Unknown"/>.
    /// </summary>
    private static readonly string[] NonFastForwardNeedles =
    [
        "! [rejected]",
        "updates were rejected",
        "non-fast-forward",
        "(fetch first)",
        "tip of your current branch is behind",
    ];

    /// <summary>A fetch refusing to move an existing tag prints it as <c>! [rejected] … (would
    /// clobber existing tag)</c>; checked before <see cref="NonFastForwardNeedles"/>, which would
    /// otherwise read the <c>! [rejected]</c> as a non-fast-forward branch.</summary>
    private const string TagClobberNeedle = "(would clobber existing tag)";

    private static readonly (GitErrorKind Kind, string[] Needles)[] Rules =
    [
        (GitErrorKind.HostKeyVerificationFailed, ["host key verification failed"]),
        (GitErrorKind.AuthenticationFailed, [
            "permission denied (publickey)",
            "authentication failed",
            "could not read username",
            "could not read password",
            "invalid username or password",
            "http basic: access denied",
            "error: 401",
            "error: 403",
            "remote: invalid credentials",
        ]),
        (GitErrorKind.NetworkUnreachable, [
            "could not resolve host",
            "connection refused",
            "connection timed out",
            "network is unreachable",
            "failed to connect to",
            "unable to access",
            "operation timed out",
            "temporary failure in name resolution",
        ]),
        (GitErrorKind.MergeConflict, [
            "automatic merge failed",
            "conflict (",
            "needs merge",
            "you have unmerged paths",
            "fix conflicts and then commit",
        ]),
        (GitErrorKind.UncommittedChanges, [
            "would be overwritten by",
            "please commit your changes or stash them",
            "cannot pull with rebase: you have unstaged changes",
            "your local changes to the following files would be overwritten",
        ]),
        // "unable to create" is deliberately not a needle here: git also uses that exact phrase
        // for ordinary filesystem failures unrelated to a lock — e.g. "unable to create file
        // src/x.cs: Permission denied" during a checkout with a build holding the file open, or
        // into a directory the user cannot write. Matching it would send an unrelated permission
        // error to the "another git process is running, check for a stale index.lock" message. index.lock and
        // the process-running message are both unambiguous on their own; keep only those.
        (GitErrorKind.LockedIndex, ["index.lock", "another git process seems to be running"]),
        (GitErrorKind.NotAGitRepository, ["not a git repository"]),
        (GitErrorKind.UnsafeRepository, ["detected dubious ownership"]),
        (GitErrorKind.LfsMissing, ["git-lfs", "this repository is configured for git lfs but"]),
        (GitErrorKind.UnrelatedHistories, ["refusing to merge unrelated histories"]),
        (GitErrorKind.DetachedHeadOperation, ["you are not currently on a branch", "detached head"]),
        (GitErrorKind.SigningToolMissing, ["cannot run gpg", "gpg failed to sign the data"]),
        (GitErrorKind.HookFailed, ["hook declined", "hook failed", "cannot spawn hook", "pre-commit hook", "commit-msg hook"]),
        (GitErrorKind.InvalidRefName, ["is not a valid ref name", "cannot lock ref", "not a valid object name", "not a valid ref"]),
        (GitErrorKind.RefNotFound, [
            "unknown revision or path",
            "ambiguous argument",
            "couldn't find remote ref",
            "no such ref",
            "pathspec",
            "bad object",
            "remote ref does not exist",
        ]),
    ];

    /// <summary>
    /// Formats an actionable message for <see cref="GitErrorKind.UnsafeRepository"/> naming the path
    /// and the exact command the user can run to allow it.
    /// </summary>
    public static string FormatUnsafeRepositoryMessage(string path) =>
        $"Repository at '{path}' is not owned by the current user. To add an exception, run: git config --global --add safe.directory \"{path}\"";

    /// <summary>The local tag names a fetch refused to move, from its
    /// <c> ! [rejected]  v1 -&gt; v1  (would clobber existing tag)</c> lines, without duplicates.</summary>
    public static IReadOnlyList<string> ClobberedTags(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr))
            return [];

        var tags = new List<string>();
        foreach (var raw in stderr.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (!line.StartsWith("! [rejected]", StringComparison.Ordinal)
                || !line.EndsWith(TagClobberNeedle, StringComparison.OrdinalIgnoreCase))
                continue;

            var arrow = line.IndexOf("->", StringComparison.Ordinal);
            if (arrow < 0)
                continue;
            var name = line[(arrow + 2)..^TagClobberNeedle.Length].Trim();
            if (name.Length > 0 && !tags.Contains(name, StringComparer.Ordinal))
                tags.Add(name);
        }
        return tags;
    }

    public static GitErrorKind Classify(int exitCode, string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return exitCode == 0 ? GitErrorKind.None : GitErrorKind.Unknown;

        var haystack = stderr.ToLowerInvariant();

        foreach (var needle in DeclinedNeedles)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal))
                return GitErrorKind.RemoteDeclined;
        }

        if (haystack.Contains(TagClobberNeedle, StringComparison.Ordinal))
            return GitErrorKind.TagConflict;

        foreach (var needle in NonFastForwardNeedles)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal))
                return GitErrorKind.RemoteRejected;
        }

        foreach (var (kind, needles) in Rules)
        {
            foreach (var needle in needles)
            {
                if (haystack.Contains(needle, StringComparison.Ordinal))
                    return kind;
            }
        }

        return GitErrorKind.Unknown;
    }
}
