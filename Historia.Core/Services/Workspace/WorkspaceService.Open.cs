namespace Historia.Services.Workspace;

public sealed partial class WorkspaceService
{
    public async Task<RepositoryContext> OpenAsync(string path, CancellationToken ct = default)
    {
        var discovered = await RepositoryDiscovery.DiscoverAsync(path, _cli, ct).ConfigureAwait(false);

        // Duplicate-open guard: focus the existing tab rather than racing on the index lock.
        var normalizedGitDir = NormalizeDir(discovered.GitDirectory);
        RepositoryContext? existing;
        lock (_sync)
        {
            existing = _open.FirstOrDefault(c =>
                string.Equals(NormalizeDir(c.GitDirectory), normalizedGitDir, StringComparison.OrdinalIgnoreCase));
        }
        if (existing is not null)
        {
            // Never raise an event while holding _sync. FocusInternal fires
            // FocusedRepositoryChanged, whose handlers marshal to the UI thread — doing that
            // under the lock deadlocks against any UI-thread reader of OpenRepositories/Focused.
            FocusInternal(existing);
            return existing;
        }

        return await OpenCoreAsync(path, focus: true, initialize: true, ct, discovered: discovered).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared open path. <paramref name="initialize"/> is false only for a session-restored tab
    /// that is not the focused one — its <see cref="InitializeContextAsync"/> is deferred until
    /// it is first focused, so a 15-tab restore costs one repository open, not fifteen.
    /// <paramref name="recordRecent"/> is false for session restore: re-opening the previous
    /// tab-set on launch must not reshuffle (or restamp) the Recent list — that list only moves
    /// when the user explicitly opens or clones a repo.
    /// </summary>
    private async Task<RepositoryContext> OpenCoreAsync(
        string path, bool focus, bool initialize, CancellationToken ct,
        Task<RepositoryContext>? prefetched = null, bool recordRecent = true, DiscoveredRepository? discovered = null)
    {
        var context = await (prefetched ?? RepositoryContext.OpenAsync(path, _contextServices, ct, discovered))
            .ConfigureAwait(false);
        context.Workspace = this;

        lock (_sync)
        {
            _open.Add(context);
            if (!initialize)
                _deferredInit.Add(context);
        }

        if (TestAfterTabAddedBeforeFocus is { } added)
            await added(context).ConfigureAwait(false);
        if (focus)
            FocusInternal(context);
        RaiseOpenChanged();

        if (recordRecent)
            await _recents.AddAsync(context.RepositoryPath, context.DisplayName, ct).ConfigureAwait(false);
        if (initialize)
        {
            lock (_sync) SetForegroundInit(context);
            _ = InitializeContextAsync(context, foreground: true);
        }

        return context;
    }

    /// <summary>Outcome of one <see cref="InitializeContextCoreAsync"/> attempt, used to
    /// decide whether <see cref="RepositoryContext.MarkFirstFullLoadDone"/> should fire.</summary>
    private enum InitOutcome
    {
        /// <summary>Identity resolution and the first refresh both ran to completion.</summary>
        Completed,

        /// <summary>Cancelled because focus moved away mid-attempt (see <see cref="FocusInternal"/>) —
        /// the tab is still open and still needs a full load, so FirstFullLoad must stay unset;
        /// the attempt is requeued and will be retried.</summary>
        Interrupted,

        /// <summary>Cancelled because the tab itself was closed (RepositoryContext.Lifetime)
        /// — nothing left to retry; FirstFullLoad is still marked done so any waiter unblocks.</summary>
        Closed,
    }

    /// <summary>
    /// Resolves the repo identity key and the cascading active profile, then
    /// performs the first full state refresh. Runs off the open path so a large repo does not
    /// hold up the tab appearing.
    /// </summary>
    private async Task InitializeContextAsync(RepositoryContext context, bool foreground)
    {
        // An attempt-scoped token, linked to (not the same as) context.Lifetime, so
        // FocusInternal can cancel just this attempt — interrupting identity/refresh work for a
        // tab that lost focus — without tearing down the tab itself (only a real close does that).
        CancellationTokenSource attemptCts;
        lock (_sync)
        {
            // One attempt per tab at a time; the running one covers this request. Trap: the background
            // queue can already have started an attempt for the tab (a watcher refresh queued
            // between the tab joining _open and taking focus). Starting a second one here would
            // leave only the second tracked, so a focus change would cancel it while the first
            // kept the tab's status running on a token focus loss can't cancel.
            if (_attemptCts.ContainsKey(context))
                return;
            attemptCts = CancellationTokenSource.CreateLinkedTokenSource(context.Lifetime);
            _attemptCts[context] = attemptCts;
        }

        try
        {
            var outcome = await InitializeContextCoreAsync(context, foreground, attemptCts.Token).ConfigureAwait(false);
            if (TestBeforeInitSettles is { } hook)
                await hook(context).ConfigureAwait(false);

            // Settle under the lock FocusInternal and RequestBackgroundRefreshAsync decide under:
            // a completed load is claimed (its attempt entry removed) and marked done in one step,
            // so no focus change or refresh ever sees a finished load that still reads as loading.
            // If a focus change took the attempt first, the tab is already requeued and this
            // attempt counts as interrupted. FirstFullLoad runs its continuations asynchronously,
            // so completing it here runs nothing under the lock.
            lock (_sync)
            {
                if (outcome == InitOutcome.Completed
                    && !(_attemptCts.TryGetValue(context, out var current) && ReferenceEquals(current, attemptCts)))
                    outcome = context.Lifetime.IsCancellationRequested ? InitOutcome.Closed : InitOutcome.Interrupted;
                if (outcome != InitOutcome.Interrupted)
                {
                    if (_attemptCts.TryGetValue(context, out var mine) && ReferenceEquals(mine, attemptCts))
                        _attemptCts.Remove(context);
                    context.MarkFirstFullLoadDone(); // unblock any auto-fetch waiter
                }
            }

            // Trap: starting this status inside the attempt, with the load marked done only
            // afterwards, lets a focus change or a focus-cancelled refresh in between requeue the
            // finished tab as an init; the queue re-runs it, and its refresh joins this status run
            // on a token focus loss can't cancel. So it starts here, after the load is settled.
            if (outcome == InitOutcome.Completed && foreground)
                _ = RefreshStatusAfterInitAsync(context);
        }
        finally
        {
            lock (_sync)
            {
                if (_attemptCts.TryGetValue(context, out var current) && ReferenceEquals(current, attemptCts))
                    _attemptCts.Remove(context);
                if (foreground && ReferenceEquals(_foregroundInit, context))
                    SetForegroundInit(null);
            }
            attemptCts.Dispose();
        }
    }

    private static readonly AsyncLocal<Func<RepositoryContext, Task>?> _testAfterTabAddedBeforeFocus = new();

    /// <summary>Test seam: awaited on the open path once the tab is in the open set and before it
    /// takes focus, in the flow that opened it.</summary>
    internal static Func<RepositoryContext, Task>? TestAfterTabAddedBeforeFocus
    {
        get => _testAfterTabAddedBeforeFocus.Value;
        set => _testAfterTabAddedBeforeFocus.Value = value;
    }

    /// <summary>Test seam: true while an init attempt for <paramref name="context"/> is
    /// running.</summary>
    internal bool HasInitAttemptForTesting(RepositoryContext context)
    {
        lock (_sync)
            return _attemptCts.ContainsKey(context);
    }

    private static readonly AsyncLocal<Func<RepositoryContext, Task>?> _testBeforeInitSettles = new();

    /// <summary>Test seam: awaited when an init attempt's work has ended, before the attempt
    /// settles as completed or interrupted — in the flow that opened the tab.</summary>
    internal static Func<RepositoryContext, Task>? TestBeforeInitSettles
    {
        get => _testBeforeInitSettles.Value;
        set => _testBeforeInitSettles.Value = value;
    }

    private async Task<InitOutcome> InitializeContextCoreAsync(RepositoryContext context, bool foreground, CancellationToken attemptToken)
    {
        // Let the graph control's own first walk for this context proceed
        // uncontended. Without this, identity resolution below (and RefreshAllAsync after
        // it) starts within a few hundred ms of the tab opening and can compete for CPU/IO
        // on a large repo. The focused tab's graph WILL paint, so
        // wait for that (large stuck-guard ceiling); a background force-init has no graph
        // attached, so only the short ceiling applies. Task.WhenAny never throws or propagates
        // either branch's outcome, so no try/catch is needed here.
        var ceiling = foreground ? GraphFirstPaintCeiling : GraphPriorityTimeout;
        await Task.WhenAny(context.FirstGraphPaint, Task.Delay(ceiling, attemptToken)).ConfigureAwait(false);

        // A focus switch cancels the attempt, which also ends the wait above. Never start identity
        // or refresh work for a tab the user already left — the background queue retries it.
        if (attemptToken.IsCancellationRequested)
            return context.Lifetime.IsCancellationRequested ? InitOutcome.Closed : InitOutcome.Interrupted;

        // Identity resolution and the first refs/status refresh don't depend on each
        // other (refresh never reads the active profile identity sets) — run them concurrently.
        // Foreground tabs refresh refs here to unlock FirstFullLoad immediately, then the caller
        // kicks the status refresh once the load is marked complete; background init runs RefreshAllAsync
        // to fully hydrate before promotion. Both use attemptToken so a focus-loss interrupt
        // cancels both at once.
        var identityTask = ResolveIdentityAsync(context, attemptToken);
        var refreshTask = foreground
            ? context.State.RefreshRefsAsync(attemptToken)
            : context.State.RefreshAllAsync(attemptToken);

        try
        {
            // identityTask never throws (it swallows everything itself, including cancellation),
            // so only refreshTask can fault this — an OperationCanceledException here means the
            // tab closed or its attempt was interrupted mid-refresh.
            await Task.WhenAll(identityTask, refreshTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Classified below.
        }

        if (attemptToken.IsCancellationRequested)
            return context.Lifetime.IsCancellationRequested ? InitOutcome.Closed : InitOutcome.Interrupted;

        return InitOutcome.Completed;
    }

    /// <summary>The tab's status right after its first load: at once while it keeps focus, not
    /// paced against earlier refreshes (it still opens the next pacing window), and from the
    /// background queue otherwise (see <see cref="RequestBackgroundRefreshAsync"/>).</summary>
    private async Task RefreshStatusAfterInitAsync(RepositoryContext context)
    {
        try
        {
            bool isFocused;
            lock (_sync)
                isFocused = ReferenceEquals(_focused, context);

            await (isFocused
                ? RefreshUnpacedAsync(context, full: false, context.Lifetime)
                : RequestRefreshAsync(context, full: false, context.Lifetime)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Tab closed.
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "workspace", $"Failed to refresh status after init: {context.DisplayName}", ex);
        }
    }

    /// <summary>Repo→profile identity resolution and the cascading active-profile bind/touch/git-config
    /// write. Never throws — a failure here must not stop the repo from
    /// opening, and cancellation is classified by the caller against <see cref="InitOutcome"/>.</summary>
    private async Task ResolveIdentityAsync(RepositoryContext context, CancellationToken ct)
    {
        try
        {
            var key = await _identity.ResolveAsync(context, ct).ConfigureAwait(false);

            // Keep the binding's last-known path fresh — the rename case working as intended.
            var existingBinding = _profiles.GetProfileForRepository(key);
            if (existingBinding is not null)
                await _profiles.BindProfileToRepositoryAsync(key, context.RepositoryPath, existingBinding.Id, ct).ConfigureAwait(false);

            var profile = _profiles.ResolveActiveProfile(key);
            context.State.SetActiveProfile(profile);
            if (profile is not null)
            {
                await _profiles.TouchProfileAsync(profile.Id, ct).ConfigureAwait(false);

                // when the repo has no existing user.name/user.email, write the profile's
                // values. A mismatch with existing values is surfaced by the repo view as a
                // non-blocking banner rather than being overwritten here.
                if (!context.IsBare)
                {
                    var (name, email) = await context.Gateway.GetConfiguredIdentityAsync(ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(email))
                    {
                        // Check the attempt token before starting the git-config write, then run
                        // the write itself with CancellationToken.None to prevent a focus switch
                        // from killing the git process and leaving .git/config.lock behind.
                        // The write keeps its own 15 s per-call timeout (ProfileService.SetConfigAsync),
                        // so an uncancellable write still can't hang forever.
                        ct.ThrowIfCancellationRequested();
                        await _profiles.ApplyProfileToGitConfigAsync(profile, context.RepositoryPath, GitConfigScope.Local, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Classified by the caller.
        }
        catch
        {
            // Identity/profile resolution failure must not stop the repo from opening.
        }
    }

    public async Task<RepositoryContext> InitAsync(string path, string? initialBranch, CancellationToken ct = default)
    {
        Directory.CreateDirectory(path);

        var args = new List<string> { "init" };
        if (!string.IsNullOrWhiteSpace(initialBranch))
        {
            args.Add("-b");
            args.Add(initialBranch);
        }

        args.Add(path);
        var result = await _cli.RunAsync(new GitCliRequest
        {
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar)) ?? path,
        }, ct).ConfigureAwait(false);

        // IsGitAvailable is only meaningful once RunAsync has waited for git to be located.
        if (!result.IsSuccess && !_cli.IsGitAvailable)
        {
            throw new HistoriaGitException(
                GitErrorKind.Unknown,
                $"Git is required to initialize a repository. Install Git {GitExecutableInfo.MinimumVersion} or newer, or set its path in Settings.",
                result.StandardError);
        }

        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, "Initialize repository");

        return await OpenAsync(path, ct).ConfigureAwait(false);
    }

    public async Task<RepositoryContext> CloneAsync(
        string url,
        string targetDirectory,
        CloneOptions options,
        IProgress<GitProgress>? progress,
        CancellationToken ct = default)
    {
        if (!_cli.IsGitAvailable)
            throw new HistoriaGitException(GitErrorKind.Unknown, "Git is required to clone. Install it or set its path in Settings.");

        ValidateCloneUrl(url);

        var createdTarget = !Directory.Exists(targetDirectory)
                            || !Directory.EnumerateFileSystemEntries(targetDirectory).Any();
        var parent = Path.GetDirectoryName(targetDirectory.TrimEnd(Path.DirectorySeparatorChar))
                     ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(parent);

        var args = new List<string> { "clone", "--progress" };
        if (options.Recursive) args.Add("--recurse-submodules");
        if (options.Depth is { } d and > 0)
        {
            args.Add("--depth");
            args.Add(d.ToString());
        }
        if (options.Sparse) args.Add("--sparse");

        // "--" marks the end of options: without it, a pasted URL beginning with '-' (e.g.
        // "--upload-pack=<command>") would be parsed by Git as another option rather than the
        // repository address.
        args.Add("--");
        args.Add(url);
        args.Add(targetDirectory);

        var result = await _cli.RunAsync(new GitCliRequest
        {
            Arguments = args,
            WorkingDirectory = parent,
            Timeout = Timeout.InfiniteTimeSpan,
            StreamOutput = true,
        }, progress, onStdoutLine: null, ct).ConfigureAwait(false);

        if (result.ErrorKind == GitErrorKind.Cancelled || ct.IsCancellationRequested)
        {
            // Delete the partial target only if Historia created it — never a pre-existing dir.
            if (createdTarget)
                TryDeleteDirectory(targetDirectory);
            throw new OperationCanceledException(ct);
        }

        if (!result.IsSuccess)
        {
            if (createdTarget)
                TryDeleteDirectory(targetDirectory);
            throw HistoriaGitException.FromCli(result, "Clone");
        }

        var context = await RepositoryContext.OpenAsync(targetDirectory, _contextServices).ConfigureAwait(false);
        context.Workspace = this;
        if (_services.GetService(typeof(Historia.Services.Graph.IGraphLayoutService))
            is Historia.Services.Graph.IGraphLayoutService graphSvc)
        {
            try
            {
                await graphSvc.PrecacheClonedRepositoryAsync(context, TabGraphQuery, progress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancellation during pre-cache keeps the clone and opens the tab.
            }
            catch (Exception ex)
            {
                Historia.Diagnostics.PerfLog.Mark($"PrecacheClonedRepositoryAsync failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var normalizedGitDir = NormalizeDir(context.GitDirectory);
        RepositoryContext? existing;
        lock (_sync)
        {
            existing = _open.FirstOrDefault(c =>
                string.Equals(NormalizeDir(c.GitDirectory), normalizedGitDir, StringComparison.OrdinalIgnoreCase));
        }
        if (existing is not null)
        {
            await context.DisposeAsync().ConfigureAwait(false);
            FocusInternal(existing);
            return existing;
        }

        return await OpenCoreAsync(
            targetDirectory, focus: true, initialize: true, CancellationToken.None, prefetched: Task.FromResult(context)).ConfigureAwait(false);
    }

    /// <summary>Under <see cref="_sync"/>: the contiguous index range occupied by
    /// <paramref name="member"/>'s group — the owner (or <paramref name="member"/> itself when
    /// it has no <see cref="RepositoryContext.LinkOwner"/>) plus every tab whose
    /// <c>LinkOwner</c> is that owner. Groups do not nest, so this walks up at most one level.
    /// Returns <c>(0, 0)</c> when the owner isn't found in <see cref="_open"/>.</summary>
    private (int Start, int Count) GroupSpan(RepositoryContext member)
    {
        var owner = member.LinkOwner ?? member;
        var start = _open.IndexOf(owner);
        if (start < 0)
            return (0, 0);
        var count = 1;
        while (start + count < _open.Count && ReferenceEquals(_open[start + count].LinkOwner, owner))
            count++;
        return (start, count);
    }

    public async Task<RepositoryContext> OpenSubmoduleAsync(RepositoryContext owner, SubmoduleInfo submodule, CancellationToken ct = default)
    {
        var root = owner.LinkOwner ?? owner;
        var submodulePath = Path.Combine(owner.RepositoryPath, submodule.Path);
        var dotGit = Path.Combine(submodulePath, ".git");
        if (!File.Exists(dotGit) && !Directory.Exists(dotGit))
        {
            throw new HistoriaGitException(GitErrorKind.NotAGitRepository,
                $"Submodule '{submodule.Name}' isn't initialized. Use Init & update first.");
        }

        var discovered = await RepositoryDiscovery.DiscoverAsync(submodulePath, _cli, ct).ConfigureAwait(false);

        var normalizedGitDir = NormalizeDir(discovered.GitDirectory);
        bool orderChanged = false;
        RepositoryContext? existing;
        lock (_sync)
        {
            existing = _open.FirstOrDefault(c =>
                string.Equals(NormalizeDir(c.GitDirectory), normalizedGitDir, StringComparison.OrdinalIgnoreCase));

            if (existing is not null && (ReferenceEquals(existing, root) || ReferenceEquals(existing, owner)))
            {
                throw new HistoriaGitException(GitErrorKind.NotAGitRepository,
                    $"Submodule '{submodule.Name}' isn't initialized. Use Init & update first.");
            }

            if (existing is not null)
            {
                if (existing.LinkOwner is null &&
                    !ReferenceEquals(existing, root) &&
                    !_open.Any(t => ReferenceEquals(t.LinkOwner, existing)))
                {
                    existing.LinkOwner = root;
                    var oldIndex = _open.IndexOf(existing);
                    _open.RemoveAt(oldIndex);
                    var (rStart, rCount) = GroupSpan(root);
                    var insertAt = Math.Clamp(rStart + rCount, 0, _open.Count);
                    _open.Insert(insertAt, existing);
                    orderChanged = oldIndex != insertAt;
                }
            }
        }

        if (existing is not null)
        {
            FocusInternal(existing);
            if (orderChanged)
                RaiseOpenChanged();
            return existing;
        }

        var context = await RepositoryContext.OpenAsync(submodulePath, _contextServices, ct, discovered).ConfigureAwait(false);
        context.Workspace = this;
        context.LinkOwner = root;

        lock (_sync)
        {
            var (start, count) = GroupSpan(root);
            var insertAt = Math.Clamp(start + count, 0, _open.Count);
            _open.Insert(insertAt, context);
        }

        FocusInternal(context);
        RaiseOpenChanged();

        lock (_sync) SetForegroundInit(context);
        _ = InitializeContextAsync(context, foreground: true);

        return context;
    }
}
