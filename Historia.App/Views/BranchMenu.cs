namespace Historia.Views;

/// <summary>
/// The branch context-menu items common to GitKraken · Fork · GitComet · GitHub Desktop, for
/// the sidebar branch rows, plus the divergence items and name trimming the graph's context menu
/// shares. Each item does its own guard + <c>RefreshAllAsync</c> + toast so callers only supply
/// the collaborators.
/// </summary>
internal static class BranchMenu
{
    internal readonly record struct Target(
        string Name,            // short branch name, e.g. "feature/x"
        string CopyName,        // "Copy branch name" payload (canonical for a remote ref)
        string TrackingRef,     // the ref to base a tracking local on for a remote row
        bool IsLocal,
        string? TipSha,         // for "Create branch/tag here"; null → omit those
        string? CurrentBranch,
        // The counterpart ref to measure divergence against — a local row's upstream
        // (e.g. "origin/main"), or a remote row's local branch of the same short name. Null
        // when there is no counterpart (no upstream / no local branch), in which case the menu
        // gets no divergence items.
        string? CounterpartRef = null,
        // The owning remote's alias (e.g. "origin"), resolved via BranchInfo.RemoteName rather
        // than string-splitting Name/UpstreamName, which may itself contain a slash.
        string? RemoteAlias = null);

    /// <summary>Max characters of a branch/tag name shown in a context-menu item before it's
    /// ellipsis-trimmed — long names otherwise force the right-click popup
    /// wider than the screen.</summary>
    internal const int MenuNameMaxChars = RefMenuItemViewModel.NameMaxChars;

    /// <summary>The counterpart and owning remote alias for a local branch whose upstream is
    /// <paramref name="upstream"/>. An upstream that is a remote-tracking branch gives that branch
    /// and its remote; one that is a local branch (<c>branch.&lt;name&gt;.remote = .</c>) has no
    /// remote side, so there is no counterpart; any other upstream is gone or not fetched, so the
    /// counterpart is kept with no alias.</summary>
    internal static (string? CounterpartRef, string? RemoteAlias) UpstreamCounterpart(
        string? upstream, IEnumerable<BranchInfo> branches)
    {
        if (upstream is null)
            return (null, null);
        string? remoteAlias = null;
        var isLocal = false;
        foreach (var b in branches)
        {
            if (b.Name != upstream)
                continue;
            if (b.IsRemote)
                remoteAlias = b.RemoteName;
            else
                isLocal = true;
        }
        return remoteAlias is null && isLocal ? (null, null) : (upstream, remoteAlias);
    }

    /// <summary>Ellipsis-trims a branch/tag name to <see cref="MenuNameMaxChars"/> for display in
    /// a menu item — e.g. "Checkout origin/feature/very-long-name as a new branch" — without
    /// affecting the untrimmed name callers still use for the actual git operation.</summary>
    internal static string TruncateBranchLabelForMenu(string name) => RefMenuItemViewModel.TrimName(name);

    /// <summary>Resolves (ahead, behind) for <paramref name="t"/> against its counterpart
    /// and returns the matching push/pull/fast-forward/merge-and-push menu item(s). It returns empty
    /// when there is no counterpart or the refs are identical, and one disabled "Push / pull
    /// unavailable" item when a counterpart exists but cannot be compared (its remote-tracking ref
    /// is gone or was never fetched, or the lookup fails). Every item acts on the
    /// local branch: the row itself, or a remote row's counterpart.
    /// <list type="bullet">
    /// <item>Local ahead only: Push, whether or not the branch is checked out — <c>git push</c>
    /// takes an explicit local ref.</item>
    /// <item>Local behind, checked out: Pull ("Fast-forward from …" when not ahead) — <c>git pull</c>
    /// always merges into whatever is checked out.</item>
    /// <item>Local behind only, not checked out: "Fast-forward … to …", which moves the branch ref
    /// without a checkout.</item>
    /// <item>Diverged: "Merge … and push" beside the Pull on the checked-out branch, or "Check out,
    /// merge … and push" on any other branch.</item>
    /// </list></summary>
    internal static async Task<IReadOnlyList<Control>> BuildDivergenceItemsAsync(
        Target t, IGitGateway gateway, IRepoActionBus actions, CancellationToken ct)
    {
        if (t.CounterpartRef is not { } counterpart)
            return [];

        var (local, remote) = t.IsLocal ? (t.TrackingRef, counterpart) : (counterpart, t.TrackingRef);
        if (t.RemoteAlias is not { } remoteAlias)
            return [Unavailable(remote)];

        AheadBehind? ab;
        try { ab = await gateway.GetDivergenceAsync(local, remote, ct); }
        catch (OperationCanceledException) { return []; }
        catch { return [Unavailable(remote)]; }
        if (ab is not { } div)
            return [Unavailable(remote)];
        if (div.Ahead == 0 && div.Behind == 0)
            return [];

        var isCurrentLocalBranch = t.IsLocal
            ? string.Equals(t.Name, t.CurrentBranch, StringComparison.Ordinal)
            : string.Equals(counterpart, t.CurrentBranch, StringComparison.Ordinal);

        var prefix = $"{remoteAlias}/";
        var remoteBranch = t.IsLocal
            ? (counterpart.StartsWith(prefix, StringComparison.Ordinal) ? counterpart[prefix.Length..] : counterpart)
            : (t.TrackingRef.StartsWith(prefix, StringComparison.Ordinal) ? t.TrackingRef[prefix.Length..] : t.TrackingRef);

        // The local branch every item acts on, and the remote-tracking ref it is measured against.
        var localBranch = t.IsLocal ? t.Name : counterpart;
        var remoteRef = t.IsLocal ? counterpart : t.TrackingRef;

        void Request(RepoNetworkAction action, string branchName) =>
            actions.RequestNetworkAction(new RepoNetworkRequest(action, remoteAlias, branchName, remoteBranch));
        void Push(string branchName) => Request(RepoNetworkAction.Push, branchName);
        void Pull(string branchName) => Request(RepoNetworkAction.Pull, branchName);

        var items = new List<Control>();
        if (t.IsLocal)
        {
            if (div.Ahead > 0 && div.Behind == 0)
                items.Add(MenuLeaf($"Push {div.Ahead} commit(s) to {remoteAlias}", () => Push(t.Name)));
            else if (div.Behind > 0 && isCurrentLocalBranch)
                items.Add(MenuLeaf(div.Ahead == 0 ? $"Fast-forward from {counterpart}" : $"Pull from {counterpart}", () => Pull(t.Name)));
        }
        else
        {
            if (div.Ahead > 0 && div.Behind == 0)
                items.Add(MenuLeaf($"Push {t.Name} to {remoteAlias}", () => Push(t.Name)));
            else if (div.Behind > 0 && isCurrentLocalBranch)
                items.Add(MenuLeaf($"Pull {t.TrackingRef}", () => Pull(t.Name)));
        }

        if (div.Ahead == 0 && div.Behind > 0 && !isCurrentLocalBranch)
        {
            items.Add(MenuLeaf($"Fast-forward {localBranch} to {remoteRef}",
                () => Request(RepoNetworkAction.FastForwardBranch, localBranch)));
        }
        else if (div.Ahead > 0 && div.Behind > 0)
        {
            items.Add(MenuLeaf(isCurrentLocalBranch ? $"Merge {remoteRef} and push" : $"Check out, merge {remoteRef} and push",
                () => Request(RepoNetworkAction.MergeAndPush, localBranch)));
        }
        return items;
    }

    // The remote side of a branch pair cannot be compared, so push and pull cannot be offered; the
    // item says so and how to fix it instead of leaving the slot empty.
    private static Control Unavailable(string remoteRef)
    {
        var item = new MenuItem { Header = "Push / pull unavailable", IsEnabled = false };
        ToolTip.SetTip(item, $"{remoteRef} is gone or hasn't been fetched. Fetch to refresh.");
        ToolTip.SetShowOnDisabled(item, true);
        return item;
    }

    public static List<Control> Build(
        Target t, RepositoryContext ctx, IDialogService dialogs, INotificationService notify,
        IStashService stash, IRepoActionBus actions, Action<string> copyToClipboard, PullRequestFlow? pullRequests = null)
    {
        var items = new List<Control>();

        var isCurrent = t.IsLocal && string.Equals(t.Name, t.CurrentBranch, StringComparison.Ordinal);

        items.Add(MenuLeaf("Checkout", () => BranchActions.CheckoutBranchAsync(ctx, notify, stash, t.Name, t.TrackingRef, t.IsLocal,
            onSwitched: name => BranchActions.AnnounceSwitch(notify, name))));

        if (t.TipSha is { Length: > 0 } sha)
        {
            var short7 = sha.Length >= 7 ? sha[..7] : sha;
            items.Add(MenuLeaf("Create branch here…", () => PromptCreateBranch(ctx, dialogs, notify, stash, sha, short7)));
            items.Add(MenuLeaf("Create tag here…", () => PromptCreateTag(ctx, dialogs, notify, sha, short7)));
        }

        if (t.IsLocal)
        {
            items.Add(MenuLeaf("Rename…", async () =>
            {
                var n = await dialogs.PromptAsync("Rename branch", $"New name for '{t.Name}':", t.Name);
                if (string.IsNullOrWhiteSpace(n) || n == t.Name) return;
                await BranchActions.RenameAsync(ctx, notify, t.Name, n.Trim());
            }));
            // Git refuses to delete the checked-out branch — omit the item rather than offer a
            // guaranteed failure.
            if (!isCurrent)
                items.Add(MenuLeaf("Delete…", () => BranchActions.DeleteAsync(ctx, dialogs, notify, t.Name)));
        }

        // Merging a branch into itself is a no-op at best — only offer it for a non-current target.
        if (!isCurrent)
        {
            var mergeRef = t.IsLocal ? t.Name : t.CopyName;
            var into = t.CurrentBranch ?? "current";
            items.Add(MenuLeaf($"Merge into {into} branch",
                () => BranchActions.MergeIntoCurrentAsync(ctx, dialogs, notify, stash, mergeRef, t.CurrentBranch)));
        }

        if (pullRequests is not null && ctx.State.Remotes.Count > 0)
        {
            if (t.IsLocal)
                items.Add(MenuLeaf("Push and create pull request…", () => pullRequests.PushAndOpenAsync(ctx, t.Name)));
            else if (t.RemoteAlias is { Length: > 0 } pullRemote)
                items.Add(MenuLeaf("Create pull request…", () => pullRequests.OpenAsync(ctx, pullRemote, t.Name)));
        }

        if (!t.IsLocal && t.RemoteAlias is { Length: > 0 } alias)
        {
            items.Add(MenuLeaf("Delete…", () =>
            {
                actions.RequestNetworkAction(new RepoNetworkRequest(
                    RepoNetworkAction.DeleteRemoteBranch, alias, null, t.Name, ctx));
                return Task.CompletedTask;
            }));
        }

        items.Add(MenuLeaf("Copy branch name", () => { copyToClipboard(t.CopyName); return Task.CompletedTask; }));

        return items;
    }

    /// <summary>Deferred so the prompt parents cleanly after the menu tears down.</summary>
    public static Task PromptCreateBranch(RepositoryContext ctx, IDialogService dialogs, INotificationService notify,
        IStashService stash, string sha, string shortSha)
    {
        Dispatcher.UIThread.Post(async () =>
            await BranchActions.CreateBranchAsync(ctx, dialogs, notify, stash, sha, shortSha));
        return Task.CompletedTask;
    }

    public static Task PromptCreateTag(RepositoryContext ctx, IDialogService dialogs, INotificationService notify,
        string sha, string shortSha)
    {
        Dispatcher.UIThread.Post(async () =>
            await BranchActions.CreateTagAsync(ctx, dialogs, notify, sha, shortSha));
        return Task.CompletedTask;
    }
}
