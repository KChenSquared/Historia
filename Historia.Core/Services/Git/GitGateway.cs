namespace Historia.Services.Git;

/// <summary>
/// <inheritdoc cref="IGitGateway"/>
/// Bound to one <see cref="RepositoryContext"/>; routes each operation to the Git CLI.
/// </summary>
public sealed partial class GitGateway(
    RepositoryContext context,
    IGitCliService cli,
    IRepoLayoutService repoLayout,
    ICredentialVaultService credentialVault,
    ISshHostResolver? sshHosts = null) : IGitGateway
{
    private readonly ISshHostResolver _sshHosts = sshHosts ?? SshHostResolver.Shared;
    private readonly IndexCaseCollisions _caseCollisions = new(cli, context.GitDirectory, context.RepositoryPath);

    public string RepositoryPath => context.RepositoryPath;

    public async Task<RepositoryStatus> GetStatusAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (context.IsBare)
            return RepositoryStatus.Empty with { ComputedAt = DateTimeOffset.Now };

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["status", "--porcelain=v2", "-z", "--branch", "--untracked-files=all", "--renames"],
            WorkingDirectory = context.RepositoryPath,
            Timeout = Timeout.InfiniteTimeSpan,
            RawStandardOutput = true,
        }, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            ct.ThrowIfCancellationRequested();
            throw HistoriaGitException.FromCli(result, "Status");
        }

        var parsed = GitStatusParser.Parse(result.StandardOutput);

        IReadOnlyDictionary<string, string> untrackedHashes = ImmutableDictionary<string, string>.Empty;
        if (GitStatusParser.ShouldHashUntracked(parsed))
        {
            var untrackedPaths = string.Join("\n", parsed.Untracked.Select(u => u.Path)) + "\n";
            var hashResult = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["hash-object", "--stdin-paths"],
                WorkingDirectory = context.RepositoryPath,
                StandardInput = untrackedPaths,
                Timeout = Timeout.InfiniteTimeSpan,
            }, ct).ConfigureAwait(false);

            if (hashResult.IsSuccess)
            {
                var lines = hashResult.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == parsed.Untracked.Count)
                {
                    var dict = new Dictionary<string, string>(parsed.Untracked.Count, StringComparer.Ordinal);
                    for (var i = 0; i < parsed.Untracked.Count; i++)
                    {
                        dict[parsed.Untracked[i].Path] = lines[i].Trim();
                    }
                    untrackedHashes = dict;
                }
                else
                {
                    PerfLog.Mark($"GetStatusAsync hash-object count mismatch: got {lines.Length}, expected {parsed.Untracked.Count}");
                }
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                PerfLog.Mark($"GetStatusAsync hash-object failed: exit={hashResult.ExitCode}, kind={hashResult.ErrorKind}");
            }
        }

        var status = GitStatusParser.PairExactMoves(parsed, untrackedHashes);
        status = await HideCaseCollisionArtifactsAsync(status, ct).ConfigureAwait(false);
        return PopulateSizes(status, context.RepositoryPath);
    }

    private async Task<RepositoryStatus> HideCaseCollisionArtifactsAsync(RepositoryStatus status, CancellationToken ct)
    {
        if (!status.Unstaged.Any(u => u.Kind == FileChangeKind.Modified))
            return status;

        try
        {
            if (!await _caseCollisions.IgnoreCaseAsync(ct).ConfigureAwait(false))
                return status;

            var modifiedPaths = status.Unstaged
                .Where(u => u.Kind == FileChangeKind.Modified)
                .Select(u => u.Path)
                .ToList();

            var groups = await _caseCollisions
                .GroupsForCandidatesAsync(modifiedPaths, specs => ChunkPathsByCommandLineLength(specs), ct)
                .ConfigureAwait(false);
            if (groups.Count == 0)
                return status;

            var candidatePaths = modifiedPaths
                .Where(groups.ContainsKey)
                .ToList();

            if (candidatePaths.Count == 0)
                return status;

            var diskHashes = await _caseCollisions.HashWorktreeAsync(candidatePaths, ct).ConfigureAwait(false);

            var workDir = context.RepositoryPath;
            var hidden = new List<string>();
            var hiddenSet = new HashSet<string>(StringComparer.Ordinal);
            var visible = new List<string>();

            // A candidate whose disk content is a twin's blob is that twin's file; the twin owns it.
            foreach (var path in candidatePaths)
            {
                if (diskHashes.TryGetValue(path, out var diskHash))
                {
                    var owner = groups[path].FirstOrDefault(peer =>
                        string.Equals(diskHash, peer.BlobId, StringComparison.OrdinalIgnoreCase)
                        && IndexCaseCollisions.SharesOneDiskEntry(workDir, path, peer.Path));
                    if (owner.Path is not null)
                    {
                        _caseCollisions.RecordOwner(owner.Path);
                        hidden.Add(path);
                        hiddenSet.Add(path);
                        continue;
                    }
                }
                visible.Add(path);
            }

            // The rest were edited, or could not be hashed. Where several names of one disk file
            // remain, the edit belongs to the owner seen before it, provided the file still carries
            // the owner's spelling; otherwise no name can be trusted and the rows stay ambiguous.
            var visibleSet = new HashSet<string>(visible, StringComparer.Ordinal);
            var ambiguous = new List<string>();
            foreach (var path in visible)
            {
                if (!diskHashes.ContainsKey(path))
                {
                    ambiguous.Add(path);
                    continue;
                }

                var twinsOnDisk = groups[path]
                    .Where(peer => visibleSet.Contains(peer.Path) && IndexCaseCollisions.SharesOneDiskEntry(workDir, path, peer.Path))
                    .ToList();
                if (twinsOnDisk.Count == 0)
                    continue;

                var owner = _caseCollisions.OwnerOf(path);
                if (owner is not null && string.Equals(owner, path, StringComparison.Ordinal)
                    && IndexCaseCollisions.IsDiskNameOf(workDir, path, twinsOnDisk[0].Path))
                    continue;

                if (owner is not null && twinsOnDisk.Any(peer => string.Equals(peer.Path, owner, StringComparison.Ordinal))
                    && IndexCaseCollisions.IsDiskNameOf(workDir, owner, path))
                {
                    hidden.Add(path);
                    hiddenSet.Add(path);
                    continue;
                }

                ambiguous.Add(path);
            }

            if (hidden.Count == 0 && ambiguous.Count == 0)
                return status;

            hidden.Sort(StringComparer.Ordinal);
            ambiguous.Sort(StringComparer.Ordinal);

            var rest = status.Unstaged.Where(u => !hiddenSet.Contains(u.Path)).ToList();
            var isDirty = status.Staged.Count + rest.Count + status.Untracked.Count + status.Conflicted.Count > 0;

            return status with
            {
                Unstaged = rest,
                HiddenCaseCollisions = hidden,
                AmbiguousCaseCollisions = ambiguous,
                IsDirty = isDirty,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "git", $"Case collision detection failed: {ex.Message}", ex);
            return status;
        }
    }

    public async Task<(string? Name, string? Email)> GetConfiguredIdentityAsync(CancellationToken ct = default)
    {
        var name = await ReadConfigAsync("--local", "user.name", ct).ConfigureAwait(false);
        var email = await ReadConfigAsync("--local", "user.email", ct).ConfigureAwait(false);
        return (name, email);
    }

    public async Task<(string? Name, string? Email)> GetGlobalIdentityAsync(CancellationToken ct = default)
    {
        var name = await ReadConfigAsync("--global", "user.name", ct).ConfigureAwait(false);
        var email = await ReadConfigAsync("--global", "user.email", ct).ConfigureAwait(false);
        return (name, email);
    }

    public async Task<bool> GetGpgSignConfiguredAsync(CancellationToken ct = default)
    {
        // No scope flag → git resolves local over global on its own.
        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "--get", "commit.gpgsign"],
            WorkingDirectory = context.RepositoryPath,
            Timeout = TimeSpan.FromSeconds(10),
        }, ct).ConfigureAwait(false);

        return result.ExitCode == 0
            && string.Equals(result.StandardOutput.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> ReadConfigAsync(string scope, string key, CancellationToken ct)
    {
        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", scope, "--get", key],
            WorkingDirectory = context.RepositoryPath,
            Timeout = TimeSpan.FromSeconds(10),
        }, ct).ConfigureAwait(false);

        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardOutput.Trim()
            : null;
    }

    public async Task MergeAsync(string refish, bool noFastForward, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        var args = new List<string> { "merge" };
        if (noFastForward) args.Add("--no-ff");
        args.Add(refish);

        var result = await context.WithWriteLockAsync(innerCt => cli.RunAsync(new GitCliRequest
        {
            Arguments = args,
            WorkingDirectory = context.RepositoryPath,
            // No wall clock: merge runs hooks, and killing it midway leaves a half-updated tree
            // and a stale index.lock. Cancelling stops it only before git starts; once running it is never killed.
            Timeout = Timeout.InfiniteTimeSpan,
        }, innerCt), ct).ConfigureAwait(false);

        if (!result.IsSuccess && result.ErrorKind != GitErrorKind.MergeConflict)
        {
            // A plain conflicted merge prints "CONFLICT (…)" / "Automatic merge failed" to
            // stdout, not stderr, so the stderr-only classifier tags it Unknown. Re-check the
            // combined output before treating it as a hard failure — a conflict is an expected
            // outcome the conflict resolver handles, not an exception.
            if (GitErrorClassifier.Classify(result.ExitCode,
                    $"{result.StandardOutput}\n{result.StandardError}") == GitErrorKind.MergeConflict)
                return;
            throw HistoriaGitException.FromCli(result, "Merge");
        }
    }

    public async Task ResetAsync(string target, GitResetMode mode, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        var flag = mode switch
        {
            GitResetMode.Soft => "--soft",
            GitResetMode.Hard => "--hard",
            _ => "--mixed",
        };

        var result = await context.WithWriteLockAsync(innerCt => cli.RunAsync(new GitCliRequest
        {
            Arguments = ["reset", flag, target],
            WorkingDirectory = context.RepositoryPath,
            // No wall clock: a --hard reset killed midway leaves a half-updated tree and a
            // stale index.lock. Cancelling stops it only before git starts; once running it is never killed.
            Timeout = Timeout.InfiniteTimeSpan,
        }, innerCt), ct).ConfigureAwait(false);

        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, "Reset");
    }

    public async Task<int> CountCommitsLeavingAsync(string target, CancellationToken ct = default)
    {
        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["rev-list", "--count", $"{target}..HEAD"],
            WorkingDirectory = context.RepositoryPath,
        }, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, "Count commits");

        return int.TryParse(result.StandardOutput.Trim(), out var n) ? n : 0;
    }

    public async Task SetUpstreamAsync(string branchName, string upstreamRef, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        var result = await context.WithWriteLockAsync(innerCt => cli.RunAsync(new GitCliRequest
        {
            Arguments = ["branch", $"--set-upstream-to={upstreamRef}", branchName],
            WorkingDirectory = context.RepositoryPath,
        }, innerCt), ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, "Set upstream");
    }

    // ── Network (always CLI) ──

    public async Task FetchAsync(string remote, GitFetchOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default)
    {
        await LeaveUiThreadAsync().ConfigureAwait(false);

        if (options.All)
        {
            // Fetch every remote individually rather than a single untargeted "git fetch --all":
            // RunNetworkAsync can only route an untargeted fetch as RouteTransport.AsIs (no
            // remote name means no remote URL to classify), which silently skips the active
            // profile's PAT/SSH key injection and — critically — makes the SSH-Only setting a
            // no-op, since the transport plan was decided before it could apply. One
            // remote failing to route doesn't stop the others; the last failure (if any) is what
            // the caller sees, matching git's own "try every remote, report failures" behaviour.
            var remotes = await context.RemoteConfig.GetRemotesAsync(ct).ConfigureAwait(false);
            HistoriaGitException? lastError = null;
            foreach (var r in remotes)
            {
                try
                {
                    await FetchAsync(r.Name, options with { All = false }, progress, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HistoriaGitException ex)
                {
                    lastError = ex;
                }
            }
            if (lastError is not null)
                throw lastError;
            return;
        }

        var args = new List<string> { "fetch", "--progress", remote };
        if (options.Prune) args.Add("--prune");
        args.AddRange(options.ReplaceTags.Select(ForcedTagRefSpec));
        if (!options.Tags)
        {
            await RunNetworkAsync(args, "Fetch", remote, progress, ct, options.Background).ConfigureAwait(false);
            return;
        }

        try
        {
            await RunNetworkAsync([.. args, "--tags"], "Fetch", remote, progress, ct, options.Background).ConfigureAwait(false);
        }
        catch (HistoriaGitException ex) when (ex.Kind == GitErrorKind.TagConflict)
        {
            // Git applies a fetch's ref updates together, so one refused tag also holds back every
            // branch. Fetching again without --tags lands the branches; the tag is still reported.
            await RunNetworkAsync(args, "Fetch", remote, progress, ct, options.Background).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The refspec that overwrites local tag <paramref name="tag"/> with the remote's.</summary>
    internal static string ForcedTagRefSpec(string tag) => "+" + TagRefSpec(tag);

    /// <summary>The refspec that maps tag <paramref name="tag"/> to the same name on the other side.
    /// The <c>refs/tags/</c> prefix keeps it from reading as an option; the characters refused here
    /// are ones git forbids in ref names and would otherwise change the refspec's meaning.</summary>
    internal static string TagRefSpec(string tag)
    {
        if (tag.Length == 0 || tag.StartsWith('-') || tag.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is ':' or '*' or '?' or '[' or '\\' or '^' or '~'))
            throw new ArgumentException($"'{tag}' isn't a tag name that can be fetched or pushed.", nameof(tag));
        return $"refs/tags/{tag}:refs/tags/{tag}";
    }

    // Deliberately not wrapped in WithWriteLockAsync: pull can raise an interactive askpass
    // credential prompt mid-process. Holding the write lock while the user sits at the prompt
    // would block all other git write operations on the repository. Any concurrency collision
    // with another write surfaces naturally as Git's own index lock error.
    public async Task PullAsync(string remote, string branch, GitPullOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default)
    {
        var args = new List<string> { "pull", "--progress" };
        args.Add(options.Rebase ? "--rebase" : "--no-rebase");
        if (options.Prune) args.Add("--prune");
        if (options.AllowUnrelatedHistories) args.Add("--allow-unrelated-histories");
        args.Add(remote);
        args.Add(branch);

        await RunNetworkAsync(args, "Pull", remote, progress, ct).ConfigureAwait(false);
    }

    public async Task PushAsync(string remote, PushSpec spec, IProgress<GitProgress>? progress, CancellationToken ct = default)
    {
        // --no-follow-tags overrides a push.followTags config, so a tag only ever leaves through
        // PushSpec.ForTag.
        var args = new List<string> { "push", "--progress", "--no-follow-tags" };
        if (spec.SetUpstream) args.Add("-u");
        if (spec.ForceWithLease)
        {
            // Never plain --force.
            args.Add("--force-with-lease");
            args.Add("--force-if-includes");
        }

        args.Add(remote);
        args.Add(spec.RefSpec);

        await RunNetworkAsync(args, "Push", remote, progress, ct).ConfigureAwait(false);
    }

    // Fully qualified: an unqualified name resolves against every remote ref, so a branch already
    // gone on the remote would delete a same-named tag instead. A qualified delete of a missing
    // branch succeeds (the remote only warns) and still drops the stale remote-tracking ref.
    public Task DeleteRemoteBranchAsync(string remote, string branch, IProgress<GitProgress>? progress, CancellationToken ct = default)
        => RunNetworkAsync(["push", "--progress", remote, "--delete", $"refs/heads/{branch}"], "Delete remote branch", remote, progress, ct);

    public Task PruneRemoteAsync(string name, IProgress<GitProgress>? progress, CancellationToken ct = default)
        => RunNetworkAsync(["remote", "prune", name], "Prune", name, progress, ct);

    /// <summary>Prefixes <paramref name="args"/> with a per-invocation override that makes
    /// <paramref name="url"/> the remote's only URL. remote.&lt;name&gt;.url is multi-valued, so a
    /// bare <c>-c</c> value would be appended to the stored one: fetch and prune would keep using
    /// the stored URL, and push would go to both. The empty value first clears the list, which
    /// Git supports from 2.46 (<see cref="GitExecutableInfo.MinimumVersion"/>).</summary>
    internal static IReadOnlyList<string> WithRemoteUrlOverride(string remote, string url, IReadOnlyList<string> args)
    {
        var key = $"remote.{remote}.url";
        return new[] { "-c", $"{key}=", "-c", $"{key}={url}" }.Concat(args).ToList();
    }

    // SSH connect timeout for a routing attempt so an SSH-unreachable host (port 22
    // blocked) fails fast instead of stalling on the OS TCP timeout before the fallback.
    private const int SshConnectTimeoutSeconds = 20;

    // Appended to the ssh command of a background fetch: no prompt of any kind (an unknown host
    // key fails rather than being asked about or accepted), and a dead connection is dropped
    // after 4 missed 15 s keepalives.
    private const string BackgroundSshOptions = " -o BatchMode=yes -o ServerAliveInterval=15 -o ServerAliveCountMax=4";

    // Prefixed to a background fetch: no housekeeping repack or maintenance run from a fetch
    // nobody is watching, and an HTTP transfer below 1000 B/s for 60 s is abandoned.
    private static readonly string[] BackgroundGitConfig =
    [
        "-c", "gc.auto=0",
        "-c", "maintenance.auto=false",
        "-c", "http.lowSpeedLimit=1000",
        "-c", "http.lowSpeedTime=60",
    ];

    /// <summary>Moves the rest of a method off a UI thread. Task.Yield would post straight back
    /// to the UI context's queue; ForceYielding schedules the continuation on the thread pool.</summary>
    private static async ValueTask LeaveUiThreadAsync()
    {
        if (SynchronizationContext.Current is not null)
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
    }

    /// <summary>One failed transport attempt, kept so the surfaced error can name every
    /// attempt in its Details.</summary>
    private sealed record NetworkAttempt(string Label, string? Url, HistoriaGitException Error, string? Note);

    /// <summary>
    /// Runs a network command over the transports <see cref="GitTransportRouting.Plan"/> orders.
    /// The next transport is tried only after a <see cref="IsTransportFailure">transport
    /// failure</see>; any other failure means git reached the remote (or failed locally) and a
    /// second transport cannot help, so it is surfaced at once. When every attempt fails, the
    /// surfaced error is the one from an attempt that reached the remote, else the primary
    /// attempt's; its Details carry each attempt's text under a transport-and-URL header.
    /// An HTTPS fallback from an SSH URL whose host is an <c>~/.ssh/config</c> alias retries
    /// against the alias's real host name.
    /// A <paramref name="background"/> fetch tries only the configured transport — no fallback in
    /// either direction — never prompts (<c>GCM_INTERACTIVE=never</c>, SSH <c>BatchMode</c>),
    /// gives up on a stalled connection, skips housekeeping and runs at background priority.
    /// When git's own URL configuration sends that fallback to an SSH URL, it runs with the SSH
    /// environment, or not at all when that URL is the one the SSH attempt already tried.
    /// </summary>
    private async Task RunNetworkAsync(
        IReadOnlyList<string> args, string operation, string? remote,
        IProgress<GitProgress>? progress, CancellationToken ct, bool background = false)
    {
        await LeaveUiThreadAsync().ConfigureAwait(false);

        string? remoteUrl = null;
        if (remote is not null)
        {
            var remotes = await context.RemoteConfig.GetRemotesAsync(ct).ConfigureAwait(false);
            remoteUrl = remotes.FirstOrDefault(r => r.Name == remote)?.Url;
        }

        var urlKind = remoteUrl is null ? RemoteUrlKind.LocalOrOther : RemoteUrlConverter.Classify(remoteUrl);
        var sshOnly = repoLayout.Get(context.RepositoryPath).SshOnly;
        var plan = GitTransportRouting.Plan(urlKind, sshOnly);
        if (background)
        {
            plan = plan.Take(1).ToList();
            args = [.. BackgroundGitConfig, .. args];
        }

        using var _ = context.BeginSelfOperation();

        var attempts = new List<NetworkAttempt>(plan.Count);
        foreach (var transport in plan)
        {
            var effectiveUrl = transport switch
            {
                RouteTransport.Ssh => RemoteUrlConverter.ToSsh(remoteUrl!).ConvertedUrl ?? remoteUrl!,
                RouteTransport.Https => RemoteUrlConverter.ToHttps(remoteUrl!).ConvertedUrl ?? remoteUrl!,
                _ => remoteUrl,
            };
            var label = transport switch
            {
                RouteTransport.Ssh => "SSH",
                RouteTransport.Https => "HTTPS",
                _ => "as-is",
            };
            string? note = null;
            string? displayUrl = null;
            var envTransport = transport;

            // Only reached after the SSH attempt failed — never on the success path, and never
            // with SSH Only on, whose plan has no HTTPS step.
            if (transport == RouteTransport.Https && remote is not null && urlKind is RemoteUrlKind.ScpLike or RemoteUrlKind.SshUrl)
            {
                var alias = await ResolveSshAliasAsync(effectiveUrl!, ct).ConfigureAwait(false);
                if (alias is { } a)
                {
                    effectiveUrl = a.HttpsUrl;
                    note = $"Retried over HTTPS as {a.HostName} using the profile token or Git's credential helper — "
                        + "this may be a different account than the SSH alias selects. Turn on SSH Only to prevent this.";
                }

                // url.<base>.insteadOf, and for a push pushInsteadOf and a stored pushurl, apply to
                // the per-call override too, so git may contact another URL than the HTTPS one.
                var push = args.Count > 0 && args[0] == "push";
                var contacted = await ContactedUrlAsync(remote, effectiveUrl!, push, ct).ConfigureAwait(false);
                if (contacted is not null && !string.Equals(contacted, effectiveUrl, StringComparison.Ordinal))
                {
                    envTransport = RemoteUrlConverter.Classify(contacted) switch
                    {
                        RemoteUrlKind.ScpLike or RemoteUrlKind.SshUrl => RouteTransport.Ssh,
                        RemoteUrlKind.Https => RouteTransport.Https,
                        _ => RouteTransport.AsIs,
                    };

                    if (envTransport == RouteTransport.Ssh)
                    {
                        var sshUrl = RemoteUrlConverter.ToSsh(remoteUrl!).ConvertedUrl ?? remoteUrl!;
                        var sshContacted = await ContactedUrlAsync(remote, sshUrl, push, ct).ConfigureAwait(false);
                        if (string.Equals(contacted, sshContacted, StringComparison.Ordinal))
                        {
                            AppLog.Write(AppLogLevel.Info, "network",
                                $"HTTPS fallback skipped: git's URL configuration sends {effectiveUrl} to {contacted}, which the SSH attempt already tried");
                            continue;
                        }
                    }

                    label = "Retry";
                    note = $"Retried with {contacted} — git's URL configuration (url.insteadOf, pushInsteadOf or a "
                        + $"remote pushurl) sends {effectiveUrl} there. Turn on SSH Only to skip this retry.";
                    displayUrl = contacted;
                }
            }

            // Per-invocation only — the stored remote.<name>.url in .git/config is never rewritten.
            var attemptArgs = remote is not null && effectiveUrl is not null && !string.Equals(effectiveUrl, remoteUrl, StringComparison.Ordinal)
                ? WithRemoteUrlOverride(remote, effectiveUrl, args)
                : args;

            var env = envTransport switch
            {
                RouteTransport.Https => await BuildCredentialEnvAsync(displayUrl ?? effectiveUrl, ct).ConfigureAwait(false),
                RouteTransport.Ssh => SshEnv(background),
                _ => null, // AsIs — defer entirely to Git's own transport / credential resolution.
            };
            if (background)
            {
                // Stored credentials still work; only the interactive sign-in window is off.
                env = new Dictionary<string, string>(env ?? ReadOnlyDictionary<string, string>.Empty)
                {
                    ["GCM_INTERACTIVE"] = "never",
                };
            }

            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = attemptArgs,
                WorkingDirectory = context.RepositoryPath,
                Timeout = Timeout.InfiniteTimeSpan, // Network ops are user-cancellable, not wall-clocked.
                StreamOutput = true,
                ExtraEnvironment = env,
                Priority = background ? GitCliPriority.BackgroundNetwork : GitCliPriority.Normal,
            }, progress, onStdoutLine: null, ct).ConfigureAwait(false);

            if (result.ErrorKind == GitErrorKind.Cancelled)
                throw new OperationCanceledException(ct);
            if (result.IsSuccess)
                return;

            var error = HistoriaGitException.FromCli(result, operation);
            attempts.Add(new NetworkAttempt(label, displayUrl ?? effectiveUrl, error, note));
            if (!IsTransportFailure(error.Kind))
                break;
        }

        throw SurfacedError(attempts, background);
    }

    /// <summary>
    /// The error to throw once every attempt failed: an attempt that reached the remote wins,
    /// else the primary attempt. The Local-config fallback is offered when a profile (not
    /// <i>Local config</i>) is the active selection and that error could be a credential or
    /// reachability problem — never for a background auto-fetch, whose caller has no user to
    /// prompt, and never for errors a different profile can't fix (missing ref, rejected push,
    /// dirty tree…), which keep their own handling.
    /// </summary>
    private HistoriaGitException SurfacedError(List<NetworkAttempt> attempts, bool background)
    {
        var chosen = attempts.FirstOrDefault(a => !IsTransportFailure(a.Error.Kind)) ?? attempts[0];
        var offerLocalConfig = context.ActiveProfile is not null && !background && IsProfileFixable(chosen.Error.Kind);

        if (attempts.Count == 1 && chosen.Note is null && !offerLocalConfig)
            return chosen.Error;

        var details = attempts.Count == 1 && chosen.Note is null
            ? chosen.Error.Details
            : string.Join("\n\n", attempts.Where(a => a != chosen).Prepend(chosen).Select(AttemptBlock));

        return chosen.Error.WithDetails(details, offerLocalConfig);
    }

    private static string AttemptBlock(NetworkAttempt a)
    {
        var header = a.Url is null ? $"{a.Label} attempt:" : $"{a.Label} attempt ({a.Url}):";
        var parts = new List<string> { header };
        if (a.Note is not null) parts.Add(a.Note);
        if (!string.IsNullOrWhiteSpace(a.Error.Details)) parts.Add(a.Error.Details);
        return string.Join('\n', parts);
    }

    /// <summary>
    /// True when the attempt may have failed before reaching the remote, so another transport
    /// could succeed. <see cref="GitErrorKind.Unknown"/> stays eligible: an unclassified refusal
    /// such as an SSH key with no access to the repository may work over HTTPS.
    /// </summary>
    internal static bool IsTransportFailure(GitErrorKind kind) => kind is
        GitErrorKind.AuthenticationFailed or GitErrorKind.NetworkUnreachable or GitErrorKind.TimedOut
        or GitErrorKind.HostKeyVerificationFailed or GitErrorKind.Unknown;

    internal static bool IsProfileFixable(GitErrorKind kind) => kind is
        GitErrorKind.AuthenticationFailed or GitErrorKind.NetworkUnreachable
        or GitErrorKind.TimedOut or GitErrorKind.Unknown;

    private readonly record struct SshAlias(string Alias, string HostName, string HttpsUrl);

    /// <summary>
    /// For an HTTPS URL converted from an SSH remote, resolves the SSH host through
    /// <c>ssh -G</c>. Returns the alias, its real host name and the HTTPS URL rebuilt on that
    /// host when the config renames it; null when the host is not an alias or cannot be resolved.
    /// </summary>
    private async Task<SshAlias?> ResolveSshAliasAsync(string httpsUrl, CancellationToken ct)
    {
        const string scheme = "https://";
        if (!httpsUrl.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return null;

        var slash = httpsUrl.IndexOf('/', scheme.Length);
        var host = slash < 0 ? httpsUrl[scheme.Length..] : httpsUrl[scheme.Length..slash];
        if (host.Length == 0 || host.StartsWith('['))
            return null;

        var resolved = await _sshHosts.ResolveHostNameAsync(host, ct).ConfigureAwait(false);
        if (resolved is null || string.Equals(resolved, host, StringComparison.OrdinalIgnoreCase))
            return null;

        return new SshAlias(host, resolved, scheme + resolved + (slash < 0 ? "/" : httpsUrl[slash..]));
    }

    /// <summary>The URL git will contact for <paramref name="remote"/> when its URL is
    /// overridden with <paramref name="url"/>: after <c>url.&lt;base&gt;.insteadOf</c> and, for a
    /// push, after <c>pushInsteadOf</c> or a stored <c>remote.&lt;name&gt;.pushurl</c>
    /// (<c>git remote get-url [--push]</c> — local, never talks to the remote); null when git
    /// cannot say.</summary>
    private async Task<string?> ContactedUrlAsync(string remote, string url, bool push, CancellationToken ct)
    {
        string[] getUrl = push ? ["remote", "get-url", "--push", remote] : ["remote", "get-url", remote];
        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = WithRemoteUrlOverride(remote, url, getUrl),
            WorkingDirectory = context.RepositoryPath,
        }, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
            return null;
        var expanded = result.StandardOutput.Trim();
        return expanded.Length == 0 ? null : expanded;
    }

    private IReadOnlyDictionary<string, string> SshEnv(bool background)
    {
        var cmd = $"ssh -o ConnectTimeout={SshConnectTimeoutSeconds}";
        var env = new Dictionary<string, string>();
        if (context.ActiveProfile?.SshKeyPath is { } key && !string.IsNullOrWhiteSpace(key))
        {
            env["HISTORIA_SSH_KEY"] = key;
            cmd = $"ssh -i \"$HISTORIA_SSH_KEY\" -o IdentitiesOnly=yes -o ConnectTimeout={SshConnectTimeoutSeconds}";
        }
        env["GIT_SSH_COMMAND"] = background ? cmd + BackgroundSshOptions : cmd;
        return env;
    }

    /// <summary>
    /// Env for the askpass helper when the remote is HTTPS and the active profile carries a PAT
    /// for that host. username = login (fallback "git"), password = the token — the form
    /// accepted by GitHub, GitLab, and Bitbucket app-passwords over HTTPS. Returns null (Git
    /// falls back to its own credential helper) when there is no usable token.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>?> BuildCredentialEnvAsync(string? remoteUrl, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(remoteUrl)
            || context.ActiveProfile is not { } profile
            || !Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return null; // a token is only ever injected over TLS, never plain http:// or git://

        var tokenRef = TokenRefForHost(profile, uri.Host);
        if (string.IsNullOrEmpty(tokenRef))
            return null;

        string? token;
        try { token = await credentialVault.RetrieveTokenAsync(tokenRef, ct).ConfigureAwait(false); }
        catch { return null; }

        if (string.IsNullOrEmpty(token))
            return null;

        return new Dictionary<string, string>
        {
            ["HISTORIA_ASKPASS_USERNAME"] = string.IsNullOrWhiteSpace(profile.GitUserName) ? "git" : profile.GitUserName,
            ["HISTORIA_ASKPASS_PASSWORD"] = token,
            // Pins the answer to the host the token was actually issued for — the askpass
            // helper refuses to answer a prompt naming any other host. uri.Host here is the
            // host TokenRefForHost just matched.
            ["HISTORIA_ASKPASS_HOST"] = uri.Host,
        };
    }

    /// <summary>
    /// The profile token to use for <paramref name="host"/>, or null. Matched by exact host (or
    /// a subdomain) of the three hosted providers — never a substring test, so
    /// <c>github.evil.com</c> does not match <c>github.com</c> — and never a "whichever token we
    /// hold" fallback, so a self-hosted or unknown host gets no token and Git falls back to its
    /// own credential helper.
    /// </summary>
    private static string? TokenRefForHost(GitProfile p, string host)
    {
        if (HostMatches(host, "github.com")) return p.GitHubTokenRef;
        if (HostMatches(host, "gitlab.com")) return p.GitLabTokenRef;
        if (HostMatches(host, "bitbucket.org")) return p.BitbucketTokenRef;
        return null;
    }

    private static bool HostMatches(string host, string domain) =>
        string.Equals(host, domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    private static RepositoryStatus PopulateSizes(RepositoryStatus status, string repoPath)
    {
        return status with
        {
            Staged = PopulateListSizes(status.Staged, repoPath),
            Unstaged = PopulateListSizes(status.Unstaged, repoPath),
            Untracked = PopulateListSizes(status.Untracked, repoPath),
            Conflicted = PopulateListSizes(status.Conflicted, repoPath),
            ComputedAt = DateTimeOffset.Now,
        };
    }

    private static IReadOnlyList<FileStatusEntry> PopulateListSizes(IReadOnlyList<FileStatusEntry> entries, string repoPath)
    {
        if (entries.Count == 0)
            return entries;

        var list = new List<FileStatusEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var size = WorkingTreeFileSize.GetOrZero(Path.Combine(repoPath, entry.Path));
            list.Add(entry.SizeBytes == size ? entry : entry with { SizeBytes = size });
        }
        return list;
    }
}
