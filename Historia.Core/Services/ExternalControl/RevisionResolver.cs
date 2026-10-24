namespace Historia.Services.ExternalControl;

/// <summary>The git reads external control needs to check a request: a revision's commit id, a
/// folder's work tree, a commit's subject and the staged paths. The only external-control code that
/// starts a git process, and it only reads.</summary>
public static class RevisionResolver
{
    /// <summary>The commit <paramref name="rev"/> names, or null when it is invalid, unknown, or
    /// names a non-commit object.</summary>
    public static async Task<GitObjectId?> ResolveCommitAsync(
        IGitCliService cli, string workingDir, string rev, CancellationToken ct = default)
    {
        if (!ControlInputValidation.TryValidateRevision(rev, out _))
            return null;

        var result = await cli.RunAsync(new GitCliRequest
        {
            WorkingDirectory = workingDir,
            Arguments = ["rev-parse", "--verify", "-q", "--end-of-options", rev + "^{commit}"],
        }, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
            return null;

        return GitObjectId.TryParse(result.StandardOutput.Trim(), out var id) ? id : null;
    }

    /// <summary>
    /// Checks and resolves the revisions a navigation names: <paramref name="rev"/> for show, diff and
    /// compare, and <paramref name="otherRev"/> as well for compare; other verbs name none. On failure
    /// <c>Error</c> says which revision is wrong, for a <c>rev-not-found</c> reply.
    /// </summary>
    public static async Task<(GitObjectId? Rev, GitObjectId? OtherRev, string? Error)> ResolveTargetRevisionsAsync(
        IGitCliService cli, string repoRoot, ControlVerb verb, string? rev, string? otherRev, CancellationToken ct = default)
    {
        if (verb is not (ControlVerb.Show or ControlVerb.Diff or ControlVerb.Compare))
            return (null, null, null);

        var first = await ResolveOneAsync(rev).ConfigureAwait(false);
        if (first.Error is not null || verb != ControlVerb.Compare)
            return (first.Id, null, first.Error);

        var second = await ResolveOneAsync(otherRev).ConfigureAwait(false);
        return (first.Id, second.Id, second.Error);

        async Task<(GitObjectId? Id, string? Error)> ResolveOneAsync(string? text)
        {
            if (!ControlInputValidation.TryValidateRevision(text, out var error))
                return (null, error);
            var id = await ResolveCommitAsync(cli, repoRoot, text!, ct).ConfigureAwait(false);
            return id is null ? (null, $"'{text}' is not a commit in this repository.") : (id, null);
        }
    }

    /// <summary>The full native path of the work tree containing <paramref name="dir"/>, or null when
    /// it is not inside one (or does not exist).</summary>
    public static async Task<string?> ReadTopLevelAsync(IGitCliService cli, string dir, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return null;

        var result = await cli.RunAsync(new GitCliRequest
        {
            WorkingDirectory = dir,
            Arguments = ["rev-parse", "--show-toplevel"],
        }, ct).ConfigureAwait(false);

        var top = result.StandardOutput.TrimEnd('\r', '\n');
        if (!result.IsSuccess || top.Length == 0)
            return null;

        try
        {
            return Path.GetFullPath(top);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>The subject line of <paramref name="commit"/>, or null when git cannot read it.</summary>
    public static async Task<string?> ReadSubjectAsync(
        IGitCliService cli, string repoRoot, GitObjectId commit, CancellationToken ct = default)
    {
        var result = await cli.RunAsync(new GitCliRequest
        {
            WorkingDirectory = repoRoot,
            Arguments = ["log", "-1", "--format=%s", "--end-of-options", commit.ToString()],
        }, ct).ConfigureAwait(false);

        return result.IsSuccess ? result.StandardOutput.TrimEnd('\r', '\n') : null;
    }

    /// <summary>Every staged path, repo-relative with forward slashes, in git's order: empty when
    /// nothing is staged, null when git fails. Works on a branch with no commits yet.</summary>
    public static async Task<IReadOnlyList<string>?> ReadStagedPathsAsync(
        IGitCliService cli, string repoRoot, CancellationToken ct = default)
    {
        // -z writes each path verbatim and NUL-terminated, so spaces and non-ASCII names are never quoted.
        var result = await cli.RunAsync(new GitCliRequest
        {
            WorkingDirectory = repoRoot,
            Arguments = ["diff", "--cached", "--name-only", "-z"],
            RawStandardOutput = true,
        }, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
            return null;

        return result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
