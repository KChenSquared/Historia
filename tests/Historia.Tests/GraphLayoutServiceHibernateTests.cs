namespace Historia.Tests;

/// <summary><see cref="IGraphLayoutService.Hibernate"/>
/// releases everything a tab's graph holds in memory on switch-away, but leaves any on-disk cache
/// in place so a re-focus is a normal, faster-than-cold reopen.</summary>
[Trait("tier", "git")]
public sealed class GraphLayoutServiceHibernateTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;
    private const int CommitCount = 20;

    public GraphLayoutServiceHibernateTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("linear-20", b => b.LinearHistory(CommitCount));
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    private static bool HasCacheEntry(GraphLayoutService svc, RepositoryContext ctx)
    {
        var cachesField = typeof(GraphLayoutService).GetField("_caches", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cwt = cachesField.GetValue(svc)!;
        var tryGetValue = cwt.GetType().GetMethod("TryGetValue")!;
        var args = new object?[] { ctx, null };
        return (bool)tryGetValue.Invoke(cwt, args)!;
    }

    [Fact]
    public async Task Hibernate_drops_the_repo_cache_entry()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        await using var ctx = _host.Open(_repo.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();

        await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });
        Assert.True(HasCacheEntry(svc, ctx));

        await svc.Hibernate(ctx);

        Assert.False(HasCacheEntry(svc, ctx));
    }

    private static object? WalkCacheReadEntry(GraphLayoutService svc, string repoPath)
    {
        var walkCache = typeof(GraphLayoutService).GetField("_walkCache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(svc)!;
        var readCache = walkCache.GetType().GetField("_readCache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(walkCache)!;
        var args = new object?[] { repoPath, null };
        readCache.GetType().GetMethod("TryGetValue")!.Invoke(readCache, args);
        return args[1];
    }

    /// <summary>Saves a walk file for the repository and reads it back through the service's own
    /// walk cache, leaving a read-cache entry for it.</summary>
    private static void PrimeWalkCacheReadEntry(GraphLayoutService svc, string repoPath, GraphQuery query)
    {
        var walkCache = (GraphWalkCache)typeof(GraphLayoutService).GetField("_walkCache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(svc)!;
        var id = GitObjectId.Parse(new string('a', 40));
        var commit = new RawCommit(id, [], new CommitMetadata
        {
            Id = id,
            MessageShort = "only",
            AuthorName = "A",
            AuthorEmail = "a@x",
            AuthorWhen = DateTimeOffset.UnixEpoch,
            CommitWhen = DateTimeOffset.UnixEpoch,
            ParentCount = 0,
        });
        walkCache.Save(repoPath, query, fingerprint: 7, exhausted: true, [commit], new Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>>());
        Assert.NotNull(walkCache.TryLoadStale(repoPath, query));
        Assert.NotNull(WalkCacheReadEntry(svc, repoPath));
    }

    [Fact]
    public async Task Forget_evicts_the_walk_cache_read_entry()
    {
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        await using var ctx = _host.Open(_repo.Path);
        // No compute first: a compute's background walk-cache save replaces the read entry, which
        // would race the primed one. Forget evicts whether or not the context has a cache.
        PrimeWalkCacheReadEntry(svc, ctx.RepositoryPath, new GraphQuery { RefScope = GraphRefScope.AllRefs });

        svc.Forget(ctx);

        Assert.False(HasCacheEntry(svc, ctx));
        Assert.Null(WalkCacheReadEntry(svc, ctx.RepositoryPath));
    }

    [Fact]
    public async Task Recompute_keeps_the_walk_cache_read_entry_for_its_own_walk()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        await using var ctx = _host.Open(_repo.Path);
        // The walk cache supports only an all-refs query, so computing and recomputing a
        // current-branch graph never saves or loads the entry the primed read created.
        var query = new GraphQuery { RefScope = GraphRefScope.CurrentBranch };
        Assert.False(GraphWalkCache.Supports(query));
        var previous = await svc.ComputeLayoutAsync(ctx, query);
        PrimeWalkCacheReadEntry(svc, ctx.RepositoryPath, new GraphQuery { RefScope = GraphRefScope.AllRefs });
        var entry = WalkCacheReadEntry(svc, ctx.RepositoryPath);

        await svc.RecomputeAsync(ctx, query, previous, InvalidationReason.HistoryRewritten);

        // The entry is the object the primed read created: a recompute that evicted it would have
        // removed it, and a later read would build a fresh one.
        Assert.Same(entry, WalkCacheReadEntry(svc, ctx.RepositoryPath));
    }

    [Fact]
    public async Task Hibernate_on_a_context_with_no_cache_is_a_no_op()
    {
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        await using var ctx = _host.Open(_repo.Path);

        await svc.Hibernate(ctx); // must not throw
        Assert.False(HasCacheEntry(svc, ctx));
    }

    [Fact]
    public async Task Refocus_after_hibernate_produces_the_same_layout()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        await using var ctx = _host.Open(_repo.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();

        var before = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });
        // Give the background spill a moment to persist the whole history to disk before we
        // drop the in-memory state — otherwise the re-focus below has nothing to resume from.
        await Task.Delay(1000);
        // Read the "before" row ids BEFORE hibernating: Layout.Commits may be the very
        // DiskCommitStore instance Hibernate disposes, same as a real bound control would need
        // to AddRef it (leases) to keep reading after the tab's cache is released.
        var beforeIds = Enumerable.Range(0, before.Layout.RowCount).Select(i => before.Layout.Commits[i].Id).ToList();

        await svc.Hibernate(ctx);

        var after = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });

        Assert.Equal(beforeIds.Count, after.Layout.RowCount);
        for (var i = 0; i < beforeIds.Count; i++)
            Assert.Equal(beforeIds[i], after.Layout.Commits[i].Id);
    }

    [Fact(Timeout = 30000)]
    public async Task A_compute_whose_cache_is_torn_down_before_its_spill_starts_still_returns()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        await using var ctx = _host.Open(_repo.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();

        // A tab switch or ref change tears the cache down (cancelling and disposing its token
        // source) while a compute that still holds it is about to start the background spill.
        GraphLayoutService.TestBeforeBackgroundSpillStart = () => svc.Forget(ctx);
        try
        {
            var result = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });
            Assert.Equal(CommitCount, result.Layout.RowCount);
        }
        finally { GraphLayoutService.TestBeforeBackgroundSpillStart = null; }
    }
}
