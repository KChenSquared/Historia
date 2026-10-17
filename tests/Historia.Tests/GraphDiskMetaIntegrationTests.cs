namespace Historia.Tests;

/// <summary>with "Read commit details from disk" on, the layout service spills per-commit
/// metadata and points <see cref="GraphLayout.Commits"/> at the paged <c>DiskCommitStore</c>,
/// while the rows still read back correctly across a re-layout.</summary>
[Trait("tier", "git")]
public sealed class GraphDiskMetaIntegrationTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    private const int CommitCount = 40;

    public GraphDiskMetaIntegrationTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("linear-40", b => b.LinearHistory(CommitCount));
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    private async Task<GraphLayoutService> DiskBackedServiceAsync()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        return new GraphLayoutService(_host.Settings, _host.Locator);
    }

    [Fact]
    public async Task Compute_spills_metadata_and_serves_it_from_disk()
    {
        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);

        var result = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });

        Assert.IsType<DiskCommitStore>(result.Layout.Commits);
        Assert.Equal(CommitCount, result.Layout.RowCount);
        Assert.Equal($"commit {CommitCount - 1:D2}", result.Layout.Commits[0].MessageShort);
        Assert.Equal("commit 00", result.Layout.Commits[CommitCount - 1].MessageShort);

        Assert.True(File.Exists(GraphCachePaths.File(_repo.Path, GraphMetaStore.HdrExt)));
        svc.Forget(ctx);
    }

    [Fact]
    public async Task Background_spill_caches_the_whole_history_without_scrolling()
    {
        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);
        ctx.MarkFirstGraphPaintDone(); ctx.MarkFirstFullLoadDone(); // no graph control / workspace here — stand in for the load signals

        // Small visible window — the rest must reach disk on its own.
        await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs }, maxRows: 5);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        (bool Ok, int RowCount, bool Exhausted) probe = default;
        while (DateTime.UtcNow < deadline)
        {
            probe = GraphMetaStore.Probe(_repo.Path, FingerprintOf(ctx), includeRemoteBranches: true);
            if (probe is { Ok: true, Exhausted: true })
                break;
            await Task.Delay(100);
        }

        Assert.True(probe.Ok, "meta store header should exist");
        Assert.True(probe.Exhausted, "background spill should have walked the whole history");
        Assert.Equal(CommitCount, probe.RowCount);
    }

    // The service computes the fingerprint privately; re-derive it the same way the store keys on
    // by opening any store file — simplest is to read it back from the header the spill just wrote.
    private static long FingerprintOf(RepositoryContext ctx)
    {
        var hdr = GraphCachePaths.File(ctx.RepositoryPath, GraphMetaStore.HdrExt);
        if (!File.Exists(hdr))
            return 0;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(hdr));
        return doc.RootElement.GetProperty("Fingerprint").GetInt64();
    }

    [Fact]
    public async Task Relaunch_over_a_torn_store_rewalks_the_full_history()
    {
        var svc = await DiskBackedServiceAsync();
        await using (var ctx = _host.Open(_repo.Path))
        {
            var first = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });
            Assert.Equal(CommitCount, first.Layout.RowCount);
            Assert.True(GraphMetaStore.ProbeExisting(_repo.Path, includeRemoteBranches: true) is { Ok: true, Exhausted: true });
            svc.Forget(ctx);
        }

        // A crash kept the header but lost the tail of the index.
        using (var idx = new FileStream(GraphCachePaths.File(_repo.Path, GraphMetaStore.IdxExt), FileMode.Open, FileAccess.Write))
            idx.SetLength((CommitCount - 10) * 8);

        var relaunched = new GraphLayoutService(_host.Settings, _host.Locator);
        await using var ctx2 = _host.Open(_repo.Path);
        var result = await relaunched.ComputeLayoutAsync(ctx2, new GraphQuery { RefScope = GraphRefScope.AllRefs });
        Assert.Equal(CommitCount, result.Layout.RowCount);
        Assert.Equal("commit 00", result.Layout.Commits[CommitCount - 1].MessageShort);
        relaunched.Forget(ctx2);
    }

    [Fact]
    public async Task Search_over_the_disk_store_finds_message_and_sha()
    {
        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);
        var result = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });

        var store = result.Layout.Commits;
        Assert.Equal(CommitCount, store.Search("commit").Count);
        Assert.Single(store.Search("commit 07"));
        var sha = store[3].Id.ToString();
        Assert.Equal(new[] { 3 }, store.Search(sha));
        Assert.Equal(new[] { 3 }, store.Search(sha[..10]));
    }

    [Fact]
    public async Task TryOptimisticSnapshot_paints_from_the_meta_store_on_a_fresh_session()
    {
        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();

        var full = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });
        Assert.Equal(CommitCount, full.Layout.RowCount);

        // A fresh GraphLayoutService instance has no in-memory RepoCache for ctx at all — same
        // shape as a new launch reopening the same path. The optimistic path must seed itself
        // from the .meta store the compute above just spilled, not require a live walk.
        var cold = new GraphLayoutService(_host.Settings, _host.Locator);
        var snapshot = cold.TryOptimisticSnapshot(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs }, maxRows: 5);

        Assert.NotNull(snapshot);
        Assert.Equal(5, snapshot!.Layout.RowCount);
        Assert.Equal($"commit {CommitCount - 1:D2}", snapshot.Layout.Commits[0].MessageShort);
    }

    /// <summary>The disk cache is keyed on the sorted tip SET, not on every ref +
    /// HEAD. Checking out between two branches that both already existed (and so were both
    /// already part of the walk's tip set) moves HEAD but changes neither the ref list nor the
    /// walk key — the on-disk `.meta.*` files must not be rewritten.</summary>
    [Fact]
    public async Task Checkout_between_existing_branches_does_not_rewrite_the_disk_cache()
    {
        _repo.Checkout("feature", create: true);
        _repo.WriteFile("f.txt", "feature work\n").Stage();
        _repo.Commit("feature commit");
        _repo.Checkout("main");

        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);
        ctx.MarkFirstGraphPaintDone(); ctx.MarkFirstFullLoadDone();

        await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });

        var datPath = GraphCachePaths.File(_repo.Path, GraphMetaStore.DatExt);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && !File.Exists(datPath))
            await Task.Delay(50);
        Assert.True(File.Exists(datPath));

        // Let the background spill settle (its own header write) before taking the baseline.
        await Task.Delay(500);
        var before = File.GetLastWriteTimeUtc(datPath);
        var beforeBytes = await File.ReadAllBytesAsync(datPath);

        svc.Forget(ctx);
        await using var ctx2 = _host.Open(_repo.Path);
        _repo.Checkout("feature");
        await svc.ComputeLayoutAsync(ctx2, new GraphQuery { RefScope = GraphRefScope.AllRefs });
        await Task.Delay(500);

        var after = File.GetLastWriteTimeUtc(datPath);
        var afterBytes = await File.ReadAllBytesAsync(datPath);
        Assert.Equal(before, after);
        Assert.Equal(beforeBytes, afterBytes);
    }

    [Fact]
    public async Task Falls_back_to_in_memory_when_the_setting_is_off()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        await using var ctx = _host.Open(_repo.Path);

        var result = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });

        Assert.IsType<ArrayCommitStore>(result.Layout.Commits);
        Assert.False(File.Exists(GraphCachePaths.File(_repo.Path, GraphMetaStore.HdrExt)));
    }
}
