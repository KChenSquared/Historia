namespace Historia.Tests;

/// <summary>The stage and unstage paths never drop a selected deletion silently, retry an index
/// that another git process holds for a moment, and never remove <c>index.lock</c> themselves.</summary>
[Trait("tier", "git")]
public sealed class GitGatewayStageSafetyTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static string LockPath(TestRepositoryBuilder repo) => Path.Combine(repo.Path, ".git", "index.lock");

    private static bool IsLsFiles(GitCliRequest request) => request.Arguments[0] == "ls-files";

    private static int Count(InterceptingGitCliService cli, string subcommand) =>
        cli.Requests.Count(r => r.Arguments[0] == subcommand);

    /// <summary>Holds <c>index.lock</c> for <paramref name="hold"/>, as a concurrent git process would.
    /// Creation completes before this returns; the lock is removed by the returned task.</summary>
    private static Task HoldIndexLock(TestRepositoryBuilder repo, TimeSpan hold)
    {
        File.WriteAllText(LockPath(repo), "");
        return Task.Run(async () =>
        {
            await Task.Delay(hold);
            File.Delete(LockPath(repo));
        });
    }

    [Fact]
    public async Task A_failing_deletion_probe_throws_instead_of_staging_a_subset()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("stage-safety-probe", r =>
            r.WriteFile("gone.txt", "1\n").WriteFile("edit.txt", "1\n").Stage().Commit("init"));
        File.Delete(Path.Combine(repo.Path, "gone.txt"));
        File.WriteAllText(Path.Combine(repo.Path, "edit.txt"), "2\n");

        var cli = new InterceptingGitCliService(_host.Cli)
        {
            Intercept = r => IsLsFiles(r) ? InterceptingGitCliService.Failure(GitErrorKind.Unknown) : null,
        };
        var ctx = _host.Open(repo.Path, cli);

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(
            () => ctx.Gateway.StageAsync(["gone.txt", "edit.txt"]));

        Assert.Equal(GitErrorKind.Unknown, ex.Kind);
        Assert.Equal(0, Count(cli, "add"));
        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Empty(status.Staged);
    }

    [Fact]
    public async Task A_timed_out_deletion_probe_throws_a_timeout_error()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("stage-safety-timeout", r =>
            r.WriteFile("gone.txt", "1\n").Stage().Commit("init"));
        File.Delete(Path.Combine(repo.Path, "gone.txt"));

        var cli = new InterceptingGitCliService(_host.Cli)
        {
            Intercept = r => IsLsFiles(r) ? InterceptingGitCliService.Failure(GitErrorKind.TimedOut, exitCode: -1) : null,
        };
        var ctx = _host.Open(repo.Path, cli);

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(() => ctx.Gateway.StageAsync(["gone.txt"]));

        Assert.Equal(GitErrorKind.TimedOut, ex.Kind);
        Assert.Equal(0, Count(cli, "add"));
    }

    [Fact]
    public async Task A_cancelled_deletion_probe_surfaces_as_cancellation()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("stage-safety-cancel", r =>
            r.WriteFile("gone.txt", "1\n").Stage().Commit("init"));
        File.Delete(Path.Combine(repo.Path, "gone.txt"));

        var cli = new InterceptingGitCliService(_host.Cli)
        {
            Intercept = r => IsLsFiles(r) ? InterceptingGitCliService.Failure(GitErrorKind.Cancelled, exitCode: -1) : null,
        };
        var ctx = _host.Open(repo.Path, cli);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ctx.Gateway.StageAsync(["gone.txt"]));
        Assert.Equal(0, Count(cli, "add"));
    }

    [Fact]
    public async Task The_deletion_probe_and_the_index_writers_have_no_wall_clock()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("stage-safety-timeouts", r =>
            r.WriteFile("gone.txt", "1\n").WriteFile("edit.txt", "1\n").Stage().Commit("init"));
        File.Delete(Path.Combine(repo.Path, "gone.txt"));
        File.WriteAllText(Path.Combine(repo.Path, "edit.txt"), "2\n");

        var cli = new InterceptingGitCliService(_host.Cli);
        var ctx = _host.Open(repo.Path, cli);

        await ctx.Gateway.StageAsync(["gone.txt", "edit.txt"]);
        await ctx.Gateway.UnstageAsync(["edit.txt"]);

        // Other ls-files calls (the case-collision probe) are not part of this path.
        var probe = Assert.Single(cli.Requests, r => IsLsFiles(r) && r.Arguments.Contains("gone.txt"));
        var writers = cli.Requests.Where(r => r.Arguments[0] is "add" or "restore").ToList();
        Assert.Equal(2, writers.Count);
        Assert.Equal(Timeout.InfiniteTimeSpan, probe.Timeout);
        Assert.All(writers, w => Assert.Equal(Timeout.InfiniteTimeSpan, w.Timeout));
    }

    [Fact]
    public async Task Staging_only_deleted_files_stages_the_deletions()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("stage-safety-all-deleted", r =>
            r.WriteFile("a.txt", "a\n").WriteFile("dir/b.txt", "b\n").WriteFile("keep.txt", "k\n").Stage().Commit("init"));
        File.Delete(Path.Combine(repo.Path, "a.txt"));
        File.Delete(Path.Combine(repo.Path, "dir", "b.txt"));

        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.StageAsync(["a.txt", "dir/b.txt"]);

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Equal(["a.txt", "dir/b.txt"], status.Staged.Select(f => f.Path).Order(StringComparer.Ordinal).ToArray());
        Assert.All(status.Staged, f => Assert.Equal(FileChangeKind.Deleted, f.Kind));
        Assert.Empty(status.Unstaged);
    }

    [Fact]
    public async Task Staging_paths_that_exist_nowhere_returns_normally()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("stage-safety-vanished", r =>
            r.WriteFile("keep.txt", "k\n").Stage().Commit("init"));

        var cli = new InterceptingGitCliService(_host.Cli);
        var ctx = _host.Open(repo.Path, cli);

        await ctx.Gateway.StageAsync(["never-existed.txt"]);

        Assert.Equal(0, Count(cli, "add"));
        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Empty(status.Staged);
    }

    [Fact]
    public async Task A_held_index_lock_that_clears_lets_the_stage_succeed()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("stage-safety-held", r =>
            r.WriteFile("keep.txt", "k\n").Stage().Commit("init"));
        File.WriteAllText(Path.Combine(repo.Path, "new.txt"), "n\n");

        var cli = new InterceptingGitCliService(_host.Cli);
        var ctx = _host.Open(repo.Path, cli);

        var release = HoldIndexLock(repo, TimeSpan.FromMilliseconds(600));
        try
        {
            await ctx.Gateway.StageAsync(["new.txt"]);
        }
        finally
        {
            await release;
        }

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Equal(["new.txt"], status.Staged.Select(f => f.Path).ToArray());
        Assert.True(Count(cli, "add") >= 1);
    }

    [Fact]
    public async Task A_held_index_lock_that_clears_lets_the_unstage_succeed()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("stage-safety-held-unstage", r =>
            r.WriteFile("keep.txt", "k\n").Stage().Commit("init"));
        File.WriteAllText(Path.Combine(repo.Path, "new.txt"), "n\n");
        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.StageAsync(["new.txt"]);

        var release = HoldIndexLock(repo, TimeSpan.FromMilliseconds(600));
        try
        {
            await ctx.Gateway.UnstageAsync(["new.txt"]);
        }
        finally
        {
            await release;
        }

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Empty(status.Staged);
        Assert.Equal(["new.txt"], status.Untracked.Select(f => f.Path).ToArray());
    }

    [Fact]
    public async Task A_lock_that_persists_surfaces_LockedIndex_after_four_retries_and_stays_on_disk()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("stage-safety-persistent", r =>
            r.WriteFile("keep.txt", "k\n").Stage().Commit("init"));
        File.WriteAllText(Path.Combine(repo.Path, "new.txt"), "n\n");

        var cli = new InterceptingGitCliService(_host.Cli);
        var ctx = _host.Open(repo.Path, cli);
        File.WriteAllText(LockPath(repo), "held by another process");
        try
        {
            var ex = await Assert.ThrowsAsync<HistoriaGitException>(() => ctx.Gateway.StageAsync(["new.txt"]));

            Assert.Equal(GitErrorKind.LockedIndex, ex.Kind);
            Assert.Equal(5, Count(cli, "add"));
            Assert.True(File.Exists(LockPath(repo)));
            Assert.Equal("held by another process", File.ReadAllText(LockPath(repo)));
        }
        finally
        {
            File.Delete(LockPath(repo));
        }
    }

    [Fact]
    public async Task A_lock_that_persists_surfaces_LockedIndex_on_unstage_and_stays_on_disk()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("stage-safety-persistent-unstage", r =>
            r.WriteFile("keep.txt", "k\n").Stage().Commit("init"));
        File.WriteAllText(Path.Combine(repo.Path, "new.txt"), "n\n");

        var cli = new InterceptingGitCliService(_host.Cli);
        var ctx = _host.Open(repo.Path, cli);
        await ctx.Gateway.StageAsync(["new.txt"]);
        File.WriteAllText(LockPath(repo), "held by another process");
        try
        {
            var ex = await Assert.ThrowsAsync<HistoriaGitException>(() => ctx.Gateway.UnstageAsync(["new.txt"]));

            Assert.Equal(GitErrorKind.LockedIndex, ex.Kind);
            Assert.Equal(5, Count(cli, "restore"));
            Assert.True(File.Exists(LockPath(repo)));
        }
        finally
        {
            File.Delete(LockPath(repo));
        }
    }

    [Fact]
    public async Task A_cancellation_during_the_retry_wait_stops_the_stage()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("stage-safety-cancel-retry", r =>
            r.WriteFile("keep.txt", "k\n").Stage().Commit("init"));
        File.WriteAllText(Path.Combine(repo.Path, "new.txt"), "n\n");

        using var cts = new CancellationTokenSource();
        var cli = new InterceptingGitCliService(_host.Cli)
        {
            // The first add reports a held lock and cancels the caller as it does, so the
            // cancellation is already pending when the gateway starts waiting to retry.
            Intercept = r =>
            {
                if (r.Arguments[0] != "add")
                    return null;
                cts.Cancel();
                return InterceptingGitCliService.Failure(GitErrorKind.LockedIndex);
            },
        };
        var ctx = _host.Open(repo.Path, cli);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ctx.Gateway.StageAsync(["new.txt"], cts.Token));
        Assert.Equal(1, Count(cli, "add"));
    }
}
