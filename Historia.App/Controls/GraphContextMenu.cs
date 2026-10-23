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

    /// <summary>The push/pull/fast-forward items for a branch against its counterpart; empty
    /// when there is none or the two are level.</summary>
    Task<IReadOnlyList<Control>> DivergenceItemsAsync(GraphMenuRef branch);
}

/// <summary>
/// The one context menu for a commit in the graph, opened alike from its row, any of its labels,
/// its <c>+N</c> overflow chip and the refs-list popup. Each branch and tag on the commit gets a
/// submenu at the top holding the items that act on that ref; the items that act on the commit
/// follow, each exactly once.
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

        var refs = t.Refs.Select(r => (Control)RefSubmenu(r, t, a)).ToList();

        var commit = new List<Control>
        {
            MenuLeaf("Checkout this commit (detached)", () => a.CheckoutDetached(sha, shortSha)),
            MenuLeaf("Create branch here…", () => a.CreateBranch(sha, shortSha)),
            MenuLeaf("Create tag here…", () => a.CreateTag(sha, shortSha)),
        };

        var history = new List<Control>();
        // Merging into, or rebasing onto, the commit HEAD's own branch already sits on is a no-op.
        var onCurrent = t.CurrentBranch is { } cur
            && t.Refs.Any(r => r.Type == DecorationType.LocalBranch && string.Equals(r.Name, cur, StringComparison.Ordinal));
        if (!onCurrent)
        {
            var targetIndex = t.Refs.ToList().FindIndex(r => r.IsBranch);
            if (targetIndex >= 0)
            {
                var name = t.Refs[targetIndex].DisplayName;
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

    private static MenuItem RefSubmenu(GraphMenuRef r, GraphMenuTarget t, IGraphMenuActions a)
    {
        var display = r.DisplayName;
        var shown = BranchMenu.TruncateBranchLabelForMenu(display);
        var items = new RefItems();

        switch (r.Type)
        {
            case DecorationType.LocalBranch:
                items.Add(MenuLeaf($"Checkout {shown}", () => a.CheckoutBranch(r.Name)));
                items.Add(MenuLeaf("Rename…", () => a.Rename(r.Name)));
                items.Add(MenuLeaf("Delete local…", () => a.DeleteLocal(r.Name)));
                items.Add(new Separator());
                items.Add(MenuLeaf("Copy name", () => a.Copy(display)));
                break;
            case DecorationType.RemoteBranch:
                if (r.Remote is { } remote)
                {
                    items.Add(MenuLeaf($"Checkout {shown} as a new branch", () => a.CheckoutTracking(r.Name, display)));
                    items.Add(MenuLeaf("Delete…", () => a.DeleteRemote(remote, r.Name)));
                    items.Add(new Separator());
                }
                items.Add(MenuLeaf("Copy name", () => a.Copy(display)));
                break;
            case DecorationType.Tag:
                foreach (var rem in t.ConfiguredRemotes)
                    items.Add(MenuLeaf($"Push {shown} to {rem}", () => a.PushTag(r.Name, rem)));
                items.Add(MenuLeaf($"Delete tag {shown}", () => a.DeleteTag(r.Name)));
                break;
        }

        var submenu = new MenuItem
        {
            Header = r.Type == DecorationType.Tag ? $"{shown} (tag)" : shown,
            ItemsSource = items,
        };
        if (display.Length > BranchMenu.MenuNameMaxChars)
            ToolTip.SetTip(submenu, display);
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

    /// <summary>Resolves a branch submenu's divergence items the first time it opens and inserts
    /// them after its checkout item, if the submenu is still open when they arrive.</summary>
    internal static async Task LoadDivergenceAsync(MenuItem submenu, GraphMenuRef r, IGraphMenuActions a)
    {
        if (submenu.ItemsSource is not RefItems items || items.DivergenceRequested)
            return;
        items.DivergenceRequested = true;

        IReadOnlyList<Control> extra;
        try { extra = await a.DivergenceItemsAsync(r); }
        catch { return; }
        if (extra.Count == 0 || !submenu.IsSubMenuOpen)
            return;

        var at = r.Type == DecorationType.LocalBranch || r.Remote is not null ? 1 : 0;
        foreach (var item in extra)
            items.Insert(at++, item);
    }

    /// <summary>A ref submenu's items; live, so divergence items can arrive after it opens.</summary>
    private sealed class RefItems : ObservableCollection<Control>
    {
        public bool DivergenceRequested { get; set; }
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
