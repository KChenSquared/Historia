namespace Historia.Tests;

[Trait("tier", "git")]
public sealed class GitGatewayCaseCollisionStatusTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    private async Task<GitCliResult> GitAsync(string repoPath, params string[] args)
        => await _host.Cli.RunAsync(new GitCliRequest { Arguments = args, WorkingDirectory = repoPath });

    private async Task<string> IndexBlobAsync(string repoPath, string path)
    {
        var ls = await GitAsync(repoPath, "ls-files", "-s", "-z");
        foreach (var entry in ls.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = entry.IndexOf('\t');
            if (entry[(tab + 1)..] == path)
                return entry.Split(' ')[1];
        }
        throw new InvalidOperationException($"{path} is not in the index");
    }

    private async Task<(TestRepositoryBuilder Repo, string LosingPath, string WinningPath)?> CreateCollisionFixtureAsync()
    {
        var repo = TestRepositoryBuilder.Create();
        if (!TestScratch.IsCaseInsensitive(repo.Path))
        {
            repo.Dispose();
            return null;
        }

        var hash1 = await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["hash-object", "-w", "--stdin"],
            WorkingDirectory = repo.Path,
            StandardInput = "blob Foo\n",
        });
        var oid1 = hash1.StandardOutput.Trim();

        var hash2 = await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["hash-object", "-w", "--stdin"],
            WorkingDirectory = repo.Path,
            StandardInput = "blob foo\n",
        });
        var oid2 = hash2.StandardOutput.Trim();

        await GitAsync(repo.Path, "update-index", "--add", "--cacheinfo", $"100644,{oid1},Foo.txt");
        await GitAsync(repo.Path, "update-index", "--add", "--cacheinfo", $"100644,{oid2},foo.txt");
        await GitAsync(repo.Path, "commit", "-m", "init collision");
        await GitAsync(repo.Path, "checkout-index", "-f", "-a");
        await GitAsync(repo.Path, "config", "core.ignorecase", "true");

        var content = (await File.ReadAllTextAsync(Path.Combine(repo.Path, "Foo.txt"))).Trim();
        return content == "blob Foo" ? (repo, "foo.txt", "Foo.txt") : (repo, "Foo.txt", "foo.txt");
    }

    [Fact]
    public async Task Twin_content_is_hidden_from_unstaged()
    {
        var fixture = await CreateCollisionFixtureAsync();
        if (fixture is null)
            return;

        using var repo = fixture.Value.Repo;
        var ctx = _host.Open(repo.Path);

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Empty(status.Unstaged);
        Assert.Equal([fixture.Value.LosingPath], status.HiddenCaseCollisions);
        Assert.Empty(status.AmbiguousCaseCollisions);
        Assert.False(status.IsDirty);
    }

    [Fact]
    public async Task Real_edit_without_a_known_owner_is_ambiguous()
    {
        var fixture = await CreateCollisionFixtureAsync();
        if (fixture is null)
            return;

        using var repo = fixture.Value.Repo;
        await File.AppendAllTextAsync(Path.Combine(repo.Path, fixture.Value.WinningPath), "real edit\n");

        var ctx = _host.Open(repo.Path);
        var status = await ctx.Gateway.GetStatusAsync();

        Assert.Equal(2, status.Unstaged.Count);
        Assert.All(status.Unstaged, u => Assert.Equal(FileChangeKind.Modified, u.Kind));
        Assert.Equal(["Foo.txt", "foo.txt"], status.AmbiguousCaseCollisions);
        Assert.Empty(status.HiddenCaseCollisions);
        Assert.True(status.IsDirty);
    }

    [Fact]
    public async Task Real_edit_after_a_refresh_shows_only_the_owner()
    {
        var fixture = await CreateCollisionFixtureAsync();
        if (fixture is null)
            return;

        using var repo = fixture.Value.Repo;
        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.GetStatusAsync();

        await File.AppendAllTextAsync(Path.Combine(repo.Path, fixture.Value.WinningPath), "real edit\n");
        var status = await ctx.Gateway.GetStatusAsync();

        var row = Assert.Single(status.Unstaged);
        Assert.Equal(fixture.Value.WinningPath, row.Path);
        Assert.Equal([fixture.Value.LosingPath], status.HiddenCaseCollisions);
        Assert.Empty(status.AmbiguousCaseCollisions);
    }

    [Fact]
    public async Task Owner_change_between_refreshes_makes_both_rows_ambiguous()
    {
        var fixture = await CreateCollisionFixtureAsync();
        if (fixture is null)
            return;

        using var repo = fixture.Value.Repo;
        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.GetStatusAsync();

        await GitAsync(repo.Path, "checkout-index", "-f", "--", fixture.Value.LosingPath);
        await File.AppendAllTextAsync(Path.Combine(repo.Path, fixture.Value.LosingPath), "real edit\n");
        var status = await ctx.Gateway.GetStatusAsync();

        Assert.Equal(2, status.Unstaged.Count);
        Assert.Equal(["Foo.txt", "foo.txt"], status.AmbiguousCaseCollisions);
        Assert.Empty(status.HiddenCaseCollisions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Staging_the_owner_leaves_the_twin_blob_unchanged(bool swapOwner)
    {
        var fixture = await CreateCollisionFixtureAsync();
        if (fixture is null)
            return;

        using var repo = fixture.Value.Repo;
        var (owner, twin) = (fixture.Value.WinningPath, fixture.Value.LosingPath);
        if (swapOwner)
        {
            await GitAsync(repo.Path, "checkout-index", "-f", "--", twin);
            (owner, twin) = (twin, owner);
        }

        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.GetStatusAsync();
        await File.AppendAllTextAsync(Path.Combine(repo.Path, owner), "real edit\n");
        var edited = await ctx.Gateway.GetStatusAsync();
        Assert.Equal(owner, Assert.Single(edited.Unstaged).Path);

        var twinBlobBefore = await IndexBlobAsync(repo.Path, twin);
        await ctx.Gateway.StageAsync([owner]);

        Assert.Equal(twinBlobBefore, await IndexBlobAsync(repo.Path, twin));
        var diskBlob = (await GitAsync(repo.Path, "hash-object", "--", owner)).StandardOutput.Trim();
        Assert.Equal(diskBlob, await IndexBlobAsync(repo.Path, owner));

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Equal(owner, Assert.Single(status.Staged).Path);
        Assert.Empty(status.Unstaged);
        Assert.Equal([twin], status.HiddenCaseCollisions);
    }

    [Fact]
    public async Task Staging_both_names_of_one_disk_file_is_refused()
    {
        var fixture = await CreateCollisionFixtureAsync();
        if (fixture is null)
            return;

        using var repo = fixture.Value.Repo;
        await File.AppendAllTextAsync(Path.Combine(repo.Path, fixture.Value.WinningPath), "real edit\n");

        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.GetStatusAsync();
        var indexBefore = (await GitAsync(repo.Path, "ls-files", "-s")).StandardOutput;

        await Assert.ThrowsAsync<HistoriaGitException>(() => ctx.Gateway.StageAsync(["Foo.txt", "foo.txt"]));

        Assert.Equal(indexBefore, (await GitAsync(repo.Path, "ls-files", "-s")).StandardOutput);
    }

    [Fact]
    public async Task Discard_of_a_real_edit_ends_clean()
    {
        var fixture = await CreateCollisionFixtureAsync();
        if (fixture is null)
            return;

        using var repo = fixture.Value.Repo;
        await File.AppendAllTextAsync(Path.Combine(repo.Path, "Foo.txt"), "real edit\n");

        var ctx = _host.Open(repo.Path);
        var beforeStatus = await ctx.Gateway.GetStatusAsync();
        Assert.Equal(2, beforeStatus.Unstaged.Count);

        await ctx.Gateway.DiscardAsync(["Foo.txt", "foo.txt"]);

        var afterStatus = await ctx.Gateway.GetStatusAsync();
        Assert.Empty(afterStatus.Unstaged);
        Assert.Equal([fixture.Value.LosingPath], afterStatus.HiddenCaseCollisions);
        Assert.False(afterStatus.IsDirty);
    }

    [Fact]
    public async Task Ignorecase_false_shows_git_status_verbatim()
    {
        var fixture = await CreateCollisionFixtureAsync();
        if (fixture is null)
            return;

        using var repo = fixture.Value.Repo;
        await GitAsync(repo.Path, "config", "core.ignorecase", "false");

        var ctx = _host.Open(repo.Path);
        var status = await ctx.Gateway.GetStatusAsync();

        Assert.Single(status.Unstaged);
        Assert.Equal(fixture.Value.LosingPath, status.Unstaged[0].Path);
        Assert.Empty(status.HiddenCaseCollisions);
        Assert.True(status.IsDirty);
    }

    [Fact]
    public async Task Worktree_config_change_invalidates_the_ignorecase_cache()
    {
        var fixture = await CreateCollisionFixtureAsync();
        if (fixture is null)
            return;

        using var repo = fixture.Value.Repo;
        await GitAsync(repo.Path, "config", "extensions.worktreeConfig", "true");

        var ctx = _host.Open(repo.Path);
        var before = await ctx.Gateway.GetStatusAsync();
        Assert.Single(before.HiddenCaseCollisions);

        await GitAsync(repo.Path, "config", "--worktree", "core.ignorecase", "false");
        var after = await ctx.Gateway.GetStatusAsync();

        Assert.Equal(fixture.Value.LosingPath, Assert.Single(after.Unstaged).Path);
        Assert.Empty(after.HiddenCaseCollisions);
    }

    [Fact]
    public async Task Steady_state_refresh_spawns_no_extra_process()
    {
        var fixture = await CreateCollisionFixtureAsync();
        if (fixture is null)
            return;

        using var repo = fixture.Value.Repo;
        var spy = new InterceptingGitCliService(_host.Cli);
        var ctx = _host.Open(repo.Path, spy);

        var firstStatus = await ctx.Gateway.GetStatusAsync();
        Assert.Empty(firstStatus.Unstaged);
        Assert.Single(firstStatus.HiddenCaseCollisions);

        var countBefore = spy.Requests.Count;
        var secondStatus = await ctx.Gateway.GetStatusAsync();
        Assert.Empty(secondStatus.Unstaged);
        Assert.Single(secondStatus.HiddenCaseCollisions);

        var secondRequests = spy.Requests.Skip(countBefore).ToList();
        Assert.DoesNotContain(secondRequests, r => r.Arguments.Contains("ls-files"));
        Assert.DoesNotContain(secondRequests, r => r.Arguments.Contains("hash-object"));
        Assert.DoesNotContain(secondRequests, r => r.Arguments.Contains("config"));
    }

    private static bool IsWholeIndexListing(GitCliRequest r) =>
        r.Arguments.Contains("ls-files") && r.Arguments.Contains("-s") && !r.Arguments.Contains("--");

    [Fact]
    public async Task Status_in_a_repo_without_collisions_never_lists_the_whole_index()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("no-collision-cache", r =>
            r.WriteFile("file.txt", "v1\n").Stage().Commit("init"));

        await GitAsync(repo.Path, "config", "core.ignorecase", "true");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "file.txt"), "v2\n");

        var spy = new InterceptingGitCliService(_host.Cli);
        var ctx = _host.Open(repo.Path, spy);

        var firstStatus = await ctx.Gateway.GetStatusAsync();
        Assert.Single(firstStatus.Unstaged);
        Assert.Empty(firstStatus.HiddenCaseCollisions);

        var countBefore = spy.Requests.Count;
        var secondStatus = await ctx.Gateway.GetStatusAsync();
        Assert.Single(secondStatus.Unstaged);
        Assert.Empty(secondStatus.HiddenCaseCollisions);

        // The tab's own refreshes (the file watcher reacting to git's index writes) share the spy,
        // so the cached answers are checked by kind rather than by total request count.
        var secondRequests = spy.Requests.Skip(countBefore).ToList();
        Assert.Contains(secondRequests, r => r.Arguments.Contains("status"));
        Assert.DoesNotContain(secondRequests, r => r.Arguments.Contains("ls-files"));
        Assert.DoesNotContain(secondRequests, r => r.Arguments.Contains("core.ignorecase"));

        await File.WriteAllTextAsync(Path.Combine(repo.Path, "file.txt"), "v3\n");
        await ctx.Gateway.GetStatusAsync();

        Assert.DoesNotContain(spy.Requests, IsWholeIndexListing);
    }

    [Fact]
    public async Task Status_asks_the_index_only_about_the_modified_paths()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("no-collision-targeted", r =>
            r.WriteFile("file.txt", "v1\n").WriteFile("other.txt", "v1\n").Stage().Commit("init"));

        await GitAsync(repo.Path, "config", "core.ignorecase", "true");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "file.txt"), "v2\n");

        var spy = new InterceptingGitCliService(_host.Cli);
        var ctx = _host.Open(repo.Path, spy);

        await ctx.Gateway.GetStatusAsync();

        var query = Assert.Single(spy.Requests, r => r.Arguments.Contains("ls-files"));
        Assert.Equal(["ls-files", "-s", "-z", "--", ":(literal,icase)file.txt"], query.Arguments);
    }

    [Fact]
    public async Task Twin_detection_never_lists_the_whole_index()
    {
        var fixture = await CreateCollisionFixtureAsync();
        if (fixture is null)
            return;

        using var repo = fixture.Value.Repo;
        var spy = new InterceptingGitCliService(_host.Cli);
        var ctx = _host.Open(repo.Path, spy);

        var status = await ctx.Gateway.GetStatusAsync();

        Assert.Equal([fixture.Value.LosingPath], status.HiddenCaseCollisions);
        Assert.DoesNotContain(spy.Requests, IsWholeIndexListing);
        Assert.Contains(spy.Requests, r => r.Arguments.Contains("ls-files") && r.Arguments.Contains("--"));
    }

    [Fact]
    public async Task A_failed_candidate_query_falls_back_to_the_whole_index_and_still_hides_the_twin()
    {
        var fixture = await CreateCollisionFixtureAsync();
        if (fixture is null)
            return;

        using var repo = fixture.Value.Repo;
        var spy = new InterceptingGitCliService(_host.Cli)
        {
            Intercept = r => r.Arguments.Contains("ls-files") && r.Arguments.Contains("--")
                ? InterceptingGitCliService.Failure()
                : null,
        };
        var ctx = _host.Open(repo.Path, spy);

        var status = await ctx.Gateway.GetStatusAsync();

        Assert.Empty(status.Unstaged);
        Assert.Equal([fixture.Value.LosingPath], status.HiddenCaseCollisions);
        Assert.Contains(spy.Requests, IsWholeIndexListing);
    }

    [Fact]
    public async Task Staging_in_a_repo_without_collisions_lists_the_index_at_most_once()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("no-collision-stage", r =>
            r.WriteFile("file.txt", "v1\n").WriteFile("other.txt", "v1\n").Stage().Commit("init"));

        await GitAsync(repo.Path, "config", "core.ignorecase", "true");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "file.txt"), "v2\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "other.txt"), "v2\n");

        var spy = new InterceptingGitCliService(_host.Cli);
        var ctx = _host.Open(repo.Path, spy);

        await ctx.Gateway.GetStatusAsync();
        await ctx.Gateway.StageAsync(["file.txt"]);
        await ctx.Gateway.GetStatusAsync();
        await ctx.Gateway.StageAsync(["other.txt"]);

        Assert.True(spy.Requests.Count(IsWholeIndexListing) <= 1);
    }
}
