namespace HistoriaApp.Tests;

/// <summary>
/// The graph's one context menu for a commit: a submenu per branch and tag at the top in a fixed
/// order, the commit's own items once each below, every item reaching the right operation, and
/// divergence items loaded the first time a branch submenu opens.
/// </summary>
[Trait("tier", "ui")]
public sealed class GraphContextMenuTests
{
    private static readonly GitObjectId Sha = new("0123456789abcdef0123456789abcdef01234567");
    private static readonly GitObjectId Other1 = new("1111111111111111111111111111111111111111");
    private static readonly GitObjectId Other2 = new("2222222222222222222222222222222222222222");

    private sealed class RecordingActions : IGraphMenuActions
    {
        public List<string> Calls { get; } = [];
        public int DivergenceCalls { get; private set; }
        public IReadOnlyList<Control> Divergence { get; set; } = [];

        private Task Record(string call)
        {
            Calls.Add(call);
            return Task.CompletedTask;
        }

        public Task CheckoutBranch(string name) => Record($"checkout {name}");
        public Task CheckoutTracking(string name, string trackingRef) => Record($"tracking {name} {trackingRef}");
        public Task CheckoutDetached(string sha, string shortSha) => Record($"detached {sha}");
        public Task CreateBranch(string sha, string shortSha) => Record($"branch {sha}");
        public Task CreateTag(string sha, string shortSha) => Record($"tag {sha}");
        public Task Merge(string refish, string display) => Record($"merge {refish}");
        public Task Rebase(string onto) => Record($"rebase {onto}");
        public Task CherryPick(IReadOnlyList<GitObjectId> ids) => Record($"cherry-pick {ids.Count}");
        public Task Revert(IReadOnlyList<GitObjectId> ids) => Record($"revert {ids.Count}");
        public Task Reset(string sha, GitResetMode mode, string shortSha) => Record($"reset {mode}");
        public Task Rename(string name) => Record($"rename {name}");
        public Task DeleteLocal(string name) => Record($"delete-local {name}");
        public Task DeleteRemote(string remote, string name) => Record($"delete-remote {remote} {name}");
        public Task PushTag(string tag, string remote) => Record($"push-tag {tag} {remote}");
        public Task DeleteTag(string tag) => Record($"delete-tag {tag}");
        public Task Copy(string text) => Record($"copy {text}");
        public Task CopyMessage(GitObjectId id) => Record($"copy-message {id}");

        public Task<IReadOnlyList<Control>> DivergenceItemsAsync(GraphMenuRef branch)
        {
            DivergenceCalls++;
            return Task.FromResult(Divergence);
        }
    }

    private static GraphDecoration Deco(DecorationType type, string label, bool isLocal = false,
        string[]? remotes = null, GraphDecorationInput[]? siblings = null) => new()
        {
            RowIndex = 0,
            Type = type,
            Label = label,
            ColorIndex = 0,
            IsLocal = isLocal,
            RemotesOrNull = remotes,
            SiblingsOrNull = siblings,
        };

    private static GraphMenuTarget Target(IReadOnlyList<GraphMenuRef> refs, string? current = "develop",
        IReadOnlyList<string>? remotes = null, IReadOnlyList<GitObjectId>? multi = null) =>
        new(Sha, refs, current, remotes ?? ["origin"], multi ?? []);

    private static IReadOnlyList<GraphMenuRef> Refs(params GraphDecoration[] decos) =>
        GraphContextMenu.RefsForRow(decos, null, "develop");

    private static IReadOnlyList<Control> Children(MenuItem item) =>
        Assert.IsAssignableFrom<IEnumerable<Control>>(item.ItemsSource).ToList();

    private static List<string> Headers(IEnumerable<Control> items) =>
        items.OfType<MenuItem>().Select(m => m.Header?.ToString() ?? "").ToList();

    private static MenuItem Item(IEnumerable<Control> items, string header) =>
        items.OfType<MenuItem>().Single(m => m.Header?.ToString() == header);

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static IEnumerable<string> LeafHeaders(IEnumerable<Control> items)
    {
        foreach (var m in items.OfType<MenuItem>())
        {
            if (m.ItemsSource is IEnumerable<Control> children)
                foreach (var h in LeafHeaders(children))
                    yield return h;
            else
                yield return m.Header?.ToString() ?? "";
        }
    }

    [AvaloniaFact]
    public void Unlabelled_commit_has_no_ref_submenus_and_merges_by_sha()
    {
        var a = new RecordingActions();
        var items = GraphContextMenu.Build(Target([]), a);

        Assert.Equal("Checkout this commit (detached)", Headers(items)[0]);
        Assert.DoesNotContain(Headers(items), h => h.StartsWith("Rebase", StringComparison.Ordinal));
        Click(Item(items, "Merge 0123456 into current branch"));
        Assert.Equal([$"merge {Sha}"], a.Calls);
    }

    [AvaloniaFact]
    public void Local_and_remote_label_gives_two_submenus_first_in_order()
    {
        var a = new RecordingActions();
        var items = GraphContextMenu.Build(Target(Refs(Deco(DecorationType.LocalBranch, "main", true, ["origin"]))), a);

        Assert.Equal(["main", "origin/main"], Headers(items).Take(2));
        Assert.IsType<Separator>(items[2]);
        Click(Item(items, "Merge main into current"));
        Click(Item(items, "Rebase current branch onto main"));
        Assert.Equal(["merge main", "rebase main"], a.Calls);
    }

    [AvaloniaFact]
    public void Both_checkouts_are_offered()
    {
        var a = new RecordingActions();
        var items = GraphContextMenu.Build(Target(Refs(Deco(DecorationType.LocalBranch, "main", true))), a);

        Click(Item(Children(Item(items, "main")), "Checkout main"));
        Click(Item(items, "Checkout this commit (detached)"));
        Assert.Equal(["checkout main", $"detached {Sha}"], a.Calls);
    }

    [AvaloniaFact]
    public void Clicked_label_refs_come_first_then_current_branch()
    {
        var feat = Deco(DecorationType.LocalBranch, "feat", true);
        var develop = Deco(DecorationType.LocalBranch, "develop", true);
        var tag = Deco(DecorationType.Tag, "v1");
        var remote = Deco(DecorationType.RemoteBranch, "zeta", false, ["origin"]);

        var byRow = GraphContextMenu.RefsForRow([tag, feat, remote, develop], null, "develop");
        Assert.Equal(["develop", "feat", "origin/zeta", "v1"], byRow.Select(r => r.DisplayName));

        var byTag = GraphContextMenu.RefsForRow([tag, feat, remote, develop], tag, "develop");
        Assert.Equal(["v1", "develop", "feat", "origin/zeta"], byTag.Select(r => r.DisplayName));
    }

    [AvaloniaFact]
    public void Folded_siblings_get_their_own_submenus()
    {
        var head = Deco(DecorationType.LocalBranch, "main", true, ["origin"],
            [new GraphDecorationInput(DecorationType.LocalBranch, "feat", IsLocal: true),
             new GraphDecorationInput(DecorationType.Tag, "v2")]);

        var items = GraphContextMenu.Build(Target(Refs(head)), new RecordingActions());

        Assert.Equal(["main", "feat", "origin/main", "v2 (tag)"], Headers(items).Take(4));
    }

    [AvaloniaFact]
    public void Head_label_gets_no_submenu()
    {
        var refs = Refs(Deco(DecorationType.Head, "HEAD"));
        Assert.Empty(refs);
    }

    [AvaloniaFact]
    public void No_header_appears_twice_anywhere_in_the_tree()
    {
        var refs = Refs(
            Deco(DecorationType.LocalBranch, "main", true, ["origin", "upstream"]),
            Deco(DecorationType.Tag, "v1"),
            Deco(DecorationType.Tag, "v2"));
        var items = GraphContextMenu.Build(Target(refs, remotes: ["origin", "upstream"], multi: [Sha, Other1, Other2]), new RecordingActions());

        var leaves = LeafHeaders(items).ToList();
        var perSubmenu = new HashSet<string> { "Copy name", "Delete…" };
        var duplicates = leaves.Where(h => !perSubmenu.Contains(h))
            .GroupBy(h => h).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicates);

        Assert.Single(leaves, "Create branch here…");
        Assert.Single(leaves, "Create tag here…");
        Assert.Single(leaves, h => h.StartsWith("Merge ", StringComparison.Ordinal));
        Assert.Single(leaves, h => h.StartsWith("Rebase ", StringComparison.Ordinal));
        foreach (var sub in items.OfType<MenuItem>().Take(refs.Count))
            Assert.Equal(Headers(Children(sub)).Distinct().Count(), Headers(Children(sub)).Count);
    }

    [AvaloniaFact]
    public void Merge_and_rebase_hidden_on_the_current_branch_commit()
    {
        var refs = Refs(Deco(DecorationType.LocalBranch, "develop", true), Deco(DecorationType.LocalBranch, "feat", true));
        var headers = LeafHeaders(GraphContextMenu.Build(Target(refs), new RecordingActions())).ToList();

        Assert.DoesNotContain(headers, h => h.StartsWith("Merge ", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Rebase ", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Rebase_absent_without_a_branch_ref()
    {
        var headers = Headers(GraphContextMenu.Build(Target(Refs(Deco(DecorationType.Tag, "v1"))), new RecordingActions()));

        Assert.DoesNotContain(headers, h => h.StartsWith("Rebase ", StringComparison.Ordinal));
        Assert.Contains("Merge 0123456 into current branch", headers);
    }

    [AvaloniaFact]
    public void Remote_submenu_checks_out_tracking_and_deletes_on_that_remote()
    {
        var a = new RecordingActions();
        var items = GraphContextMenu.Build(Target(Refs(Deco(DecorationType.RemoteBranch, "feat", false, ["origin"]))), a);

        var sub = Children(Item(items, "origin/feat"));
        Click(Item(sub, "Checkout origin/feat as a new branch"));
        Click(Item(sub, "Delete…"));
        Click(Item(sub, "Copy name"));
        Assert.Equal(["tracking feat origin/feat", "delete-remote origin feat", "copy origin/feat"], a.Calls);
    }

    [AvaloniaFact]
    public void Local_submenu_renames_deletes_and_copies()
    {
        var a = new RecordingActions();
        var items = GraphContextMenu.Build(Target(Refs(Deco(DecorationType.LocalBranch, "feat", true))), a);

        var sub = Children(Item(items, "feat"));
        Click(Item(sub, "Rename…"));
        Click(Item(sub, "Delete local…"));
        Click(Item(sub, "Copy name"));
        Assert.Equal(["rename feat", "delete-local feat", "copy feat"], a.Calls);
    }

    [AvaloniaFact]
    public void Tag_submenu_pushes_to_each_configured_remote_and_deletes()
    {
        var a = new RecordingActions();
        var items = GraphContextMenu.Build(Target(Refs(Deco(DecorationType.Tag, "v1")), remotes: ["origin", "upstream"]), a);

        var sub = Children(Item(items, "v1 (tag)"));
        Assert.Equal(["Push v1 to origin", "Push v1 to upstream", "Delete tag v1"], Headers(sub));
        foreach (var m in sub.OfType<MenuItem>())
            Click(m);
        Assert.Equal(["push-tag v1 origin", "push-tag v1 upstream", "delete-tag v1"], a.Calls);
    }

    [AvaloniaFact]
    public void Copy_submenu_copies_sha_short_sha_and_message()
    {
        var a = new RecordingActions();
        var items = GraphContextMenu.Build(Target([]), a);

        Assert.IsType<MenuItem>(items[^1]);
        foreach (var m in Children(Item(items, "Copy")).OfType<MenuItem>())
            Click(m);
        Assert.Equal([$"copy {Sha}", "copy 0123456", $"copy-message {Sha}"], a.Calls);
    }

    [AvaloniaFact]
    public void Multi_select_adds_cherry_pick_and_revert_for_selection()
    {
        var a = new RecordingActions();
        var single = Headers(GraphContextMenu.Build(Target([]), a));
        Assert.DoesNotContain(single, h => h.Contains("selected", StringComparison.Ordinal));

        var items = GraphContextMenu.Build(Target([], multi: [Sha, Other1, Other2]), a);
        Click(Item(items, "Cherry-pick 3 selected (oldest first)"));
        Click(Item(items, "Revert 3 selected"));
        Assert.Equal(["cherry-pick 3", "revert 3"], a.Calls);
    }

    [AvaloniaFact]
    public void Reset_submenu_offers_three_modes()
    {
        var a = new RecordingActions();
        var items = GraphContextMenu.Build(Target([]), a);

        foreach (var m in Children(Item(items, "Reset current branch to here")).OfType<MenuItem>())
            Click(m);
        Assert.Equal(["reset Soft", "reset Mixed", "reset Hard"], a.Calls);
    }

    [AvaloniaFact]
    public void Long_ref_name_is_trimmed_with_full_name_tooltip()
    {
        var longName = new string('x', 40);
        var a = new RecordingActions();
        var items = GraphContextMenu.Build(Target(Refs(Deco(DecorationType.LocalBranch, longName, true))), a);

        var sub = items.OfType<MenuItem>().First();
        var header = sub.Header?.ToString();
        Assert.NotNull(header);
        Assert.EndsWith("…", header);
        Assert.True(header.Length <= BranchMenu.MenuNameMaxChars);
        Assert.Equal(longName, ToolTip.GetTip(sub));

        Click((MenuItem)Children(sub)[0]);
        Assert.Equal([$"checkout {longName}"], a.Calls);
    }

    [Fact]
    public void TruncateBranchLabelForMenu_preserves_short_names_and_ellipsizes_long_ones()
    {
        Assert.Equal("main", BranchMenu.TruncateBranchLabelForMenu("main"));
        var exact = new string('a', BranchMenu.MenuNameMaxChars);
        Assert.Equal(exact, BranchMenu.TruncateBranchLabelForMenu(exact));
        var oneOver = new string('b', BranchMenu.MenuNameMaxChars + 1);
        Assert.Equal(oneOver[..(BranchMenu.MenuNameMaxChars - 1)] + "…", BranchMenu.TruncateBranchLabelForMenu(oneOver));
    }

    [AvaloniaFact]
    public async Task Divergence_items_are_inserted_when_branch_submenu_opens()
    {
        var a = new RecordingActions { Divergence = [new MenuItem { Header = "Push 2 commit(s) to origin" }] };
        var refs = Refs(Deco(DecorationType.LocalBranch, "main", true));
        var sub = GraphContextMenu.Build(Target(refs), a).OfType<MenuItem>().First();

        sub.IsSubMenuOpen = true;
        await GraphContextMenu.LoadDivergenceAsync(sub, refs[0], a);
        await GraphContextMenu.LoadDivergenceAsync(sub, refs[0], a);

        Assert.Equal(1, a.DivergenceCalls);
        Assert.Equal(["Checkout main", "Push 2 commit(s) to origin", "Rename…"], Headers(Children(sub)).Take(3));
    }

    [AvaloniaFact]
    public async Task Divergence_items_are_dropped_when_submenu_closed_first()
    {
        var a = new RecordingActions { Divergence = [new MenuItem { Header = "Push 2 commit(s) to origin" }] };
        var refs = Refs(Deco(DecorationType.LocalBranch, "main", true));
        var sub = GraphContextMenu.Build(Target(refs), a).OfType<MenuItem>().First();

        await GraphContextMenu.LoadDivergenceAsync(sub, refs[0], a);

        Assert.DoesNotContain("Push 2 commit(s) to origin", Headers(Children(sub)));
    }

    [AvaloniaFact]
    public void Separators_never_lead_trail_or_double()
    {
        var cases = new[]
        {
            Target([]),
            Target(Refs(Deco(DecorationType.LocalBranch, "develop", true))),
            Target(Refs(Deco(DecorationType.Tag, "v1")), remotes: []),
        };
        foreach (var t in cases)
        {
            var items = GraphContextMenu.Build(t, new RecordingActions());
            Assert.IsNotType<Separator>(items[0]);
            Assert.IsNotType<Separator>(items[^1]);
            for (var i = 1; i < items.Count; i++)
                Assert.False(items[i] is Separator && items[i - 1] is Separator);
        }
    }
}
