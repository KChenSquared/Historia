namespace Historia.Tests;

/// <summary>
/// The ref navigator view model: the flat rows of each section (folders, branches, tags and so on),
/// the open and expanded state, the filters, selection, checkout, and the power tools (stashes,
/// submodules, worktrees) seeded from the cache, loaded once the first graph paint has landed and
/// reloaded only when what they depend on changes. The services are fakes and the repository is a
/// real, tiny one.
/// </summary>
[Trait("tier", "git")]
public sealed class RefNavigatorViewModelTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];
    private readonly List<RefNavigatorViewModel> _vms = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var vm in _vms)
            vm.Dispose();
        await _host.DisposeAsync();
        foreach (var repo in _repos)
            repo.Dispose();
    }

    private TestRepositoryBuilder NewRepo(Action<TestRepositoryBuilder>? configure = null)
    {
        var repo = TestRepositoryBuilder.Create();
        _repos.Add(repo);
        repo.WriteFile("a.txt", "one\n").Stage();
        repo.Commit("c1");
        configure?.Invoke(repo);
        return repo;
    }

    private async Task<RepositoryContext> OpenAsync(TestRepositoryBuilder repo, bool paintDone = true)
    {
        var ctx = _host.Open(repo.Path);
        if (paintDone)
            ctx.MarkFirstGraphPaintDone();
        await ctx.State.RefreshRefsAsync();
        return ctx;
    }

    private static StashEntry Stash(int index, string message, char id) => new()
    {
        Index = index,
        CommitId = new GitObjectId(new string(id, 40)),
        Message = message,
        AuthorName = "T",
        AuthorEmail = "t@example.com",
        CreatedAt = DateTimeOffset.Now,
        BranchName = "main",
    };

    private sealed class Rig
    {
        public Rig(RepositoryContext? focused, IUiDispatcher? ui = null, IStashService? stashes = null)
        {
            Workspace.Set(focused);
            Vm = new RefNavigatorViewModel(Workspace, Nav, Dialogs, Notifications, stashes ?? Stashes, Submodules, Worktrees,
                Cache, Actions, ui ?? new ImmediateUiDispatcher(), RemoteDialogs);
        }

        public FakeWorkspace Workspace { get; } = new();
        public NavigationBus Nav { get; } = new();
        public FakeDialogService Dialogs { get; } = new();
        public FakeNotificationService Notifications { get; } = new();
        public RecordingStashService Stashes { get; } = new();
        public StubSubmoduleService Submodules { get; } = new();
        public StubWorktreeService Worktrees { get; } = new();
        public StubRepoStateCache Cache { get; } = new();
        public RepoActionBus Actions { get; } = new();
        public FakeRemoteDialogs RemoteDialogs { get; } = new();
        public RefNavigatorViewModel Vm { get; }
    }

    /// <summary>A view model bound to <paramref name="focused"/>. <paramref name="configure"/> runs
    /// first, so stashes and the like can be set up before the lists are loaded at bind.</summary>
    private Rig NewRig(RepositoryContext? focused, IUiDispatcher? ui = null, Action<Rig>? configure = null, IStashService? stashes = null)
    {
        var rig = new Rig(configure is null ? focused : null, ui, stashes);
        _vms.Add(rig.Vm);
        if (configure is not null)
        {
            configure(rig);
            rig.Workspace.ChangeFocus(focused);
        }
        return rig;
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 300 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition(), $"Timed out waiting for {what}.");
    }

    private static string[] Texts(RefSectionViewModel section) => [.. section.Rows.Select(r => r switch
    {
        RefFolderRowViewModel f => f.Segment,
        RefLeafRowViewModel l => l.Text,
        _ => "---",
    })];

    // ── flattening and expand state ──────────────────────────────────────────

    [Fact]
    public async Task Local_branches_are_a_flat_list_with_depth_zero_folders_open_and_deeper_ones_collapsed()
    {
        var repo = NewRepo(r =>
        {
            r.Git("branch", "feature/auth/login");
            r.Git("branch", "feature/auth/logout");
            r.Git("branch", "fix/x");
        });
        var rig = NewRig(await OpenAsync(repo));

        Assert.Equal(["feature", "auth", "fix", "x", "main"], Texts(rig.Vm.Local));
        var feature = Assert.IsType<RefFolderRowViewModel>(rig.Vm.Local.Rows[0]);
        var auth = Assert.IsType<RefFolderRowViewModel>(rig.Vm.Local.Rows[1]);
        Assert.Equal((0, true), (feature.Depth, feature.IsOpen));
        Assert.Equal((1, false), (auth.Depth, auth.IsOpen));
        Assert.Equal("Local Branches (4)", rig.Vm.Local.Title);
        Assert.Equal("LOCAL BRANCHES (4)", rig.Vm.Local.HeaderText);

        var main = Assert.IsType<RefBranchRowViewModel>(rig.Vm.Local.Rows[^1]);
        Assert.True(main.IsCurrent);
        Assert.True(main.IsAccent);
    }

    [Fact]
    public async Task Toggling_a_folder_expands_and_collapses_it_and_the_state_is_remembered()
    {
        var repo = NewRepo(r =>
        {
            r.Git("branch", "feature/auth/login");
            r.Git("branch", "feature/auth/logout");
        });
        var rig = NewRig(await OpenAsync(repo));

        var auth = (RefFolderRowViewModel)rig.Vm.Local.Rows[1];
        rig.Vm.ToggleFolderCommand.Execute(auth);
        Assert.Equal(["feature", "auth", "login", "logout", "main"], Texts(rig.Vm.Local));
        Assert.Equal([0, 1, 2, 2, 0], rig.Vm.Local.Rows.Select(r => r switch
        {
            RefFolderRowViewModel f => f.Depth,
            RefLeafRowViewModel l => l.Depth,
            _ => -1,
        }));

        var feature = (RefFolderRowViewModel)rig.Vm.Local.Rows[0];
        rig.Vm.ToggleFolderCommand.Execute(feature);
        Assert.Equal(["feature", "main"], Texts(rig.Vm.Local));

        rig.Vm.ToggleFolderCommand.Execute((RefFolderRowViewModel)rig.Vm.Local.Rows[0]);
        Assert.Equal(["feature", "auth", "login", "logout", "main"], Texts(rig.Vm.Local));
    }

    [Fact]
    public async Task A_refs_change_replaces_the_rows_once()
    {
        var repo = NewRepo();
        var ctx = await OpenAsync(repo);
        var rig = NewRig(ctx);
        var changes = 0;
        rig.Vm.Local.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RefSectionViewModel.Rows))
                changes++;
        };

        repo.Git("branch", "topic");
        await ctx.State.RefreshRefsAsync();

        Assert.Equal(["main", "topic"], Texts(rig.Vm.Local));
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Sections_are_titled_with_counts_and_a_closed_section_holds_no_rows()
    {
        var repo = NewRepo(r =>
        {
            r.Git("tag", "v1");
            r.Git("tag", "v2");
        });
        var rig = NewRig(await OpenAsync(repo));

        Assert.Equal("Tags (2)", rig.Vm.Tags.Title);
        Assert.False(rig.Vm.Tags.IsOpen);
        Assert.Empty(rig.Vm.Tags.Rows);

        rig.Vm.Tags.ToggleCommand.Execute(null);

        Assert.True(rig.Vm.Tags.IsOpen);
        Assert.Equal(["v2", "v1"], Texts(rig.Vm.Tags));
        Assert.All(rig.Vm.Tags.Rows, r => Assert.IsType<RefTagRowViewModel>(r));
        Assert.True(rig.Vm.Local.IsOpen);
        Assert.True(rig.Vm.Remotes.IsOpen);
        Assert.False(rig.Vm.Stashes.IsOpen);
        Assert.False(rig.Vm.Submodules.IsOpen);
        Assert.False(rig.Vm.Worktrees.IsOpen);
        Assert.Equal(
            [RefSectionKind.Local, RefSectionKind.Remotes, RefSectionKind.Tags, RefSectionKind.Stashes, RefSectionKind.Submodules, RefSectionKind.Worktrees],
            rig.Vm.Sections.Select(s => s.Kind));
    }

    [Fact]
    public async Task A_detached_head_is_the_first_local_row_and_selecting_it_selects_the_commit()
    {
        var repo = NewRepo(r => r.Git("checkout", "--detach"));
        var ctx = await OpenAsync(repo);
        var rig = NewRig(ctx);

        var head = Assert.IsType<RefLeafRowViewModel>(rig.Vm.Local.Rows[0]);
        Assert.Equal($"HEAD (detached at {repo.HeadSha[..7]})", head.Text);
        Assert.True(head.IsCurrent);
        Assert.Equal("Local Branches (1)", rig.Vm.Local.Title);

        var revealed = new List<GitObjectId>();
        ctx.State.RevealCommitRequested += (_, e) => revealed.Add(e.CommitId);
        rig.Vm.SelectCommand.Execute(head);

        Assert.Equal(new GitObjectId(repo.HeadSha), ctx.State.SelectedCommitId);
        Assert.Single(revealed);
    }

    [Fact]
    public async Task Remote_roots_list_every_configured_remote_and_number_them_when_there_are_two()
    {
        var repo = NewRepo(r =>
        {
            r.Git("remote", "add", "origin", "https://example.com/repo.git");
            r.Git("remote", "add", "upstream", "https://example.com/up.git");
            r.Git("update-ref", "refs/remotes/origin/main", "HEAD");
            r.Git("update-ref", "refs/remotes/origin/feature/x", "HEAD");
            r.Git("update-ref", "refs/remotes/ghost/main", "HEAD");
        });
        var rig = NewRig(await OpenAsync(repo));

        Assert.Equal("Remotes (2)", rig.Vm.Remotes.Title);
        var roots = rig.Vm.Remotes.Rows.OfType<RefFolderRowViewModel>().Where(f => f.IsRemoteRoot).ToList();
        Assert.Equal(["ghost", "origin", "upstream"], roots.Select(r => r.Segment));
        Assert.Equal([0, 1, 2], roots.Select(r => r.CloudNumber));
        Assert.Equal(["ghost", "1 · origin", "2 · upstream"], roots.Select(r => r.Tooltip));
        Assert.Equal([false, true, true], roots.Select(r => r.HasMenu));

        var origin = roots.Single(r => r.Segment == "origin");
        Assert.Contains("feature", Texts(rig.Vm.Remotes));
        Assert.True(origin.IsOpen);
        rig.Vm.ToggleFolderCommand.Execute(origin);
        Assert.DoesNotContain("feature", Texts(rig.Vm.Remotes));
    }

    // ── filters ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_filter_hoists_ranked_matches_above_the_tree_and_leaves_the_tree_alone()
    {
        var repo = NewRepo(r =>
        {
            r.Git("branch", "feature/auth-rework");
            r.Git("branch", "fix/authz-header");
            r.Git("branch", "chore/deps");
        });
        var rig = NewRig(await OpenAsync(repo));
        var unfiltered = rig.Vm.Local.Rows.ToList();

        rig.Vm.Local.ToggleFilterCommand.Execute(null);
        Assert.True(rig.Vm.Local.ShowFilterBox);
        rig.Vm.Local.FilterText = "auth";

        var rows = rig.Vm.Local.Rows;
        var separator = rows.ToList().FindIndex(r => r is RefSeparatorRowViewModel);
        Assert.Equal(2, separator);
        Assert.Equal(["feature/auth-rework", "fix/authz-header"], rows.Take(separator).Cast<RefBranchRowViewModel>().Select(b => b.Text).Order());
        Assert.Equal(Texts(rig.Vm.Local).Skip(separator + 1), UnfilteredTexts(unfiltered));
        Assert.Equal("Local Branches (4)", rig.Vm.Local.Title);
    }

    private static string[] UnfilteredTexts(IEnumerable<RefRowViewModel> rows) => [.. rows.Select(r => r switch
    {
        RefFolderRowViewModel f => f.Segment,
        RefLeafRowViewModel l => l.Text,
        _ => "---",
    })];

    [Fact]
    public async Task A_filter_with_no_match_says_so_and_escape_clears_and_closes_it()
    {
        var repo = NewRepo(r => r.Git("branch", "topic"));
        var rig = NewRig(await OpenAsync(repo));

        rig.Vm.Local.ToggleFilterCommand.Execute(null);
        rig.Vm.Local.FilterText = "zzzz";
        Assert.Equal("No matches.", Texts(rig.Vm.Local)[0]);
        Assert.True(Assert.IsType<RefLeafRowViewModel>(rig.Vm.Local.Rows[0]).IsFaint);

        rig.Vm.Local.CloseFilterCommand.Execute(null);

        Assert.False(rig.Vm.Local.IsFilterOpen);
        Assert.False(rig.Vm.Local.ShowFilterBox);
        Assert.Equal("", rig.Vm.Local.FilterText);
        Assert.Equal(["main", "topic"], Texts(rig.Vm.Local));
    }

    [Fact]
    public async Task Opening_a_filter_requests_focus_and_reexpanding_a_section_does_not()
    {
        var repo = NewRepo(r => r.Git("branch", "topic"));
        var rig = NewRig(await OpenAsync(repo));
        var requests = 0;
        rig.Vm.Local.FocusFilterRequested += (_, _) => requests++;

        rig.Vm.Local.ToggleFilterCommand.Execute(null);
        Assert.Equal(1, requests);

        rig.Vm.Local.ToggleCommand.Execute(null);
        rig.Vm.Local.ToggleCommand.Execute(null);
        Assert.True(rig.Vm.Local.ShowFilterBox);
        Assert.Equal(1, requests);

        rig.Vm.Local.CloseFilterCommand.Execute(null);
        Assert.Equal(1, requests);

        rig.Vm.Local.ToggleFilterCommand.Execute(null);
        Assert.Equal(2, requests);

        rig.Vm.Local.ToggleFilterCommand.Execute(null);
        Assert.False(rig.Vm.Local.IsFilterOpen);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task Opening_a_filter_expands_a_collapsed_section()
    {
        var repo = NewRepo(r => r.Git("tag", "v1.0.0"));
        var rig = NewRig(await OpenAsync(repo));
        Assert.False(rig.Vm.Tags.IsOpen);

        rig.Vm.Tags.ToggleFilterCommand.Execute(null);

        Assert.True(rig.Vm.Tags.IsOpen);
        Assert.True(rig.Vm.Tags.IsFilterOpen);
        Assert.Equal(["v1.0.0"], Texts(rig.Vm.Tags));
    }

    [Fact]
    public async Task The_stash_filter_matches_the_numbered_label()
    {
        var repo = NewRepo();
        var rig = NewRig(await OpenAsync(repo), configure: r => r.Stashes.Entries = [Stash(0, "On a: x", '1'), Stash(1, "On b: y", '2')]);
        rig.Vm.Stashes.ToggleFilterCommand.Execute(null);
        await WaitForAsync(() => Texts(rig.Vm.Stashes).Contains("1. On a: x"), "the stash list");

        rig.Vm.Stashes.FilterText = "2. On";

        Assert.Equal("2. On b: y", Texts(rig.Vm.Stashes)[0]);
        Assert.Equal("Stashes (2)", rig.Vm.Stashes.Title);
    }

    // ── selection and checkout ───────────────────────────────────────────────

    [Fact]
    public async Task Selecting_a_stash_selects_without_asking_the_graph_to_reveal_it()
    {
        var repo = NewRepo();
        var ctx = await OpenAsync(repo);
        var entry = Stash(0, "wip", 'a');
        var rig = NewRig(ctx, configure: r => r.Stashes.Entries = [entry]);
        rig.Vm.Stashes.ToggleCommand.Execute(null);
        await WaitForAsync(() => rig.Vm.Stashes.Rows.OfType<RefStashRowViewModel>().Any(), "the stash list");
        var revealed = false;
        ctx.State.RevealCommitRequested += (_, _) => revealed = true;

        rig.Vm.SelectCommand.Execute(rig.Vm.Stashes.Rows[0]);

        Assert.Equal(entry.CommitId, ctx.State.SelectedCommitId);
        Assert.False(revealed);
    }

    [Fact]
    public async Task Selecting_a_branch_selects_and_reveals_its_tip()
    {
        var repo = NewRepo(r => r.Git("branch", "topic"));
        var ctx = await OpenAsync(repo);
        var rig = NewRig(ctx);
        var revealed = new List<GitObjectId>();
        ctx.State.RevealCommitRequested += (_, e) => revealed.Add(e.CommitId);

        var topic = rig.Vm.Local.Rows.OfType<RefBranchRowViewModel>().Single(b => b.Text == "topic");
        rig.Vm.SelectCommand.Execute(topic);

        Assert.Equal(new GitObjectId(repo.HeadSha), ctx.State.SelectedCommitId);
        Assert.Equal([new GitObjectId(repo.HeadSha)], revealed);
    }

    [Fact]
    public async Task Checking_out_a_local_branch_switches_to_it_and_says_so()
    {
        var repo = NewRepo(r => r.Git("branch", "topic"));
        var ctx = await OpenAsync(repo);
        var rig = NewRig(ctx);

        var topic = rig.Vm.Local.Rows.OfType<RefBranchRowViewModel>().Single(b => b.Text == "topic");
        await rig.Vm.CheckoutCommand.ExecuteAsync(topic);

        Assert.Equal("topic", repo.Git("rev-parse", "--abbrev-ref", "HEAD").Trim());
        var notice = Assert.Single(rig.Notifications.Published);
        Assert.Equal(NotificationSeverity.Success, notice.Severity);
        Assert.Equal("Switched to topic.", notice.Message);
        Assert.True(rig.Vm.Local.Rows.OfType<RefBranchRowViewModel>().Single(b => b.Text == "topic").IsCurrent);
    }

    [Fact]
    public async Task Checking_out_a_remote_row_with_no_local_counterpart_creates_a_tracking_branch()
    {
        var repo = NewRepo(r =>
        {
            r.Git("remote", "add", "origin", "https://example.com/repo.git");
            r.Git("update-ref", "refs/remotes/origin/feat", "HEAD");
        });
        var ctx = await OpenAsync(repo);
        var rig = NewRig(ctx);

        var feat = rig.Vm.Remotes.Rows.OfType<RefBranchRowViewModel>().Single();
        Assert.True(feat.IsRemote);
        await rig.Vm.CheckoutCommand.ExecuteAsync(feat);

        Assert.Equal("feat", repo.Git("rev-parse", "--abbrev-ref", "HEAD").Trim());
        Assert.Equal("origin/feat", repo.Git("rev-parse", "--abbrev-ref", "feat@{upstream}").Trim());
        Assert.Equal("Switched to feat.", Assert.Single(rig.Notifications.Published).Message);
    }

    [Fact]
    public async Task Checkout_after_stash_and_retry_announces_the_switch()
    {
        var repo = NewRepo(r =>
        {
            r.WriteFile("other.txt", "c2\n").Stage();
            r.Commit("c2");
            r.Git("branch", "older");
            r.WriteFile("other.txt", "c3\n").Stage();
            r.Commit("c3");
            r.WriteFile("other.txt", "edited\n");
        });
        var ctx = await OpenAsync(repo);
        var rig = NewRig(ctx, stashes: new StashService(_host.Cli));

        var older = rig.Vm.Local.Rows.OfType<RefBranchRowViewModel>().Single(b => b.Text == "older");
        await rig.Vm.CheckoutCommand.ExecuteAsync(older);

        var refusal = Assert.Single(rig.Notifications.Published);
        Assert.Equal(NotificationSeverity.Error, refusal.Severity);
        var retry = Assert.Single(refusal.Actions);
        Assert.Equal("Stash & retry", retry.Label);

        await retry.Invoke();

        Assert.Equal("older", repo.Git("rev-parse", "--abbrev-ref", "HEAD").Trim());
        var notice = rig.Notifications.Published[^1];
        Assert.Equal(NotificationSeverity.Success, notice.Severity);
        Assert.Equal("Switched to older.", notice.Message);
        Assert.Equal(1, rig.Notifications.Published.Count(n => n.Message == "Switched to older."));
    }

    [Fact]
    public async Task Checking_out_the_current_branch_does_nothing()
    {
        var repo = NewRepo();
        var rig = NewRig(await OpenAsync(repo));

        await rig.Vm.CheckoutCommand.ExecuteAsync((RefBranchRowViewModel)rig.Vm.Local.Rows[0]);

        Assert.Empty(rig.Notifications.Published);
    }

    [Fact]
    public async Task Double_clicking_a_branch_row_checks_it_out()
    {
        var repo = NewRepo(r => r.Git("branch", "topic"));
        var rig = NewRig(await OpenAsync(repo));

        var topic = rig.Vm.Local.Rows.OfType<RefBranchRowViewModel>().Single(b => b.Text == "topic");
        rig.Vm.ActivateCommand.Execute(topic);
        await WaitForAsync(() => repo.Git("rev-parse", "--abbrev-ref", "HEAD").Trim() == "topic", "the checkout");
    }

    // ── destructive commands confirm ─────────────────────────────────────────

    [Fact]
    public async Task Deleting_a_tag_confirms_naming_it_and_cancel_keeps_it()
    {
        var repo = NewRepo(r => r.Git("tag", "v1"));
        var ctx = await OpenAsync(repo);
        var rig = NewRig(ctx);
        rig.Vm.Tags.ToggleCommand.Execute(null);
        var row = (RefTagRowViewModel)rig.Vm.Tags.Rows[0];

        rig.Dialogs.EnqueueConfirmDestructive(false);
        await rig.Vm.DeleteTagCommand.ExecuteAsync(row);
        var call = Assert.Single(rig.Dialogs.ConfirmDestructiveCalls);
        Assert.Contains("v1", call.Effect);
        Assert.Equal("Delete", call.ConfirmLabel);
        Assert.Contains("v1", repo.Git("tag", "--list"));

        rig.Dialogs.EnqueueConfirmDestructive(true);
        await rig.Vm.DeleteTagCommand.ExecuteAsync(row);
        Assert.Equal(2, rig.Dialogs.ConfirmDestructiveCalls.Count);
        Assert.DoesNotContain("v1", repo.Git("tag", "--list"));
        Assert.Equal("Tags (0)", rig.Vm.Tags.Title);
        Assert.Equal(["No tags."], Texts(rig.Vm.Tags));
    }

    [Fact]
    public async Task Dropping_a_stash_confirms_and_cancel_drops_nothing()
    {
        var repo = NewRepo();
        var entry = Stash(0, "keep me", 'a');
        var rig = NewRig(await OpenAsync(repo), configure: r => r.Stashes.Entries = [entry]);
        rig.Vm.Stashes.ToggleCommand.Execute(null);
        await WaitForAsync(() => rig.Vm.Stashes.Rows.OfType<RefStashRowViewModel>().Any(), "the stash list");
        var row = (RefStashRowViewModel)rig.Vm.Stashes.Rows[0];

        rig.Dialogs.EnqueueConfirmDestructive(false);
        await rig.Vm.StashDropCommand.ExecuteAsync(row);
        Assert.Single(rig.Dialogs.ConfirmDestructiveCalls);
        Assert.Contains("keep me", rig.Dialogs.ConfirmDestructiveCalls[0].Effect);
        Assert.Empty(rig.Stashes.Dropped);

        rig.Dialogs.EnqueueConfirmDestructive(true);
        await rig.Vm.StashDropCommand.ExecuteAsync(row);
        Assert.Equal([entry.CommitId], rig.Stashes.Dropped);
    }

    [Fact]
    public async Task Applying_a_stash_does_not_ask_and_reloads_the_list()
    {
        var repo = NewRepo();
        var entry = Stash(0, "wip", 'a');
        var rig = NewRig(await OpenAsync(repo), configure: r => r.Stashes.Entries = [entry]);
        rig.Vm.Stashes.ToggleCommand.Execute(null);
        await WaitForAsync(() => rig.Vm.Stashes.Rows.OfType<RefStashRowViewModel>().Any(), "the stash list");
        var lists = rig.Stashes.ListCount;

        await rig.Vm.StashApplyCommand.ExecuteAsync((RefStashRowViewModel)rig.Vm.Stashes.Rows[0]);
        await WaitForAsync(() => rig.Stashes.ListCount > lists, "the reload");

        Assert.Equal([entry.CommitId], rig.Stashes.Applied);
        Assert.Empty(rig.Dialogs.ConfirmDestructiveCalls);
    }

    [Fact]
    public async Task Creating_a_branch_from_a_stash_asks_for_a_name_first()
    {
        var repo = NewRepo();
        var entry = Stash(0, "wip", 'a');
        var rig = NewRig(await OpenAsync(repo), configure: r => r.Stashes.Entries = [entry]);
        rig.Vm.Stashes.ToggleCommand.Execute(null);
        await WaitForAsync(() => rig.Vm.Stashes.Rows.OfType<RefStashRowViewModel>().Any(), "the stash list");
        var row = (RefStashRowViewModel)rig.Vm.Stashes.Rows[0];

        await rig.Vm.StashBranchCommand.ExecuteAsync(row);
        Assert.Empty(rig.Stashes.Branched);

        rig.Dialogs.EnqueuePrompt("  from-stash ");
        await rig.Vm.StashBranchCommand.ExecuteAsync(row);
        Assert.Equal([(entry.CommitId, "from-stash")], rig.Stashes.Branched);
    }

    [Fact]
    public async Task Removing_a_worktree_confirms_naming_its_path()
    {
        var repo = NewRepo();
        var rig = NewRig(await OpenAsync(repo));
        rig.Worktrees.Entries =
        [
            new WorktreeInfo { Path = "/work/main", Branch = "main", IsMainWorktree = true, IsLocked = false },
            new WorktreeInfo { Path = "/work/topic", Branch = "topic", IsMainWorktree = false, IsLocked = true },
        ];
        rig.Vm.Worktrees.ToggleCommand.Execute(null);
        await WaitForAsync(() => rig.Vm.Worktrees.Rows.OfType<RefWorktreeRowViewModel>().Any(), "the worktree list");

        var rows = rig.Vm.Worktrees.Rows.OfType<RefWorktreeRowViewModel>().ToList();
        Assert.Equal(["main  (main)", "topic  🔒"], rows.Select(r => r.Text));
        Assert.Equal([false, true], rows.Select(r => r.HasMenu));
        Assert.Equal([false, true], rows.Select(r => r.IsLocked));
        Assert.Equal("Prune stale", Texts(rig.Vm.Worktrees)[^1]);

        rig.Dialogs.EnqueueConfirmDestructive(false);
        await rig.Vm.WorktreeRemoveCommand.ExecuteAsync(rows[1]);
        Assert.Contains("/work/topic", Assert.Single(rig.Dialogs.ConfirmDestructiveCalls).Effect);
        Assert.Empty(rig.Worktrees.Removed);

        rig.Dialogs.EnqueueConfirmDestructive(true);
        await rig.Vm.WorktreeRemoveCommand.ExecuteAsync(rows[1]);
        Assert.Equal(["/work/topic"], rig.Worktrees.Removed);

        await rig.Vm.WorktreeUnlockCommand.ExecuteAsync(rows[1]);
        Assert.Equal(["/work/topic"], rig.Worktrees.Unlocked);
    }

    [Fact]
    public async Task Removing_a_submodule_confirms_naming_what_is_lost()
    {
        var repo = NewRepo();
        var rig = NewRig(await OpenAsync(repo));
        var sm = new SubmoduleInfo { Name = "libs/core", Path = "libs/core", Url = "u", Status = SubmoduleStatus.Clean };
        rig.Submodules.Entries = [sm];
        rig.Vm.Submodules.ToggleCommand.Execute(null);
        await WaitForAsync(() => rig.Vm.Submodules.Rows.OfType<RefSubmoduleRowViewModel>().Any(), "the submodule list");
        var row = (RefSubmoduleRowViewModel)rig.Vm.Submodules.Rows[0];
        Assert.Equal("core  (Clean)", row.Text);
        Assert.Equal("libs/core", row.Tooltip);

        rig.Dialogs.EnqueueConfirmDestructive(false);
        await rig.Vm.SubmoduleRemoveCommand.ExecuteAsync(row);
        var call = Assert.Single(rig.Dialogs.ConfirmDestructiveCalls);
        Assert.Contains("libs/core", call.Effect);
        Assert.Contains(".git/modules/libs/core", call.Effect);
        Assert.Empty(rig.Submodules.Removed);

        rig.Dialogs.EnqueueConfirmDestructive(true);
        await rig.Vm.SubmoduleRemoveCommand.ExecuteAsync(row);
        Assert.Equal(["libs/core"], rig.Submodules.Removed);
    }

    [Fact]
    public async Task A_submodule_update_shows_a_busy_row_until_it_finishes()
    {
        var repo = NewRepo();
        var rig = NewRig(await OpenAsync(repo));
        var sm = new SubmoduleInfo { Name = "core", Path = "core", Url = "u", Status = SubmoduleStatus.Uninitialized };
        rig.Submodules.Entries = [sm];
        rig.Vm.Submodules.ToggleCommand.Execute(null);
        await WaitForAsync(() => rig.Vm.Submodules.Rows.OfType<RefSubmoduleRowViewModel>().Any(), "the submodule list");
        Assert.Equal(["core  (Uninitialized)", "Update all"], Texts(rig.Vm.Submodules));

        var gate = new TaskCompletionSource();
        rig.Submodules.InitGate = gate.Task;
        var running = rig.Vm.SubmoduleInitAndUpdateCommand.ExecuteAsync((RefSubmoduleRowViewModel)rig.Vm.Submodules.Rows[0]);

        Assert.Equal(["core  — Initializing…", "Update all"], Texts(rig.Vm.Submodules));
        Assert.IsType<RefLeafRowViewModel>(rig.Vm.Submodules.Rows[0]);

        gate.SetResult();
        await running;
        await WaitForAsync(() => Texts(rig.Vm.Submodules)[0] != "core  — Initializing…", "the busy row to clear");
        Assert.Equal([(string?)"core"], rig.Submodules.Initialized);
    }

    // ── power tools ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Power_tools_show_the_cached_lists_first_and_load_after_the_first_graph_paint()
    {
        var repo = NewRepo();
        var ctx = await OpenAsync(repo, paintDone: false);
        var cached = Stash(0, "cached", 'c');
        var fresh = Stash(0, "fresh", 'f');
        var rig = new Rig(null);
        _vms.Add(rig.Vm);
        rig.Cache.Snapshots[ctx.RepositoryPath] = new RepoStateSnapshot(null, null, null, [cached], [], []);
        rig.Stashes.Entries = [fresh];

        rig.Workspace.ChangeFocus(ctx);

        await WaitForAsync(() => rig.Vm.Stashes.Title == "Stashes (1)", "the cached stash list");
        rig.Vm.Stashes.ToggleCommand.Execute(null);
        Assert.Equal(["1. cached"], Texts(rig.Vm.Stashes));
        await Task.Delay(100);
        Assert.Equal(0, rig.Stashes.ListCount);

        ctx.MarkFirstGraphPaintDone();
        await WaitForAsync(() => Texts(rig.Vm.Stashes).SequenceEqual(["1. fresh"]), "the stash list");
        Assert.Equal(1, rig.Stashes.ListCount);
        Assert.Contains(rig.Cache.Saved, s => s.Stashes is { Count: 1 } list && list[0].Message == "fresh");
    }

    [Fact]
    public async Task A_section_with_nothing_cached_says_loading_until_its_list_arrives()
    {
        var repo = NewRepo();
        var ctx = await OpenAsync(repo, paintDone: false);
        var rig = NewRig(ctx);

        rig.Vm.Stashes.ToggleCommand.Execute(null);
        Assert.Equal(["Loading…"], Texts(rig.Vm.Stashes));
        Assert.Equal("Stashes", rig.Vm.Stashes.Title);

        ctx.MarkFirstGraphPaintDone();
        await WaitForAsync(() => Texts(rig.Vm.Stashes).SequenceEqual(["No stashes."]), "the stash list");
        Assert.Equal("Stashes (0)", rig.Vm.Stashes.Title);
    }

    [Fact]
    public async Task Binding_reads_the_cache_off_the_calling_thread_and_applies_it_afterwards()
    {
        var repo = NewRepo();
        var ctx = await OpenAsync(repo, paintDone: false);
        var rig = new Rig(null);
        _vms.Add(rig.Vm);
        rig.Cache.Snapshots[ctx.RepositoryPath] = new RepoStateSnapshot(null, null, null, [Stash(0, "cached", 'c')], [], []);
        var gate = rig.Cache.BlockLoads();

        rig.Workspace.ChangeFocus(ctx);

        Assert.True(rig.Cache.LoadEntered.Wait(TimeSpan.FromSeconds(30)), "the cache was never read");
        Assert.False(rig.Cache.GateTimedOut, "the bind waited for the cache read");
        Assert.Equal(["main"], Texts(rig.Vm.Local));
        Assert.Equal("Stashes", rig.Vm.Stashes.Title);

        gate.Set();
        await WaitForAsync(() => rig.Vm.Stashes.Title == "Stashes (1)", "the cached stash list");
    }

    [Fact]
    public async Task Binding_reads_only_the_power_tools_half_of_the_cache()
    {
        var repo = NewRepo();
        var ctx = await OpenAsync(repo, paintDone: false);
        var rig = new Rig(null);
        _vms.Add(rig.Vm);
        rig.Cache.Snapshots[ctx.RepositoryPath] = new RepoStateSnapshot(null, null, null, [Stash(0, "cached", 'c')], [], []);

        rig.Workspace.ChangeFocus(ctx);
        await WaitForAsync(() => rig.Vm.Stashes.Title == "Stashes (1)", "the cached stash list");

        Assert.Equal(0, rig.Cache.FullLoads);
        Assert.True(rig.Cache.Loads > 0);
    }

    [Fact]
    public async Task A_stash_change_during_the_bind_read_keeps_the_cached_submodules_and_worktrees()
    {
        var repo = NewRepo();
        var ctx = await OpenAsync(repo, paintDone: false);
        var rig = new Rig(null);
        _vms.Add(rig.Vm);
        var sm = new SubmoduleInfo { Name = "libs/core", Path = "libs/core", Url = "u", Status = SubmoduleStatus.Clean };
        rig.Cache.Snapshots[ctx.RepositoryPath] = new RepoStateSnapshot(null, null, null, [Stash(0, "cached", 'c')], [sm], []);
        var gate = rig.Cache.BlockLoads();

        rig.Workspace.ChangeFocus(ctx);
        Assert.True(rig.Cache.LoadEntered.Wait(TimeSpan.FromSeconds(30)), "the cache was never read");

        // A stash-only change bumps the cache generation while the bind's read is still running.
        rig.Vm.OnRefsChanged(ctx.State, new RefsChangedEventArgs(ctx.State.Branches, ctx.State.Tags, ctx.State.Remotes)
        {
            StashStamp = ctx.State.StashStamp + 1,
        });
        gate.Set();

        await WaitForAsync(() => rig.Vm.Submodules.Title == "Submodules (1)", "the cached submodule list");
        Assert.Equal("Stashes (1)", rig.Vm.Stashes.Title);
    }

    [Fact]
    public async Task A_refs_change_reads_the_cache_off_the_calling_thread()
    {
        var repo = NewRepo();
        var ctx = await OpenAsync(repo);
        var ui = new RecordingUiDispatcher();
        var rig = NewRig(ctx, ui);
        ui.DrainPosted();
        await WaitForAsync(() => rig.Stashes.ListCount == 1, "the first stash list");
        var loads = rig.Cache.Loads;
        var gate = rig.Cache.BlockLoads();

        rig.Stashes.Entries = [Stash(0, "pushed", 'b')];
        rig.Vm.OnRefsChanged(ctx.State, new RefsChangedEventArgs(ctx.State.Branches, ctx.State.Tags, ctx.State.Remotes)
        {
            StashStamp = ctx.State.StashStamp + 1,
        });
        ui.DrainPosted();

        await WaitForAsync(() => rig.Cache.Loads > loads, "the cache read to start");
        Assert.False(rig.Cache.GateTimedOut, "the refs change waited for the cache read");
        Assert.Equal(1, rig.Stashes.ListCount);

        gate.Set();
        await WaitForAsync(() => rig.Stashes.ListCount == 2, "the relisted stashes");
    }

    [Fact]
    public async Task Saving_the_power_tools_does_not_hold_up_the_rebuilt_rows()
    {
        var repo = NewRepo();
        var ctx = await OpenAsync(repo, paintDone: false);
        var rig = NewRig(ctx);
        rig.Stashes.Entries = [Stash(0, "fresh", 'f')];
        rig.Vm.Stashes.ToggleCommand.Execute(null);
        var gate = rig.Cache.BlockSaves();

        ctx.MarkFirstGraphPaintDone();

        await WaitForAsync(() => Texts(rig.Vm.Stashes).SequenceEqual(["1. fresh"]), "the stash list");
        Assert.True(rig.Cache.SaveEntered.Wait(TimeSpan.FromSeconds(30)), "the lists were never saved");
        Assert.Empty(rig.Cache.Saved);

        gate.Set();
        await WaitForAsync(() => rig.Cache.Saved.Count > 0, "the save to finish");
        Assert.False(rig.Cache.GateTimedOut);
    }

    [Fact]
    public async Task A_cache_read_that_lands_after_a_rebind_is_not_applied_to_the_new_repository()
    {
        var repo = NewRepo();
        var other = NewRepo();
        var ctx = await OpenAsync(repo, paintDone: false);
        var ctx2 = await OpenAsync(other, paintDone: false);
        var rig = new Rig(null);
        _vms.Add(rig.Vm);
        rig.Cache.Snapshots[ctx.RepositoryPath] = new RepoStateSnapshot(null, null, null, [Stash(0, "stale", 'a')], [], []);
        var gate = rig.Cache.BlockLoads();

        rig.Workspace.ChangeFocus(ctx);
        Assert.True(rig.Cache.LoadEntered.Wait(TimeSpan.FromSeconds(30)), "the cache was never read");
        rig.Workspace.ChangeFocus(ctx2);
        gate.Set();
        await WaitForAsync(() => rig.Cache.Loads >= 2, "both cache reads");
        await Task.Delay(100);

        Assert.Equal("Stashes", rig.Vm.Stashes.Title);
        Assert.Equal(ctx2, rig.Vm.Repository);
    }

    [Fact]
    public async Task A_tags_only_change_rebuilds_only_the_tags_section()
    {
        var repo = NewRepo(r => r.Git("tag", "v1"));
        var ctx = await OpenAsync(repo);
        var rig = NewRig(ctx);
        rig.Vm.Tags.ToggleCommand.Execute(null);
        var rebuilt = TrackRowChanges(rig.Vm);

        repo.Git("tag", "v2");
        await ctx.State.RefreshRefsAsync();

        Assert.Equal(["v2", "v1"], Texts(rig.Vm.Tags));
        Assert.Equal(["Tags"], rebuilt.Distinct());
    }

    [Fact]
    public async Task A_branch_only_change_leaves_the_tags_section_alone()
    {
        var repo = NewRepo(r => r.Git("tag", "v1"));
        var ctx = await OpenAsync(repo);
        var rig = NewRig(ctx);
        rig.Vm.Tags.ToggleCommand.Execute(null);
        var rebuilt = TrackRowChanges(rig.Vm);

        repo.Git("branch", "topic");
        await ctx.State.RefreshRefsAsync();

        Assert.Equal(["main", "topic"], Texts(rig.Vm.Local));
        Assert.DoesNotContain("Tags", rebuilt);
    }

    [Fact]
    public async Task A_raise_that_changes_nothing_rebuilds_no_ref_section()
    {
        var repo = NewRepo(r => r.Git("tag", "v1"));
        var ctx = await OpenAsync(repo);
        var rig = NewRig(ctx);
        rig.Vm.Tags.ToggleCommand.Execute(null);
        var rebuilt = TrackRowChanges(rig.Vm);

        rig.Vm.OnRefsChanged(ctx.State, new RefsChangedEventArgs(ctx.State.Branches, ctx.State.Tags, ctx.State.Remotes)
        {
            StashStamp = ctx.State.StashStamp,
        });

        Assert.Empty(rebuilt);
    }

    [Fact]
    public async Task Tags_sort_by_name_ignoring_case_then_ordinal()
    {
        var repo = NewRepo(r =>
        {
            r.Git("tag", "b-1");
            r.Git("tag", "B-2");
        });
        var rig = NewRig(await OpenAsync(repo));
        rig.Vm.Tags.ToggleCommand.Execute(null);

        // Case-insensitive first (descending, like the other tag orders), ordinal as the tie-break.
        Assert.Equal(["B-2", "b-1"], Texts(rig.Vm.Tags));
    }

    private static List<string> TrackRowChanges(RefNavigatorViewModel vm)
    {
        var rebuilt = new List<string>();
        foreach (var section in new[] { vm.Local, vm.Remotes, vm.Tags })
        {
            var name = section.Kind.ToString();
            section.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(RefSectionViewModel.Rows))
                    rebuilt.Add(name);
            };
        }
        return rebuilt;
    }

    [Fact]
    public async Task A_tags_only_refresh_rebuilds_without_relisting_stashes()
    {
        var repo = NewRepo();
        var ctx = await OpenAsync(repo);
        var rig = NewRig(ctx);
        await WaitForAsync(() => rig.Stashes.ListCount == 1, "the first stash list");
        rig.Vm.Tags.ToggleCommand.Execute(null);

        repo.Git("tag", "v9");
        await ctx.State.RefreshRefsAsync();
        await Task.Delay(150);

        Assert.Equal(1, rig.Stashes.ListCount);
        Assert.Equal(["v9"], Texts(rig.Vm.Tags));
        Assert.Equal("Tags (1)", rig.Vm.Tags.Title);
    }

    [Fact]
    public async Task A_branch_refresh_does_not_relist_stashes()
    {
        var repo = NewRepo();
        var ctx = await OpenAsync(repo);
        var rig = NewRig(ctx);
        await WaitForAsync(() => rig.Stashes.ListCount == 1, "the first stash list");

        repo.Git("branch", "new-b");
        await ctx.State.RefreshRefsAsync();
        await Task.Delay(150);

        Assert.Equal(1, rig.Stashes.ListCount);
        Assert.Equal(["main", "new-b"], Texts(rig.Vm.Local));
    }

    [Fact]
    public async Task An_external_stash_push_relists_stashes_once()
    {
        var repo = NewRepo();
        var ctx = await OpenAsync(repo);
        var rig = NewRig(ctx);
        await WaitForAsync(() => rig.Stashes.ListCount == 1, "the first stash list");
        rig.Vm.Stashes.ToggleCommand.Execute(null);
        rig.Stashes.Entries = [Stash(0, "ext-entry", 'e')];

        repo.WriteFile("a.txt", "dirty\n");
        repo.Git("stash", "push", "-m", "ext-entry");
        await ctx.State.RefreshRefsAsync();

        await WaitForAsync(() => Texts(rig.Vm.Stashes).Contains("1. ext-entry"), "the relisted stashes");
        Assert.Equal(2, rig.Stashes.ListCount);
    }

    [Fact]
    public async Task Rebinding_relists_stashes()
    {
        var repo = NewRepo();
        var other = NewRepo();
        var ctx = await OpenAsync(repo);
        var ctx2 = await OpenAsync(other);
        var rig = NewRig(ctx);
        await WaitForAsync(() => rig.Stashes.ListCount == 1, "the first stash list");

        rig.Workspace.ChangeFocus(ctx2);
        await WaitForAsync(() => rig.Stashes.ListCount == 2, "the second repository's stash list");
        Assert.Equal(ctx2, rig.Vm.Repository);

        rig.Workspace.ChangeFocus(ctx);
        await WaitForAsync(() => rig.Stashes.ListCount == 3, "the first repository's stash list again");
    }

    [Fact]
    public async Task A_raise_from_the_previous_repository_after_a_rebind_is_ignored()
    {
        var repo = NewRepo();
        var other = NewRepo();
        var ctx = await OpenAsync(repo);
        var ctx2 = await OpenAsync(other);
        var rig = NewRig(ctx);
        await WaitForAsync(() => rig.Stashes.ListCount == 1, "the first stash list");
        rig.Workspace.ChangeFocus(ctx2);
        await WaitForAsync(() => rig.Stashes.ListCount == 2, "the second repository's stash list");

        var stale = new RefsChangedEventArgs([], [], []) { StashStamp = ctx2.State.StashStamp + 1 };
        rig.Vm.OnRefsChanged(ctx.State, stale);
        await Task.Delay(150);

        Assert.Equal(2, rig.Stashes.ListCount);
        Assert.Equal(ctx2.State.StashStamp, rig.Vm.LoadedStashStamp);
    }

    [Fact]
    public async Task A_raise_posts_through_the_dispatcher_and_is_dropped_once_disposed()
    {
        var repo = NewRepo();
        var ctx = await OpenAsync(repo);
        var ui = new RecordingUiDispatcher();
        var rig = NewRig(ctx, ui);
        ui.DrainPosted();
        Assert.Equal(["main"], Texts(rig.Vm.Local));

        repo.Git("branch", "topic");
        await ctx.State.RefreshRefsAsync();
        Assert.Equal(["main"], Texts(rig.Vm.Local));
        ui.DrainPosted();
        Assert.Equal(["main", "topic"], Texts(rig.Vm.Local));

        repo.Git("branch", "other");
        await ctx.State.RefreshRefsAsync();
        rig.Vm.Dispose();
        ui.DrainPosted();
        Assert.Equal(["main", "topic"], Texts(rig.Vm.Local));
    }

    // ── focus ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task With_no_repository_there_are_no_sections()
    {
        var repo = NewRepo();
        var ctx = await OpenAsync(repo);
        var rig = NewRig(null);
        Assert.Empty(rig.Vm.Sections);
        Assert.Null(rig.Vm.Repository);

        rig.Workspace.ChangeFocus(ctx);
        Assert.Equal(6, rig.Vm.Sections.Count);
        Assert.Equal(["main"], Texts(rig.Vm.Local));

        rig.Workspace.ChangeFocus(null);
        Assert.Empty(rig.Vm.Sections);
    }

    [Fact]
    public async Task Section_state_survives_a_switch_but_a_closed_repository_resets_it()
    {
        var repo = NewRepo(r => r.Git("tag", "v1"));
        var other = NewRepo();
        var ctx = await OpenAsync(repo);
        var ctx2 = await OpenAsync(other);
        var rig = NewRig(ctx);
        rig.Workspace.Open(ctx, ctx2);
        rig.Vm.Tags.ToggleCommand.Execute(null);
        rig.Vm.Local.ToggleFilterCommand.Execute(null);
        rig.Vm.Local.FilterText = "ma";

        rig.Workspace.ChangeFocus(ctx2);
        Assert.True(rig.Vm.Tags.IsOpen);
        Assert.True(rig.Vm.Local.IsFilterOpen);
        Assert.Equal("ma", rig.Vm.Local.FilterText);

        rig.Workspace.ChangeFocus(ctx);
        rig.Workspace.Open(ctx2);
        rig.Workspace.ChangeFocus(ctx2);
        Assert.False(rig.Vm.Tags.IsOpen);
        Assert.False(rig.Vm.Local.IsFilterOpen);
        Assert.Equal("", rig.Vm.Local.FilterText);
        Assert.True(rig.Vm.Local.IsOpen);
        Assert.True(rig.Vm.Remotes.IsOpen);
    }

    [Fact]
    public async Task Disposing_stops_following_the_workspace_and_the_repository()
    {
        var repo = NewRepo();
        var ctx = await OpenAsync(repo);
        var rig = NewRig(ctx);
        Assert.Equal(1, rig.Workspace.Subscribers);

        rig.Vm.Dispose();

        Assert.Equal(0, rig.Workspace.Subscribers);
        repo.Git("branch", "late");
        await ctx.State.RefreshRefsAsync();
        Assert.Equal(["main"], Texts(rig.Vm.Local));
    }

    // ── add remote ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Add_remote_asks_the_remote_dialogs_for_the_focused_repository()
    {
        var repo = NewRepo();
        var ctx = await OpenAsync(repo);
        var rig = NewRig(ctx);
        rig.RemoteDialogs.AddAnswer = new AddRemoteResult("origin", "https://example.com/repo.git", Fetch: false);

        await rig.Vm.Remotes.AddRemoteCommand.ExecuteAsync(null);

        Assert.Single(rig.RemoteDialogs.AddCalls);
        Assert.Contains("origin", repo.Git("remote"));
        Assert.Equal("Remotes (1)", rig.Vm.Remotes.Title);
        Assert.Contains("origin", Texts(rig.Vm.Remotes));
    }

    // ── labels ───────────────────────────────────────────────────────────────

    [Fact]
    public void Row_labels_follow_the_displayed_form()
    {
        Assert.Equal("3. On main: wip", RefNavigatorViewModel.StashLabel(Stash(2, "On main: wip", '1')));
        Assert.Equal("core", RefNavigatorViewModel.SubmoduleFolderName("libs/core/"));
        Assert.Equal("core", RefNavigatorViewModel.SubmoduleFolderName("core"));

        var sm = new SubmoduleInfo { Name = "libs/core", Path = "libs/core", Url = "u", Status = SubmoduleStatus.Modified };
        Assert.Equal("core  (Modified)", RefNavigatorViewModel.SubmoduleRowLabel(sm, busy: false));
        Assert.Equal("core  — Initializing…", RefNavigatorViewModel.SubmoduleRowLabel(sm, busy: true));
    }

    // ── fakes ────────────────────────────────────────────────────────────────

    private sealed class RecordingStashService : IStashService
    {
        private int _listCount;

        public IReadOnlyList<StashEntry> Entries { get; set; } = [];
        public int ListCount => Volatile.Read(ref _listCount);
        public List<GitObjectId> Dropped { get; } = [];
        public List<GitObjectId> Applied { get; } = [];
        public List<(GitObjectId Id, string Name)> Branched { get; } = [];

        public Task<IReadOnlyList<StashEntry>> ListAsync(RepositoryContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _listCount);
            return Task.FromResult(Entries);
        }

        public Task DropAsync(RepositoryContext context, GitObjectId commitId, CancellationToken ct = default)
        {
            Dropped.Add(commitId);
            return Task.CompletedTask;
        }

        public Task ApplyAsync(RepositoryContext context, GitObjectId commitId, bool restoreIndex, CancellationToken ct = default)
        {
            Applied.Add(commitId);
            return Task.CompletedTask;
        }

        public Task BranchFromAsync(RepositoryContext context, GitObjectId commitId, string branchName, CancellationToken ct = default)
        {
            Branched.Add((commitId, branchName));
            return Task.CompletedTask;
        }

        public Task<bool> SaveAsync(RepositoryContext context, StashSaveOptions options, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> StashStagedAsync(RepositoryContext context, string? message, CancellationToken ct = default) => Task.FromResult(true);
        public Task ApplyAndDropAsync(RepositoryContext context, GitObjectId commitId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(RepositoryContext context, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubSubmoduleService : ISubmoduleService
    {
        public IReadOnlyList<SubmoduleInfo> Entries { get; set; } = [];
        public Task? InitGate { get; set; }
        public List<string> Removed { get; } = [];
        public List<string?> Initialized { get; } = [];

        public Task<IReadOnlyList<SubmoduleInfo>> ListAsync(RepositoryContext context, CancellationToken ct = default) => Task.FromResult(Entries);

        public async Task InitAndUpdateAsync(RepositoryContext context, string? path, bool recursive, CancellationToken ct = default)
        {
            if (InitGate is { } gate)
                await gate;
            Initialized.Add(path);
        }

        public Task RemoveAsync(RepositoryContext context, SubmoduleInfo submodule, CancellationToken ct = default)
        {
            Removed.Add(submodule.Path);
            return Task.CompletedTask;
        }

        public Task AddAsync(RepositoryContext context, string url, string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task SyncAsync(RepositoryContext context, string? path, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubWorktreeService : IWorktreeService
    {
        public IReadOnlyList<WorktreeInfo> Entries { get; set; } = [];
        public List<string> Removed { get; } = [];
        public List<string> Unlocked { get; } = [];

        public Task<IReadOnlyList<WorktreeInfo>> ListAsync(RepositoryContext context, CancellationToken ct = default) => Task.FromResult(Entries);

        public Task RemoveAsync(RepositoryContext context, string path, bool force, CancellationToken ct = default)
        {
            Removed.Add(path);
            return Task.CompletedTask;
        }

        public Task UnlockAsync(RepositoryContext context, string path, CancellationToken ct = default)
        {
            Unlocked.Add(path);
            return Task.CompletedTask;
        }

        public Task AddAsync(RepositoryContext context, WorktreeAddRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task LockAsync(RepositoryContext context, string path, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveAsync(RepositoryContext context, string path, string newPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task PruneAsync(RepositoryContext context, CancellationToken ct = default) => Task.CompletedTask;
        public Task RepairAsync(RepositoryContext context, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubRepoStateCache : IRepoStateCache
    {
        private readonly ConcurrentQueue<(IReadOnlyList<StashEntry>? Stashes, IReadOnlyList<SubmoduleInfo>? Submodules, IReadOnlyList<WorktreeInfo>? Worktrees)> _saved = new();
        private ManualResetEventSlim? _loadGate;
        private ManualResetEventSlim? _saveGate;
        private int _loads;
        private int _fullLoads;
        private int _gateTimedOut;

        public ConcurrentDictionary<string, RepoStateSnapshot> Snapshots { get; } = new(StringComparer.Ordinal);
        public ManualResetEventSlim LoadEntered { get; } = new();
        public ManualResetEventSlim SaveEntered { get; } = new();

        public IReadOnlyList<(IReadOnlyList<StashEntry>? Stashes, IReadOnlyList<SubmoduleInfo>? Submodules, IReadOnlyList<WorktreeInfo>? Worktrees)> Saved => [.. _saved];

        public int Loads => Volatile.Read(ref _loads);

        /// <summary>True when a blocked read or save waited out its safety timeout, meaning the caller was waiting for it.</summary>
        public bool GateTimedOut => Volatile.Read(ref _gateTimedOut) != 0;

        /// <summary>Makes every read wait until the returned gate is set.</summary>
        public ManualResetEventSlim BlockLoads() => _loadGate = new ManualResetEventSlim();

        /// <summary>Makes every save wait until the returned gate is set.</summary>
        public ManualResetEventSlim BlockSaves() => _saveGate = new ManualResetEventSlim();

        /// <summary>How many times the whole snapshot (refs included) was read; the sidebar only needs the power tools.</summary>
        public int FullLoads => Volatile.Read(ref _fullLoads);

        public RepoStateSnapshot? TryLoad(string repoPath)
        {
            Interlocked.Increment(ref _fullLoads);
            return Snapshots.TryGetValue(repoPath, out var snapshot) ? snapshot : null;
        }

        public RepoStateSnapshot? TryLoadPowerTools(string repoPath)
        {
            Interlocked.Increment(ref _loads);
            LoadEntered.Set();
            if (_loadGate is { } gate && !gate.Wait(TimeSpan.FromSeconds(10)))
                Volatile.Write(ref _gateTimedOut, 1);
            return Snapshots.TryGetValue(repoPath, out var snapshot) ? snapshot : null;
        }

        public void SaveRefs(string repoPath, IReadOnlyList<BranchInfo> branches, IReadOnlyList<TagInfo> tags, IReadOnlyList<RemoteInfo> remotes) { }

        public void SavePowerTools(string repoPath, IReadOnlyList<StashEntry>? stashes, IReadOnlyList<SubmoduleInfo>? submodules, IReadOnlyList<WorktreeInfo>? worktrees)
        {
            SaveEntered.Set();
            if (_saveGate is { } gate && !gate.Wait(TimeSpan.FromSeconds(10)))
                Volatile.Write(ref _gateTimedOut, 1);
            _saved.Enqueue((stashes, submodules, worktrees));
        }

        public int WipeAll() => 0;
    }

    private sealed class FakeWorkspace : IWorkspaceService
    {
        private EventHandler<FocusedRepositoryChangedEventArgs>? _focusedChanged;
        private List<RepositoryContext> _open = [];

        public int Subscribers => _focusedChanged?.GetInvocationList().Length ?? 0;

        public RepositoryContext? Focused { get; private set; }
        public IReadOnlyList<RepositoryContext> OpenRepositories => _open;

        public event EventHandler<FocusedRepositoryChangedEventArgs>? FocusedRepositoryChanged
        {
            add => _focusedChanged += value;
            remove => _focusedChanged -= value;
        }

#pragma warning disable CS0067
        public event EventHandler<OpenRepositoriesChangedEventArgs>? OpenRepositoriesChanged;
#pragma warning restore CS0067

        /// <summary>Sets what is open, without raising anything.</summary>
        public void Open(params RepositoryContext[] open) => _open = [.. open];

        /// <summary>Sets the focus before anything listens. Everything focused is also open.</summary>
        public void Set(RepositoryContext? ctx)
        {
            Focused = ctx;
            _open = ctx is null ? [] : [ctx];
        }

        public void ChangeFocus(RepositoryContext? ctx)
        {
            var old = Focused;
            Focused = ctx;
            if (ctx is not null && !_open.Contains(ctx))
                _open = [.. _open, ctx];
            _focusedChanged?.Invoke(this, new FocusedRepositoryChangedEventArgs(old, ctx));
        }

        public void Focus(RepositoryContext context) => ChangeFocus(context);
        public Task<RepositoryContext> OpenAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RepositoryContext> InitAsync(string path, string? initialBranch, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RepositoryContext> CloneAsync(string url, string targetDirectory, Historia.Services.Workspace.CloneOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RepositoryContext> OpenSubmoduleAsync(RepositoryContext owner, SubmoduleInfo submodule, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CloseAsync(RepositoryContext context) => Task.CompletedTask;
        public void EnsureInitialized(RepositoryContext context) { }
        public void Move(RepositoryContext context, int newIndex) { }
        public Task RestoreSessionAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
