namespace Historia.Tests;

/// <summary>Linked worktrees share their repository's refs, so they walk the same tips and share
/// one on-disk store instead of writing an identical copy each.</summary>
[Trait("tier", "git")]
public sealed class GraphWorktreeStoreTests : IAsyncDisposable
{
    private const int CommitCount = 40;

    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _main;
    private readonly string _scratch = TestScratch.NewDirectory("historia-worktree-");
    private readonly string _worktree;

    public GraphWorktreeStoreTests()
    {
        _main = TestRepositoryBuilder.FromTemplate("linear-40", b => b.LinearHistory(CommitCount));
        _worktree = Path.Combine(_scratch, "wt");
        _main.Git("worktree", "add", "--quiet", "-b", "side", _worktree);
    }

    public async ValueTask DisposeAsync()
    {
        _main.Dispose();
        await _host.DisposeAsync();
        TestScratch.Delete(_scratch);
    }

    [Fact]
    public void A_linked_worktree_names_its_main_worktree_as_its_store()
    {
        Assert.Equal(GraphCachePaths.Stem(_main.Path), GraphCachePaths.Stem(_worktree));
    }

    [Fact]
    public void A_bare_repositorys_worktree_names_the_bare_repository_as_its_store()
    {
        var bare = Path.Combine(_scratch, "bare.git");
        TestGit.RunChecked(_scratch, ["clone", "--quiet", "--bare", _main.Path, bare]);
        var bareWorktree = Path.Combine(_scratch, "bare-wt");
        TestGit.RunChecked(bare, ["worktree", "add", "--quiet", "-b", "bare-side", bareWorktree]);

        Assert.Equal(GraphCachePaths.Stem(bare), GraphCachePaths.Stem(bareWorktree));
    }

    [Fact]
    public void A_checkout_whose_git_file_has_no_commondir_keeps_its_own_store()
    {
        // A submodule's checkout: a .git file pointing at a git dir with no commondir.
        var sub = Path.Combine(_scratch, "sub");
        var subGitDir = Path.Combine(_scratch, "modules", "sub");
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(subGitDir);
        File.WriteAllText(Path.Combine(sub, ".git"), "gitdir: " + subGitDir + "\n");

        Assert.Equal(GraphCachePaths.StoreRoot(sub), sub);
        Assert.NotEqual(GraphCachePaths.Stem(_main.Path), GraphCachePaths.Stem(sub));
    }

    [Fact]
    public async Task Opening_a_worktree_reuses_the_main_worktrees_store()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        var query = new GraphQuery { RefScope = GraphRefScope.AllRefs };

        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        await using (var main = _host.Open(_main.Path))
        {
            var first = await svc.ComputeLayoutAsync(main, query);
            Assert.Equal(CommitCount, first.Layout.RowCount);
            svc.Forget(main);
        }
        Assert.True(GraphMetaStore.ProbeExisting(_main.Path, includeRemoteBranches: true) is { Ok: true, Exhausted: true });
        var hdr = GraphCachePaths.File(_main.Path, GraphMetaStore.HdrExt);
        var writtenAt = File.GetLastWriteTimeUtc(hdr);

        await using var wt = _host.Open(_worktree);
        var fromWorktree = await svc.ComputeLayoutAsync(wt, query);

        Assert.Equal(CommitCount, fromWorktree.Layout.RowCount);
        Assert.IsType<DiskCommitStore>(fromWorktree.Layout.Commits);
        Assert.Single(Directory.GetFiles(AppPaths.CacheDirectory, "graph-*.meta.hdr"));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(hdr)); // served, not re-spilled
        svc.Forget(wt);
    }

    [Fact]
    public async Task A_sibling_of_the_focused_worktree_is_not_queued_for_headless_caching()
    {
        var gate = new FocusGate();
        var writer = new GraphBackgroundWriter(new RecordingGitCliService(), gate);
        await using var main = _host.Open(_main.Path);
        await using var wt = _host.Open(_worktree);
        gate.Focused = main;

        writer.EnqueueInactiveTab(wt, _ => Task.CompletedTask);

        Assert.False(writer.HasInactiveJob(wt));
        Assert.False(writer.HasInactiveJob(main));
    }

    [Fact]
    public async Task A_queued_sibling_job_holds_off_the_other_worktree_and_is_cancelled_by_it()
    {
        var gate = new FocusGate(); // closed: the job stays queued
        var writer = new GraphBackgroundWriter(new RecordingGitCliService(), gate);
        await using var main = _host.Open(_main.Path);
        await using var wt = _host.Open(_worktree);

        var ran = false;
        writer.EnqueueInactiveTab(wt, _ => { ran = true; return Task.CompletedTask; });

        Assert.True(writer.HasInactiveJob(main));
        await writer.CancelInactiveTabAsync(main).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(writer.HasInactiveJob(wt));
        gate.Open();
        await Task.Delay(100);
        Assert.False(ran);
    }

    private sealed class FocusGate : IBackgroundWorkGate
    {
        private readonly TaskCompletionSource _tcs = new();
        public bool IsOpen => _tcs.Task.IsCompleted;
        public RepositoryContext? Focused { get; set; }
        public Task WaitUntilOpenAsync(CancellationToken ct = default) => _tcs.Task.WaitAsync(ct);
        public void ReportGraphSettled(RepositoryContext context) { }
        public void Open() => _tcs.TrySetResult();
    }
}
