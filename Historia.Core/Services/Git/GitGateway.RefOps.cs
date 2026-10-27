namespace Historia.Services.Git;

public sealed partial class GitGateway
{
    public async Task CheckoutAsync(string refName, GitCheckoutOptions options, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        await context.WithWriteLockAsync(async innerCt =>
        {
            await CheckoutInternalAsync(refName, options, innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async Task CheckoutInternalAsync(string refName, GitCheckoutOptions options, CancellationToken ct)
    {
        if (options.CreateBranch && options.NewBranchName is { } newName)
        {
            var branchResult = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["branch", "--no-track", newName, refName],
                WorkingDirectory = context.RepositoryPath,
            }, ct).ConfigureAwait(false);

            if (!branchResult.IsSuccess)
                throw HistoriaGitException.FromCli(branchResult, "Create branch");

            try
            {
                await RunCheckoutCmdAsync(newName, options.Force, ct).ConfigureAwait(false);
            }
            catch
            {
                await RollbackCreatedBranchAsync(newName).ConfigureAwait(false);
                throw;
            }
            return;
        }

        await RunCheckoutCmdAsync(refName, options.Force, ct).ConfigureAwait(false);
    }

    // Removes a branch this call just created after its checkout failed, unless HEAD already
    // moved onto it. The branch points at a commit that already exists, so deleting it loses
    // nothing, and it must run even when the operation was cancelled: neither request uses the
    // caller's token. The caller already holds the write lock, so this takes none.
    private async Task RollbackCreatedBranchAsync(string name)
    {
        var headCheck = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["rev-parse", "--abbrev-ref", "HEAD"],
            WorkingDirectory = context.RepositoryPath,
            Timeout = TimeSpan.FromSeconds(10),
        }, CancellationToken.None).ConfigureAwait(false);

        if (headCheck.IsSuccess && headCheck.StandardOutput.Trim() != name)
        {
            await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["branch", "-D", name],
                WorkingDirectory = context.RepositoryPath,
                Timeout = TimeSpan.FromSeconds(10),
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RunCheckoutCmdAsync(string target, bool force, CancellationToken ct)
    {
        var args = new List<string> { "checkout" };
        if (force)
            args.Add("-f");
        args.Add(target);
        args.Add("--");

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = args,
            WorkingDirectory = context.RepositoryPath,
            // No wall clock: a large checkout (or an LFS smudge download) routinely passes the
            // 30 s default, and a checkout killed midway leaves a half-updated tree and a stale
            // index.lock. Cancelling stops it only before git starts; once running it is never killed.
            Timeout = Timeout.InfiniteTimeSpan,
        }, ct).ConfigureAwait(false);

        if (result.IsSuccess)
            return;

        var (conflictPaths, hasConflict) = TryParseCheckoutConflict(result.StandardError);
        if (hasConflict)
        {
            if (conflictPaths.Count == 0)
                throw new HistoriaGitException(GitErrorKind.UncommittedChanges, result.StandardError.Trim());

            var summaryPaths = conflictPaths.Count <= 3
                ? string.Join(", ", conflictPaths)
                : $"{string.Join(", ", conflictPaths.Take(3))} and {conflictPaths.Count - 3} more";
            var message = $"Checkout would overwrite local changes to {summaryPaths}.";
            var details = "error: Your local changes to the following files would be overwritten by checkout:\n\t"
                + string.Join("\n\t", conflictPaths)
                + "\nPlease commit your changes or stash them before you switch branches.\nAborting";

            throw new HistoriaGitException(GitErrorKind.UncommittedChanges, message, details);
        }

        throw HistoriaGitException.FromCli(result, "Checkout");
    }

    private static (List<string> Paths, bool Matched) TryParseCheckoutConflict(string stderr)
    {
        var paths = new List<string>();
        if (string.IsNullOrWhiteSpace(stderr) || !stderr.Contains("would be overwritten by checkout", StringComparison.OrdinalIgnoreCase))
            return (paths, false);

        var lines = stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var inFileList = false;
        foreach (var line in lines)
        {
            if (line.Contains("would be overwritten by checkout", StringComparison.OrdinalIgnoreCase))
            {
                inFileList = true;
                continue;
            }

            if (inFileList)
            {
                if (line.StartsWith("Please ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Aborting", StringComparison.OrdinalIgnoreCase))
                    break;

                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                    paths.Add(trimmed.Replace('\\', '/'));
            }
        }

        return (paths, true);
    }

    public async Task<BranchInfo> CreateBranchAsync(string name, string startPoint, bool checkout, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        return await context.WithWriteLockAsync(async innerCt =>
        {
            var branchResult = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["branch", "--no-track", name, startPoint],
                WorkingDirectory = context.RepositoryPath,
            }, innerCt).ConfigureAwait(false);

            if (!branchResult.IsSuccess)
                throw HistoriaGitException.FromCli(branchResult, "Create branch");

            if (checkout)
            {
                try
                {
                    await CheckoutInternalAsync(name, new GitCheckoutOptions(), innerCt).ConfigureAwait(false);
                }
                catch
                {
                    await RollbackCreatedBranchAsync(name).ConfigureAwait(false);
                    throw;
                }
            }

            var snapshot = await context.RefSnapshots.GetAsync(innerCt).ConfigureAwait(false);
            var remotes = await context.RemoteConfig.GetRemotesAsync(innerCt).ConfigureAwait(false);
            var branches = BranchProjection.Project(snapshot, remotes);
            var branch = branches.FirstOrDefault(b => b.Name == name || b.CanonicalName == name);
            if (branch is null)
                throw new HistoriaGitException(GitErrorKind.RefNotFound, $"Branch '{name}' was not found after creation.");

            return branch;
        }, ct).ConfigureAwait(false);
    }

    public async Task DeleteBranchAsync(string name, bool force, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        await context.WithWriteLockAsync(async innerCt =>
        {
            var refCheck = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["rev-parse", "--verify", "-q", $"refs/heads/{name}"],
                WorkingDirectory = context.RepositoryPath,
            }, innerCt).ConfigureAwait(false);

            if (!refCheck.IsSuccess)
                throw new HistoriaGitException(GitErrorKind.RefNotFound, $"Branch '{name}' not found.");

            var headCheck = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["symbolic-ref", "-q", "HEAD"],
                WorkingDirectory = context.RepositoryPath,
            }, innerCt).ConfigureAwait(false);

            if (headCheck.IsSuccess && headCheck.StandardOutput.Trim() == $"refs/heads/{name}")
                throw new InvalidOperationException("Cannot delete the current branch.");

            if (!force)
            {
                var upstreamCheck = await cli.RunAsync(new GitCliRequest
                {
                    Arguments = ["rev-parse", "--verify", "-q", $"{name}@{{upstream}}"],
                    WorkingDirectory = context.RepositoryPath,
                }, innerCt).ConfigureAwait(false);

                if (!upstreamCheck.IsSuccess)
                {
                    var mergeCheck = await cli.RunAsync(new GitCliRequest
                    {
                        Arguments = ["merge-base", "--is-ancestor", name, "HEAD"],
                        WorkingDirectory = context.RepositoryPath,
                    }, innerCt).ConfigureAwait(false);

                    if (!mergeCheck.IsSuccess)
                        throw new InvalidOperationException($"Branch '{name}' is not fully merged. Use force to delete.");
                }
            }

            var deleteResult = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["branch", "-D", name],
                WorkingDirectory = context.RepositoryPath,
            }, innerCt).ConfigureAwait(false);

            if (!deleteResult.IsSuccess)
                throw HistoriaGitException.FromCli(deleteResult, "Delete branch");
        }, ct).ConfigureAwait(false);
    }

    public async Task RenameBranchAsync(string oldName, string newName, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        await context.WithWriteLockAsync(async innerCt =>
        {
            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["branch", "-m", oldName, newName],
                WorkingDirectory = context.RepositoryPath,
            }, innerCt).ConfigureAwait(false);

            if (!result.IsSuccess)
                throw HistoriaGitException.FromCli(result, "Rename branch");
        }, ct).ConfigureAwait(false);
    }

    public async Task<TagInfo> CreateTagAsync(string name, string target, string? message, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        return await context.WithWriteLockAsync(async innerCt =>
        {
            GitCliResult result;
            if (string.IsNullOrEmpty(message))
            {
                result = await cli.RunAsync(new GitCliRequest
                {
                    Arguments = ["-c", "tag.gpgSign=false", "tag", name, target],
                    WorkingDirectory = context.RepositoryPath,
                }, innerCt).ConfigureAwait(false);
            }
            else
            {
                var (localName, localEmail) = await GetConfiguredIdentityAsync(innerCt).ConfigureAwait(false);
                var taggerName = localName;
                var taggerEmail = localEmail;
                if (string.IsNullOrWhiteSpace(taggerName) || string.IsNullOrWhiteSpace(taggerEmail))
                {
                    var (globalName, globalEmail) = await GetGlobalIdentityAsync(innerCt).ConfigureAwait(false);
                    taggerName ??= globalName;
                    taggerEmail ??= globalEmail;
                }
                taggerName = !string.IsNullOrWhiteSpace(taggerName) ? taggerName : "Historia";
                taggerEmail = !string.IsNullOrWhiteSpace(taggerEmail) ? taggerEmail : "Historia@localhost";

                var env = new Dictionary<string, string>
                {
                    ["GIT_COMMITTER_NAME"] = taggerName,
                    ["GIT_COMMITTER_EMAIL"] = taggerEmail,
                };

                result = await cli.RunAsync(new GitCliRequest
                {
                    Arguments = ["-c", "tag.gpgSign=false", "tag", "-a", name, "-F", "-", target],
                    WorkingDirectory = context.RepositoryPath,
                    StandardInput = message,
                    ExtraEnvironment = env,
                }, innerCt).ConfigureAwait(false);
            }

            if (!result.IsSuccess)
                throw HistoriaGitException.FromCli(result, "Create tag");

            var refResult = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["for-each-ref", "--format=" + ForEachRefParser.Format, $"refs/tags/{name}"],
                WorkingDirectory = context.RepositoryPath,
            }, innerCt).ConfigureAwait(false);

            if (!refResult.IsSuccess)
                throw HistoriaGitException.FromCli(refResult, "Tag for-each-ref");

            var entries = ForEachRefParser.Parse(refResult.StandardOutput);
            var entry = entries.FirstOrDefault();
            if (entry is null)
                throw new HistoriaGitException(GitErrorKind.RefNotFound, $"Tag '{name}' not found after creation.");

            var targetId = (entry.Peeled ?? entry.Target) ?? GitObjectId.Zero;
            return new TagInfo
            {
                Name = name,
                TargetCommitId = targetId,
                IsAnnotated = entry.IsAnnotatedTag,
                Message = entry.Message,
                TaggerName = entry.TaggerName,
                TaggerWhen = entry.TaggerWhen,
            };
        }, ct).ConfigureAwait(false);
    }

    public async Task DeleteTagAsync(string name, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        await context.WithWriteLockAsync(async innerCt =>
        {
            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["tag", "-d", name],
                WorkingDirectory = context.RepositoryPath,
            }, innerCt).ConfigureAwait(false);

            if (!result.IsSuccess)
                throw HistoriaGitException.FromCli(result, "Delete tag");
        }, ct).ConfigureAwait(false);
    }

    public async Task AddRemoteAsync(string name, string url, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        await context.WithWriteLockAsync(async innerCt =>
        {
            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["remote", "add", "--", name, url],
                WorkingDirectory = context.RepositoryPath,
            }, innerCt).ConfigureAwait(false);

            if (!result.IsSuccess)
                throw HistoriaGitException.FromCli(result, "Add remote");
        }, ct).ConfigureAwait(false);
    }

    public async Task RemoveRemoteAsync(string name, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        await context.WithWriteLockAsync(async innerCt =>
        {
            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["remote", "remove", name],
                WorkingDirectory = context.RepositoryPath,
            }, innerCt).ConfigureAwait(false);

            if (!result.IsSuccess)
                throw HistoriaGitException.FromCli(result, "Remove remote");
        }, ct).ConfigureAwait(false);
    }

    public async Task SetRemoteUrlAsync(string name, string url, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        await context.WithWriteLockAsync(async innerCt =>
        {
            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["remote", "set-url", name, url],
                WorkingDirectory = context.RepositoryPath,
            }, innerCt).ConfigureAwait(false);

            if (!result.IsSuccess)
                throw HistoriaGitException.FromCli(result, "Set remote url");
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> RenameRemoteAsync(string oldName, string newName, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        return await context.WithWriteLockAsync(async innerCt =>
        {
            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["remote", "rename", "--", oldName, newName],
                WorkingDirectory = context.RepositoryPath,
            }, innerCt).ConfigureAwait(false);

            if (!result.IsSuccess)
                throw HistoriaGitException.FromCli(result, "Rename remote");

            return ParseUnupdatedRefspecs(result.StandardError);
        }, ct).ConfigureAwait(false);
    }

    // `git config --unset-all` exits 5 when the key is absent, which for a clear means the
    // push URL is already gone.
    private const int GitConfigKeyAbsentExitCode = 5;

    public async Task SetRemotePushUrlAsync(string name, string? pushUrl, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        await context.WithWriteLockAsync(async innerCt =>
        {
            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = pushUrl is null
                    ? ["config", "--unset-all", $"remote.{name}.pushurl"]
                    : ["remote", "set-url", "--push", name, pushUrl],
                WorkingDirectory = context.RepositoryPath,
            }, innerCt).ConfigureAwait(false);

            var alreadyClear = pushUrl is null && result.ExitCode == GitConfigKeyAbsentExitCode;
            if (!result.IsSuccess && !alreadyClear)
                throw HistoriaGitException.FromCli(result, "Set push url");
        }, ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> ParseUnupdatedRefspecs(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return [];

        var list = new List<string>();
        var lines = stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var inRefspecs = false;

        foreach (var line in lines)
        {
            if (line.Contains("refspec", StringComparison.OrdinalIgnoreCase))
            {
                inRefspecs = true;
                continue;
            }

            if (inRefspecs)
            {
                if (line.StartsWith("\t") || line.StartsWith("  "))
                {
                    var trimmed = line.Trim();
                    if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("Please", StringComparison.OrdinalIgnoreCase))
                        list.Add(trimmed);
                }
                else
                {
                    inRefspecs = false;
                }
            }
        }

        return list;
    }
}
