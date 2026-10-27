namespace Historia.Tests;

public sealed class IndexCaseCollisionsTests
{
    [Fact]
    public void ParseGroups_groups_colliding_paths_and_leaves_singletons_out()
    {
        var output =
            "100644 1111111111111111111111111111111111111111 0\tFoo.txt\0" +
            "100644 2222222222222222222222222222222222222222 0\tfoo.txt\0" +
            "100644 3333333333333333333333333333333333333333 0\tbar.txt\0";

        var groups = IndexCaseCollisions.ParseGroups(output);

        Assert.Equal(2, groups.Count);
        Assert.False(groups.ContainsKey("bar.txt"));

        var fooPeers = groups["Foo.txt"];
        var singleFooPeer = Assert.Single(fooPeers);
        Assert.Equal("foo.txt", singleFooPeer.Path);
        Assert.Equal("2222222222222222222222222222222222222222", singleFooPeer.BlobId);

        var lowerFooPeers = groups["foo.txt"];
        var singleLowerFooPeer = Assert.Single(lowerFooPeers);
        Assert.Equal("Foo.txt", singleLowerFooPeer.Path);
        Assert.Equal("1111111111111111111111111111111111111111", singleLowerFooPeer.BlobId);
    }

    [Fact]
    public void ParseGroups_skips_non_zero_stages()
    {
        var output =
            "100644 1111111111111111111111111111111111111111 1\tFoo.txt\0" +
            "100644 2222222222222222222222222222222222222222 2\tfoo.txt\0" +
            "100644 3333333333333333333333333333333333333333 3\tFOO.txt\0";

        var groups = IndexCaseCollisions.ParseGroups(output);

        Assert.Empty(groups);
    }

    [Fact]
    public void ParseGroups_handles_paths_with_spaces_and_tabs()
    {
        var output =
            "100644 1111111111111111111111111111111111111111 0\tfolder name/foo\tbar.txt\0" +
            "100644 2222222222222222222222222222222222222222 0\tFOLDER NAME/FOO\tBAR.TXT\0";

        var groups = IndexCaseCollisions.ParseGroups(output);

        Assert.Equal(2, groups.Count);
        Assert.True(groups.ContainsKey("folder name/foo\tbar.txt"));
        Assert.True(groups.ContainsKey("FOLDER NAME/FOO\tBAR.TXT"));

        var peers = groups["folder name/foo\tbar.txt"];
        var peer = Assert.Single(peers);
        Assert.Equal("FOLDER NAME/FOO\tBAR.TXT", peer.Path);
        Assert.Equal("2222222222222222222222222222222222222222", peer.BlobId);
    }

    [Fact]
    public void ParseGroups_three_way_group_lists_other_two_as_peers_of_each_member()
    {
        var output =
            "100644 1111111111111111111111111111111111111111 0\tdir/a\0" +
            "100644 2222222222222222222222222222222222222222 0\tDIR/A\0" +
            "100644 3333333333333333333333333333333333333333 0\tDir/a\0";

        var groups = IndexCaseCollisions.ParseGroups(output);

        Assert.Equal(3, groups.Count);

        var peers1 = groups["dir/a"];
        Assert.Equal(2, peers1.Count);
        Assert.Contains(peers1, p => p.Path == "DIR/A" && p.BlobId == "2222222222222222222222222222222222222222");
        Assert.Contains(peers1, p => p.Path == "Dir/a" && p.BlobId == "3333333333333333333333333333333333333333");

        var peers2 = groups["DIR/A"];
        Assert.Equal(2, peers2.Count);
        Assert.Contains(peers2, p => p.Path == "dir/a" && p.BlobId == "1111111111111111111111111111111111111111");
        Assert.Contains(peers2, p => p.Path == "Dir/a" && p.BlobId == "3333333333333333333333333333333333333333");

        var peers3 = groups["Dir/a"];
        Assert.Equal(2, peers3.Count);
        Assert.Contains(peers3, p => p.Path == "dir/a" && p.BlobId == "1111111111111111111111111111111111111111");
        Assert.Contains(peers3, p => p.Path == "DIR/A" && p.BlobId == "2222222222222222222222222222222222222222");
    }

    private const string TwinListing =
        "100644 1111111111111111111111111111111111111111 0\tFoo.txt\0" +
        "100644 2222222222222222222222222222222222222222 0\tfoo.txt\0";

    private sealed class ScriptedCli(Func<GitCliRequest, CancellationToken, Task<GitCliResult>> handler) : IGitCliService
    {
        public ConcurrentQueue<GitCliRequest> Requests { get; } = new();
        public bool IsGitAvailable => true;

        public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            return handler(request, ct);
        }

        public Task<GitCliResult> RunAsync(
            GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default)
            => RunAsync(request, ct);
    }

    private static GitCliResult Ok(string stdout) => new(0, stdout, "", GitErrorKind.None, TimeSpan.Zero);

    private static (string WorkDir, string GitDir) NewRepoFolders()
    {
        var workDir = TestScratch.NewPath("case-collisions-");
        var gitDir = Path.Combine(workDir, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "index"), "index");
        File.WriteAllText(Path.Combine(workDir, "a.txt"), "a");
        return (workDir, gitDir);
    }

    private static IEnumerable<IReadOnlyList<string>> OneChunk(IReadOnlyList<string> specs) => [specs];

    [Fact]
    public async Task Staging_listing_does_not_wait_for_a_worktree_hash()
    {
        var (workDir, gitDir) = NewRepoFolders();
        try
        {
            var hashEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hashRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cli = new ScriptedCli(async (request, ct) =>
            {
                switch (request.Arguments[0])
                {
                    case "hash-object":
                        hashEntered.TrySetResult();
                        await hashRelease.Task.WaitAsync(ct);
                        return Ok("3333333333333333333333333333333333333333\n");
                    case "config":
                        return Ok("true\n");
                    default:
                        return Ok(TwinListing);
                }
            });
            var collisions = new IndexCaseCollisions(cli, gitDir, workDir);

            var hashing = collisions.HashWorktreeAsync(["a.txt"]);
            await hashEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var ignoreCase = await collisions.IgnoreCaseAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var groups = await collisions.GroupsForStagingAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(ignoreCase);
            Assert.Equal(2, groups.Count);
            Assert.False(hashing.IsCompleted);

            hashRelease.SetResult();
            var hashes = await hashing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("3333333333333333333333333333333333333333", hashes["a.txt"]);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task Worktree_hash_does_not_wait_for_a_staging_listing()
    {
        var (workDir, gitDir) = NewRepoFolders();
        try
        {
            var listEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var listRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cli = new ScriptedCli(async (request, ct) =>
            {
                if (request.Arguments[0] == "ls-files")
                {
                    listEntered.TrySetResult();
                    await listRelease.Task.WaitAsync(ct);
                    return Ok(TwinListing);
                }
                return Ok("3333333333333333333333333333333333333333\n");
            });
            var collisions = new IndexCaseCollisions(cli, gitDir, workDir);

            var listing = collisions.GroupsAsync();
            await listEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var hashes = await collisions.HashWorktreeAsync(["a.txt"]).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("3333333333333333333333333333333333333333", hashes["a.txt"]);
            Assert.False(listing.IsCompleted);

            listRelease.SetResult();
            Assert.Equal(2, (await listing.WaitAsync(TimeSpan.FromSeconds(10))).Count);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task Candidate_query_uses_literal_icase_pathspecs_and_is_cached()
    {
        var (workDir, gitDir) = NewRepoFolders();
        try
        {
            var cli = new ScriptedCli((_, _) => Task.FromResult(Ok(TwinListing)));
            var collisions = new IndexCaseCollisions(cli, gitDir, workDir);

            var first = await collisions.GroupsForCandidatesAsync(["Foo.txt", "dir/bar.txt"], OneChunk);
            var second = await collisions.GroupsForCandidatesAsync(["Foo.txt", "dir/bar.txt"], OneChunk);

            var request = Assert.Single(cli.Requests);
            Assert.Equal(
                ["ls-files", "-s", "-z", "--", ":(literal,icase)Foo.txt", ":(literal,icase)dir/bar.txt"],
                request.Arguments);
            Assert.Equal(2, first.Count);
            Assert.Same(first, second);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task Candidate_query_merges_chunks_and_keeps_each_entry_once()
    {
        var (workDir, gitDir) = NewRepoFolders();
        try
        {
            var cli = new ScriptedCli((_, _) => Task.FromResult(Ok(TwinListing)));
            var collisions = new IndexCaseCollisions(cli, gitDir, workDir);

            var groups = await collisions.GroupsForCandidatesAsync(
                ["Foo.txt", "foo.txt"], specs => specs.Select(spec => (IReadOnlyList<string>)[spec]));

            Assert.Equal(2, cli.Requests.Count);
            Assert.Equal(2, groups.Count);
            Assert.Single(groups["Foo.txt"]);
            Assert.Single(groups["foo.txt"]);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task Candidate_query_without_twins_returns_no_groups()
    {
        var (workDir, gitDir) = NewRepoFolders();
        try
        {
            var cli = new ScriptedCli((_, _) => Task.FromResult(Ok(
                "100644 1111111111111111111111111111111111111111 0\tbar.txt\0")));
            var collisions = new IndexCaseCollisions(cli, gitDir, workDir);

            Assert.Empty(await collisions.GroupsForCandidatesAsync(["bar.txt"], OneChunk));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task Candidate_query_falls_back_to_the_whole_index_when_it_fails()
    {
        var (workDir, gitDir) = NewRepoFolders();
        try
        {
            var cli = new ScriptedCli((request, _) => Task.FromResult(request.Arguments.Contains("--")
                ? new GitCliResult(128, "", "fatal: bad pathspec", GitErrorKind.Unknown, TimeSpan.Zero)
                : Ok(TwinListing)));
            var collisions = new IndexCaseCollisions(cli, gitDir, workDir);

            var groups = await collisions.GroupsForCandidatesAsync(["Foo.txt"], OneChunk);

            Assert.Equal(2, groups.Count);
            Assert.Equal(2, cli.Requests.Count);
            Assert.Equal(["ls-files", "-s", "-z"], cli.Requests.Last().Arguments);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task Many_candidates_use_one_whole_index_listing()
    {
        var (workDir, gitDir) = NewRepoFolders();
        try
        {
            var cli = new ScriptedCli((_, _) => Task.FromResult(Ok(TwinListing)));
            var collisions = new IndexCaseCollisions(cli, gitDir, workDir);
            var paths = Enumerable.Range(0, IndexCaseCollisions.MaxTargetedCandidates + 1).Select(i => $"f{i}.txt").ToList();

            await collisions.GroupsForCandidatesAsync(paths, OneChunk);

            var request = Assert.Single(cli.Requests);
            Assert.Equal(["ls-files", "-s", "-z"], request.Arguments);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task A_twin_found_by_the_candidate_query_discards_an_empty_staging_listing()
    {
        var (workDir, gitDir) = NewRepoFolders();
        try
        {
            var listing = "";
            var cli = new ScriptedCli((_, _) => Task.FromResult(Ok(listing)));
            var collisions = new IndexCaseCollisions(cli, gitDir, workDir);

            Assert.Empty(await collisions.GroupsForStagingAsync());
            var before = cli.Requests.Count;
            Assert.Empty(await collisions.GroupsForStagingAsync());
            Assert.Equal(before, cli.Requests.Count);

            listing = TwinListing;
            await collisions.GroupsForCandidatesAsync(["Foo.txt"], OneChunk);

            Assert.Equal(2, (await collisions.GroupsForStagingAsync()).Count);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
