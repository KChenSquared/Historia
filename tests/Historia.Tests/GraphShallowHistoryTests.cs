namespace Historia.Tests;

/// <summary>Deepening a shallow clone changes the history its unchanged tips reach, so the walk
/// key, the ref-state token and the ref fingerprint must all move with it — otherwise the
/// exhausted on-disk store written while shallow keeps serving the truncated history.</summary>
[Trait("tier", "git")]
public sealed class GraphShallowHistoryTests : IAsyncDisposable
{
    private const int CommitCount = 40;
    private const int Depth = 5;

    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _origin;
    private readonly string _cloneParent = TestScratch.NewDirectory("historia-shallow-");
    private readonly string _clone;

    public GraphShallowHistoryTests()
    {
        _origin = TestRepositoryBuilder.FromTemplate("linear-40", b => b.LinearHistory(CommitCount));
        var url = "file://" + _origin.Path.Replace('\\', '/');
        TestGit.RunChecked(_cloneParent, ["clone", "--quiet", "--depth", "5", url, "clone"]);
        _clone = Path.Combine(_cloneParent, "clone");
    }

    public async ValueTask DisposeAsync()
    {
        _origin.Dispose();
        await _host.DisposeAsync();
        TestScratch.Delete(_cloneParent);
    }

    [Fact]
    public void Unshallow_changes_the_ref_state_token_and_history_shape()
    {
        var gitDir = Path.Combine(_clone, ".git");
        var tokenBefore = RefStateToken.Compute(gitDir).Token;
        var shapeBefore = RefStateToken.HistoryShape(gitDir);

        TestGit.RunChecked(_clone, ["fetch", "--quiet", "--unshallow"]);

        Assert.NotEqual("", shapeBefore);
        Assert.Equal("", RefStateToken.HistoryShape(gitDir));
        Assert.NotEqual(tokenBefore, RefStateToken.Compute(gitDir).Token);
    }

    [Fact]
    public void A_full_clone_has_no_history_shape()
    {
        Assert.Equal("", RefStateToken.HistoryShape(Path.Combine(_origin.Path, ".git")));
    }

    [Fact]
    public async Task Relaunch_after_unshallow_shows_the_full_history_not_the_cached_shallow_one()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        var query = new GraphQuery { RefScope = GraphRefScope.AllRefs };

        var first = new GraphLayoutService(_host.Settings, _host.Locator);
        await using (var ctx = _host.Open(_clone))
        {
            var shallow = await first.ComputeLayoutAsync(ctx, query);
            Assert.Equal(Depth, shallow.Layout.RowCount);
            await WaitForExhaustedStoreAsync(Depth);
            first.Forget(ctx);
        }

        TestGit.RunChecked(_clone, ["fetch", "--quiet", "--unshallow"]);

        var relaunched = new GraphLayoutService(_host.Settings, _host.Locator);
        await using (var ctx = _host.Open(_clone))
        {
            var full = await relaunched.ComputeLayoutAsync(ctx, query);
            Assert.Equal(CommitCount, full.Layout.RowCount);
            relaunched.Forget(ctx);
        }
    }

    private async Task WaitForExhaustedStoreAsync(int rows)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var probe = GraphMetaStore.ProbeExisting(_clone, includeRemoteBranches: true);
            if (probe is { Ok: true, Exhausted: true } && probe.RowCount == rows)
                return;
            await Task.Delay(50);
        }
        Assert.Fail("the shallow walk never reached disk as an exhausted store");
    }
}
