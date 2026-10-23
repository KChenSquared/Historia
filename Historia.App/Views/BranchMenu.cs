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

    /// <summary>Ellipsis-trims a branch/tag name to <see cref="MenuNameMaxChars"/> for display in
    /// a menu item — e.g. "Checkout origin/feature/very-long-name as a new branch" — without
    /// affecting the untrimmed name callers still use for the actual git operation.</summary>
    internal static string TruncateBranchLabelForMenu(string name) => RefMenuItemViewModel.TrimName(name);

    /// <summary>Resolves (ahead, behind) for <paramref name="t"/> against its counterpart
    /// and returns the matching push/pull/fast-forward menu item(s), or empty when there is no
    /// counterpart, the lookup fails, or the refs are identical. <c>git pull</c> always merges
    /// into whatever is currently checked out, so a Pull/Fast-forward item is only offered when
    /// the branch in question is the current branch; a Push item needs no such guard since
    /// <c>git push</c> takes an explicit local ref regardless of checkout state.</summary>
    internal static async Task<IReadOnlyList<Control>> BuildDivergenceItemsAsync(
        Target t, IGitGateway gateway, IRepoActionBus actions, CancellationToken ct)
    {
        if (t.CounterpartRef is not { } counterpart || t.RemoteAlias is not { } remoteAlias)
            return [];

        var (local, remote) = t.IsLocal ? (t.TrackingRef, counterpart) : (counterpart, t.TrackingRef);

        AheadBehind? ab;
        try { ab = await gateway.GetDivergenceAsync(local, remote, ct); }
        catch { return []; }
        if (ab is not { } div || (div.Ahead == 0 && div.Behind == 0))
            return [];

        var isCurrentLocalBranch = t.IsLocal
            ? string.Equals(t.Name, t.CurrentBranch, StringComparison.Ordinal)
            : string.Equals(counterpart, t.CurrentBranch, StringComparison.Ordinal);

        var prefix = $"{remoteAlias}/";
        var remoteBranch = t.IsLocal
            ? (counterpart.StartsWith(prefix, StringComparison.Ordinal) ? counterpart[prefix.Length..] : counterpart)
            : (t.TrackingRef.StartsWith(prefix, StringComparison.Ordinal) ? t.TrackingRef[prefix.Length..] : t.TrackingRef);

        void Push(string branchName) => actions.RequestNetworkAction(new RepoNetworkRequest(RepoNetworkAction.Push, remoteAlias, branchName, remoteBranch));
        void Pull(string branchName) => actions.RequestNetworkAction(new RepoNetworkRequest(RepoNetworkAction.Pull, remoteAlias, branchName, remoteBranch));

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
        return items;
    }

    public static List<Control> Build(
        Target t, RepositoryContext ctx, IDialogService dialogs, INotificationService notify,
        IStashService stash, IRepoActionBus actions, Action<string> copyToClipboard)
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
