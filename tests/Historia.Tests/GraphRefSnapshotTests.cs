namespace Historia.Tests;

/// <summary>
/// The commit graph reads refs from <see cref="RepositoryContext.RefSnapshots"/> instead of
/// enumerating them through LibGit2Sharp. Two guarantees ride on that: the walk key (what every
/// on-disk graph store is keyed on) must be exactly the one LibGit2Sharp-resolved tips gave, or
/// every existing store would miss once; and a walk step whose refs haven't changed must never
/// wait on the repo gate.
/// </summary>
[Trait("tier", "git")]
public sealed class GraphRefSnapshotTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _repos)
            r.Dispose();
        await _host.DisposeAsync();
    }

    private TestRepositoryBuilder Repo(string key, Action<TestRepositoryBuilder> build)
    {
        var repo = TestRepositoryBuilder.FromTemplate(key, build);
        _repos.Add(repo);
        return repo;
    }

    private static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    /// <summary>Reference tip resolution through LibGit2Sharp, independent of the ref snapshot:
    /// LibGit2Sharp refs, direct-resolved, peeled to commits, tree/blob targets dropped.</summary>
    private static string[] LibGit2Tips(Repository repo, GraphQuery query)
    {
        static Commit? Peel(Repository repo, string target)
        {
            try { return repo.Lookup<Commit>(target) ?? repo.Lookup(target)?.Peel<Commit>(); }
            catch { return null; }
        }

        var shas = new SortedSet<string>(StringComparer.Ordinal);
        switch (query.RefScope)
        {
            case GraphRefScope.CurrentBranch:
                if (!repo.Info.IsHeadUnborn)
                    shas.Add(repo.Head.Tip.Sha);
                break;
            case GraphRefScope.Selected:
                foreach (var name in query.SelectedRefs ?? [])
                    if (repo.Refs[name]?.ResolveToDirectReference()?.TargetIdentifier is { } t && Peel(repo, t) is { } c)
                        shas.Add(c.Sha);
                break;
            default:
                foreach (var r in repo.Refs)
                {
                    if (!(r.IsLocalBranch || (query.IncludeRemoteBranches && r.IsRemoteTrackingBranch) || r.IsTag))
                        continue;
                    if (r.ResolveToDirectReference()?.TargetIdentifier is { } t && Peel(repo, t) is { } c)
                        shas.Add(c.Sha);
                }
                break;
        }
        return [.. shas];
    }

    public static TheoryData<string> Queries() =>
    [
        "all", "all-no-remotes", "current", "selected",
    ];

    private static GraphQuery QueryFor(string name) => name switch
    {
        "all" => new GraphQuery { RefScope = GraphRefScope.AllRefs },
        "all-no-remotes" => new GraphQuery { RefScope = GraphRefScope.AllRefs, IncludeRemoteBranches = false },
        "current" => new GraphQuery { RefScope = GraphRefScope.CurrentBranch },
        "selected" => new GraphQuery
        {
            RefScope = GraphRefScope.Selected,
            SelectedRefs = ["refs/heads/feature", "refs/tags/nested", "refs/tags/tree-tag", "HEAD", "refs/heads/missing"],
        },
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Walk_key_matches_libgit2_tip_resolution(string queryName)
    {
        var repo = Repo("ref-shapes", RefSnapshotProviderTests.BuildRefShapes);
        await using var ctx = _host.Open(repo.Path);
        var query = QueryFor(queryName);

        var getRefState = typeof(GraphLayoutService).GetMethod("TryGetRefStateAsync", PrivateStatic)!;
        var task = (Task)getRefState.Invoke(null, [ctx, query, CancellationToken.None])!;
        await task;
        var state = (System.Runtime.CompilerServices.ITuple)task.GetType().GetProperty("Result")!.GetValue(task)!;
        var tips = (string[])state[2]!;
        var walkKey = (long)state[3]!;

        using var libgitRepo = new Repository(ctx.RepositoryPath);
        var expected = LibGit2Tips(libgitRepo, query);
        var computeWalkKey = typeof(GraphLayoutService).GetMethod("ComputeWalkKey", PrivateStatic)!;

        Assert.NotEmpty(expected);
        Assert.Equal(expected, tips);
        Assert.Equal((long)computeWalkKey.Invoke(null, [expected, query, ""])!, walkKey);
    }

    private static async Task<TaskCompletionSource> HoldRepoGateAsync(RepositoryContext ctx)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = GraphLayoutService.GetGateFor(ctx).ExecuteAsync(async () =>
        {
            entered.SetResult();
            await release.Task;
        });
        await entered.Task;
        return release;
    }

    private async Task<(GraphLayoutService Svc, RepositoryContext Ctx, GraphLayoutResult Initial, TestRepositoryBuilder Repo)> OpenLiveWalkAsync()
    {
        var repo = Repo("linear-3000", b => b.LinearHistory(3_000));
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        var ctx = _host.Open(repo.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();
        var initial = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs }, maxRows: 100);
        return (svc, ctx, initial, repo);
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

    [Fact]
    public async Task Resume_with_unchanged_refs_skips_the_repo_gate()
    {
        if (!_host.Locator.Current.Found)
            return; // git.exe not discoverable — the CLI walk never engages

        var (svc, ctx, initial, _) = await OpenLiveWalkAsync();
        await using var _ctx = ctx;
        var scansBefore = GraphLayoutService.RefScanCountForTesting(ctx.RepositoryPath);

        var release = await HoldRepoGateAsync(ctx);
        try
        {
            // 100 + 400 + 2,000 overscan = 2,500 wanted, 2,100 walked: a resume. With the gate
            // held by someone else it can only finish if it never asks for the gate.
            var extend = svc.ExtendLayoutAsync(ctx, initial, additionalRows: 400);
            var finished = await Task.WhenAny(extend, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.Same(extend, finished);
            Assert.Equal(500, (await extend).Layout.RowCount);
        }
        finally
        {
            release.SetResult();
        }

        Assert.Equal(scansBefore, GraphLayoutService.RefScanCountForTesting(ctx.RepositoryPath));
        Assert.Equal(2_500, Get<int>(GetCache(svc, ctx), "LiveWalkPos"));
    }

    private static List<GitObjectId> Ids(GraphLayoutResult r) =>
        Enumerable.Range(0, r.Layout.RowCount).Select(i => r.Layout.Commits[i].Id).ToList();

    [Fact]
    public async Task Ref_change_mid_crawl_supersedes_the_walk()
    {
        if (!_host.Locator.Current.Found)
            return;

        var (svc, ctx, initial, repo) = await OpenLiveWalkAsync();
        await using var _ctx = ctx;
        var cache = GetCache(svc, ctx);
        var keyBefore = Get<long>(cache, "WalkKey");

        // A commit the old walk never saw, on a new tip: a walk over the new tips would place it
        // among its first rows.
        var side = repo.Git("commit-tree", "HEAD~5^{tree}", "-p", "HEAD~5", "-m", "side").Trim();
        repo.Git("update-ref", "refs/heads/side", side);
        var extended = await svc.ExtendLayoutAsync(ctx, initial, additionalRows: 400);

        Assert.True(Get<bool>(cache, "WalkSuperseded"));
        Assert.Equal(keyBefore, Get<long>(cache, "WalkKey"));
        Assert.Null(cache.GetType().GetProperty("LiveWalk")!.GetValue(cache));
        // The rows the old walk already produced still extend the old layout, and nothing else.
        var ids = Ids(extended);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.DoesNotContain(GitObjectId.Parse(side), ids);
        Assert.Equal(Ids(initial), ids.Take(initial.Layout.RowCount));
    }

    [Fact]
    public async Task A_resume_never_rewrites_the_ref_state()
    {
        if (!_host.Locator.Current.Found)
            return;

        var (svc, ctx, initial, repo) = await OpenLiveWalkAsync();
        await using var _ctx = ctx;
        var cache = GetCache(svc, ctx);
        var fingerprint = Get<long>(cache, "Fingerprint");
        var decorations = cache.GetType().GetProperty("Decorations")!.GetValue(cache);

        // Fast resume: no ref file changed.
        var extended = await svc.ExtendLayoutAsync(ctx, initial, additionalRows: 400);
        Assert.Equal(500, extended.Layout.RowCount);
        Assert.Equal(fingerprint, Get<long>(cache, "Fingerprint"));
        Assert.Same(decorations, cache.GetType().GetProperty("Decorations")!.GetValue(cache));

        // Slow resume: HEAD moved to a new branch on the same commit — the fingerprint changes,
        // the tip set doesn't, so the walk resumes through the gate.
        repo.Git("checkout", "-q", "-b", "other");
        var scansBefore = GraphLayoutService.RefScanCountForTesting(ctx.RepositoryPath);
        var again = await svc.ExtendLayoutAsync(ctx, extended, additionalRows: 400);

        Assert.Equal(900, again.Layout.RowCount);
        Assert.True(GraphLayoutService.RefScanCountForTesting(ctx.RepositoryPath) > scansBefore);
        Assert.False(Get<bool>(cache, "WalkSuperseded"));
        Assert.Equal(fingerprint, Get<long>(cache, "Fingerprint"));
        Assert.Same(decorations, cache.GetType().GetProperty("Decorations")!.GetValue(cache));
    }

    [Fact]
    public async Task Checkout_mid_crawl_repaints_badges_on_next_compute()
    {
        if (!_host.Locator.Current.Found)
            return;

        var (svc, ctx, initial, repo) = await OpenLiveWalkAsync();
        await using var _ctx = ctx;
        var query = new GraphQuery { RefScope = GraphRefScope.AllRefs };

        repo.Git("checkout", "-q", "-b", "other");
        await svc.ExtendLayoutAsync(ctx, initial, additionalRows: 400);

        // The resume left the old fingerprint with the old badges, so the compute sees the refs
        // moved and rebuilds them — also when the rebuilt walk comes from the .gz walk cache,
        // whose key (the tip set) the checkout didn't change.
        var fresh = await svc.ComputeLayoutAsync(ctx, query, maxRows: 100);
        var head = Assert.Single(fresh.Layout.Decorations, d => d.RowIndex == 0 && d.IsHead);
        Assert.Equal("other", head.Label);
    }

    [Fact]
    public async Task Recompute_with_unchanged_refs_returns_previous_without_the_gate()
    {
        if (!_host.Locator.Current.Found)
            return;

        var (svc, ctx, initial, _) = await OpenLiveWalkAsync();
        await using var _ctx = ctx;

        var release = await HoldRepoGateAsync(ctx);
        try
        {
            var recompute = svc.RecomputeAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs }, initial,
                InvalidationReason.RefsChanged, maxRows: 100);
            var finished = await Task.WhenAny(recompute, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.Same(recompute, finished);
            Assert.Same(initial, await recompute);
        }
        finally
        {
            release.SetResult();
        }
    }
}
