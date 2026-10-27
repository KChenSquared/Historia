namespace Historia.Services.Git;

public sealed partial class GitGateway
{
    private static readonly IReadOnlyDictionary<string, string> LiteralPathspecEnv =
        new Dictionary<string, string> { ["GIT_LITERAL_PATHSPECS"] = "1" };

    private static readonly TimeSpan[] LockedIndexRetryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(750),
        TimeSpan.FromMilliseconds(1000),
    ];

    public async Task StageAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0)
            return;

        using var _ = context.BeginSelfOperation();

        await context.WithWriteLockAsync(async innerCt =>
        {
            var validPaths = await FilterValidStagePathsAsync(paths, innerCt).ConfigureAwait(false);
            if (validPaths.Count == 0)
                return;

            var normalizedPaths = validPaths.Select(p => p.Replace('\\', '/')).ToList();
            normalizedPaths = await StageCaseTwinsExactlyAsync(normalizedPaths, innerCt).ConfigureAwait(false);
            if (normalizedPaths.Count == 0)
                return;

            var stdin = string.Join('\0', normalizedPaths) + '\0';

            var result = await RunIndexWriterAsync(new GitCliRequest
            {
                Arguments = ["add", "-A", "--pathspec-from-file=-", "--pathspec-file-nul"],
                WorkingDirectory = context.RepositoryPath,
                StandardInput = stdin,
                ExtraEnvironment = LiteralPathspecEnv,
                // No wall clock: a writer killed mid-way leaves index.lock behind.
                Timeout = Timeout.InfiniteTimeSpan,
            }, innerCt).ConfigureAwait(false);

            if (!result.IsSuccess && !(result.ExitCode == 1 && IsIgnoredAdviceOnly(result.StandardError)))
                throw HistoriaGitException.FromCli(result, "Stage");
        }, ct).ConfigureAwait(false);
    }

    public async Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0)
            return;

        using var _ = context.BeginSelfOperation();

        await context.WithWriteLockAsync(async innerCt =>
        {
            var headCheck = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["rev-parse", "--verify", "-q", "HEAD"],
                WorkingDirectory = context.RepositoryPath,
            }, innerCt).ConfigureAwait(false);

            if (!headCheck.IsSuccess && !IsUnbornHeadProbe(headCheck))
                throw HistoriaGitException.FromCli(headCheck, "Unstage");

            var normalizedPaths = paths.Select(p => p.Replace('\\', '/')).ToList();
            var stdin = string.Join('\0', normalizedPaths) + '\0';

            if (!headCheck.IsSuccess)
            {
                var result = await RunIndexWriterAsync(new GitCliRequest
                {
                    Arguments = ["rm", "--cached", "-q", "--pathspec-from-file=-", "--pathspec-file-nul"],
                    WorkingDirectory = context.RepositoryPath,
                    StandardInput = stdin,
                    ExtraEnvironment = LiteralPathspecEnv,
                    Timeout = Timeout.InfiniteTimeSpan,
                }, innerCt).ConfigureAwait(false);

                if (!result.IsSuccess)
                    throw HistoriaGitException.FromCli(result, "Unstage");
                return;
            }

            var restoreResult = await RunIndexWriterAsync(new GitCliRequest
            {
                Arguments = ["restore", "--staged", "--pathspec-from-file=-", "--pathspec-file-nul"],
                WorkingDirectory = context.RepositoryPath,
                StandardInput = stdin,
                ExtraEnvironment = LiteralPathspecEnv,
                Timeout = Timeout.InfiniteTimeSpan,
            }, innerCt).ConfigureAwait(false);

            if (!restoreResult.IsSuccess)
                throw HistoriaGitException.FromCli(restoreResult, "Unstage");
        }, ct).ConfigureAwait(false);
    }

    public async Task DiscardAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0)
            return;

        using var _ = context.BeginSelfOperation();

        await context.WithWriteLockAsync(async innerCt =>
        {
            var normalizedPaths = paths.Select(p => p.Replace('\\', '/')).Distinct(StringComparer.Ordinal).ToList();
            var chunks = ChunkPathsByCommandLineLength(normalizedPaths).ToList();

            // 1. Probe the index: collect paths present in the index.
            var inIndex = new HashSet<string>(StringComparer.Ordinal);
            foreach (var chunk in chunks)
            {
                var lsFilesArgs = new List<string>(chunk.Count + 3) { "ls-files", "-z", "--" };
                lsFilesArgs.AddRange(chunk);

                var lsFilesResult = await cli.RunAsync(new GitCliRequest
                {
                    Arguments = lsFilesArgs,
                    WorkingDirectory = context.RepositoryPath,
                    ExtraEnvironment = LiteralPathspecEnv,
                }, innerCt).ConfigureAwait(false);

                if (!lsFilesResult.IsSuccess)
                    throw HistoriaGitException.FromCli(lsFilesResult, "Discard");

                if (!string.IsNullOrEmpty(lsFilesResult.StandardOutput))
                {
                    var entries = lsFilesResult.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var entry in entries)
                        inIndex.Add(entry);
                }
            }

            // 2. Probe intent-to-add: paths added with 'git add -N' appear as added in a worktree diff.
            // Rename detection is off: an intent-to-add path paired with a deleted tracked path would
            // otherwise show as renamed, be missed by the filter, and be emptied by the restore.
            var intentToAdd = new HashSet<string>(StringComparer.Ordinal);
            foreach (var chunk in chunks)
            {
                var diffArgs = new List<string>(chunk.Count + 6) { "diff", "-z", "--no-renames", "--name-only", "--diff-filter=A", "--" };
                diffArgs.AddRange(chunk);

                var diffResult = await cli.RunAsync(new GitCliRequest
                {
                    Arguments = diffArgs,
                    WorkingDirectory = context.RepositoryPath,
                    ExtraEnvironment = LiteralPathspecEnv,
                }, innerCt).ConfigureAwait(false);

                if (!diffResult.IsSuccess)
                    throw HistoriaGitException.FromCli(diffResult, "Discard");

                if (!string.IsNullOrEmpty(diffResult.StandardOutput))
                {
                    var entries = diffResult.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var entry in entries)
                        intentToAdd.Add(entry);
                }
            }

            // 3. Probe case sensitivity: core.ignorecase.
            var configResult = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["config", "--type=bool", "--get", "core.ignorecase"],
                WorkingDirectory = context.RepositoryPath,
            }, innerCt).ConfigureAwait(false);

            bool ignoreCase;
            if (configResult.IsSuccess)
            {
                ignoreCase = string.Equals(configResult.StandardOutput.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            }
            else if (configResult.ExitCode == 1 && configResult.ErrorKind is not (GitErrorKind.Cancelled or GitErrorKind.TimedOut))
            {
                ignoreCase = false;
            }
            else
            {
                throw HistoriaGitException.FromCli(configResult, "Discard");
            }

            // 4. Partition paths into restore (tracked in index, not intent-to-add) and remove (untracked / intent-to-add).
            var toRestore = new List<string>();
            var toRemove = new List<string>();
            foreach (var path in normalizedPaths)
            {
                if (inIndex.Contains(path) && !intentToAdd.Contains(path))
                    toRestore.Add(path);
                else
                    toRemove.Add(path);
            }

            if (ignoreCase && toRemove.Count > 0)
            {
                // Probe the index case-insensitively for paths in toRemove to guard against recycling
                // files that resolve to tracked files on a case-insensitive file system.
                // Chunk the prefixed pathspecs so the prefix counts toward the command-line limit.
                var icaseChunks = ChunkPathsByCommandLineLength(toRemove.Select(p => $":(literal,icase){p}").ToList());
                foreach (var chunk in icaseChunks)
                {
                    var icaseArgs = new List<string>(chunk.Count + 3) { "ls-files", "-z", "--" };
                    icaseArgs.AddRange(chunk);

                    var icaseResult = await cli.RunAsync(new GitCliRequest
                    {
                        Arguments = icaseArgs,
                        WorkingDirectory = context.RepositoryPath,
                    }, innerCt).ConfigureAwait(false);

                    if (!icaseResult.IsSuccess)
                        throw HistoriaGitException.FromCli(icaseResult, "Discard");

                    if (!string.IsNullOrEmpty(icaseResult.StandardOutput))
                    {
                        var entries = icaseResult.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                        foreach (var entry in entries)
                            inIndex.Add(entry);
                    }
                }

                var trackedIgnoreCase = new HashSet<string>(inIndex.Where(p => !intentToAdd.Contains(p)), StringComparer.OrdinalIgnoreCase);
                var filteredToRemove = new List<string>(toRemove.Count);
                foreach (var path in toRemove)
                {
                    if (trackedIgnoreCase.Contains(path))
                    {
                        // On a case-insensitive file system that name resolves to a tracked file on disk,
                        // so deleting it would destroy the tracked file.
                        AppLog.Write(AppLogLevel.Warn, "git", $"Discard: skipping untracked removal of '{path}' matching a tracked path under case-insensitive comparison.");
                    }
                    else
                    {
                        filteredToRemove.Add(path);
                    }
                }
                toRemove = filteredToRemove;
            }

            innerCt.ThrowIfCancellationRequested();

            // 5. Remove untracked and intent-to-add paths first.
            if (toRemove.Count > 0)
            {
                var rmResult = await cli.RunAsync(new GitCliRequest
                {
                    Arguments = ["rm", "--cached", "-q", "--ignore-unmatch", "--pathspec-from-file=-", "--pathspec-file-nul"],
                    WorkingDirectory = context.RepositoryPath,
                    StandardInput = string.Join('\0', toRemove) + '\0',
                    ExtraEnvironment = LiteralPathspecEnv,
                    Timeout = Timeout.InfiniteTimeSpan,
                }, innerCt).ConfigureAwait(false);

                if (!rmResult.IsSuccess)
                    throw HistoriaGitException.FromCli(rmResult, "Discard");

                innerCt.ThrowIfCancellationRequested();

                foreach (var path in toRemove)
                {
                    RecycleBin.Delete(Path.Combine(context.RepositoryPath, path));
                }
            }

            // 6. Restore tracked paths from the index.
            if (toRestore.Count > 0)
            {
                var restoreResult = await cli.RunAsync(new GitCliRequest
                {
                    Arguments = ["restore", "--worktree", "--pathspec-from-file=-", "--pathspec-file-nul"],
                    WorkingDirectory = context.RepositoryPath,
                    StandardInput = string.Join('\0', toRestore) + '\0',
                    ExtraEnvironment = LiteralPathspecEnv,
                    Timeout = Timeout.InfiniteTimeSpan,
                }, innerCt).ConfigureAwait(false);

                if (!restoreResult.IsSuccess)
                    throw HistoriaGitException.FromCli(restoreResult, "Discard");
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task<GitObjectId> CommitAsync(CommitRequest request, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        return await context.WithWriteLockAsync(async innerCt =>
        {
            var args = new List<string> { "commit", "-F", "-" };
            if (request.Sign) args.Add("-S");
            if (request.AmendPreviousCommit) args.Add("--amend");
            if (request.AllowEmptyCommit) args.Add("--allow-empty");
            args.Add("--author");
            args.Add($"{request.AuthorName} <{request.AuthorEmail}>");

            var env = new Dictionary<string, string>
            {
                ["GIT_COMMITTER_NAME"] = request.CommitterName ?? request.AuthorName,
                ["GIT_COMMITTER_EMAIL"] = request.CommitterEmail ?? request.AuthorEmail,
            };

            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = args,
                WorkingDirectory = context.RepositoryPath,
                StandardInput = request.Message ?? "",
                ExtraEnvironment = env,
                // No wall clock: hooks (lint, tests) and a signing passphrase prompt can take
                // minutes. The caller's token still cancels it.
                Timeout = Timeout.InfiniteTimeSpan,
            }, innerCt).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                var kind = result.ErrorKind;
                if (kind != GitErrorKind.HookFailed)
                {
                    var combinedKind = GitErrorClassifier.Classify(result.ExitCode, $"{result.StandardOutput}\n{result.StandardError}");
                    if (combinedKind == GitErrorKind.HookFailed)
                        kind = GitErrorKind.HookFailed;
                }
                if (kind == GitErrorKind.HookFailed)
                    throw new HistoriaGitException(GitErrorKind.HookFailed, "Commit hook failed.", $"{result.StandardOutput}\n{result.StandardError}".Trim());

                throw HistoriaGitException.FromCli(result, "Commit");
            }

            var revParse = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["rev-parse", "HEAD"],
                WorkingDirectory = context.RepositoryPath,
            }, innerCt).ConfigureAwait(false);

            if (!revParse.IsSuccess)
                throw HistoriaGitException.FromCli(revParse, "Commit rev-parse");

            return GitObjectId.Parse(revParse.StandardOutput.Trim());
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The paths worth handing to <c>git add</c>: those on disk, plus those missing from disk that
    /// the index still tracks (a deletion to stage). A path in neither place is skipped and logged.
    /// A probe that fails throws rather than guessing, so a deletion is never dropped silently.
    /// </summary>
    private async Task<IReadOnlyList<string>> FilterValidStagePathsAsync(
        IReadOnlyList<string> paths, CancellationToken ct)
    {
        var existingOnDisk = new HashSet<string>(StringComparer.Ordinal);
        var missingFromDisk = new List<string>();

        foreach (var path in paths)
        {
            var fullPath = Path.Combine(context.RepositoryPath, path);
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
                existingOnDisk.Add(path);
            else
                missingFromDisk.Add(path);
        }

        if (missingFromDisk.Count == 0)
            return paths;

        var trackedInIndex = new HashSet<string>(StringComparer.Ordinal);
        var chunks = ChunkPathsByCommandLineLength(missingFromDisk);
        foreach (var chunk in chunks)
        {
            var args = new List<string>(chunk.Count + 3) { "ls-files", "-z", "--" };
            args.AddRange(chunk);

            // Runs inside the write lock, so no wall clock: a probe stopped by a timeout would
            // be the same silent drop this method exists to prevent.
            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = args,
                WorkingDirectory = context.RepositoryPath,
                ExtraEnvironment = LiteralPathspecEnv,
                Timeout = Timeout.InfiniteTimeSpan,
            }, ct).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                if (result.ErrorKind == GitErrorKind.Cancelled)
                    throw new OperationCanceledException(ct);
                throw HistoriaGitException.FromCli(result, "Stage");
            }

            if (!string.IsNullOrEmpty(result.StandardOutput))
            {
                var tracked = result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                foreach (var t in tracked)
                    trackedInIndex.Add(t);
            }
        }

        var resultList = new List<string>(paths.Count);
        var skipped = new List<string>();
        foreach (var path in paths)
        {
            if (existingOnDisk.Contains(path) || trackedInIndex.Contains(path))
                resultList.Add(path);
            else
                skipped.Add(path);
        }

        if (skipped.Count > 0)
        {
            const int logged = 10;
            var names = string.Join(", ", skipped.Take(logged));
            var more = skipped.Count > logged ? $" and {skipped.Count - logged} more" : "";
            AppLog.Write(AppLogLevel.Info, "git",
                $"Stage: skipped {skipped.Count} path(s) missing from both disk and index: {names}{more}.");
        }

        return resultList;
    }

    /// <summary>
    /// Runs an idempotent index writer, retrying while another git process holds
    /// <c>index.lock</c>: the lock is usually a status refresh or a fetch that finishes within a
    /// second. The lock file is never removed here; when the retries run out the locked result is
    /// returned for the caller to surface.
    /// </summary>
    private async Task<GitCliResult> RunIndexWriterAsync(GitCliRequest request, CancellationToken ct)
    {
        var result = await cli.RunAsync(request, ct).ConfigureAwait(false);
        foreach (var delay in LockedIndexRetryDelays)
        {
            if (result.ErrorKind != GitErrorKind.LockedIndex)
                break;

            AppLog.Write(AppLogLevel.Info, "git",
                $"{request.Arguments[0]}: index is locked, retrying in {delay.TotalMilliseconds:0} ms.");
            await Task.Delay(delay, ct).ConfigureAwait(false);
            result = await cli.RunAsync(request, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Stages index paths that have a twin differing only in case by writing their exact index
    /// entries, because <c>git add</c> under <c>core.ignorecase</c> may record the content under the
    /// twin's name instead. Returns the paths left for <c>git add</c>. Refuses to
    /// stage two names of one disk file together, since both entries would receive the same content.
    /// </summary>
    private async Task<List<string>> StageCaseTwinsExactlyAsync(List<string> paths, CancellationToken ct)
    {
        if (!await _caseCollisions.IgnoreCaseAsync(ct).ConfigureAwait(false))
            return paths;

        var groups = await _caseCollisions.GroupsForStagingAsync(ct).ConfigureAwait(false);
        if (groups.Count == 0)
            return paths;

        var workDir = context.RepositoryPath;
        var exact = new List<(string Path, string Mode)>();
        var rest = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            var mode = IndexCaseCollisions.ModeOf(groups, path);
            if (mode is "100644" or "100755" && File.Exists(Path.Combine(workDir, path)))
                exact.Add((path, mode));
            else
                rest.Add(path);
        }

        if (exact.Count == 0)
            return paths;

        for (var i = 0; i < exact.Count; i++)
        {
            for (var j = i + 1; j < exact.Count; j++)
            {
                if (string.Equals(exact[i].Path, exact[j].Path, StringComparison.OrdinalIgnoreCase)
                    && IndexCaseCollisions.SharesOneDiskEntry(workDir, exact[i].Path, exact[j].Path))
                    throw new HistoriaGitException(GitErrorKind.Unknown,
                        $"Stage failed: {exact[i].Path} and {exact[j].Path} are one file on disk. Stage only the name you edited.");
            }
        }

        var hash = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["hash-object", "-w", "--stdin-paths"],
            WorkingDirectory = workDir,
            StandardInput = string.Join('\n', exact.Select(e => e.Path)) + '\n',
            Timeout = Timeout.InfiniteTimeSpan,
        }, ct).ConfigureAwait(false);

        if (!hash.IsSuccess)
            throw HistoriaGitException.FromCli(hash, "Stage");

        var oids = hash.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (oids.Length != exact.Count)
            throw new HistoriaGitException(GitErrorKind.Unknown, "Stage failed: git hash-object returned an unexpected number of ids.");

        var indexInfo = new StringBuilder();
        for (var i = 0; i < exact.Count; i++)
            indexInfo.Append(exact[i].Mode).Append(' ').Append(oids[i].Trim()).Append('\t').Append(exact[i].Path).Append('\0');

        var update = await RunIndexWriterAsync(new GitCliRequest
        {
            Arguments = ["update-index", "-z", "--index-info"],
            WorkingDirectory = workDir,
            StandardInput = indexInfo.ToString(),
            Timeout = Timeout.InfiniteTimeSpan,
        }, ct).ConfigureAwait(false);

        if (!update.IsSuccess)
            throw HistoriaGitException.FromCli(update, "Stage");

        return rest;
    }

    private static IEnumerable<List<string>> ChunkPathsByCommandLineLength(
        IReadOnlyList<string> paths, int maxChars = 25000)
    {
        var currentChunk = new List<string>();
        var currentLength = 0;

        foreach (var path in paths)
        {
            var itemLength = path.Length + 1;
            if (currentChunk.Count > 0 && currentLength + itemLength > maxChars)
            {
                yield return currentChunk;
                currentChunk = new List<string>();
                currentLength = 0;
            }

            currentChunk.Add(path);
            currentLength += itemLength;
        }

        if (currentChunk.Count > 0)
            yield return currentChunk;
    }

    /// <summary><c>rev-parse --verify -q HEAD</c> exits 1 on an unborn HEAD; cancellation,
    /// timeouts and a broken repository report differently.</summary>
    private static bool IsUnbornHeadProbe(GitCliResult headCheck) =>
        headCheck.ExitCode == 1
        && headCheck.ErrorKind is not (GitErrorKind.Cancelled or GitErrorKind.TimedOut);

    private static bool IsIgnoredAdviceOnly(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return false;

        var lines = stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var hasIgnoredNotice = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("The following paths are ignored", StringComparison.OrdinalIgnoreCase))
            {
                hasIgnoredNotice = true;
                continue;
            }
            if (line.StartsWith("hint:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.StartsWith("fatal: no files added to index", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return hasIgnoredNotice;
    }
}
