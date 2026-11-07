namespace Historia.Tests;

/// <summary>
/// A cache notices when the store it holds has been replaced by another writer, and its teardown
/// never pulls the store out from under an extend that is already writing.
/// </summary>
[Trait("tier", "git")]
public sealed class GraphCacheGenerationTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];

    private static readonly GraphQuery Q = new() { RefScope = GraphRefScope.AllRefs };

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _repos)
            r.Dispose();
        await _host.DisposeAsync();
    }

    /// <summary>Open for the foreground spill of painted rows, never open for the background
    /// full-history spill — so the store holds exactly what the test wrote and nothing races it.</summary>
    private sealed class PaintOnlyGate : IBackgroundWorkGate
    {
        public bool IsOpen => true;
        public Task WaitUntilOpenAsync(CancellationToken ct = default) => Task.Delay(Timeout.Infinite, ct);
        public void ReportGraphSettled(RepositoryContext context) { }
    }

    private TestRepositoryBuilder NewRepo(string template, int commits)
    {
        var repo = TestRepositoryBuilder.FromTemplate(template, b => b.LinearHistory(commits));
        _repos.Add(repo);
        return repo;
    }

    private GraphLayoutService DiskService() => new(
        _host.Settings, _host.Locator, _host.Provider.GetRequiredService<IGitCliService>(), new PaintOnlyGate());

    private static List<RawCommit> LoadRawCommits(string repoPath)
    {
        using var gitRepo = new Repository(repoPath);
        return gitRepo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = gitRepo.Refs }).Select(c =>
        {
            var meta = new CommitMetadata
            {
                Id = c.Id.ToGitObjectId(),
                MessageShort = c.MessageShort,
                AuthorName = c.Author.Name,
                AuthorEmail = c.Author.Email,
                AuthorWhen = c.Author.When,
                CommitWhen = c.Committer.When,
                ParentCount = c.Parents.Count(),
            };
            return new RawCommit(c.Id.ToGitObjectId(), c.Parents.Select(p => p.Id.ToGitObjectId()).ToArray(), meta);
        }).ToList();
    }

    private static List<GitObjectId> Ids(GraphLayoutResult r) =>
        Enumerable.Range(0, r.Layout.RowCount).Select(i => r.Layout.Commits[i].Id).ToList();

    private async Task<List<GitObjectId>> InMemoryOrderAsync(string repoPath)
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
        var memSvc = new GraphLayoutService(_host.Settings, _host.Locator);
        await using var memCtx = _host.Open(repoPath);
        var ids = Ids(await memSvc.ComputeLayoutAsync(memCtx, Q));
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        return ids;
    }

    private static object GetCache(GraphLayoutService svc, RepositoryContext ctx)
    {
        var cwt = typeof(GraphLayoutService).GetField("_caches", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(svc)!;
        var args = new object?[] { ctx, null };
        Assert.True((bool)cwt.GetType().GetMethod("TryGetValue")!.Invoke(cwt, args)!);
        return args[1]!;
    }

    private static T Get<T>(object target, string property) =>
        (T)target.GetType().GetProperty(property)!.GetValue(target)!;

    [Fact(Timeout = 60_000)]
    public async Task A_walk_key_that_returns_after_another_writers_reset_reopens_the_store()
    {
        const int painted = 100;
        const long otherKey = 0x1357_9BDF_0246_8ACE;
        var repo = NewRepo("linear-3000", 3_000);
        var expected = await InMemoryOrderAsync(repo.Path);

        var svc = DiskService();
        await using var ctx = _host.Open(repo.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();

        // The tab paints `painted` rows and holds the store it wrote them to.
        var layout = await svc.ComputeLayoutAsync(ctx, Q, painted);
        var probe = GraphMetaStore.ProbeExisting(repo.Path, includeRemoteBranches: true);
        Assert.True(probe.Ok);
        Assert.Equal(painted, probe.RowCount);
        var heldGeneration = ((DiskCommitStore)layout.Layout.Commits).Generation;
        Assert.Equal(heldGeneration, GraphMetaStore.ProbeGeneration(repo.Path, true).Generation);

        // Another writer resets the store to a different walk, then back to this walk's key with
        // the same first rows: the header matches again, but it names new files.
        var raw = LoadRawCommits(repo.Path);
        var other = Enumerable.Range(0, 10).Select(i => new RawCommit(
            new GitObjectId(i.ToString("x40")), [], new CommitMetadata
            {
                Id = new GitObjectId(i.ToString("x40")),
                MessageShort = "other",
                AuthorName = "A",
                AuthorEmail = "a@x",
                AuthorWhen = DateTimeOffset.UnixEpoch,
                CommitWhen = DateTimeOffset.UnixEpoch,
                ParentCount = 0,
            })).ToList();
        Assert.Equal(SpillOutcome.Appended, new GraphMetaWriter(repo.Path, otherKey, true).AppendSpillBatch(other, 0, other.Count, exhausted: false).Outcome);
        Assert.Equal(SpillOutcome.Appended, new GraphMetaWriter(repo.Path, probe.WalkKey, true).AppendSpillBatch(raw.GetRange(0, painted), 0, painted, exhausted: false).Outcome);
        var currentGeneration = GraphMetaStore.ProbeGeneration(repo.Path, true).Generation;
        Assert.NotEqual(heldGeneration, currentGeneration);

        var cache = GetCache(svc, ctx);
        var spilledBefore = Get<int>(cache, "SpilledRows");
        var extended = await svc.ExtendLayoutAsync(ctx, layout, additionalRows: 500);

        Assert.Equal(expected.Take(extended.Layout.RowCount), Ids(extended));
        Assert.Equal(painted + 500, extended.Layout.RowCount);
        var store = Assert.IsType<DiskCommitStore>(extended.Layout.Commits);
        Assert.Equal(currentGeneration, store.Generation);
        var spilledAfter = Get<int>(cache, "SpilledRows");
        Assert.True(spilledAfter > spilledBefore, $"resident rows were never trimmed (spilled {spilledBefore} -> {spilledAfter})");
        Assert.True(spilledAfter >= extended.Layout.RowCount);
    }

    [Fact(Timeout = 120_000)]
    public async Task Hibernate_during_a_foreground_extend_completes_without_fallback()
    {
        const int commitCount = 300;
        const int iterations = 50;
        var repo = NewRepo("linear-300", commitCount);
        var expected = await InMemoryOrderAsync(repo.Path);
        var svc = DiskService();

        // One tab for every round, as in a session: it is hibernated on each switch away and
        // computes again on the switch back, so a round costs one walk and not a tab open too.
        await using var ctx = _host.Open(repo.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();
        string CacheState() => svc.GetCacheStateForTesting(ctx)?.ToString() ?? "none, or a cache lock was busy";

        var loop = System.Diagnostics.Stopwatch.StartNew();
        var slowest = TimeSpan.Zero;
        for (var i = 0; i < iterations; i++)
        {
            Assert.True(
                loop.Elapsed < LoopBudget,
                $"only {i} of {iterations} rounds ran in {loop.Elapsed.TotalSeconds:0} s (slowest round {slowest.TotalMilliseconds:0} ms): the runner is too slow for this loop, no step is stuck");
            var round = System.Diagnostics.Stopwatch.StartNew();

            var layout = await StepAsync(svc.ComputeLayoutAsync(ctx, Q, 20), "compute the painted window", i, CacheState);
            // The painted graph's own lease on the store: the tab switch releases the cache's.
            var lease = layout.Layout.Commits.AddRef();
            try
            {
                var extend = Task.Run(() => svc.ExtendLayoutAsync(ctx, layout, additionalRows: 100));
                await Task.Delay(i % 4);
                await StepAsync(svc.Hibernate(ctx), "hibernate", i, CacheState);
                var extended = await StepAsync(extend, "finish the extend that raced the hibernate", i, CacheState);

                Assert.Equal(expected.Take(extended.Layout.RowCount), Ids(extended));
            }
            finally
            {
                lease?.Dispose();
            }
            if (round.Elapsed > slowest)
                slowest = round.Elapsed;
        }
    }

    /// <summary>How long one awaited step of the hibernate loop may take before the test names it.
    /// A step is a walk or a teardown: seconds on a loaded runner, never this long unless stuck.</summary>
    private static readonly TimeSpan StepDeadline = TimeSpan.FromSeconds(20);

    /// <summary>The whole loop gives up here, naming how far it got, so a slow runner reads as slow
    /// and not as the test timeout's silent kill.</summary>
    private static readonly TimeSpan LoopBudget = TimeSpan.FromSeconds(100);

    private static async Task<T> StepAsync<T>(Task<T> step, string name, int iteration, Func<string> state)
    {
        try { return await step.WaitAsync(StepDeadline); }
        catch (TimeoutException) { throw StepStuck(name, iteration, state); }
    }

    private static async Task StepAsync(Task step, string name, int iteration, Func<string> state)
    {
        try { await step.WaitAsync(StepDeadline); }
        catch (TimeoutException) { throw StepStuck(name, iteration, state); }
    }

    private static TimeoutException StepStuck(string name, int iteration, Func<string> state) =>
        new($"iteration {iteration}: '{name}' did not finish within {StepDeadline.TotalSeconds:0} s. Cache: {state()}");
}
