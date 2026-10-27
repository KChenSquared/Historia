namespace Historia.Tests;

/// <summary>the additive OpenDiff contract on the state service.</summary>
[Trait("tier", "git")]
public class RepositoryStateServiceTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    public RepositoryStateServiceTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("repo-state-seed", b =>
        {
            b.WriteFile("a.txt", "one\n").Stage();
            b.Commit("init");
        });
    }

    private IRepositoryStateService NewState() => _host.Open(_repo.Path).State;

    private static DiffRequest Req(string path) =>
        new() { Source = DiffSource.UnstagedChanges, Path = path, ContextLines = 3 };

    [Fact]
    public void SetOpenDiff_raises_and_updates_current()
    {
        var state = NewState();
        OpenDiffChangedEventArgs? seen = null;
        state.OpenDiffChanged += (_, e) => seen = e;

        var r = Req("a.txt");
        state.SetOpenDiff(r);

        Assert.Equal(r, state.OpenDiff);
        Assert.NotNull(seen);
        Assert.Equal(r, seen!.Request);
    }

    [Fact]
    public void SetOpenDiff_is_idempotent_for_an_equal_request()
    {
        var state = NewState();
        state.SetOpenDiff(Req("a.txt"));

        var raises = 0;
        state.OpenDiffChanged += (_, _) => raises++;
        state.SetOpenDiff(Req("a.txt")); // value-equal record

        Assert.Equal(0, raises);
    }

    [Fact]
    public void SetOpenDiff_raises_again_for_an_equal_request_with_a_focus_line()
    {
        var state = NewState();
        var focused = Req("a.txt") with { FocusLine = 12 };
        state.SetOpenDiff(focused);

        var seen = new List<DiffRequest?>();
        state.OpenDiffChanged += (_, e) => seen.Add(e.Request);
        state.SetOpenDiff(Req("a.txt") with { FocusLine = 12 });

        Assert.Equal([focused], seen);
        Assert.Equal(focused, state.OpenDiff);
    }

    [Fact]
    public void RequestRevealCommit_raises_RevealCommitRequested()
    {
        var state = NewState();
        RevealCommitEventArgs? seen = null;
        state.RevealCommitRequested += (_, e) => seen = e;

        var id = new GitObjectId("0123456789012345678901234567890123456789");
        state.RequestRevealCommit(id);

        Assert.NotNull(seen);
        Assert.Equal(id, seen!.CommitId);
    }

    [Fact]
    public void SetSelectedCommit_clears_OpenDiff_and_raises_null()
    {
        var state = NewState();
        state.SetOpenDiff(Req("a.txt"));

        OpenDiffChangedEventArgs? seen = null;
        state.OpenDiffChanged += (_, e) => seen = e;

        state.SetSelectedCommit(new GitObjectId("0123456789abcdef0123456789abcdef01234567"));

        Assert.Null(state.OpenDiff);
        Assert.NotNull(seen);
        Assert.Null(seen!.Request);
    }

    [Fact]
    public void SetSelectedCommit_does_not_raise_OpenDiff_when_none_is_open()
    {
        var state = NewState();
        var raises = 0;
        state.OpenDiffChanged += (_, _) => raises++;

        state.SetSelectedCommit(new GitObjectId("0123456789abcdef0123456789abcdef01234567"));

        Assert.Equal(0, raises);
    }

    [Fact]
    public void A_status_refresh_style_reselect_of_null_does_not_disturb_an_open_diff()
    {
        // SetSelectedCommit(null) when nothing is selected is a no-op and must not clear a diff.
        var state = NewState();
        state.SetOpenDiff(Req("a.txt"));
        state.SetSelectedCommit(null);
        Assert.NotNull(state.OpenDiff);
    }

    [Fact]
    public async Task RefreshAllAsync_saves_the_refreshed_refs_to_the_repo_state_cache()
    {
        await using var ctx = _host.Open(_repo.Path);
        var cache = new RecordingRepoStateCache();
        var state = new RepositoryStateService(ctx, ui: null, repoCache: cache);

        await state.RefreshAllAsync();

        Assert.Single(cache.SavedRefs);
        var (path, branches, _, _) = cache.SavedRefs[0];
        Assert.Equal(ctx.RepositoryPath, path);
        Assert.Contains(branches, b => b.IsCurrent);
    }

    [Fact]
    public async Task Conflict_session_active_while_conflicted()
    {
        await using var ctx = _host.Open(_repo.Path);
        var gateway = new FakeGateway();
        var state = new RepositoryStateService(ctx, ui: null, repoCache: null, gateway: gateway);

        gateway.StatusToReturn = Historia.Models.RepositoryStatus.Empty with
        {
            Conflicted = [new FileStatusEntry { Path = "f.txt", Kind = FileChangeKind.Conflicted, IsBinary = false, IsSubmodule = false }],
        };

        await state.RefreshStatusAsync();

        Assert.True(state.IsConflictSessionActive);
    }

    [Fact]
    public async Task Keep_open_keeps_session_active_after_last_file_resolved()
    {
        await using var ctx = _host.Open(_repo.Path);
        var gateway = new FakeGateway();
        var state = new RepositoryStateService(ctx, ui: null, repoCache: null, gateway: gateway);

        var mergeHead = Path.Combine(ctx.GitDirectory, "MERGE_HEAD");
        await File.WriteAllTextAsync(mergeHead, "1234567890123456789012345678901234567890\n");

        try
        {
            gateway.StatusToReturn = Historia.Models.RepositoryStatus.Empty with
            {
                Conflicted = [new FileStatusEntry { Path = "f.txt", Kind = FileChangeKind.Conflicted, IsBinary = false, IsSubmodule = false }],
            };

            await state.RefreshStatusAsync();
            Assert.True(state.KeepConflictResolverOpen);
            Assert.True(state.IsConflictSessionActive);

            // Last file resolved
            gateway.StatusToReturn = Historia.Models.RepositoryStatus.Empty with
            {
                Conflicted = [],
            };

            await state.RefreshStatusAsync();

            Assert.Empty(state.Status.Conflicted);
            Assert.True(state.KeepConflictResolverOpen);
            Assert.True(state.IsConflictSessionActive);
        }
        finally
        {
            if (File.Exists(mergeHead)) File.Delete(mergeHead);
        }
    }

    [Fact]
    public async Task Session_ends_and_raises_changed_when_operation_ends()
    {
        await using var ctx = _host.Open(_repo.Path);
        var gateway = new FakeGateway();
        var state = new RepositoryStateService(ctx, ui: null, repoCache: null, gateway: gateway);

        var mergeHead = Path.Combine(ctx.GitDirectory, "MERGE_HEAD");
        await File.WriteAllTextAsync(mergeHead, "1234567890123456789012345678901234567890\n");

        try
        {
            gateway.StatusToReturn = Historia.Models.RepositoryStatus.Empty with
            {
                Conflicted = [new FileStatusEntry { Path = "f.txt", Kind = FileChangeKind.Conflicted, IsBinary = false, IsSubmodule = false }],
            };

            await state.RefreshStatusAsync();
            Assert.True(state.IsConflictSessionActive);

            // Operation completes and merge head is removed
            File.Delete(mergeHead);
            gateway.StatusToReturn = Historia.Models.RepositoryStatus.Empty with
            {
                Conflicted = [],
            };

            bool changedRaised = false;
            state.ConflictSessionChanged += (_, _) => changedRaised = true;

            await state.RefreshStatusAsync();

            Assert.True(changedRaised);
            Assert.False(state.KeepConflictResolverOpen);
            Assert.False(state.IsConflictSessionActive);
        }
        finally
        {
            if (File.Exists(mergeHead)) File.Delete(mergeHead);
        }
    }

    [Fact]
    public void SetSelectedConflictPath_raises_once_per_change_and_not_for_same_value()
    {
        var state = NewState();
        int raises = 0;
        string? lastPath = null;
        state.SelectedConflictPathChanged += (_, e) =>
        {
            raises++;
            lastPath = e.Path;
        };

        state.SetSelectedConflictPath("f1.txt");
        Assert.Equal(1, raises);
        Assert.Equal("f1.txt", lastPath);
        Assert.Equal("f1.txt", state.SelectedConflictPath);

        // Same value does not raise
        state.SetSelectedConflictPath("f1.txt");
        Assert.Equal(1, raises);

        // Change value raises
        state.SetSelectedConflictPath("f2.txt");
        Assert.Equal(2, raises);
        Assert.Equal("f2.txt", lastPath);
        Assert.Equal("f2.txt", state.SelectedConflictPath);

        // Setting null raises
        state.SetSelectedConflictPath(null);
        Assert.Equal(3, raises);
        Assert.Null(lastPath);
        Assert.Null(state.SelectedConflictPath);
    }

    [Fact]
    public async Task SelectedConflictPath_clears_when_conflict_session_ends_via_status()
    {
        await using var ctx = _host.Open(_repo.Path);
        var gateway = new FakeGateway();
        var state = new RepositoryStateService(ctx, ui: null, repoCache: null, gateway: gateway);

        var mergeHead = Path.Combine(ctx.GitDirectory, "MERGE_HEAD");
        await File.WriteAllTextAsync(mergeHead, "1234567890123456789012345678901234567890\n");

        try
        {
            gateway.StatusToReturn = Historia.Models.RepositoryStatus.Empty with
            {
                Conflicted = [new FileStatusEntry { Path = "f.txt", Kind = FileChangeKind.Conflicted, IsBinary = false, IsSubmodule = false }],
            };

            await state.RefreshStatusAsync();
            Assert.True(state.IsConflictSessionActive);

            state.SetSelectedConflictPath("f.txt");
            Assert.Equal("f.txt", state.SelectedConflictPath);

            bool pathChangedRaised = false;
            string? clearedPath = "not-cleared";
            state.SelectedConflictPathChanged += (_, e) =>
            {
                pathChangedRaised = true;
                clearedPath = e.Path;
            };

            // Operation completes and merge head is removed
            File.Delete(mergeHead);
            gateway.StatusToReturn = Historia.Models.RepositoryStatus.Empty with
            {
                Conflicted = [],
            };

            await state.RefreshStatusAsync();

            Assert.False(state.IsConflictSessionActive);
            Assert.Null(state.SelectedConflictPath);
            Assert.True(pathChangedRaised);
            Assert.Null(clearedPath);
        }
        finally
        {
            if (File.Exists(mergeHead)) File.Delete(mergeHead);
        }
    }

    [Fact]
    public async Task SelectedConflictPath_clears_when_keep_open_is_released()
    {
        await using var ctx = _host.Open(_repo.Path);
        var gateway = new FakeGateway();
        var state = new RepositoryStateService(ctx, ui: null, repoCache: null, gateway: gateway);

        var mergeHead = Path.Combine(ctx.GitDirectory, "MERGE_HEAD");
        await File.WriteAllTextAsync(mergeHead, "1234567890123456789012345678901234567890\n");

        try
        {
            // In-progress merge, 0 conflicts left, keep-open was set
            gateway.StatusToReturn = Historia.Models.RepositoryStatus.Empty;
            await state.RefreshStatusAsync();

            state.SetKeepConflictResolverOpen(true);
            Assert.True(state.IsConflictSessionActive);

            state.SetSelectedConflictPath("f.txt");
            Assert.Equal("f.txt", state.SelectedConflictPath);

            bool pathChangedRaised = false;
            string? clearedPath = "not-cleared";
            state.SelectedConflictPathChanged += (_, e) =>
            {
                pathChangedRaised = true;
                clearedPath = e.Path;
            };

            state.SetKeepConflictResolverOpen(false);

            Assert.False(state.IsConflictSessionActive);
            Assert.Null(state.SelectedConflictPath);
            Assert.True(pathChangedRaised);
            Assert.Null(clearedPath);
        }
        finally
        {
            if (File.Exists(mergeHead)) File.Delete(mergeHead);
        }
    }

    [Fact]
    public void Closing_the_resolver_raises_once_and_is_idempotent()
    {
        var state = NewState();
        int raises = 0;
        state.ConflictResolverClosedChanged += (_, _) => raises++;

        Assert.False(state.IsConflictResolverClosed);

        state.SetConflictResolverClosed(true);
        Assert.True(state.IsConflictResolverClosed);
        Assert.Equal(1, raises);

        state.SetConflictResolverClosed(true);
        Assert.Equal(1, raises);

        state.SetConflictResolverClosed(false);
        Assert.False(state.IsConflictResolverClosed);
        Assert.Equal(2, raises);

        state.SetConflictResolverClosed(false);
        Assert.Equal(2, raises);
    }

    [Fact]
    public async Task Session_start_clears_closed_flag()
    {
        await using var ctx = _host.Open(_repo.Path);
        var gateway = new FakeGateway();
        var state = new RepositoryStateService(ctx, ui: null, repoCache: null, gateway: gateway);

        state.SetConflictResolverClosed(true);
        int raises = 0;
        state.ConflictResolverClosedChanged += (_, _) => raises++;

        gateway.StatusToReturn = Historia.Models.RepositoryStatus.Empty with
        {
            Conflicted = [new FileStatusEntry { Path = "f.txt", Kind = FileChangeKind.Conflicted, IsBinary = false, IsSubmodule = false }],
        };
        await state.RefreshStatusAsync();

        Assert.True(state.IsConflictSessionActive);
        Assert.False(state.IsConflictResolverClosed);
        Assert.Equal(1, raises);
    }

    [Fact]
    public async Task Session_end_clears_closed_flag()
    {
        await using var ctx = _host.Open(_repo.Path);
        var gateway = new FakeGateway();
        var state = new RepositoryStateService(ctx, ui: null, repoCache: null, gateway: gateway);

        var mergeHead = Path.Combine(ctx.GitDirectory, "MERGE_HEAD");
        await File.WriteAllTextAsync(mergeHead, "1234567890123456789012345678901234567890\n");

        try
        {
            gateway.StatusToReturn = Historia.Models.RepositoryStatus.Empty with
            {
                Conflicted = [new FileStatusEntry { Path = "f.txt", Kind = FileChangeKind.Conflicted, IsBinary = false, IsSubmodule = false }],
            };
            await state.RefreshStatusAsync();

            state.SetConflictResolverClosed(true);
            int raises = 0;
            state.ConflictResolverClosedChanged += (_, _) => raises++;

            File.Delete(mergeHead);
            gateway.StatusToReturn = Historia.Models.RepositoryStatus.Empty;
            await state.RefreshStatusAsync();

            Assert.False(state.IsConflictSessionActive);
            Assert.False(state.IsConflictResolverClosed);
            Assert.Equal(1, raises);
        }
        finally
        {
            if (File.Exists(mergeHead)) File.Delete(mergeHead);
        }
    }

    [Fact]
    public async Task Conflicts_on_the_next_rebase_step_clear_closed_flag()
    {
        await using var ctx = _host.Open(_repo.Path);
        var gateway = new FakeGateway();
        var state = new RepositoryStateService(ctx, ui: null, repoCache: null, gateway: gateway);

        var rebaseDir = Path.Combine(ctx.GitDirectory, "rebase-merge");
        Directory.CreateDirectory(rebaseDir);
        var conflicted = Historia.Models.RepositoryStatus.Empty with
        {
            Conflicted = [new FileStatusEntry { Path = "f.txt", Kind = FileChangeKind.Conflicted, IsBinary = false, IsSubmodule = false }],
        };

        try
        {
            gateway.StatusToReturn = conflicted;
            await state.RefreshStatusAsync();
            state.SetConflictResolverClosed(true);

            // Every file resolved: the session stays active for Continue, and the resolver stays closed.
            gateway.StatusToReturn = Historia.Models.RepositoryStatus.Empty;
            await state.RefreshStatusAsync();
            Assert.True(state.IsConflictSessionActive);
            Assert.True(state.IsConflictResolverClosed);

            int raises = 0;
            state.ConflictResolverClosedChanged += (_, _) => raises++;

            gateway.StatusToReturn = conflicted;
            await state.RefreshStatusAsync();

            Assert.True(state.IsConflictSessionActive);
            Assert.False(state.IsConflictResolverClosed);
            Assert.Equal(1, raises);
        }
        finally
        {
            Directory.Delete(rebaseDir, recursive: true);
        }
    }

    [Fact]
    public async Task Closed_flag_does_not_change_IsConflictSessionActive()
    {
        await using var ctx = _host.Open(_repo.Path);
        var gateway = new FakeGateway();
        var state = new RepositoryStateService(ctx, ui: null, repoCache: null, gateway: gateway);

        gateway.StatusToReturn = Historia.Models.RepositoryStatus.Empty with
        {
            Conflicted = [new FileStatusEntry { Path = "f.txt", Kind = FileChangeKind.Conflicted, IsBinary = false, IsSubmodule = false }],
        };
        await state.RefreshStatusAsync();
        Assert.True(state.IsConflictSessionActive);

        int sessionRaises = 0;
        state.ConflictSessionChanged += (_, _) => sessionRaises++;

        state.SetConflictResolverClosed(true);

        Assert.True(state.IsConflictSessionActive);
        Assert.Equal(0, sessionRaises);

        state.SetConflictResolverClosed(false);
        Assert.True(state.IsConflictSessionActive);
        Assert.Equal(0, sessionRaises);
    }

    [Fact]
    public async Task Conflict_session_start_clears_selected_commit()
    {
        await using var ctx = _host.Open(_repo.Path);
        var gateway = new FakeGateway();
        var state = new RepositoryStateService(ctx, ui: null, repoCache: null, gateway: gateway);

        var commitId = new GitObjectId("1234567890123456789012345678901234567890");
        state.SetSelectedCommit(commitId);
        Assert.Equal(commitId, state.SelectedCommitId);

        int commitChangedRaises = 0;
        GitObjectId? finalSelectedCommit = commitId;
        state.SelectedCommitChanged += (_, e) =>
        {
            commitChangedRaises++;
            finalSelectedCommit = e.CommitId;
        };

        var mergeHead = Path.Combine(ctx.GitDirectory, "MERGE_HEAD");
        await File.WriteAllTextAsync(mergeHead, "1234567890123456789012345678901234567890\n");

        try
        {
            gateway.StatusToReturn = Historia.Models.RepositoryStatus.Empty with
            {
                Conflicted = [new FileStatusEntry { Path = "f.txt", Kind = FileChangeKind.Conflicted, IsBinary = false, IsSubmodule = false }],
            };

            await state.RefreshStatusAsync();

            Assert.True(state.IsConflictSessionActive);
            Assert.Null(state.SelectedCommitId);
            Assert.Equal(1, commitChangedRaises);
            Assert.Null(finalSelectedCommit);
        }
        finally
        {
            if (File.Exists(mergeHead)) File.Delete(mergeHead);
        }
    }

    [Fact]
    public async Task Selecting_a_commit_during_the_session_is_not_undone()
    {
        await using var ctx = _host.Open(_repo.Path);
        var gateway = new FakeGateway();
        var state = new RepositoryStateService(ctx, ui: null, repoCache: null, gateway: gateway);

        var mergeHead = Path.Combine(ctx.GitDirectory, "MERGE_HEAD");
        await File.WriteAllTextAsync(mergeHead, "1234567890123456789012345678901234567890\n");

        try
        {
            gateway.StatusToReturn = Historia.Models.RepositoryStatus.Empty with
            {
                Conflicted = [new FileStatusEntry { Path = "f.txt", Kind = FileChangeKind.Conflicted, IsBinary = false, IsSubmodule = false }],
            };

            await state.RefreshStatusAsync();
            Assert.True(state.IsConflictSessionActive);
            Assert.Null(state.SelectedCommitId);

            // User selects a commit to browse during the active conflict session
            var browseCommitId = new GitObjectId("abcdefabcdefabcdefabcdefabcdefabcdefabcd");
            state.SetSelectedCommit(browseCommitId);
            Assert.Equal(browseCommitId, state.SelectedCommitId);

            // Later status refresh with session still active leaves selected commit alone
            await state.RefreshStatusAsync();
            Assert.True(state.IsConflictSessionActive);
            Assert.Equal(browseCommitId, state.SelectedCommitId);
        }
        finally
        {
            if (File.Exists(mergeHead)) File.Delete(mergeHead);
        }
    }

    [Fact]
    public async Task RefreshRefs_raises_RefsChanged_when_only_detached_head_moves()
    {
        var gateway = new FakeGateway();
        var headId1 = new GitObjectId("1111111111111111111111111111111111111111");
        var headId2 = new GitObjectId("2222222222222222222222222222222222222222");
        gateway.InfoToReturn = gateway.InfoToReturn with
        {
            IsHeadDetached = true,
            HeadCommitId = headId1,
        };

        var ctx = _host.Open(_repo.Path);
        var state = new RepositoryStateService(ctx, gateway: gateway);

        await state.RefreshRefsAsync();

        var raises = 0;
        state.RefsChanged += (_, _) => raises++;

        gateway.InfoToReturn = gateway.InfoToReturn with
        {
            HeadCommitId = headId2,
        };

        await state.RefreshRefsAsync();

        Assert.Equal(1, raises);
        Assert.Equal(headId2, state.Repository.HeadCommitId);
    }

    [Fact]
    public async Task RefreshRefs_updates_Repository_head_state()
    {
        var gateway = new FakeGateway();
        var headId = new GitObjectId("1111111111111111111111111111111111111111");
        gateway.InfoToReturn = gateway.InfoToReturn with
        {
            IsHeadDetached = true,
            HeadCommitId = headId,
        };

        var ctx = _host.Open(_repo.Path);
        var state = new RepositoryStateService(ctx, gateway: gateway);

        await state.RefreshRefsAsync();

        Assert.True(state.Repository.IsHeadDetached);
        Assert.Equal(headId, state.Repository.HeadCommitId);
    }

    [Fact]
    public async Task RefreshRefs_does_not_raise_when_nothing_changed()
    {
        var gateway = new FakeGateway();
        var headId = new GitObjectId("1111111111111111111111111111111111111111");
        gateway.InfoToReturn = gateway.InfoToReturn with
        {
            IsHeadDetached = true,
            HeadCommitId = headId,
        };

        var ctx = _host.Open(_repo.Path);
        var state = new RepositoryStateService(ctx, gateway: gateway);

        await state.RefreshRefsAsync();

        var raises = 0;
        state.RefsChanged += (_, _) => raises++;

        await state.RefreshRefsAsync();

        Assert.Equal(0, raises);
    }

    [Fact]
    public async Task RefreshRefs_raises_when_status_refresh_saw_head_move_first()
    {
        var gateway = new FakeGateway();
        var headId1 = new GitObjectId("1111111111111111111111111111111111111111");
        var headId2 = new GitObjectId("2222222222222222222222222222222222222222");
        gateway.InfoToReturn = gateway.InfoToReturn with
        {
            IsHeadDetached = true,
            HeadCommitId = headId1,
        };

        var ctx = _host.Open(_repo.Path);
        var state = new RepositoryStateService(ctx, gateway: gateway);

        await state.RefreshRefsAsync();

        var raises = 0;
        state.RefsChanged += (_, _) => raises++;

        gateway.InfoToReturn = gateway.InfoToReturn with
        {
            HeadCommitId = headId2,
        };

        await state.RefreshStatusAsync();
        await state.RefreshRefsAsync();

        Assert.Equal(1, raises);
        Assert.Equal(headId2, state.Repository.HeadCommitId);
    }

    // ── Status raises ──

    private static FileStatusEntry Entry(string path, long size = 0) => new()
    {
        Path = path,
        Kind = FileChangeKind.Modified,
        IsBinary = false,
        IsSubmodule = false,
        SizeBytes = size,
    };

    private static Historia.Models.RepositoryStatus StatusOf(params FileStatusEntry[] unstaged) =>
        Historia.Models.RepositoryStatus.Empty with
        {
            Unstaged = unstaged,
            IsDirty = unstaged.Length > 0,
            ComputedAt = DateTimeOffset.UtcNow,
        };

    private (RepositoryStateService State, FakeGateway Gateway, ScriptedUi Ui, ManualTime Time, List<(long At, Historia.Models.RepositoryStatus Status)> Raised) NewRaiseState(bool onUiThread)
    {
        var gateway = new FakeGateway();
        var time = new ManualTime();
        var ui = new ScriptedUi(time) { OnUiThread = onUiThread };
        var state = new RepositoryStateService(_host.Open(_repo.Path), ui, gateway: gateway, time: time);
        var raised = new List<(long At, Historia.Models.RepositoryStatus Status)>();
        state.StatusChanged += (_, e) => raised.Add((time.Ms, e.Status));
        return (state, gateway, ui, time, raised);
    }

    [Fact(Timeout = 10000)]
    public async Task An_unchanged_status_raises_once()
    {
        var (state, gateway, _, time, raised) = NewRaiseState(onUiThread: true);

        gateway.StatusToReturn = StatusOf(Entry("a.txt", 10));
        await state.RefreshStatusAsync();
        time.Advance(1000);
        // A new object equal in value, computed later.
        gateway.StatusToReturn = StatusOf(Entry("a.txt", 10));
        await state.RefreshStatusAsync();

        Assert.Single(raised);
    }

    [Fact(Timeout = 10000)]
    public async Task A_size_change_raises()
    {
        var (state, gateway, _, time, raised) = NewRaiseState(onUiThread: true);

        gateway.StatusToReturn = StatusOf(Entry("a.txt", 10));
        await state.RefreshStatusAsync();
        time.Advance(1000);
        gateway.StatusToReturn = StatusOf(Entry("a.txt", 11));
        await state.RefreshStatusAsync();

        Assert.Equal(2, raised.Count);
        Assert.Equal(11, raised[1].Status.Unstaged[0].SizeBytes);
    }

    [Fact(Timeout = 10000)]
    public async Task An_operation_state_change_raises_even_when_the_status_is_unchanged()
    {
        var (state, gateway, _, time, raised) = NewRaiseState(onUiThread: true);
        var mergeHead = Path.Combine(_repo.Path, ".git", "MERGE_HEAD");

        gateway.StatusToReturn = StatusOf(Entry("a.txt", 10));
        await state.RefreshStatusAsync();
        time.Advance(1000);

        await File.WriteAllTextAsync(mergeHead, "1234567890123456789012345678901234567890\n");
        try
        {
            gateway.StatusToReturn = StatusOf(Entry("a.txt", 10));
            await state.RefreshStatusAsync();
            time.Advance(1000);
        }
        finally
        {
            File.Delete(mergeHead);
        }

        gateway.StatusToReturn = StatusOf(Entry("a.txt", 10));
        await state.RefreshStatusAsync();

        Assert.Equal(3, raised.Count);
    }

    [Fact]
    public void Equivalence_compares_sizes_kinds_sections_and_counts()
    {
        var a = StatusOf(Entry("a.txt", 10));
        Assert.True(RepositoryStatusEquivalence.Equivalent(a, StatusOf(Entry("a.txt", 10))));
        Assert.False(RepositoryStatusEquivalence.Equivalent(a, StatusOf(Entry("a.txt", 11))));
        Assert.False(RepositoryStatusEquivalence.Equivalent(a, StatusOf(Entry("a.txt", 10) with { Kind = FileChangeKind.Deleted })));
        Assert.False(RepositoryStatusEquivalence.Equivalent(a, a with { Unstaged = [], Staged = [Entry("a.txt", 10)] }));
        Assert.False(RepositoryStatusEquivalence.Equivalent(a, a with { AheadBy = 1 }));
        Assert.False(RepositoryStatusEquivalence.Equivalent(a, a with { BehindBy = 1 }));
        Assert.False(RepositoryStatusEquivalence.Equivalent(a, a with { HiddenCaseCollisions = ["A.txt"] }));
    }

    [Fact]
    public void Equivalence_compares_the_branch()
    {
        var status = StatusOf();
        var info = new RepositoryInfo
        {
            WorkingDirectory = "w",
            GitDirectory = "g",
            DisplayName = "d",
            IsBare = false,
            IsHeadDetached = false,
            IsHeadUnborn = false,
            CurrentBranchName = "main",
        };

        Assert.True(RepositoryStatusEquivalence.Equivalent(status, info, status, info with { }));
        Assert.False(RepositoryStatusEquivalence.Equivalent(status, info, status, info with { CurrentBranchName = "feature" }));
    }

    [Fact(Timeout = 10000)]
    public async Task The_first_raise_after_idle_is_one_hop_without_the_timer()
    {
        var (state, gateway, ui, _, raised) = NewRaiseState(onUiThread: false);

        gateway.StatusToReturn = StatusOf(Entry("a.txt"));
        await state.RefreshStatusAsync();

        var post = Assert.Single(ui.Posted);
        Assert.Equal(UiPriority.Normal, post.Priority);
        Assert.Empty(raised);
        ui.DrainPosted();

        Assert.Single(raised);
        Assert.Equal(0, ui.Timer!.StartCount);
        Assert.Equal(UiPriority.Normal, ui.Timer.Priority);
    }

    [Fact(Timeout = 10000)]
    public async Task A_raise_on_the_ui_thread_posts_nothing()
    {
        var (state, gateway, ui, _, raised) = NewRaiseState(onUiThread: true);

        gateway.StatusToReturn = StatusOf(Entry("a.txt"));
        await state.RefreshStatusAsync();

        Assert.Empty(ui.Posted);
        Assert.Single(raised);
    }

    [Fact(Timeout = 10000)]
    public async Task A_status_within_the_window_of_a_raise_waits_for_the_trailing_timer()
    {
        var (state, gateway, ui, time, raised) = NewRaiseState(onUiThread: true);

        gateway.StatusToReturn = StatusOf(Entry("a.txt"));
        await state.RefreshStatusAsync();
        time.Advance(20);
        gateway.StatusToReturn = StatusOf(Entry("b.txt"));
        await state.RefreshStatusAsync();

        Assert.Single(raised);
        Assert.True(ui.Timer!.IsRunning);

        time.Advance(50);
        ui.PumpTimers();

        Assert.Equal(2, raised.Count);
        Assert.Equal("b.txt", raised[1].Status.Unstaged[0].Path);
    }

    [Fact(Timeout = 10000)]
    public async Task A_burst_never_leaves_a_status_waiting_over_150_ms()
    {
        var (state, gateway, ui, time, raised) = NewRaiseState(onUiThread: true);
        var arrivals = new List<long>();

        // One new status every 10 ms for 600 ms.
        for (int i = 0; i < 60; i++)
        {
            gateway.StatusToReturn = StatusOf(Entry($"f{i:D3}.txt"));
            arrivals.Add(time.Ms);
            await state.RefreshStatusAsync();
            time.Advance(10);
            ui.PumpTimers();
        }

        for (int i = 0; i < 20; i++)
        {
            time.Advance(10);
            ui.PumpTimers();
        }

        Assert.Equal("f059.txt", raised[^1].Status.Unstaged[0].Path);
        // Coalesced: far fewer raises than statuses, and more than the leading one alone.
        Assert.InRange(raised.Count, 4, 30);

        var firstUnraised = 0;
        foreach (var (at, status) in raised)
        {
            var index = int.Parse(status.Unstaged[0].Path.AsSpan(1, 3));
            var waited = at - arrivals[firstUnraised];
            Assert.True(waited <= 150, $"status {firstUnraised} waited {waited} ms");
            firstUnraised = index + 1;
        }
    }

    [Fact(Timeout = 10000)]
    public async Task Status_duration_is_recorded_after_each_run()
    {
        var (state, gateway, _, time, _) = NewRaiseState(onUiThread: true);
        Assert.Equal(TimeSpan.Zero, state.LastStatusDuration);
        Assert.Null(state.LastStatusCompletedAt);

        gateway.OnStatusRead = () => time.Advance(700);
        await state.RefreshStatusAsync();

        Assert.Equal(TimeSpan.FromMilliseconds(700), state.LastStatusDuration);
        Assert.NotNull(state.LastStatusCompletedAt);
    }

    [Fact(Timeout = 10000)]
    public async Task Unchanged_refs_do_not_save_the_ref_cache()
    {
        var gateway = new FakeGateway();
        var cache = new RecordingRepoStateCache();
        var state = new RepositoryStateService(_host.Open(_repo.Path), ui: null, repoCache: cache, gateway: gateway);
        var main = new BranchInfo { Name = "main", CanonicalName = "refs/heads/main", IsRemote = false, IsCurrent = true, TipCommitId = null };
        var tag = new TagInfo { Name = "v1", TargetCommitId = new GitObjectId("0123456789012345678901234567890123456789"), IsAnnotated = false };

        gateway.BranchesToReturn = [main];
        await state.RefreshAllAsync();
        Assert.Single(cache.SavedRefs);

        await state.RefreshAllAsync();
        Assert.Single(cache.SavedRefs);

        gateway.TagsToReturn = [tag];
        await state.RefreshAllAsync();
        Assert.Equal(2, cache.SavedRefs.Count);

        gateway.BranchesToReturn = [main, main with { Name = "dev", CanonicalName = "refs/heads/dev", IsCurrent = false }];
        await state.RefreshAllAsync();
        Assert.Equal(3, cache.SavedRefs.Count);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _repo.Dispose();
    }

    private sealed class FakeGateway : IGitGateway
    {
        public string RepositoryPath => "C:\\fake\\repo";

        public Historia.Models.RepositoryStatus StatusToReturn { get; set; } = Historia.Models.RepositoryStatus.Empty;
        public RepositoryInfo InfoToReturn { get; set; } = new()
        {
            WorkingDirectory = "C:\\fake\\repo",
            GitDirectory = "C:\\fake\\repo\\.git",
            DisplayName = "fake",
            IsBare = false,
            IsHeadDetached = false,
            IsHeadUnborn = false,
            CurrentBranchName = "main",
        };

        public IReadOnlyList<BranchInfo> BranchesToReturn { get; set; } = [];
        public IReadOnlyList<TagInfo> TagsToReturn { get; set; } = [];
        public Action? OnStatusRead { get; set; }

        public Task<Historia.Models.RepositoryStatus> GetStatusAsync(CancellationToken ct = default)
        {
            OnStatusRead?.Invoke();
            return Task.FromResult(StatusToReturn);
        }

        public Task<RepositoryInfo> GetRepositoryInfoAsync(CancellationToken ct = default) => Task.FromResult(InfoToReturn);
        public Task<IReadOnlyList<BranchInfo>> GetBranchesAsync(CancellationToken ct = default) => Task.FromResult(BranchesToReturn);
        public Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken ct = default) => Task.FromResult(TagsToReturn);
        public Task<IReadOnlyList<RemoteInfo>> GetRemotesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RemoteInfo>>([]);
        public Task<AheadBehind?> GetDivergenceAsync(string local, string remote, CancellationToken ct = default) => Task.FromResult<AheadBehind?>(null);
        public Task<CommitDetail> GetCommitDetailAsync(GitObjectId id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<FileStatusEntry>> GetCommitFilesAsync(GitObjectId id, int parentIndex, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string? Name, string? Email)> GetConfiguredIdentityAsync(CancellationToken ct = default) => Task.FromResult<(string?, string?)>((null, null));
        public Task<FileDiff> GetFileDiffAsync(DiffRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<(string Path, string? Patch)>> GetStagedPatchesAsync(bool againstHeadParent, long? maxPatchBytes = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string? Name, string? Email)> GetGlobalIdentityAsync(CancellationToken ct = default) => Task.FromResult<(string?, string?)>((null, null));
        public Task<bool> GetGpgSignConfiguredAsync(CancellationToken ct = default) => Task.FromResult(false);

        public Task StageAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DiscardAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitObjectId> CommitAsync(CommitRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task MergeAsync(string refish, bool noFastForward, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ResetAsync(string target, GitResetMode mode, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CountCommitsLeavingAsync(string target, CancellationToken ct = default) => throw new NotImplementedException();

        public Task CheckoutAsync(string refName, GitCheckoutOptions options, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BranchInfo> CreateBranchAsync(string name, string startPoint, bool checkout, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteBranchAsync(string name, bool force, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RenameBranchAsync(string oldName, string newName, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetUpstreamAsync(string branchName, string upstreamRef, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TagInfo> CreateTagAsync(string name, string target, string? message, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteTagAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();

        public Task AddRemoteAsync(string name, string url, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RemoveRemoteAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetRemoteUrlAsync(string name, string url, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> RenameRemoteAsync(string oldName, string newName, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetRemotePushUrlAsync(string name, string? pushUrl, CancellationToken ct = default) => throw new NotImplementedException();

        public Task FetchAsync(string remote, GitFetchOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
        public Task PullAsync(string remote, string branch, GitPullOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
        public Task PushAsync(string remote, PushSpec spec, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteRemoteBranchAsync(string remote, string branch, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
        public Task PruneRemoteAsync(string name, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
    }

    /// <summary>A clock the test advances by hand, in milliseconds.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ms;
        public long Ms => Interlocked.Read(ref _ms);
        public void Advance(long ms) => Interlocked.Add(ref _ms, ms);
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ms;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(Ms);
    }

    /// <summary>A dispatcher whose posts and timer the test drives; the timer is due on the manual clock.</summary>
    private sealed class ScriptedUi(ManualTime time) : IUiDispatcher
    {
        private readonly Queue<Action> _queue = new();

        public bool OnUiThread { get; init; }
        public List<(Action Action, UiPriority? Priority)> Posted { get; } = [];
        public ScriptedTimer? Timer { get; private set; }

        public bool CheckAccess() => OnUiThread;

        public void Post(Action action)
        {
            Posted.Add((action, null));
            _queue.Enqueue(action);
        }

        public void Post(Action action, UiPriority priority)
        {
            Posted.Add((action, priority));
            _queue.Enqueue(action);
        }

        public IUiTimer CreateTimer(TimeSpan interval, Action tick) => Timer = new ScriptedTimer(time, interval, tick, null);

        public IUiTimer CreateTimer(TimeSpan interval, Action tick, UiPriority priority) => Timer = new ScriptedTimer(time, interval, tick, priority);

        public void DrainPosted()
        {
            while (_queue.Count > 0)
                _queue.Dequeue()();
        }

        /// <summary>Fires the timer when it is running and its interval has passed on the manual clock.</summary>
        public void PumpTimers()
        {
            if (Timer is { IsRunning: true } t && time.Ms >= t.DueAt)
                t.Fire();
        }
    }

    private sealed class ScriptedTimer(ManualTime time, TimeSpan interval, Action tick, UiPriority? priority) : IUiTimer
    {
        public bool IsRunning { get; private set; }
        public int StartCount { get; private set; }
        public long DueAt { get; private set; }
        public UiPriority? Priority { get; } = priority;

        public void Start()
        {
            StartCount++;
            IsRunning = true;
            DueAt = time.Ms + (long)interval.TotalMilliseconds;
        }

        public void Stop() => IsRunning = false;
        public void Dispose() => IsRunning = false;
        public void Fire() => tick();
    }

    private sealed class RecordingRepoStateCache : IRepoStateCache
    {
        public List<(string Path, IReadOnlyList<BranchInfo> Branches, IReadOnlyList<TagInfo> Tags, IReadOnlyList<RemoteInfo> Remotes)> SavedRefs { get; } = [];

        public RepoStateSnapshot? TryLoad(string repoPath) => null;
        public void SaveRefs(string repoPath, IReadOnlyList<BranchInfo> branches, IReadOnlyList<TagInfo> tags, IReadOnlyList<RemoteInfo> remotes)
            => SavedRefs.Add((repoPath, branches, tags, remotes));
        public void SavePowerTools(string repoPath, IReadOnlyList<StashEntry>? stashes, IReadOnlyList<SubmoduleInfo>? submodules, IReadOnlyList<WorktreeInfo>? worktrees) { }
        public int WipeAll() => 0;
    }
}
