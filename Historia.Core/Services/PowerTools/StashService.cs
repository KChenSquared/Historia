namespace Historia.Services.PowerTools;

/// <inheritdoc cref="IStashService"/>
public sealed class StashService(IGitCliService cli) : IStashService
{
    public Task<IReadOnlyList<StashEntry>> ListAsync(RepositoryContext context, CancellationToken ct = default)
        => ListStashesAsync(context, ct);

    public async Task<bool> SaveAsync(RepositoryContext context, StashSaveOptions options, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        var args = new List<string> { "stash", "push" };
        if (options.IncludeUntracked) args.Add("-u");
        if (options.KeepIndex) args.Add("--keep-index");
        if (!string.IsNullOrWhiteSpace(options.Message))
        {
            args.Add("-m");
            args.Add(options.Message);
        }

        // `--` and pathspecs come AFTER the message; Git 2.35+.
        if (options.Paths is { Count: > 0 })
        {
            args.Add("--");
            args.AddRange(options.Paths);
        }

        // No wall clock: hooks and large tree updates run long, and a kill midway leaves a
        // half-updated tree and a stale index.lock.
        // Cancelling stops it only before git starts; once running it is never killed.
        var result = await context.WithWriteLockAsync(innerCt => cli.RunAsync(new GitCliRequest
        {
            Arguments = args,
            WorkingDirectory = context.RepositoryPath,
            Timeout = Timeout.InfiniteTimeSpan,
        }, innerCt), ct)
            .ConfigureAwait(false);

        if (result.StandardOutput.Contains("No local changes to save", StringComparison.OrdinalIgnoreCase)
            || result.StandardError.Contains("No local changes to save", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, "Stash");

        await context.State.RefreshAllAsync(context.Lifetime).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> StashStagedAsync(RepositoryContext context, string? message, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        var args = new List<string> { "stash", "push", "--staged" };
        if (!string.IsNullOrWhiteSpace(message))
        {
            args.Add("-m");
            args.Add(message);
        }

        var result = await context.WithWriteLockAsync(innerCt => cli.RunAsync(new GitCliRequest
        {
            Arguments = args,
            WorkingDirectory = context.RepositoryPath,
            Timeout = Timeout.InfiniteTimeSpan,
        }, innerCt), ct)
            .ConfigureAwait(false);

        // Nothing staged → git prints one of these and exits non-zero; treat as a no-op.
        var combined = result.StandardOutput + result.StandardError;
        if (combined.Contains("No staged changes", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("No local changes to save", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, "Stash staged");

        await context.State.RefreshAllAsync(context.Lifetime).ConfigureAwait(false);
        return true;
    }

    public Task ApplyAndDropAsync(RepositoryContext context, GitObjectId commitId, CancellationToken ct = default)
        => MutateAsync(context, commitId, "pop", extra: null, ct);

    public Task ApplyAsync(RepositoryContext context, GitObjectId commitId, bool restoreIndex, CancellationToken ct = default)
        => MutateAsync(context, commitId, "apply", extra: restoreIndex ? "--index" : null, ct);

    public Task DropAsync(RepositoryContext context, GitObjectId commitId, CancellationToken ct = default)
        => MutateAsync(context, commitId, "drop", extra: null, ct);

    public Task BranchFromAsync(RepositoryContext context, GitObjectId commitId, string branchName, CancellationToken ct = default)
        => MutateAsync(context, commitId, "branch", extra: branchName, ct, branchNameFirst: true);

    public async Task ClearAsync(RepositoryContext context, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        var result = await context.WithWriteLockAsync(innerCt => cli.RunAsync(new GitCliRequest
        {
            Arguments = ["stash", "clear"],
            WorkingDirectory = context.RepositoryPath,
        }, innerCt), ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, "Stash clear");
        await context.State.RefreshAllAsync(context.Lifetime).ConfigureAwait(false);
    }

    private async Task MutateAsync(RepositoryContext context, GitObjectId commitId, string verb, string? extra,
        CancellationToken ct, bool branchNameFirst = false)
    {
        using var _ = context.BeginSelfOperation();

        // protocol: re-list, find the entry by commit id, act on its CURRENT index.
        var current = await ListStashesAsync(context, ct).ConfigureAwait(false);
        var entry = current.FirstOrDefault(e => e.CommitId == commitId)
                    ?? throw new HistoriaGitException(GitErrorKind.RefNotFound,
                        "The stash list changed — refresh the stash panel and try again.");

        var stashRef = $"stash@{{{entry.Index}}}";
        var args = new List<string> { "stash", verb };
        if (branchNameFirst && extra is not null)
        {
            args.Add(extra);       // branch name
            args.Add(stashRef);
        }
        else
        {
            if (extra is not null) args.Add(extra);
            args.Add(stashRef);
        }

        var result = await context.WithWriteLockAsync(innerCt => cli.RunAsync(new GitCliRequest
        {
            Arguments = args,
            WorkingDirectory = context.RepositoryPath,
            Timeout = Timeout.InfiniteTimeSpan,
        }, innerCt), ct)
            .ConfigureAwait(false);

        // A conflicting pop/apply leaves the working tree with markers and the entry intact.
        if (!result.IsSuccess && result.ErrorKind != GitErrorKind.MergeConflict)
            throw HistoriaGitException.FromCli(result, $"Stash {verb}");

        await context.State.RefreshAllAsync(context.Lifetime).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<StashEntry>> ListStashesAsync(RepositoryContext context, CancellationToken ct)
    {
        // When the ref snapshot is available and has no refs/stash, there are no stashes.
        RefSnapshot? snapshot;
        try
        {
            snapshot = await context.RefSnapshots.GetAsync(ct).ConfigureAwait(false);
        }
        catch (HistoriaGitException)
        {
            snapshot = null;
        }

        if (snapshot is not null && snapshot.Find("refs/stash") is null)
            return [];

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments =
            [
                "log",
                "-g",
                "--no-show-signature",
                "--no-renames",
                "--diff-merges=first-parent",
                "--name-only",
                "-z",
                "--format=__HISTORIA_STASH__%x00%H%x00%P%x00%an%x00%ae%x00%aI%x00%gs%x00",
                "refs/stash",
            ],
            WorkingDirectory = context.RepositoryPath,
            RawStandardOutput = true,
        }, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            // When the ref disappears after the snapshot or the snapshot could not be read,
            // verify via CLI probe: only a missing refs/stash means "no stashes".
            if (result.ErrorKind != GitErrorKind.Cancelled && !await StashRefExistsAsync(context.RepositoryPath, ct).ConfigureAwait(false))
                return [];
            throw HistoriaGitException.FromCli(result, "List stashes");
        }

        return ParseStashLog(result.StandardOutput);
    }

    /// <summary>True unless <c>rev-parse</c> reports refs/stash missing (exit 1 under
    /// <c>--verify -q</c>); an inconclusive probe counts as present so the caller surfaces the
    /// original failure.</summary>
    private async Task<bool> StashRefExistsAsync(string repositoryPath, CancellationToken ct)
    {
        var probe = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["rev-parse", "--verify", "-q", "refs/stash"],
            WorkingDirectory = repositoryPath,
        }, ct).ConfigureAwait(false);
        return probe.ExitCode != 1;
    }

    internal static IReadOnlyList<StashEntry> ParseStashLog(string stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return [];

        var parts = stdout.Split('\0');
        var entries = new List<StashEntry>();
        var index = 0;
        var i = 0;

        while (i < parts.Length)
        {
            if (parts[i] != "__HISTORIA_STASH__")
            {
                i++;
                continue;
            }

            if (i + 6 >= parts.Length)
                break;

            var commitSha = parts[i + 1].Trim();
            if (!GitObjectId.TryParse(commitSha, out var commitId))
            {
                i++;
                continue;
            }

            var parentStr = parts[i + 2].Trim();
            var parentCount = string.IsNullOrEmpty(parentStr)
                ? 0
                : parentStr.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var includesUntracked = parentCount == 3;

            var authorName = parts[i + 3];
            var authorEmail = parts[i + 4];
            var createdAt = DateTimeOffset.TryParse(parts[i + 5], CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
                ? dto
                : DateTimeOffset.UtcNow;
            var message = parts[i + 6];

            i += 7;
            if (i < parts.Length && parts[i].Length == 0)
                i++;

            var fileCount = 0;
            while (i < parts.Length && parts[i] != "__HISTORIA_STASH__")
            {
                var p = parts[i].TrimStart('\r', '\n');
                if (p.Length > 0)
                    fileCount++;
                i++;
            }

            entries.Add(new StashEntry
            {
                Index = index++,
                CommitId = commitId,
                Message = message,
                AuthorName = authorName,
                AuthorEmail = authorEmail,
                CreatedAt = createdAt,
                BranchName = ExtractBranch(message),
                IncludesUntracked = includesUntracked,
                FileCount = fileCount,
            });
        }

        return entries;
    }

    private static string ExtractBranch(string message)
    {
        // "WIP on main: 1234abcd Subject" / "On main: message"
        var onIdx = message.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        if (onIdx < 0)
            onIdx = message.StartsWith("On ", StringComparison.OrdinalIgnoreCase) ? 0 : -1;
        var start = onIdx < 0 ? -1 : message.IndexOf("on ", onIdx, StringComparison.OrdinalIgnoreCase) + 3;
        if (start < 3)
            return "";
        var colon = message.IndexOf(':', start);
        return colon > start ? message[start..colon].Trim() : "";
    }
}
