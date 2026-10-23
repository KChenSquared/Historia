namespace Historia.Tests;

[Trait("tier", "git")]
public sealed class RevisionResolverTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static TestRepositoryBuilder BuildRepo() =>
        TestRepositoryBuilder.FromTemplate("revision-resolver", b =>
        {
            b.WriteFile("a.txt", "one\n");
            b.Stage().Commit("first");
            b.WriteFile("a.txt", "two\n");
            b.Stage().Commit("second");
            b.Git("branch", "feature");
            b.Git("tag", "v1");
        });

    [Fact]
    public async Task Ordinary_revisions_resolve_to_commits()
    {
        using var repo = BuildRepo();
        var head = new GitObjectId(repo.HeadSha);
        var first = new GitObjectId(repo.Git("rev-parse", "HEAD~1").Trim());

        Assert.Equal(head, await RevisionResolver.ResolveCommitAsync(_host.Cli, repo.Path, "HEAD"));
        Assert.Equal(head, await RevisionResolver.ResolveCommitAsync(_host.Cli, repo.Path, repo.HeadSha[..8]));
        Assert.Equal(head, await RevisionResolver.ResolveCommitAsync(_host.Cli, repo.Path, "feature"));
        Assert.Equal(head, await RevisionResolver.ResolveCommitAsync(_host.Cli, repo.Path, "v1"));
        Assert.Equal(first, await RevisionResolver.ResolveCommitAsync(_host.Cli, repo.Path, "HEAD~1"));
    }

    [Fact]
    public async Task An_unknown_revision_is_null()
    {
        using var repo = BuildRepo();

        Assert.Null(await RevisionResolver.ResolveCommitAsync(_host.Cli, repo.Path, "nope"));
    }

    [Fact]
    public async Task A_tree_id_is_not_a_commit()
    {
        using var repo = BuildRepo();
        var tree = repo.Git("rev-parse", "HEAD^{tree}").Trim();

        Assert.Null(await RevisionResolver.ResolveCommitAsync(_host.Cli, repo.Path, tree));
    }

    [Fact]
    public async Task An_option_like_revision_is_rejected_before_git_runs()
    {
        using var repo = BuildRepo();
        var cli = new InterceptingGitCliService(_host.Cli);

        Assert.Null(await RevisionResolver.ResolveCommitAsync(cli, repo.Path, "--all"));
        Assert.Null(await RevisionResolver.ResolveCommitAsync(cli, repo.Path, "-rf"));
        Assert.Empty(cli.Requests);
    }

    [Fact]
    public async Task The_git_arguments_begin_with_the_verify_prefix_and_pass_the_revision_last()
    {
        using var repo = BuildRepo();
        var cli = new InterceptingGitCliService(_host.Cli);

        await RevisionResolver.ResolveCommitAsync(cli, repo.Path, "HEAD");

        var request = Assert.Single(cli.Requests);
        Assert.Equal(["rev-parse", "--verify", "-q", "--end-of-options", "HEAD^{commit}"], request.Arguments);
    }

    [Fact]
    public async Task The_top_level_is_found_from_a_subfolder_as_a_native_path()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("sub/deeper/x.txt", "x\n");

        var top = await RevisionResolver.ReadTopLevelAsync(_host.Cli, Path.Combine(repo.Path, "sub", "deeper"));

        Assert.NotNull(top);
        Assert.Equal(Path.GetFullPath(repo.Path), top, ignoreCase: OperatingSystem.IsWindows());
        if (OperatingSystem.IsWindows())
            Assert.DoesNotContain('/', top!);
    }

    [Fact]
    public async Task Outside_a_work_tree_the_top_level_is_null()
    {
        var dir = TestScratch.NewDirectory("ctl-not-a-repo-");
        try
        {
            Assert.Null(await RevisionResolver.ReadTopLevelAsync(_host.Cli, dir));
            Assert.Null(await RevisionResolver.ReadTopLevelAsync(_host.Cli, Path.Combine(dir, "missing")));
        }
        finally
        {
            TestScratch.Delete(dir);
        }
    }

    [Fact]
    public async Task The_subject_of_a_commit_is_read()
    {
        using var repo = BuildRepo();
        var first = new GitObjectId(repo.Git("rev-parse", "HEAD~1").Trim());
        var cli = new InterceptingGitCliService(_host.Cli);

        Assert.Equal("first", await RevisionResolver.ReadSubjectAsync(cli, repo.Path, first));
        Assert.Equal(["log", "-1", "--format=%s", "--end-of-options", first.ToString()], Assert.Single(cli.Requests).Arguments);
    }

    [Fact]
    public async Task Staged_paths_keep_spaces_and_non_ascii_names()
    {
        using var repo = BuildRepo();
        repo.WriteFile("a b.txt", "space\n");
        repo.WriteFile("dir/ünï.txt", "accent\n");
        repo.WriteFile("a.txt", "three\n");
        repo.WriteFile("unstaged.txt", "left alone\n");
        repo.Stage("a b.txt").Stage("dir/ünï.txt").Stage("a.txt");

        var staged = await RevisionResolver.ReadStagedPathsAsync(_host.Cli, repo.Path);

        Assert.Equal(["a b.txt", "a.txt", "dir/ünï.txt"], staged);
    }

    [Fact]
    public async Task An_empty_index_lists_no_staged_paths()
    {
        using var repo = BuildRepo();

        var staged = await RevisionResolver.ReadStagedPathsAsync(_host.Cli, repo.Path);

        Assert.NotNull(staged);
        Assert.Empty(staged);
    }

    [Fact]
    public async Task A_branch_with_no_commits_lists_its_staged_paths()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("new file.txt", "x\n");
        repo.WriteFile("other.txt", "y\n");
        repo.Stage("new file.txt");

        var staged = await RevisionResolver.ReadStagedPathsAsync(_host.Cli, repo.Path);

        Assert.Equal(["new file.txt"], staged);
    }
}
