namespace Historia.Services.UI;

/// <inheritdoc cref="IRepoNetworkActionRunner"/>
public sealed class RepoNetworkActionRunner : IRepoNetworkActionRunner
{
    private readonly IWorkspaceService _workspace;
    private readonly INotificationService _notifications;
    private readonly IDialogService _dialogs;
    private readonly IStashService _stashes;
    private readonly ITerminalSessionService _terminals;
    private readonly IProfileService _profiles;
    private readonly IRepoIdentityService _identity;
    private readonly IGraphLayoutService _graph;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _ui;

    private readonly object _opsSync = new();
    private readonly Dictionary<RepositoryContext, RunningOp> _ops = [];

    private sealed class RunningOp(string label, CancellationTokenSource cts, ProgressThrottle throttle)
    {
        public string Label { get; } = label;
        public CancellationTokenSource Cts { get; } = cts;
        public ProgressThrottle Throttle { get; } = throttle;
        public GitProgress? Last { get; set; }
    }

    /// <summary>Delivers the latest progress of one operation at most <see cref="MinInterval"/>
    /// apart. A report that arrives inside the interval is held as the single pending value and
    /// delivered by a timer when the interval ends; a report that starts a new phase is delivered
    /// at once and replaces anything pending. <see cref="Complete"/> delivers the end of the
    /// operation (null) at once and discards anything pending. Offered from another thread, a
    /// delivery is posted to the UI thread, so the reader thread never queues more than the
    /// throttle lets through; a posted report that runs after <see cref="Complete"/> is dropped, so
    /// the end is always the last delivery. Subscribers are never called while the throttle's lock
    /// is held.</summary>
    internal sealed class ProgressThrottle : IDisposable
    {
        public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(100);

        private readonly TimeProvider _time;
        private readonly IUiDispatcher _ui;
        private readonly Action<GitProgress?> _deliver;
        private readonly IUiTimer _timer;
        private readonly object _sync = new();
        private GitProgress? _latest;
        private string? _deliveredPhase;
        private long _deliveredAt;
        private bool _hasDelivered;
        private bool _pending;
        private bool _closed;

        public ProgressThrottle(TimeProvider time, IUiDispatcher ui, Action<GitProgress?> deliver)
        {
            _time = time;
            _ui = ui;
            _deliver = deliver;
            _timer = ui.CreateTimer(MinInterval, OnTick);
        }

        public void Offer(GitProgress progress)
        {
            bool deliverNow;
            bool startTimer = false;
            lock (_sync)
            {
                if (_closed)
                    return;
                _latest = progress;
                var phaseChanged = !_hasDelivered || !string.Equals(progress.Phase, _deliveredPhase, StringComparison.Ordinal);
                deliverNow = phaseChanged || _time.GetElapsedTime(_deliveredAt) >= MinInterval;
                if (deliverNow)
                    MarkDelivered(progress);
                else if (!_pending)
                {
                    _pending = true;
                    startTimer = true;
                }
            }

            if (deliverNow)
            {
                _timer.Stop();
                DeliverProgress(progress);
            }
            else if (startTimer)
            {
                _timer.Start();
            }
        }

        public void Complete()
        {
            Dispose();
            if (_ui.CheckAccess())
                _deliver(null);
            else
                _ui.Post(() => _deliver(null), UiPriority.Background);
        }

        /// <summary>Stops delivering without announcing the end of the operation.</summary>
        public void Dispose()
        {
            lock (_sync)
            {
                _closed = true;
                _pending = false;
                _latest = null;
            }
            _timer.Stop();
            _timer.Dispose();
        }

        private void OnTick()
        {
            GitProgress? progress;
            lock (_sync)
            {
                progress = _pending && !_closed ? _latest : null;
                _pending = false;
                if (progress is not null)
                    MarkDelivered(progress);
            }

            _timer.Stop();
            if (progress is not null)
                DeliverProgress(progress);
        }

        private void DeliverProgress(GitProgress progress)
        {
            if (_ui.CheckAccess())
                DeliverUnlessClosed(progress);
            else
                _ui.Post(() => DeliverUnlessClosed(progress), UiPriority.Background);
        }

        // Re-checked where the delivery runs: a report posted just before Complete must not
        // arrive after the end.
        private void DeliverUnlessClosed(GitProgress progress)
        {
            lock (_sync)
            {
                if (_closed)
                    return;
            }
            _deliver(progress);
        }

        private void MarkDelivered(GitProgress progress)
        {
            _hasDelivered = true;
            _deliveredPhase = progress.Phase;
            _deliveredAt = _time.GetTimestamp();
            _pending = false;
        }
    }

    /// <summary>Runs the handler on the reporting thread. <see cref="Progress{T}"/> would post every
    /// git progress line to the UI thread's context before the throttle could drop it.</summary>
    private sealed class SynchronousProgress(Action<GitProgress> handler) : IProgress<GitProgress>
    {
        public void Report(GitProgress value) => handler(value);
    }

    public event EventHandler<RepoNetworkProgress>? ProgressChanged;

    public RepoNetworkActionRunner(
        IRepoActionBus actionBus,
        IWorkspaceService workspace,
        INotificationService notifications,
        IDialogService dialogs,
        IStashService stashes,
        ITerminalSessionService terminals,
        IProfileService profiles,
        IRepoIdentityService identity,
        IGraphLayoutService graph,
        ISettingsService settings,
        IUiDispatcher ui)
    {
        _workspace = workspace;
        _notifications = notifications;
        _dialogs = dialogs;
        _stashes = stashes;
        _terminals = terminals;
        _profiles = profiles;
        _identity = identity;
        _graph = graph;
        _settings = settings;
        _ui = ui;

        actionBus.NetworkActionRequested += (_, request) => _ = RunAsync(request);
    }

    /// <summary>a tab whose selection went stale (profile deleted / default changed)
    /// re-resolves on the next Git operation, not eagerly.</summary>
    private async Task EnsureActiveProfileFreshAsync(RepositoryContext repo)
    {
        try
        {
            var key = repo.IdentityKey ?? await _identity.ResolveAsync(repo, repo.Lifetime);
            var fresh = _profiles.ResolveActiveProfile(key);
            var current = repo.State.ActiveProfile;
            if (fresh?.Id != current?.Id)
                repo.State.SetActiveProfile(fresh);
        }
        catch
        {
            // Resolution failure must not block the operation.
        }
    }

    private void RefreshCommitGraphAfterFetch(RepositoryContext repo, RepoNetworkAction op)
    {
        // A manual Fetch/Pull refreshes only the active tab's commit-graph; its refs
        // cache and graph store already refresh through RefreshAllAsync -> RefsChanged.
        // Other tabs are never queued here.
        try { _graph.RefreshCommitGraphAfterFetch(repo); }
        catch { /* best effort */ }

        Historia.Diagnostics.PerfLog.Mark($"manual {op}: commit-graph refresh requested ({repo.DisplayName})");
    }

    internal static PushSpec BuildPushSpec(string branch, string? remoteBranch, bool targetHasUpstream)
    {
        var refspec = remoteBranch is not null && !string.Equals(branch, remoteBranch, StringComparison.Ordinal)
            ? $"{branch}:{remoteBranch}"
            : branch;
        return new PushSpec
        {
            RefSpec = refspec,
            SetUpstream = !targetHasUpstream,
        };
    }

    internal static bool TargetHasUpstream(IEnumerable<BranchInfo> branches, string branch)
    {
        var target = branches.FirstOrDefault(b => !b.IsRemote && string.Equals(b.Name, branch, StringComparison.Ordinal));
        return target?.UpstreamName is not null;
    }

    /// <summary>The prune confirmation text: prune deletes remote-tracking refs, which can be the
    /// last pointer to some commits, so it names the remote and the scope of the deletion.</summary>
    internal static string PruneEffect(string remote) =>
        $"Prune deletes every remote-tracking branch under '{remote}/' whose branch no longer exists on {remote}. "
        + "Commits reachable only from those refs become unreferenced locally. "
        + "Your local branches are not touched.";

    /// <summary>The remote branch delete confirmation text: names the remote and branch, explains that
    /// the branch is deleted on the remote for everyone who uses it, and clarifies that any local branch
    /// is kept with its upstream shown as gone.</summary>
    internal static string RemoteBranchDeleteEffect(string remote, string branch) =>
        $"Delete '{remote}/{branch}' on {remote} for everyone who uses it. "
        + "Your local branch (if any) is kept, with its upstream shown as gone.";

    internal static string OpLabel(RepoNetworkAction op) => op switch
    {
        RepoNetworkAction.DeleteRemoteBranch => "Delete remote branch",
        RepoNetworkAction.PushTag => "Push tag",
        _ => op.ToString(),
    };

    /// <summary>The error card text for a failed tag push. git refuses a tag the remote already has
    /// as <c>! [rejected] … (already exists)</c>, which classifies as a rejected branch push, so that
    /// case is named here; every other failure keeps its own message.</summary>
    internal static string TagPushFailure(string remote, string tag, HistoriaGitException ex) =>
        ex.Details?.Contains("(already exists)", StringComparison.OrdinalIgnoreCase) == true
            ? $"Couldn't push tag {tag}: {remote} already has a tag named {tag}."
            : $"Couldn't push tag {tag} to {remote}: {HistoriaGitException.Describe(ex.Kind)}.";

    private async Task ExecOpAsync(RepositoryContext repo, RepoNetworkAction op, string remote, string? branch, string? remoteBranch,
        IProgress<GitProgress> progress, CancellationToken ct)
    {
        switch (op)
        {
            case RepoNetworkAction.Fetch:
                await repo.Gateway.FetchAsync(remote, GitFetchOptions.From(_settings.Current), progress, ct);
                RefreshCommitGraphAfterFetch(repo, op);
                _notifications.Publish(NotificationSeverity.Success, $"Fetched {remote}.");
                break;
            case RepoNetworkAction.Pull when branch is not null:
                if (!string.Equals(branch, repo.State.CurrentBranch?.Name, StringComparison.Ordinal))
                {
                    _notifications.Publish(NotificationSeverity.Warning, "Pull only runs on the checked-out branch.");
                    return;
                }
                var targetRemoteBranch = remoteBranch ?? branch;
                await repo.Gateway.PullAsync(remote, targetRemoteBranch, GitPullOptions.From(_settings.Current), progress, ct);
                if (_settings.Current.FetchAfterPull)
                    await FollowUpFetchAsync(repo, remote, targetRemoteBranch, progress, ct);
                RefreshCommitGraphAfterFetch(repo, op);
                _notifications.Publish(NotificationSeverity.Success, $"Pulled {remote}/{targetRemoteBranch}.");
                break;
            case RepoNetworkAction.Push when branch is not null:
                var hasUpstream = TargetHasUpstream(repo.State.Branches, branch);
                var spec = BuildPushSpec(branch, remoteBranch, hasUpstream);
                await repo.Gateway.PushAsync(remote, spec, progress, ct);
                RefreshCommitGraphAfterFetch(repo, op);
                _notifications.Publish(NotificationSeverity.Success, $"Pushed to {remote}.");
                break;
            default:
                _notifications.Publish(NotificationSeverity.Warning, "No current branch to operate on.");
                break;
        }
    }

    /// <summary>A pull is followed by a fetch of every branch and tag so the remote-tracking refs
    /// match the remote; the fetch rides on the pull the user just triggered (no network call the
    /// user did not initiate), not a new timer or poll. A push needs none: git updates the
    /// remote-tracking ref of each branch it pushes. A pull that succeeded must never be reported
    /// as failed because the fetch afterwards did not; the follow-up failing is a warning, not an
    /// error. A cancelled follow-up is a cancelled operation and propagates so the outer handler
    /// reports it the same way as a cancelled pull.</summary>
    private async Task FollowUpFetchAsync(RepositoryContext repo, string remote, string branch,
        IProgress<GitProgress> progress, CancellationToken ct)
    {
        try
        {
            await repo.Gateway.FetchAsync(remote, GitFetchOptions.From(_settings.Current), progress, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HistoriaGitException ex) when (ex.Kind == GitErrorKind.TagConflict)
        {
            _notifications.Publish(NotificationSeverity.Warning,
                $"Pulled {remote}/{branch}, but {TagConflictPrompt.Subject(ex)} on {remote}.", ex.Details,
                TagConflictActions(repo, remote, ex));
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Warning,
                $"Pulled {remote}/{branch}, but the follow-up fetch failed.", ex.NotificationDetails());
        }
    }

    private IReadOnlyList<NotificationAction> TagConflictActions(RepositoryContext repo, string remote, HistoriaGitException ex) =>
        TagConflictPrompt.ActionsFor(repo, remote, ex, this, _dialogs, _notifications);

    /// <summary>
    /// a push rejected as non-fast-forward means <c>remote/branch</c> has commits this
    /// local branch doesn't. Offer the three ways out in a modal the user can't lose behind
    /// later toasts — integrate then re-push (rebase or merge), or overwrite the remote
    /// (force-with-lease). A rebase/merge that stops in conflicts leaves the push for the user
    /// to retry once the resolver panel is dealt with. Returns true when the recovery ran git, so
    /// the caller refreshes once the network gate is released.
    /// </summary>
    private async Task<bool> RecoverFromPushRejectionAsync(
        RepositoryContext repo, string remote, string branch, HistoriaGitException ex,
        IProgress<GitProgress> progress, CancellationToken ct)
    {
        if (!string.Equals(branch, repo.State.CurrentBranch?.Name, StringComparison.Ordinal))
        {
            _notifications.Publish(NotificationSeverity.Warning, ex.Message, ex.Details);
            return false;
        }

        // Left→right: Fetch & merge (blue default) · Fetch & rebase · Force push (red) · Cancel.
        const int Merge = 0, Rebase = 1, Force = 2;
        var choice = await _dialogs.ChooseAsync(
            "Push rejected",
            $"'{remote}/{branch}' has commits that aren't in your local '{branch}'. "
            + "Bring them in and push again, or overwrite the remote with your local branch.",
            [
                "Fetch & merge, then push",
                "Fetch & rebase, then push",
                "Force push (with lease)",
            ],
            defaultIndex: Merge,
            destructiveIndex: Force);

        if (choice < 0)
        {
            // The modal already explained the rejection; Cancel means "leave it" — the record
            // goes to the dev log, not a card the user didn't ask to see again.
            AppLog.Write(AppLogLevel.Info, "declined", $"Push rejected modal cancelled: {ex.Message}", ex);
            return false;
        }

        var setUpstream = !TargetHasUpstream(repo.State.Branches, branch);
        try
        {
            if (choice is Rebase or Merge)
            {
                await repo.Gateway.PullAsync(remote, branch,
                    GitPullOptions.From(_settings.Current) with { Rebase = choice == Rebase }, progress, ct);
                await repo.Gateway.PushAsync(remote,
                    new PushSpec { RefSpec = branch, SetUpstream = setUpstream }, progress, ct);
                _notifications.Publish(NotificationSeverity.Success, $"Pushed to {remote}.");
            }
            else if (choice == Force)
            {
                // Rewrites the *remote* ref: --force-with-lease/--force-if-includes stop us
                // clobbering commits pushed since our last fetch, but not ones we've never had.
                // A second, explicitly destructive confirm (warning glyph, Cancel the
                // primary + Enter button) stands between the modal and an operation that can
                // lose other people's work.
                if (!await _dialogs.ConfirmDestructiveAsync("Force push — destructive",
                        $"This force-pushes {branch} to {remote} and overwrites {remote}/{branch} "
                        + "with your local branch.\n\n"
                        + $"Any commits on {remote}/{branch} that aren't in your local history — "
                        + "including work pushed by other people — are permanently discarded. "
                        + "--force-with-lease only guards against changes made since your last fetch.\n\n"
                        + "Prefer \"Fetch & rebase\" unless you are certain you want to replace the remote branch.",
                        "Force push"))
                    return false;
                await repo.Gateway.PushAsync(remote,
                    new PushSpec { RefSpec = branch, ForceWithLease = true, SetUpstream = setUpstream }, progress, ct);
                _notifications.Publish(NotificationSeverity.Success, $"Force-pushed to {remote}.");
            }
        }
        catch (OperationCanceledException)
        {
            AppLog.Write(AppLogLevel.Info, "declined", "Push cancelled");
        }
        catch (HistoriaGitException retryEx) when (retryEx.Kind == GitErrorKind.MergeConflict)
        {
            // The rebase/merge stopped in conflicts — the refresh below surfaces the resolver
            // panel; the push waits until the user finishes and triggers it again.
            _notifications.Publish(NotificationSeverity.Warning,
                "Resolve the conflicts, then push again.", retryEx.Details);
        }
        catch (Exception retryEx)
        {
            _notifications.Publish(NotificationSeverity.Error,
                retryEx.NotificationMessage("Push"), retryEx.NotificationDetails());
        }

        return true;
    }

    public void Cancel(RepositoryContext repo)
    {
        CancellationTokenSource? cts;
        lock (_opsSync)
            cts = _ops.TryGetValue(repo, out var running) ? running.Cts : null;
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* finished meanwhile */ }
    }

    public GitProgress? CurrentProgress(RepositoryContext repo)
    {
        lock (_opsSync)
            return _ops.TryGetValue(repo, out var running) ? running.Last : null;
    }

    /// <summary>Records <paramref name="progress"/> as the latest of <paramref name="repo"/>'s
    /// running operation and offers it to the operation's throttle. A report that arrives after
    /// the operation ended is dropped.</summary>
    private void Report(RepositoryContext repo, GitProgress progress)
    {
        RunningOp? running;
        lock (_opsSync)
        {
            if (!_ops.TryGetValue(repo, out running))
                return;
            running.Last = progress;
        }
        running.Throttle.Offer(progress);
    }

    private void NotifyBusy(RepositoryContext repo, string label) =>
        _notifications.Publish(NotificationSeverity.Info, $"{label} on {repo.DisplayName} is still running.");

    private static string? FirstDetailLine(HistoriaGitException ex) =>
        (ex.Details ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
        ?? ex.Message;

    private async Task RunAsync(RepoNetworkRequest request)
    {
        var op = request.Action;
        if ((request.Repo ?? _workspace.Focused) is not { } repo)
            return;
        if (BusyLabel(repo) is { } busyWith)
        {
            NotifyBusy(repo, busyWith);
            return;
        }

        if (op == RepoNetworkAction.DeleteRemoteBranch && (string.IsNullOrEmpty(request.Remote) || string.IsNullOrEmpty(request.RemoteBranch)))
        {
            _notifications.Publish(NotificationSeverity.Warning, "No remote branch to delete.");
            AppLog.Write(AppLogLevel.Warn, "invalid_request", "DeleteRemoteBranch requested without Remote or RemoteBranch specified.");
            return;
        }

        if (op == RepoNetworkAction.PushTag && (string.IsNullOrEmpty(request.Remote) || string.IsNullOrEmpty(request.Tag)))
        {
            _notifications.Publish(NotificationSeverity.Warning, "No tag or remote to push to.");
            AppLog.Write(AppLogLevel.Warn, "invalid_request", "PushTag requested without Remote or Tag specified.");
            return;
        }

        string remote;
        if (request.Remote is { } targetRemote)
        {
            remote = targetRemote;
        }
        else
        {
            var remotes = repo.State.Remotes;
            if (remotes.Count == 0)
            {
                _notifications.Publish(NotificationSeverity.Warning, "This repository has no remotes configured.");
                return;
            }
            remote = remotes.Any(r => r.Name == "origin") ? "origin" : remotes[0].Name;
        }

        await RunGatedAsync(repo, OpLabel(op), ct => ExecuteAsync(repo, op, remote, request, ct));
    }

    public Task RunExclusiveAsync(RepositoryContext repo, string label, Func<IProgress<GitProgress>, CancellationToken, Task> operation) =>
        RunGatedAsync(repo, label, async ct =>
        {
            Report(repo, new GitProgress { Phase = $"{label}…" });
            await operation(new SynchronousProgress(p => Report(repo, p)), ct);
            return null;
        });

    /// <summary>Registers <paramref name="label"/> as <paramref name="repo"/>'s running operation,
    /// takes the network gate (see <see cref="EnterGateAsync"/>) and runs <paramref name="body"/>
    /// holding both; a cancellation at any point ends silently. The work <paramref name="body"/>
    /// returns runs after the gate is released, still under the running-operation registration,
    /// so other holders of the gate do not wait for it.</summary>
    private async Task RunGatedAsync(RepositoryContext repo, string label, Func<CancellationToken, Task<Func<Task>?>> body)
    {
        var running = new RunningOp(
            label,
            CancellationTokenSource.CreateLinkedTokenSource(repo.Lifetime),
            new ProgressThrottle(TimeProvider.System, _ui, p => ProgressChanged?.Invoke(this, new RepoNetworkProgress(repo, p))));
        bool registered;
        string? takenBy = null;
        lock (_opsSync)
        {
            registered = _ops.TryAdd(repo, running);
            if (!registered)
                takenBy = _ops[repo].Label;
        }
        if (!registered)
        {
            running.Throttle.Dispose();
            running.Cts.Dispose();
            NotifyBusy(repo, takenBy!);
            return;
        }

        var ct = running.Cts.Token;
        try
        {
            Func<Task>? afterGate = null;
            IDisposable? gate = null;
            try
            {
                gate = await EnterGateAsync(repo, label, ct);
                if (gate is null)
                    return;
                afterGate = await body(ct);
            }
            finally
            {
                gate?.Dispose();
            }

            if (afterGate is not null)
                await afterGate();
        }
        catch (OperationCanceledException)
        {
            AppLog.Write(AppLogLevel.Info, "declined", $"{label} cancelled");
        }
        finally
        {
            lock (_opsSync)
                _ops.Remove(repo);
            running.Cts.Dispose();
            running.Throttle.Complete();
        }
    }

    /// <summary>The label of the user's operation or lock cleanup holding <paramref name="repo"/>,
    /// or null when it is free or only auto-fetch is running (which a request waits for).</summary>
    private string? BusyLabel(RepositoryContext repo)
    {
        lock (_opsSync)
        {
            if (_ops.TryGetValue(repo, out var running))
                return running.Label;
        }
        return repo.CurrentNetworkOperation is { Owner: not NetworkOperationOwner.AutoFetch } holder ? holder.Label : null;
    }

    private const double NetworkGateLogThresholdMs = 50.0;

    /// <summary>Takes <paramref name="repo"/>'s network gate, waiting (cancellably, with a
    /// progress line) when auto-fetch holds it. Null — after a notice — when another kind of
    /// operation took it first.</summary>
    private async Task<IDisposable?> EnterGateAsync(RepositoryContext repo, string label, CancellationToken ct)
    {
        var info = new NetworkOperationInfo(NetworkOperationOwner.User, label);
        if (repo.TryBeginNetworkOperation(info, out var lease))
            return lease;

        if (repo.CurrentNetworkOperation is { Owner: not NetworkOperationOwner.AutoFetch } holder)
        {
            NotifyBusy(repo, holder.Label);
            return null;
        }

        Report(repo, new GitProgress { Phase = "Waiting for auto-fetch…" });
        var holderLabel = repo.CurrentNetworkOperation?.Label ?? "auto-fetch";
        var waitStart = Stopwatch.GetTimestamp();
        var cancelled = false;
        try
        {
            return await repo.BeginNetworkOperationAsync(info, ct);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            throw;
        }
        finally
        {
            var waitMs = Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
            if (waitMs > NetworkGateLogThresholdMs)
                PerfLog.Mark($"network gate wait {waitMs:F0}ms ({repo.DisplayName}, {label}) behind {holderLabel}{(cancelled ? ", cancelled" : "")}");
        }
    }

    /// <summary>Runs the operation under the gate. The returned work — the repository refresh that
    /// shows what the operation changed — is for the caller to run once the gate is released; null
    /// when there is nothing to refresh.</summary>
    private async Task<Func<Task>?> ExecuteAsync(RepositoryContext repo, RepoNetworkAction op, string remote, RepoNetworkRequest request, CancellationToken ct)
    {
        var branch = request.Branch ?? repo.State.CurrentBranch?.Name;

        if (op == RepoNetworkAction.Prune
            && !await _dialogs.ConfirmDestructiveAsync("Prune remote — destructive", PruneEffect(remote), "Prune"))
        {
            AppLog.Write(AppLogLevel.Info, "declined", $"Prune of {remote} declined");
            return null;
        }

        if (op == RepoNetworkAction.DeleteRemoteBranch
            && !await _dialogs.ConfirmDestructiveAsync("Delete remote branch", RemoteBranchDeleteEffect(remote, request.RemoteBranch!), "Delete"))
        {
            AppLog.Write(AppLogLevel.Info, "declined", $"Delete of {remote}/{request.RemoteBranch} declined");
            return null;
        }

        var progress = new SynchronousProgress(p => Report(repo, p));
        Report(repo, new GitProgress { Phase = $"{OpLabel(op)}…" });

        // Prune and remote-branch delete are confirmed above, once; the same delegate serves the first
        // attempt and the Local-config retry so the retry never asks again.
        Func<Task> execute;
        switch (op)
        {
            case RepoNetworkAction.Prune:
                execute = async () =>
                {
                    await repo.Gateway.PruneRemoteAsync(remote, progress, ct);
                    _notifications.Publish(NotificationSeverity.Success, $"Pruned {remote}.");
                };
                break;
            case RepoNetworkAction.DeleteRemoteBranch:
                execute = async () =>
                {
                    await repo.Gateway.DeleteRemoteBranchAsync(remote, request.RemoteBranch!, progress, ct);
                    _notifications.Publish(NotificationSeverity.Success, $"Deleted {remote}/{request.RemoteBranch}.");
                };
                break;
            case RepoNetworkAction.PushTag:
                execute = async () =>
                {
                    await repo.Gateway.PushAsync(remote, PushSpec.ForTag(request.Tag!), progress, ct);
                    _notifications.Publish(NotificationSeverity.Success, $"Pushed tag {request.Tag} to {remote}.");
                };
                break;
            default:
                execute = () => ExecOpAsync(repo, op, remote, branch, request.RemoteBranch, progress, ct);
                break;
        }
        Func<Task>? afterGate = null;
        try
        {
            await EnsureActiveProfileFreshAsync(repo);
            await execute();
            afterGate = () => RefreshAfterOperationAsync(repo, op);
        }
        catch (OperationCanceledException)
        {
            AppLog.Write(AppLogLevel.Info, "declined", $"{op} cancelled");
        }
        catch (HistoriaGitException ex) when (ex.FallbackToLocalConfigAvailable)
        {
            var previous = repo.State.ActiveProfile;
            var confirmed = await _dialogs.ConfirmAsync(
                "Switch to Local config?",
                $"{OpLabel(op)} from '{remote}' failed as '{previous?.DisplayName ?? "the selected profile"}'"
                + (FirstDetailLine(ex) is { } detail ? $":\n\n{detail}\n\n" : ". ")
                + "Switch this repository to Local config and try again?",
                confirmLabel: "Switch and retry");
            if (confirmed)
            {
                try
                {
                    var key = repo.IdentityKey ?? await _identity.ResolveAsync(repo, repo.Lifetime);
                    await _profiles.SetLocalConfigForRepositoryAsync(key, repo.RepositoryPath, repo.Lifetime);
                    repo.State.SetActiveProfile(null);
                    await execute();
                    afterGate = () => RefreshAfterOperationAsync(repo, op);
                }
                catch (Exception retryEx)
                {
                    // persist the switch only if the retry succeeds — roll back on failure.
                    try
                    {
                        var key = repo.IdentityKey ?? await _identity.ResolveAsync(repo, repo.Lifetime);
                        if (previous is not null)
                            await _profiles.BindProfileToRepositoryAsync(key, repo.RepositoryPath, previous.Id, repo.Lifetime);
                        else
                            await _profiles.UnbindRepositoryAsync(key, repo.Lifetime);
                        repo.State.SetActiveProfile(previous);
                    }
                    catch { /* best effort rollback */ }
                    _notifications.Publish(NotificationSeverity.Error, retryEx.NotificationMessage(OpLabel(op)), retryEx.NotificationDetails());
                }
            }
            else
            {
                // The user declined "Switch to Local config?" — nothing changed, so a card would
                // just restate the choice they just made.
                AppLog.Write(AppLogLevel.Info, "declined", $"{op}: switch to Local config declined: {ex.Message}", ex);
            }
        }
        catch (HistoriaGitException ex) when (ex.Kind == GitErrorKind.UncommittedChanges && op == RepoNetworkAction.Pull && branch is not null)
        {
            var pullTarget = request.RemoteBranch ?? branch;
            _notifications.Publish(NotificationSeverity.Error, ex.Message, ex.Details,
            [
                new NotificationAction("Stash & retry", () => BranchActions.StashAndRetryAsync(repo, _notifications, _stashes,
                    () => RunExclusiveAsync(repo, "Pull",
                        (p, pct) => repo.Gateway.PullAsync(remote, pullTarget, GitPullOptions.From(_settings.Current), p, pct)), "Pull")),
            ]);
        }
        catch (HistoriaGitException ex) when (ex.Kind == GitErrorKind.RemoteRejected && op == RepoNetworkAction.Push && branch is not null)
        {
            if (await RecoverFromPushRejectionAsync(repo, remote, branch, ex, progress, ct))
                afterGate = () => RefreshAfterOperationAsync(repo, op);
        }
        catch (HistoriaGitException ex) when (ex.Kind == GitErrorKind.UnrelatedHistories && op == RepoNetworkAction.Pull && branch is not null)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.Message, ex.Details,
            [
                new NotificationAction("Merge anyway", async () =>
                {
                    // finally, not a trailing call: identical trees merge clean, but if they don't,
                    // the merge still throws and the UI needs the refreshed (conflicted) state to
                    // show the resolver panel instead of looking like nothing happened.
                    try
                    {
                        await RunExclusiveAsync(repo, "Pull", (p, pct) => repo.Gateway.PullAsync(remote, branch,
                            GitPullOptions.From(_settings.Current) with { AllowUnrelatedHistories = true }, p, pct));
                    }
                    finally { await repo.State.RefreshAllAsync(repo.Lifetime); }
                }),
                new NotificationAction($"Reset to {remote}/{branch}", async () =>
                {
                    if (!await _dialogs.ConfirmDestructiveAsync("Reset branch",
                            $"Move {branch} to {remote}/{branch}, discarding its current local commits? This cannot be undone.",
                            "Reset"))
                        return;
                    try { await repo.Gateway.ResetAsync($"{remote}/{branch}", GitResetMode.Hard, repo.Lifetime); }
                    finally { await repo.State.RefreshAllAsync(repo.Lifetime); }
                }),
            ]);
        }
        catch (HistoriaGitException ex) when (ex.Kind == GitErrorKind.TagConflict)
        {
            // Every other ref was fetched before the tag was reported, so the refresh shows them;
            // it runs once the gate is free. A pull stops there and never merges.
            afterGate = () =>
            {
                RefreshCommitGraphAfterFetch(repo, op);
                return RefreshAfterOperationAsync(repo, op);
            };
            var pull = op == RepoNetworkAction.Pull;
            _notifications.Publish(pull ? NotificationSeverity.Error : NotificationSeverity.Warning,
                pull
                    ? $"Pull stopped: {TagConflictPrompt.Subject(ex)} on {remote}. Replace it, then pull again."
                    : $"Fetched {remote}, but {TagConflictPrompt.Subject(ex)} there.",
                ex.Details, TagConflictActions(repo, remote, ex));
        }
        catch (HistoriaGitException ex) when (ex.Kind == GitErrorKind.HostKeyVerificationFailed)
        {
            // A brand-new SSH remote: the host's key isn't in known_hosts yet, and git's own
            // non-interactive run (GIT_TERMINAL_PROMPT=0) can't prompt to trust it. An interactive
            // `ssh` connection once is what actually fixes this — Historia can't safely automate
            // trusting an unknown host key on the user's behalf.
            _notifications.Publish(NotificationSeverity.Error, ex.Message, ex.Details,
            [
                new NotificationAction("Open terminal", () =>
                {
                    _ = _terminals.OpenOrFocusAsync(RepoTerminalOrigin.For(repo));
                    return Task.CompletedTask;
                }),
            ]);
        }
        catch (HistoriaGitException ex) when (op == RepoNetworkAction.PushTag)
        {
            _notifications.Publish(NotificationSeverity.Error, TagPushFailure(remote, request.Tag!, ex), ex.Details,
                StaleLockPrompt.ActionsFor(repo, ex, _dialogs, _notifications));
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage(OpLabel(op)), ex.NotificationDetails(),
                StaleLockPrompt.ActionsFor(repo, ex, _dialogs, _notifications));
        }

        return afterGate;
    }

    /// <summary>Refreshes the repository after a network operation, once the gate is free. A failed
    /// refresh is reported like a failed operation; a cancelled one ends silently.</summary>
    private async Task RefreshAfterOperationAsync(RepositoryContext repo, RepoNetworkAction op)
    {
        try
        {
            await repo.State.RefreshAllAsync(repo.Lifetime);
        }
        catch (OperationCanceledException)
        {
            AppLog.Write(AppLogLevel.Info, "declined", $"{op} refresh cancelled");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage(OpLabel(op)), ex.NotificationDetails());
        }
    }
}
