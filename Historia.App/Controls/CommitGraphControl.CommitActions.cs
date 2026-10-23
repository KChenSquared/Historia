namespace Historia.Controls;

// The graph's commit context menu (built by GraphContextMenu, with this control as its
// IGraphMenuActions), the drop-intent menu, and the git operations they invoke (checkout, create
// branch/tag, reset, cherry-pick, revert, merge, delete), as part of the CommitGraphControl
// partial class. The Avalonia Pen/Brush render pass, hit-testing and layout live in
// CommitGraphControl.cs.
public sealed partial class CommitGraphControl : IGraphMenuActions
{
    private void CheckoutRefEntry(DecorationType type, string label, bool isLocal, string[]? remotes)
    {
        if (type is DecorationType.LocalBranch or DecorationType.RemoteBranch)
        {
            CheckoutFromBadge(new GraphDecoration
            {
                RowIndex = 0,
                Type = type,
                Label = label,
                ColorIndex = 0,
                IsLocal = isLocal,
                RemotesOrNull = remotes,
            });
            return;
        }
        if (type == DecorationType.Tag && _context is { } ctx)
            _ = RunConfirmedDetachedCheckout(ctx, label, $"Check out tag '{label}' in a detached HEAD?");
    }

    private async Task RunConfirmedDetachedCheckout(RepositoryContext ctx, string refish, string prompt)
    {
        if (!await _dialogs.ConfirmAsync("Detached checkout", prompt))
            return;
        await BranchActions.RunWithStashRecovery(ctx, _notifications, _stashes,
            () => ctx.Gateway.CheckoutAsync(refish, new GitCheckoutOptions(), ctx.Lifetime), "Checkout");
    }

    // ── Context menus ──

    /// <summary>Opens the one context menu for the commit on <paramref name="row"/>, selecting
    /// it first; <paramref name="clicked"/> is the label that was right-clicked, whose refs lead
    /// the menu.</summary>
    private void ShowGraphContextMenu(int row, GraphDecoration? clicked)
    {
        if (_layout is not { } l || row < 0 || row >= l.Layout.RowCount)
            return;
        _state?.SetSelectedCommit(l.Layout.Commits[row].Id);
        if (BuildGraphContextMenuItems(row, clicked) is { Count: > 0 } items)
            OpenMenu(new ContextMenu { ItemsSource = items });
    }

    internal IReadOnlyList<Control> BuildGraphContextMenuItems(int row, GraphDecoration? clicked)
    {
        if (_context is not { } ctx || _layout is not { } l || row < 0 || row >= l.Layout.RowCount)
            return [];
        var current = _state?.CurrentBranch?.Name;
        IReadOnlyList<GraphDecoration> rowDecorations = DecorationsByRow(l.Layout).TryGetValue(row, out var decos) ? decos : [];
        IReadOnlyList<GitObjectId> multi = _multiSelect.Count > 1
            ? _multiSelect.OrderByDescending(r => r).Select(r => l.Layout.Commits[r].Id).ToList()
            : [];
        var target = new GraphMenuTarget(
            l.Layout.Commits[row].Id,
            GraphContextMenu.RefsForRow(rowDecorations, clicked, current),
            current,
            ctx.State.Remotes.Select(r => r.Name).ToList(),
            multi);
        return GraphContextMenu.Build(target, this);
    }

    // ── Context-menu operations ──

    Task IGraphMenuActions.CheckoutBranch(string name)
        => _context is { } ctx ? BranchActions.CheckoutAsync(ctx, _notifications, _stashes, name) : Task.CompletedTask;

    Task IGraphMenuActions.CheckoutTracking(string name, string trackingRef)
        => _context is { } ctx ? BranchActions.CheckoutTrackingAsync(ctx, _notifications, _stashes, name, trackingRef) : Task.CompletedTask;

    Task IGraphMenuActions.CheckoutDetached(string sha, string shortSha)
        => _context is { } ctx ? RunConfirmedDetachedCheckout(ctx, sha, $"Check out {shortSha} in a detached HEAD?") : Task.CompletedTask;

    Task IGraphMenuActions.CreateBranch(string sha, string shortSha)
        => _context is { } ctx ? PromptCreateBranch(ctx, sha, shortSha) : Task.CompletedTask;

    Task IGraphMenuActions.CreateTag(string sha, string shortSha)
        => _context is { } ctx ? PromptCreateTag(ctx, sha, shortSha) : Task.CompletedTask;

    Task IGraphMenuActions.Merge(string refish, string display)
        => _context is { } ctx ? MergeAsync(ctx, refish, display) : Task.CompletedTask;

    Task IGraphMenuActions.Rebase(string onto)
    {
        _nav.RequestNavigate($"rebase?onto={Uri.EscapeDataString(onto)}");
        return Task.CompletedTask;
    }

    Task IGraphMenuActions.CherryPick(IReadOnlyList<GitObjectId> ids) => CherryPickAsync(ids);

    Task IGraphMenuActions.Revert(IReadOnlyList<GitObjectId> ids) => RevertAsync(ids);

    Task IGraphMenuActions.Reset(string sha, GitResetMode mode, string shortSha)
        => _context is { } ctx ? ResetAsync(ctx, sha, mode, shortSha) : Task.CompletedTask;

    async Task IGraphMenuActions.Rename(string name)
    {
        if (_context is not { } ctx)
            return;
        var newName = await _dialogs.PromptAsync("Rename branch", $"New name for '{name}':", name);
        if (string.IsNullOrWhiteSpace(newName) || newName == name)
            return;
        await Guard(() => ctx.Gateway.RenameBranchAsync(name, newName.Trim(), ctx.Lifetime), ctx, "Rename");
    }

    Task IGraphMenuActions.DeleteLocal(string name)
        => _context is { } ctx ? BranchActions.DeleteAsync(ctx, _dialogs, _notifications, name) : Task.CompletedTask;

    Task IGraphMenuActions.DeleteRemote(string remote, string name)
    {
        if (_context is { } ctx)
            _actionBus.RequestNetworkAction(new RepoNetworkRequest(RepoNetworkAction.DeleteRemoteBranch, remote, null, name, ctx));
        return Task.CompletedTask;
    }

    // The only way a tag reaches a remote: one unforced push per remote, offered whether or not
    // the remote already has it; a refusal comes back as an error card.
    Task IGraphMenuActions.PushTag(string tag, string remote)
    {
        if (_context is { } ctx)
            _actionBus.RequestNetworkAction(new RepoNetworkRequest(RepoNetworkAction.PushTag, remote, null, Repo: ctx, Tag: tag));
        return Task.CompletedTask;
    }

    Task IGraphMenuActions.DeleteTag(string tag)
        => _context is { } ctx ? ConfirmAndDeleteTagAsync(ctx, tag) : Task.CompletedTask;

    Task IGraphMenuActions.Copy(string text)
    {
        CopyText(text);
        return Task.CompletedTask;
    }

    Task IGraphMenuActions.CopyMessage(GitObjectId id)
    {
        CopyCommitMessage(id);
        return Task.CompletedTask;
    }

    /// <summary>The push/pull/fast-forward items for a branch against its counterpart: a local
    /// branch against its upstream, a remote branch against the local branch of the same name.</summary>
    async Task<IReadOnlyList<Control>> IGraphMenuActions.DivergenceItemsAsync(GraphMenuRef branch)
    {
        if (_context is not { } ctx)
            return [];
        var name = branch.Name;
        string? counterpartRef = null, remoteAlias = null;
        if (branch.Type == DecorationType.LocalBranch)
        {
            var local = _state?.Branches.FirstOrDefault(b => !b.IsRemote && b.Name == name);
            if (local?.UpstreamName is { } upstream)
            {
                counterpartRef = upstream;
                remoteAlias = _state?.Branches.FirstOrDefault(b => b.IsRemote && b.Name == upstream)?.RemoteName;
            }
        }
        else if (branch.Remote is { } remote)
        {
            if (_state?.Branches.Any(b => !b.IsRemote && b.Name == name) ?? false)
                counterpartRef = name;
            remoteAlias = remote;
        }
        if (counterpartRef is null || remoteAlias is null)
            return [];

        var isLocal = branch.Type == DecorationType.LocalBranch;
        var target = new BranchMenu.Target(
            Name: name, CopyName: name, TrackingRef: isLocal ? name : $"{remoteAlias}/{name}",
            IsLocal: isLocal, TipSha: null, CurrentBranch: _state?.CurrentBranch?.Name,
            CounterpartRef: counterpartRef, RemoteAlias: remoteAlias);
        return await BranchMenu.BuildDivergenceItemsAsync(target, ctx.Gateway, _actionBus, ctx.Lifetime);
    }

    private async Task ConfirmAndDeleteTagAsync(RepositoryContext ctx, string name)
    {
        if (!await _dialogs.ConfirmDestructiveAsync("Delete tag", $"Delete local tag '{name}'? This cannot be undone.", "Delete"))
            return;
        await Guard(() => ctx.Gateway.DeleteTagAsync(name, ctx.Lifetime), ctx, "Delete tag");
    }

    /// <summary>Prompt for a new branch at <paramref name="sha"/> and create it.
    /// Used by the graph context menu.</summary>
    private Task PromptCreateBranch(RepositoryContext ctx, string sha, string shortSha)
        => BranchMenu.PromptCreateBranch(ctx, _dialogs, _notifications, _stashes, sha, shortSha);

    /// <summary>Prompt for a new lightweight tag at <paramref name="sha"/>.</summary>
    private Task PromptCreateTag(RepositoryContext ctx, string sha, string shortSha)
        => BranchMenu.PromptCreateTag(ctx, _dialogs, _notifications, sha, shortSha);

    private void CheckoutFromBadge(GraphDecoration deco)
    {
        if (_context is not { } ctx)
            return;
        if (deco.Type is not (DecorationType.LocalBranch or DecorationType.RemoteBranch))
            return;

        _ = CheckoutThenMaybeSyncAsync(ctx, deco);
    }

    // Resolved on use: only a badge pull needs it, and it shares the repository's network gate
    // with Fetch / Pull / Push and auto-fetch.
    private Historia.Services.UI.IRepoNetworkActionRunner NetworkRunner =>
        _services.GetRequiredService<Historia.Services.UI.IRepoNetworkActionRunner>();

    /// <summary>Double-clicking a branch badge checks the branch out through the shared checkout:
    /// a remote badge with no local counterpart creates a tracking branch, every other badge checks
    /// the local branch out. When a remote badge switched to an existing local branch — a
    /// local-badge double-click stays a plain, silent checkout — it then measures divergence against
    /// that remote and either fast-forwards silently (a guaranteed no-loss fast-forward) or asks
    /// before pulling a merge (the local branch also has commits the remote lacks).</summary>
    internal async Task CheckoutThenMaybeSyncAsync(RepositoryContext ctx, GraphDecoration deco)
    {
        var name = deco.Label;
        var remotes = deco.RemotesOrNull ?? [];
        var hadLocal = ctx.State.Branches.Any(b => !b.IsRemote && string.Equals(b.Name, name, StringComparison.Ordinal));
        var tracking = remotes.Length > 0 ? $"{remotes[0]}/{name}" : name;

        await BranchActions.CheckoutBranchAsync(ctx, _notifications, _stashes, name, tracking, deco.IsLocal);

        // The checkout reports failures itself and returns normally, so HEAD is the only reliable signal.
        if (!string.Equals(ctx.State.CurrentBranch?.Name, name, StringComparison.Ordinal))
            return;

        // A branch just created from the remote is level with it; only an existing one can lag.
        if (deco.IsLocal || !hadLocal || remotes.Length == 0)
            return;
        var remote = remotes[0];
        var upstream = $"{remote}/{name}";

        AheadBehind? ab;
        try { ab = await ctx.Gateway.GetDivergenceAsync(name, upstream, ctx.Lifetime); }
        catch { return; }
        if (ab is not { Behind: > 0 } div)
            return;

        if (div.Ahead == 0)
        {
            // ahead == 0 && behind > 0: a guaranteed fast-forward — nothing local to lose.
            await BranchActions.RunWithStashRecovery(ctx, _notifications, _stashes,
                () => NetworkRunner.RunExclusiveAsync(ctx, "Pull",
                    (p, ct) => ctx.Gateway.PullAsync(remote, name, GitPullOptions.From(_settings.Current), p, ct)), "Pull");
            return;
        }

        if (!await _dialogs.ConfirmAsync("Pull and merge?",
                $"{upstream} has {div.Behind} commit(s) you don't have, and you have {div.Ahead} it doesn't. Pull and merge?"))
            return;
        await BranchActions.RunWithStashRecovery(ctx, _notifications, _stashes,
            () => NetworkRunner.RunExclusiveAsync(ctx, "Pull",
                (p, ct) => ctx.Gateway.PullAsync(remote, name, GitPullOptions.From(_settings.Current), p, ct)), "Pull");
    }

    private void ShowDropIntentMenu(GitObjectId commitId, string shortSha, string branchLabel)
    {
        if (_context is not { } ctx)
            return;
        var items = new List<Control>
        {
            new MenuItem { Header = $"Drop {shortSha} → {branchLabel}", IsEnabled = false },
            new Separator(),
            MenuLeaf($"Merge {shortSha} into current branch", () => MergeAsync(ctx, commitId.ToString(), shortSha)),
            MenuLeaf("Reset current branch to this commit (mixed)", () => ResetAsync(ctx, commitId.ToString(), GitResetMode.Mixed, shortSha)),
            MenuLeaf($"Cherry-pick {shortSha} onto current branch", () => CherryPickAsync([commitId])),
            MenuLeaf($"Rebase current branch onto {branchLabel}", () => _nav.RequestNavigate($"rebase?onto={Uri.EscapeDataString(branchLabel)}")),
        };
        OpenMenu(new ContextMenu { ItemsSource = items });
    }

    private async Task ResetAsync(RepositoryContext ctx, string sha, GitResetMode mode, string shortSha)
    {
        var target = _state?.CurrentBranch?.Name ?? "HEAD";
        if (mode == GitResetMode.Hard)
        {
            if (!await _dialogs.ConfirmDestructiveAsync("Reset branch",
                    $"Move {target} to {shortSha} (hard)? Uncommitted changes will be lost.", "Reset"))
                return;
        }
        else
        {
            // soft/mixed only rewrites the branch pointer, but when the target
            // isn't an ancestor of HEAD the commits between them leave the branch — reflog-
            // recoverable, but no longer reachable from any branch. Count with `rev-list --count`
            // (not libgit2's CalculateHistoryDivergence, which takes ~22 s on far-apart
            // commits) and only escalate to the destructive confirm when commits actually leave. If
            // the count itself fails, fall back to the ordinary confirm rather than block reset.
            int leaving;
            try { leaving = await ctx.Gateway.CountCommitsLeavingAsync(sha, ctx.Lifetime); }
            catch { leaving = 0; }
            if (leaving > 0)
            {
                if (!await _dialogs.ConfirmDestructiveAsync("Reset branch",
                        $"Move {target} to {shortSha} ({mode})? {leaving} commit(s) will leave " +
                        $"{target}; they stay recoverable only through the reflog.", "Reset"))
                    return;
            }
            else if (!await _dialogs.ConfirmAsync("Reset branch",
                         $"Move {target} to {shortSha} ({mode})?", confirmLabel: "Reset"))
            {
                return;
            }
        }
        await Guard(() => ctx.Gateway.ResetAsync(sha, mode, ctx.Lifetime), ctx, "Reset");
    }

    private async Task CherryPickAsync(IReadOnlyList<GitObjectId> ids)
    {
        if (_context is not { } ctx)
            return;
        var isMerge = ids.Count == 1 && await CommitMergeDetection.IsMergeAsync(_layout, _layoutQuery, ctx.Gateway, ids[0], ctx.Lifetime);
        var mainline = isMerge ? 1 : (int?)null;
        if (!await _dialogs.ConfirmAsync("Cherry-pick",
                $"Cherry-pick {ids.Count} commit(s) onto {_state?.CurrentBranch?.Name ?? "the current branch"}?"
                + (isMerge ? " (merge commit — mainline parent 1)" : "")))
            return;
        try
        {
            await _cherryPick.CherryPickAsync(ctx, ids, noCommit: false, mainline, ctx.Lifetime);
            var c = ctx.State.Status.Conflicted.Count;
            _notifications.Publish(c > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success,
                c > 0 ? $"Cherry-pick paused on {c} conflict(s)." : "Cherry-pick complete.");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.Message, ex.NotificationDetails());
        }
    }

    private async Task RevertAsync(IReadOnlyList<GitObjectId> ids)
    {
        if (_context is not { } ctx)
            return;
        var isMerge = ids.Count == 1 && await CommitMergeDetection.IsMergeAsync(_layout, _layoutQuery, ctx.Gateway, ids[0], ctx.Lifetime);
        if (!await _dialogs.ConfirmAsync("Revert", $"Revert {ids.Count} commit(s)?" + (isMerge ? " (merge commit — mainline parent 1)" : "")))
            return;
        try
        {
            await _cherryPick.RevertAsync(ctx, ids, isMerge ? 1 : null, ctx.Lifetime);
            var c = ctx.State.Status.Conflicted.Count;
            _notifications.Publish(c > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success,
                c > 0 ? $"Revert paused on {c} conflict(s)." : "Revert complete.");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.Message, ex.NotificationDetails());
        }
    }

    private async Task MergeAsync(RepositoryContext ctx, string refish, string display)
    {
        if (!await _dialogs.ConfirmAsync("Merge", $"Merge {display} into {_state?.CurrentBranch?.Name ?? "the current branch"}?"))
            return;
        await BranchActions.RunWithStashRecovery(ctx, _notifications, _stashes, async () =>
        {
            await ctx.Gateway.MergeAsync(refish, noFastForward: false, ctx.Lifetime);
            await ctx.State.RefreshAllAsync(ctx.Lifetime);
            var conflicts = ctx.State.Status.Conflicted.Count;
            _notifications.Publish(conflicts > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success,
                conflicts > 0 ? $"Merge paused on {conflicts} conflict(s) — resolve, then commit." : $"Merged {display}.");
        }, "Merge");
    }

    private async Task Guard(Func<Task> op, RepositoryContext ctx, string what)
    {
        try
        {
            await op();
            await ctx.State.RefreshAllAsync(ctx.Lifetime);
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage(what), ex.NotificationDetails());
        }
    }

    private void CopyText(string s)
        => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(s);

    /// <summary>Copies the full commit message exactly as committed. Uses the message cache
    /// when warm, else fetches the detail once.</summary>
    private void CopyCommitMessage(GitObjectId id)
    {
        if (TryGetCachedMessage(id, out var message))
        {
            CopyText(message);
            return;
        }
        if (_context is not { } ctx)
            return;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var d = await ctx.Gateway.GetCommitDetailAsync(id, ctx.Lifetime);
                CacheMessage(id, d.Message);
                CopyText(d.Message);
            }
            catch (Exception ex)
            {
                _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Copy message"), ex.NotificationDetails());
            }
        });
    }
}
