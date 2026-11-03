namespace Historia.Tests;

/// <summary><c>IGitGateway.FastForwardBranchAsync</c> moves a branch that is not checked out to a
/// remote-tracking ref with <c>update-ref</c>, and refuses anything that is not a fast-forward,
/// the checked-out branch and a branch checked out in another worktree.</summary>
[Trait("tier", "git")]
public sealed class GitGatewayFastForwardBranchTests : IAsyncDisposable
{
    private const string OriginMain = "refs/remotes/origin/main";

    private readonly MiniHost _host = new();
    private readonly List<string> _scratch = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var dir in _scratch)
            TestScratch.Delete(dir);
        await _host.DisposeAsync();
    }

    private GitGateway MakeGateway(RepositoryContext ctx) => new(
        ctx,
        _host.Provider.GetRequiredService<IGitCliService>(),
        _host.Provider.GetRequiredService<IRepoLayoutService>(),
        _host.Provider.GetRequiredService<ICredentialVaultService>());

    /// <summary>A repository with <c>main</c> pushed to a bare <c>origin</c>, then
    /// <c>feature</c> checked out. <c>origin/main</c> is level with <c>main</c>.</summary>
    private TestRepositoryBuilder RepoWithRemote()
    {
        var repo = TestRepositoryBuilder.Create();
        var remote = TestScratch.NewDirectory("historia-ff-remote-");
        _scratch.Add(remote);
        repo.Git("init", "--bare", "-b", "main", remote);
        repo.WriteFile("a.txt", "1").Stage().Commit("base");
        repo.Git("remote", "add", "origin", remote);
        repo.Git("push", "-u", "origin", "main");
        repo.Checkout("feature", create: true);
        return repo;
    }

    /// <summary>Commits on <c>main</c> through a temporary branch, pushes it to
    /// <c>origin/main</c> and fetches, so <c>origin/main</c> is one commit ahead of the local
    /// <c>main</c>. Returns the new commit's id.</summary>
    private static string AdvanceOriginMain(TestRepositoryBuilder repo)
    {
        repo.Checkout("main");
        repo.Checkout("advance", create: true);
        repo.WriteFile("b.txt", "remote").Stage().Commit("remote side");
        repo.Git("push", "origin", "advance:main");
        repo.Checkout("feature");
        repo.Git("branch", "-D", "advance");
        repo.Git("fetch", "origin");
        return repo.Git("rev-parse", OriginMain).Trim();
    }

    private static string Sha(TestRepositoryBuilder repo, string rev) => repo.Git("rev-parse", rev).Trim();

    [Fact]
    public async Task FastForwards_non_current_branch_to_remote_tracking_ref()
    {
        using var repo = RepoWithRemote();
        var target = AdvanceOriginMain(repo);
        var headBefore = Sha(repo, "HEAD");

        await using var ctx = _host.Open(repo.Path);
        await MakeGateway(ctx).FastForwardBranchAsync("main", OriginMain);

        Assert.Equal(target, Sha(repo, "refs/heads/main"));
        Assert.Equal(headBefore, Sha(repo, "HEAD"));
        Assert.Equal("refs/heads/feature", repo.Git("symbolic-ref", "HEAD").Trim());
        Assert.Contains("fast-forward to " + OriginMain, repo.Git("reflog", "-1", "--format=%gs", "refs/heads/main"));
    }

    [Fact]
    public async Task Refuses_current_branch()
    {
        using var repo = RepoWithRemote();
        AdvanceOriginMain(repo);
        repo.Checkout("main");
        var before = Sha(repo, "refs/heads/main");

        await using var ctx = _host.Open(repo.Path);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MakeGateway(ctx).FastForwardBranchAsync("main", OriginMain));

        Assert.Contains("checked out", ex.Message);
        Assert.Equal(before, Sha(repo, "refs/heads/main"));
    }

    [Fact]
    public async Task Refuses_branch_checked_out_in_another_worktree()
    {
        using var repo = RepoWithRemote();
        AdvanceOriginMain(repo);
        var worktree = TestScratch.NewPath("historia-ff-worktree-");
        _scratch.Add(worktree);
        repo.Git("worktree", "add", worktree, "main");
        var before = Sha(repo, "refs/heads/main");

        try
        {
            await using var ctx = _host.Open(repo.Path);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => MakeGateway(ctx).FastForwardBranchAsync("main", OriginMain));

            Assert.Contains("another worktree", ex.Message);
            Assert.Equal(before, Sha(repo, "refs/heads/main"));
        }
        finally
        {
            repo.Git("worktree", "remove", "--force", worktree);
        }
    }

    [Fact]
    public async Task Refuses_diverged_branch_and_leaves_ref_unchanged()
    {
        using var repo = RepoWithRemote();
        AdvanceOriginMain(repo);
        repo.Checkout("main");
        repo.WriteFile("c.txt", "local").Stage().Commit("local side");
        repo.Checkout("feature");
        var before = Sha(repo, "refs/heads/main");

        await using var ctx = _host.Open(repo.Path);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MakeGateway(ctx).FastForwardBranchAsync("main", OriginMain));

        Assert.Contains("can't be fast-forwarded", ex.Message);
        Assert.Equal(before, Sha(repo, "refs/heads/main"));
    }

    [Fact]
    public async Task Level_branch_is_a_no_op()
    {
        using var repo = RepoWithRemote();
        var before = Sha(repo, "refs/heads/main");
        var reflogBefore = repo.Git("reflog", "refs/heads/main");

        await using var ctx = _host.Open(repo.Path);
        await MakeGateway(ctx).FastForwardBranchAsync("main", OriginMain);

        Assert.Equal(before, Sha(repo, "refs/heads/main"));
        Assert.Equal(reflogBefore, repo.Git("reflog", "refs/heads/main"));
    }

    [Fact]
    public async Task Missing_branch_is_RefNotFound()
    {
        using var repo = RepoWithRemote();

        await using var ctx = _host.Open(repo.Path);
        var ex = await Assert.ThrowsAsync<HistoriaGitException>(
            () => MakeGateway(ctx).FastForwardBranchAsync("nope", OriginMain));

        Assert.Equal(GitErrorKind.RefNotFound, ex.Kind);
    }
}
