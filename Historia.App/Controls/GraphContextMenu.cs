namespace Historia.Controls;

/// <summary>One branch or tag on a commit, given its own submenu in the graph context menu: a
/// local branch, a remote branch (<see cref="Remote"/> is its alias, <see cref="Name"/> the short
/// branch name) or a tag.</summary>
internal readonly record struct GraphMenuRef(DecorationType Type, string Name, string? Remote = null)
{
    public bool IsBranch => Type is DecorationType.LocalBranch or DecorationType.RemoteBranch;

    /// <summary>The name as git and the user know it: <c>main</c>, <c>origin/main</c>, <c>v1.0</c>.</summary>
    public string DisplayName => Remote is { } r ? $"{r}/{Name}" : Name;
}

/// <summary>Everything the graph context menu needs about the right-clicked commit.</summary>
internal sealed record GraphMenuTarget(
    GitObjectId Commit,
    IReadOnlyList<GraphMenuRef> Refs,
    string? CurrentBranch,
    IReadOnlyList<string> ConfiguredRemotes,
    IReadOnlyList<GitObjectId> MultiSelect);

/// <summary>The operations behind the graph context menu's items. Each one does its own
/// confirmation, error reporting and refresh.</summary>
internal interface IGraphMenuActions
{
    Task CheckoutBranch(string name);
    Task CheckoutTracking(string name, string trackingRef);
    Task CheckoutDetached(string sha, string shortSha);
    Task CreateBranch(string sha, string shortSha);
    Task CreateTag(string sha, string shortSha);
    Task Merge(string refish, string display);
    Task Rebase(string onto);
    Task CherryPick(IReadOnlyList<GitObjectId> ids);
    Task Revert(IReadOnlyList<GitObjectId> ids);
    Task Reset(string sha, GitResetMode mode, string shortSha);
    Task Rename(string name);
    Task DeleteLocal(string name);
    Task DeleteRemote(string remote, string name);
    Task PushTag(string tag, string remote);
    Task DeleteTag(string tag);
    Task Copy(string text);
    Task CopyMessage(GitObjectId id);

    /// <summary>Pushes a local branch to its remote, then opens the Create pull request dialog on it.</summary>
    Task PushAndCreatePullRequest(string localBranch);

    /// <summary>Opens the Create pull request dialog on a remote-tracking branch, without pushing.</summary>
    Task CreatePullRequest(string remote, string branch);

    /// <summary>The push/pull/fast-forward items for a branch against its counterpart; empty
    /// when there is none or the two are level.</summary>
    Task<IReadOnlyList<Control>> DivergenceItemsAsync(GraphMenuRef branch);
}

/// <summary>
/// The one context menu for a commit in the graph, opened alike from its row, any of its labels,
/// its <c>+N</c> overflow chip and the refs-list popup. Each branch and tag on the commit gets a
/// submenu at the top holding the items that act on that ref; the items that act on the commit
/// follow, each exactly once. A remote branch with the same name as a local branch on the commit
/// sits inside that local branch's submenu as its own <c>remote/name</c> submenu (merge into the
/// current branch, delete) instead of at the top.
/// </summary>
internal static class GraphContextMenu
{
    /// <summary>The refs on a row, one per submenu, in menu order: the clicked label's refs
    /// first, then the current branch, the other local branches, remote branches and tags, each
    /// in badge order. Folded sibling refs are included; the detached-HEAD label is not.</summary>
    public static IReadOnlyList<GraphMenuRef> RefsForRow(
        IReadOnlyList<GraphDecoration> rowDecorations, GraphDecoration? clicked, string? currentBranch)
    {
        var all = new List<GraphMenuRef>();
        foreach (var d in rowDecorations)
        {
            Expand(all, d.Type, d.Label, d.IsLocal, d.RemotesOrNull);
            foreach (var s in d.Siblings)
                Expand(all, s.Type, s.Label, s.IsLocal, s.Remotes);
        }

        var first = new List<GraphMenuRef>();
        if (clicked is { } c)
            Expand(first, c.Type, c.Label, c.IsLocal, c.RemotesOrNull);

        int Rank(GraphMenuRef r) => r.Type switch
        {
            DecorationType.LocalBranch when string.Equals(r.Name, currentBranch, StringComparison.Ordinal) => 0,
            DecorationType.LocalBranch => 1,
            DecorationType.RemoteBranch => 2,
            _ => 3,
        };

        return first
            .Concat(all.Select((r, i) => (r, i)).OrderBy(x => Rank(x.r)).ThenBy(x => x.i).Select(x => x.r))
            .Distinct()
            .ToList();
    }

    private static void Expand(List<GraphMenuRef> into, DecorationType type, string label, bool isLocal, string[]? remotes)
    {
        switch (type)
        {
            case DecorationType.Tag:
                into.Add(new GraphMenuRef(DecorationType.Tag, label));
                return;
            case DecorationType.LocalBranch or DecorationType.RemoteBranch:
                if (isLocal)
                    into.Add(new GraphMenuRef(DecorationType.LocalBranch, label));
                if (remotes is { Length: > 0 })
                {
                    foreach (var rem in remotes)
                        into.Add(new GraphMenuRef(DecorationType.RemoteBranch, label, rem));
                }
                else if (!isLocal)
                {
                    into.Add(new GraphMenuRef(DecorationType.RemoteBranch, label));
                }
                return;
        }
    }

    public static IReadOnlyList<Control> Build(GraphMenuTarget t, IGraphMenuActions a)
    {
        var sha = t.Commit.ToString();
        var shortSha = t.Commit.ToShortString(7);

        // Merging into, or rebasing onto, the commit HEAD's own branch already sits on is a no-op.
        var onCurrent = t.CurrentBranch is { } cur
            && t.Refs.Any(r => r.Type == DecorationType.LocalBranch && string.Equals(r.Name, cur, StringComparison.Ordinal));
        var canMergeIntoCurrent = !onCurrent && t.CurrentBranch is not null;

        var localNames = t.Refs.Where(r => r.Type == DecorationType.LocalBranch).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        bool IsNested(GraphMenuRef r) => r.Type == DecorationType.RemoteBranch && r.Remote is not null && localNames.Contains(r.Name);
        var refs = new List<Control>();
        foreach (var r in t.Refs)
        {
            if (IsNested(r))
                continue;
            var nested = r.Type == DecorationType.LocalBranch
                ? t.Refs.Where(n => IsNested(n) && string.Equals(n.Name, r.Name, StringComparison.Ordinal)).ToList()
                : [];
            refs.Add(RefSubmenu(r, nested, canMergeIntoCurrent, t, a));
        }

        var commit = new List<Control>
        {
            MenuLeaf("Checkout this commit (detached)", () => a.CheckoutDetached(sha, shortSha)),
            MenuLeaf("Create branch here…", () => a.CreateBranch(sha, shortSha)),
            MenuLeaf("Create tag here…", () => a.CreateTag(sha, shortSha)),
        };

        var history = new List<Control>();
        if (!onCurrent)
        {
            if (t.Refs.Where(r => r.IsBranch).Select(r => (GraphMenuRef?)r).FirstOrDefault() is { } target)
            {
                var name = target.DisplayName;
                var shown = BranchMenu.TruncateBranchLabelForMenu(name);
                history.Add(MenuLeaf($"Merge {shown} into current", () => a.Merge(name, name)));
                history.Add(MenuLeaf($"Rebase current branch onto {shown}", () => a.Rebase(name)));
            }
            else
            {
                history.Add(MenuLeaf($"Merge {shortSha} into current branch", () => a.Merge(sha, shortSha)));
            }
        }
        history.Add(MenuLeaf("Cherry-pick onto current branch", () => a.CherryPick([t.Commit])));
        history.Add(MenuLeaf("Revert this commit", () => a.Revert([t.Commit])));
        if (t.MultiSelect.Count > 1)
        {
            var selected = t.MultiSelect;
            history.Add(MenuLeaf($"Cherry-pick {selected.Count} selected (oldest first)", () => a.CherryPick(selected)));
            history.Add(MenuLeaf($"Revert {selected.Count} selected", () => a.Revert(selected)));
        }
        history.Add(new MenuItem
        {
            Header = "Reset current branch to here",
            ItemsSource = new Control[]
            {
                MenuLeaf("Soft (keep index and working tree)", () => a.Reset(sha, GitResetMode.Soft, shortSha)),
                MenuLeaf("Mixed (keep working tree)", () => a.Reset(sha, GitResetMode.Mixed, shortSha)),
                MenuLeaf("Hard (discard changes)", () => a.Reset(sha, GitResetMode.Hard, shortSha)),
            },
        });

        var copy = new List<Control>
        {
            new MenuItem
            {
                Header = "Copy",
                ItemsSource = new Control[]
                {
                    MenuLeaf("Copy SHA", () => a.Copy(sha)),
                    MenuLeaf("Copy short SHA", () => a.Copy(shortSha)),
                    MenuLeaf("Copy commit message", () => a.CopyMessage(t.Commit)),
                },
            },
        };

        return Join(refs, commit, history, copy);
    }

    /// <summary>The submenu for one ref. <paramref name="nested"/> holds the remote branches of
    /// the same name on the commit, listed inside a local branch's submenu.</summary>
    private static MenuItem RefSubmenu(GraphMenuRef r, IReadOnlyList<GraphMenuRef> nested, bool canMergeIntoCurrent,
        GraphMenuTarget t, IGraphMenuActions a)
    {
        var display = r.DisplayName;
        var shown = BranchMenu.TruncateBranchLabelForMenu(display);
        var items = new RefItems();

        switch (r.Type)
        {
            case DecorationType.LocalBranch:
                items.Add(MenuLeaf($"Checkout {shown}", () => a.CheckoutBranch(r.Name)));
                if (t.ConfiguredRemotes.Count > 0)
                    items.Add(MenuLeaf("Push and create pull request…", () => a.PushAndCreatePullRequest(r.Name)));
                items.Add(MenuLeaf("Rename…", () => a.Rename(r.Name)));
                items.Add(MenuLeaf("Delete local…", () => a.DeleteLocal(r.Name)));
                foreach (var n in nested)
                    items.Add(NestedRemoteSubmenu(n, canMergeIntoCurrent, t, a));
                items.Add(new Separator());
                items.Add(MenuLeaf("Copy name", () => a.Copy(display)));
                break;
            case DecorationType.RemoteBranch:
                if (r.Remote is { } remote)
                {
                    items.Add(MenuLeaf($"Checkout {shown} as a new branch", () => a.CheckoutTracking(r.Name, display)));
                    if (canMergeIntoCurrent)
                        items.Add(MergeIntoCurrentLeaf(display, t, a));
                    if (t.ConfiguredRemotes.Count > 0)
                        items.Add(MenuLeaf("Create pull request…", () => a.CreatePullRequest(remote, r.Name)));
                    items.Add(MenuLeaf("Delete…", () => a.DeleteRemote(remote, r.Name)));
                    items.Add(new Separator());
                }
                items.Add(MenuLeaf("Copy name", () => a.Copy(display)));
                break;
            case DecorationType.Tag:
                foreach (var rem in t.ConfiguredRemotes)
                    items.Add(MenuLeaf($"Push {shown} to {rem}", () => a.PushTag(r.Name, rem)));
                if (items.Count > 0)
                    items.Add(new Separator());
                items.Add(MenuLeaf($"Delete tag {shown}", () => a.DeleteTag(r.Name)));
                items.Add(new Separator());
                items.Add(MenuLeaf("Copy tag name", () => a.Copy(r.Name)));
                break;
        }

        var submenu = NamedSubmenu(r.Type == DecorationType.Tag ? $"{shown} (tag)" : shown, display, items);
        if (r.IsBranch)
        {
            submenu.SubmenuOpened += (_, e) =>
            {
                if (ReferenceEquals(e.Source, submenu))
                    _ = LoadDivergenceAsync(submenu, r, a);
            };
        }
        return submenu;
    }

    /// <summary>A remote branch listed inside the local branch of the same name: merge it into the
    /// current branch (absent when that would be a no-op or HEAD is detached) and delete it on its
    /// remote, which confirms first.</summary>
    private static MenuItem NestedRemoteSubmenu(GraphMenuRef r, bool canMergeIntoCurrent, GraphMenuTarget t, IGraphMenuActions a)
    {
        var display = r.DisplayName;
        var remote = r.Remote!;
        var items = new List<Control>();
        if (canMergeIntoCurrent)
            items.Add(MergeIntoCurrentLeaf(display, t, a));
        items.Add(MenuLeaf("Delete…", () => a.DeleteRemote(remote, r.Name)));
        return NamedSubmenu(BranchMenu.TruncateBranchLabelForMenu(display), display, items);
    }

    /// <summary>Merges a remote branch into the current one; only built when HEAD is on a branch.</summary>
    private static MenuItem MergeIntoCurrentLeaf(string display, GraphMenuTarget t, IGraphMenuActions a) =>
        MenuLeaf($"Merge into {BranchMenu.TruncateBranchLabelForMenu(t.CurrentBranch!)}", () => a.Merge(display, display));

    /// <summary>A ref's submenu under <paramref name="header"/>, with the full name as its tooltip
    /// when the header had to trim it.</summary>
    private static MenuItem NamedSubmenu(string header, string display, IEnumerable<Control> items)
    {
        var submenu = new MenuItem { Header = header, ItemsSource = items };
        if (display.Length > BranchMenu.MenuNameMaxChars)
            ToolTip.SetTip(submenu, display);
        return submenu;
    }

    /// <summary>Resolves a branch submenu's divergence items the first time it opens and inserts
    /// them after its checkout item once the submenu is open: if it closed before they arrived,
    /// the next open inserts them without asking git again.</summary>
    internal static async Task LoadDivergenceAsync(MenuItem submenu, GraphMenuRef r, IGraphMenuActions a)
    {
        if (submenu.ItemsSource is not RefItems items || items.DivergenceInserted)
            return;
        items.Divergence ??= a.DivergenceItemsAsync(r);

        IReadOnlyList<Control> extra;
        try { extra = await items.Divergence; }
        catch { return; }
        if (extra.Count == 0 || items.DivergenceInserted || !submenu.IsSubMenuOpen)
            return;
        items.DivergenceInserted = true;

        var at = r.Type == DecorationType.LocalBranch || r.Remote is not null ? 1 : 0;
        foreach (var item in extra)
            items.Insert(at++, item);
    }

    /// <summary>A ref submenu's items; live, so divergence items can arrive after it opens.</summary>
    private sealed class RefItems : ObservableCollection<Control>
    {
        public Task<IReadOnlyList<Control>>? Divergence { get; set; }
        public bool DivergenceInserted { get; set; }
    }

    private static List<Control> Join(params List<Control>[] sections)
    {
        var all = new List<Control>();
        foreach (var section in sections)
        {
            if (section.Count == 0)
                continue;
            if (all.Count > 0)
                all.Add(new Separator());
            all.AddRange(section);
        }
        return all;
    }
}
