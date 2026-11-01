namespace Historia.Tests;

/// <summary>
/// Verifies working tree staging, discard, unstage, and commit operations through GitGateway.
/// </summary>
[Trait("tier", "git")]
public sealed class GitGatewayWorkingTreeTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Stage_then_commit_advances_head()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gw-stage-seed", r =>
        {
            r.WriteFile("a", "1").Stage().Commit("first");
            r.WriteFile("b", "2");
        });

        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.StageAsync(["b"]);
        var id = await ctx.Gateway.CommitAsync(new CommitRequest
        {
            Message = "second",
            AuthorName = "Alice",
            AuthorEmail = "alice@example.com",
        });

        using var git = new Repository(repo.Path);
        Assert.Equal(id, git.Head.Tip!.Id.ToGitObjectId());
        Assert.Equal("Alice", git.Head.Tip.Author.Name);
        Assert.Equal("alice@example.com", git.Head.Tip.Author.Email);
    }

    [Fact]
    public async Task DiscardWorkingTree_keeps_staged_changes()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gw-discard-keeps-staged", r =>
        {
            r.WriteFile("tracked.txt", "original\n").Stage().Commit("init");
            r.WriteFile("tracked.txt", "modified\n");
            r.WriteFile("sub/nested.txt", "orig nested\n").Stage("sub/nested.txt").Commit("nested");
            r.WriteFile("sub/nested.txt", "mod nested\n");
            r.WriteFile("rename_me.txt", "rename me\n").Stage("rename_me.txt").Commit("rename init");
            r.WriteFile("untracked.txt", "untracked\n");
            r.WriteFile("sub/untracked2.txt", "untracked2\n");
            r.WriteFile("staged_new.txt", "staged new\n").Stage("staged_new.txt");
        });
        File.Move(Path.Combine(repo.Path, "rename_me.txt"), Path.Combine(repo.Path, "renamed.txt"));

        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.DiscardAsync([
            "tracked.txt",
            "sub/nested.txt",
            "untracked.txt",
            "sub/untracked2.txt",
            "staged_new.txt",
            "renamed.txt",
            "rename_me.txt"
        ]);

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Single(status.Staged);
        Assert.Equal("staged_new.txt", status.Staged[0].Path);
        Assert.Empty(status.Unstaged);
        Assert.Empty(status.Untracked);
        Assert.True(File.Exists(Path.Combine(repo.Path, "staged_new.txt")));
        Assert.Equal("staged new\n", await File.ReadAllTextAsync(Path.Combine(repo.Path, "staged_new.txt")));
        Assert.False(File.Exists(Path.Combine(repo.Path, "sub/untracked2.txt")));
        Assert.False(File.Exists(Path.Combine(repo.Path, "renamed.txt")));
        Assert.True(File.Exists(Path.Combine(repo.Path, "rename_me.txt")));
        Assert.Equal("orig nested", (await File.ReadAllTextAsync(Path.Combine(repo.Path, "sub/nested.txt"))).Trim());
    }

    // 1500 paths of ~85 chars is five times the 25 000-character chunk size (and the Windows
    // argv limit); each file costs several milliseconds of filesystem work on a scanned disk,
    // so a larger count only slows the suite.
    private const int PathCount = 1500;

    [Fact]
    public async Task Stage_and_discard_paths_past_the_argv_limit_with_long_names()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gw-many-paths", r =>
        {
            r.WriteFile("init.txt", "root\n").Stage().Commit("root");
        });

        var dir = Path.Combine(repo.Path, "nested_folder_with_a_fairly_long_name");
        Directory.CreateDirectory(dir);

        var paths = new List<string>(PathCount);
        for (var i = 0; i < PathCount; i++)
        {
            var relativePath = $"nested_folder_with_a_fairly_long_name/test_file_item_with_long_name_{i:D5}.txt";
            paths.Add(relativePath);
            File.WriteAllText(Path.Combine(repo.Path, relativePath), "initial\n");
        }

        var ctx = _host.Open(repo.Path);

        // 1. Stage every path (argv length would exceed limit; stdin pathspec works)
        await ctx.Gateway.StageAsync(paths);
        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Equal(PathCount, status.Staged.Count);

        // 2. Commit them to HEAD
        await ctx.Gateway.CommitAsync(new CommitRequest
        {
            Message = "commit many files",
            AuthorName = "Dev",
            AuthorEmail = "dev@example.com",
        });

        // 3. Modify them in the working tree
        for (var i = 0; i < PathCount; i++)
        {
            File.WriteAllText(Path.Combine(repo.Path, paths[i]), "modified\n");
        }

        // 4. Discard every path (exercises ls-files / diff chunking over argv limit and restore from index)
        await ctx.Gateway.DiscardAsync(paths);
        var statusAfter = await ctx.Gateway.GetStatusAsync();
        Assert.False(statusAfter.IsDirty);
    }

    [Fact]
    public async Task Stage_with_ignored_path_in_set_succeeds_and_stages_non_ignored()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gw-ignored-seed", r =>
        {
            r.WriteFile(".gitignore", "*.log\n").Stage().Commit("ignore rules");
        });

        await File.WriteAllTextAsync(Path.Combine(repo.Path, "regular.txt"), "regular content\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "ignored.log"), "debug log\n");

        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.StageAsync(["regular.txt", "ignored.log"]);

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Contains(status.Staged, s => s.Path == "regular.txt");
        Assert.DoesNotContain(status.Staged, s => s.Path == "ignored.log");
    }

    [Fact]
    public async Task Stage_with_path_deleted_since_status_skips_deleted_and_stages_others()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gw-deleted-seed", r =>
        {
            r.WriteFile("base.txt", "base\n").Stage().Commit("init");
        });

        await File.WriteAllTextAsync(Path.Combine(repo.Path, "remain.txt"), "remain content\n");
        var deletedPath = "deleted_before_stage.txt";

        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.StageAsync(["remain.txt", deletedPath]);

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Contains(status.Staged, s => s.Path == "remain.txt");
    }

    [Fact]
    public async Task Commit_with_core_hooks_path_runs_hook()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gw-hooks-seed", r =>
        {
            r.WriteFile("a.txt", "initial\n").Stage().Commit("init");
        });

        var hooksDir = Path.Combine(repo.Path, ".custom-hooks");
        Directory.CreateDirectory(hooksDir);
        var preCommitHook = Path.Combine(hooksDir, "pre-commit");
        await File.WriteAllTextAsync(preCommitHook, "#!/bin/sh\necho \"pre-commit hook declined\" >&2\nexit 1\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(preCommitHook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "core.hooksPath", hooksDir],
            WorkingDirectory = repo.Path,
        });

        await File.WriteAllTextAsync(Path.Combine(repo.Path, "a.txt"), "modified\n");
        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.StageAsync(["a.txt"]);

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(async () =>
        {
            await ctx.Gateway.CommitAsync(new CommitRequest
            {
                Message = "test hook failure",
                AuthorName = "Bob",
                AuthorEmail = "bob@example.com",
            });
        });

        Assert.Equal(GitErrorKind.HookFailed, ex.Kind);
    }

    [Fact]
    public async Task Commit_with_a_hook_whose_interpreter_is_missing_says_it_could_not_start()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var repo = TestRepositoryBuilder.FromTemplate("gw-hooks-seed", r =>
        {
            r.WriteFile("a.txt", "initial\n").Stage().Commit("init");
        });

        var hooks = Path.Combine(repo.Path, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        await File.WriteAllTextAsync(Path.Combine(hooks, "pre-commit"), "#!/no/such/interpreter\nexit 0\n");

        await File.WriteAllTextAsync(Path.Combine(repo.Path, "a.txt"), "modified\n");
        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.StageAsync(["a.txt"]);

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(async () =>
        {
            await ctx.Gateway.CommitAsync(new CommitRequest
            {
                Message = "test hook interpreter",
                AuthorName = "Bob",
                AuthorEmail = "bob@example.com",
            });
        });

        Assert.Equal(GitErrorKind.HookFailed, ex.Kind);
        Assert.Contains("could not start", ex.Message);
    }

    [Fact]
    public async Task Signed_commit_args_unchanged()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gw-signed-seed", r =>
        {
            r.WriteFile("a.txt", "initial\n").Stage().Commit("init");
        });

        var spy = new InterceptingGitCliService(_host.Cli)
        {
            Intercept = req =>
            {
                if (req.Arguments.Contains("commit"))
                {
                    return new GitCliResult(0, "[main 1234567] dummy", "", GitErrorKind.None, TimeSpan.Zero);
                }
                if (req.Arguments.Contains("rev-parse") && req.Arguments.Contains("HEAD"))
                {
                    return new GitCliResult(0, "1111222233334444555566667777888899990000\n", "", GitErrorKind.None, TimeSpan.Zero);
                }
                return null;
            }
        };

        var ctx = _host.Open(repo.Path, spy);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "a.txt"), "modified\n");
        await ctx.Gateway.StageAsync(["a.txt"]);

        var id = await ctx.Gateway.CommitAsync(new CommitRequest
        {
            Message = "signed commit",
            AuthorName = "Signer",
            AuthorEmail = "signer@example.com",
            CommitterName = "Committer",
            CommitterEmail = "committer@example.com",
            Sign = true,
            AmendPreviousCommit = true,
            AllowEmptyCommit = true,
        });

        Assert.Equal("1111222233334444555566667777888899990000", id.ToString());

        var commitReq = spy.Requests.FirstOrDefault(r => r.Arguments.Contains("commit"));
        Assert.NotNull(commitReq);
        Assert.Contains("-S", commitReq.Arguments);
        Assert.Contains("--amend", commitReq.Arguments);
        Assert.Contains("--allow-empty", commitReq.Arguments);
        Assert.Contains("--author", commitReq.Arguments);
        Assert.Contains("Signer <signer@example.com>", commitReq.Arguments);
        Assert.Equal("signed commit", commitReq.StandardInput);
        Assert.Equal("Committer", commitReq.ExtraEnvironment?["GIT_COMMITTER_NAME"]);
        Assert.Equal("committer@example.com", commitReq.ExtraEnvironment?["GIT_COMMITTER_EMAIL"]);
    }

    [Theory]
    [InlineData("ls-files", GitErrorKind.TimedOut, -1)]
    [InlineData("ls-files", GitErrorKind.Unknown, 128)]
    [InlineData("diff", GitErrorKind.TimedOut, -1)]
    [InlineData("config", GitErrorKind.Cancelled, -1)]
    [InlineData("config", GitErrorKind.Unknown, 128)]
    public async Task Discard_stops_without_deleting_when_a_probe_fails(string failingCommand, GitErrorKind kind, int exitCode)
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gw-discard-probe-seed", r =>
            r.WriteFile("tracked.txt", "original\n").Stage().Commit("init"));
        var trackedPath = Path.Combine(repo.Path, "tracked.txt");
        await File.WriteAllTextAsync(trackedPath, "modified\n");

        var armed = false;
        var spy = new InterceptingGitCliService(_host.Cli)
        {
            Intercept = req => armed && req.Arguments.Contains(failingCommand)
                ? new GitCliResult(exitCode, "", "", kind, TimeSpan.Zero)
                : null,
        };
        var ctx = _host.Open(repo.Path, spy);
        armed = true;

        await Assert.ThrowsAsync<HistoriaGitException>(() => ctx.Gateway.DiscardAsync(["tracked.txt"]));

        Assert.True(File.Exists(trackedPath));
        Assert.Equal("modified\n", await File.ReadAllTextAsync(trackedPath));
        Assert.DoesNotContain(spy.Requests, r => r.Arguments.Contains("rm") || r.Arguments.Contains("restore"));
    }

    [Fact]
    public async Task Work_tree_writes_have_no_wall_clock_timeout()
    {
        // A checkout/commit/merge/reset killed by the 30 s default leaves a half-updated tree
        // and a stale index.lock.
        using var repo = TestRepositoryBuilder.FromTemplate("gw-timeout-seed", r =>
            r.WriteFile("a.txt", "1\n").Stage().Commit("init"));
        var spy = new InterceptingGitCliService(_host.Cli);
        var ctx = _host.Open(repo.Path, spy);

        await ctx.Gateway.CreateBranchAsync("side", "HEAD", checkout: true);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "a.txt"), "2\n");
        await ctx.Gateway.StageAsync(["a.txt"]);
        await ctx.Gateway.CommitAsync(new CommitRequest { Message = "two", AuthorName = "A", AuthorEmail = "a@example.com" });
        await ctx.Gateway.CheckoutAsync("main", new GitCheckoutOptions());
        await ctx.Gateway.MergeAsync("side", noFastForward: false);
        await ctx.Gateway.ResetAsync("HEAD~1", GitResetMode.Hard);

        foreach (var verb in new[] { "checkout", "commit", "merge", "reset" })
        {
            var requests = spy.Requests.Where(r => r.Arguments.Contains(verb)).ToList();
            Assert.NotEmpty(requests);
            Assert.All(requests, r => Assert.Equal(Timeout.InfiniteTimeSpan, r.Timeout));
        }
    }
}
