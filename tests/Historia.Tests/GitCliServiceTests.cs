namespace Historia.Tests;

[Trait("tier", "git")]

public class GitCliServiceTests
{
    private static async Task<GitCliService> MakeAsync()
    {
        var locator = new GitExecutableLocator();
        await locator.ResolveAsync(null);
        return new GitCliService(locator);
    }

    [Fact]
    public async Task Arguments_with_spaces_and_unicode_are_passed_literally_not_shell_interpreted()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("git-cli-seed", r =>
            r.WriteFile("seed", "x").Stage().Commit("seed"));

        const string tricky = "a file — with spaces & 日本語.txt";
        repo.WriteFile(tricky, "content\n");

        var add = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["add", "--", tricky],
            WorkingDirectory = repo.Path,
        });
        Assert.True(add.IsSuccess, add.StandardError);

        var status = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["status", "--porcelain"],
            WorkingDirectory = repo.Path,
        });
        Assert.Contains(tricky, status.StandardOutput);
    }

    [Fact]
    public async Task A_leading_dash_path_after_double_dash_is_not_treated_as_an_option()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("git-cli-seed", r =>
            r.WriteFile("seed", "x").Stage().Commit("seed"));
        repo.WriteFile("-weird-name", "content\n");

        var add = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["add", "--", "-weird-name"],
            WorkingDirectory = repo.Path,
        });

        Assert.True(add.IsSuccess, add.StandardError);
    }

    [Fact]
    public async Task Checkout_of_a_missing_ref_is_classified_not_surfaced_raw()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("git-cli-seed", r =>
            r.WriteFile("seed", "x").Stage().Commit("seed"));

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["checkout", "no-such-branch-xyz"],
            WorkingDirectory = repo.Path,
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEqual(GitErrorKind.None, result.ErrorKind);
    }

    [Fact]
    public async Task Cancellation_before_start_reports_cancelled_without_starting_git()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("empty-repo", _ => { });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["log"],
            WorkingDirectory = repo.Path,
        }, cts.Token);

        Assert.Equal(GitErrorKind.Cancelled, result.ErrorKind);
    }

    [Fact]
    public async Task Cancellation_before_start_never_starts_a_write()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("empty-repo", _ => { });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // The alias leaves a marker if git runs at all; under a write scope a started process is
        // never killed, so a wrongly started one would have finished and written it.
        using var scope = GitWriteScope.Enter();
        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["-c", "alias.mark=!echo hi > marker", "mark"],
            WorkingDirectory = repo.Path,
        }, cts.Token);

        Assert.Equal(GitErrorKind.Cancelled, result.ErrorKind);
        Assert.False(File.Exists(Path.Combine(repo.Path, "marker")));
    }

    /// <summary>Runs <c>echo started; wait N seconds; echo finished</c> and cancels as soon as git has
    /// printed the first line, so the cancellation always lands after the process started. With
    /// <paramref name="shellBuiltinWait"/> the wait is a loop inside the shell itself instead of a
    /// <c>sleep</c> child: a tree kill of concurrent MSYS processes can end a <c>sleep</c> early on
    /// its own, after which the shell prints "finished" even though git was killed.</summary>
    private static async Task<GitCliResult> RunAndCancelAfterStartAsync(
        GitCliService cli, string workingDirectory, int waitSeconds, GitCliCancelPolicy policy,
        bool shellBuiltinWait = false)
    {
        using var cts = new CancellationTokenSource();
        var wait = shellBuiltinWait
            ? $"SECONDS=0; while [ $SECONDS -lt {waitSeconds} ]; do :; done"
            : $"sleep {waitSeconds}";
        var request = new GitCliRequest
        {
            Arguments = ["-c", $"alias.slow=!echo started; {wait}; echo finished", "slow"],
            WorkingDirectory = workingDirectory,
            CancelPolicy = policy,
            Timeout = TimeSpan.FromSeconds(60),
        };

        return await cli.RunAsync(request, progress: null, onStdoutLine: line =>
        {
            if (line == "started")
                cts.Cancel();
        }, cts.Token);
    }

    [Fact]
    public async Task Cancellation_after_start_kills_the_process_by_default()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("empty-repo", _ => { });

        var result = await RunAndCancelAfterStartAsync(
            cli, repo.Path, waitSeconds: 30, GitCliCancelPolicy.KillOnCancel, shellBuiltinWait: true);

        Assert.Equal(GitErrorKind.Cancelled, result.ErrorKind);
        Assert.True(result.Duration < TimeSpan.FromSeconds(20), $"took {result.Duration}");
        Assert.DoesNotContain("finished", result.StandardOutput);
    }

    [Fact]
    public async Task Cancellation_after_start_inside_a_write_scope_returns_the_real_exit_code()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("empty-repo", _ => { });

        using var scope = GitWriteScope.Enter();
        var result = await RunAndCancelAfterStartAsync(cli, repo.Path, waitSeconds: 2, GitCliCancelPolicy.KillOnCancel);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(GitErrorKind.None, result.ErrorKind);
        Assert.Contains("finished", result.StandardOutput);
    }

    [Fact]
    public async Task Cancellation_after_start_with_the_never_after_start_policy_returns_the_real_exit_code()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("empty-repo", _ => { });

        var result = await RunAndCancelAfterStartAsync(cli, repo.Path, waitSeconds: 2, GitCliCancelPolicy.NeverAfterStart);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("finished", result.StandardOutput);
    }

    [Fact]
    public async Task A_timeout_after_start_does_not_kill_a_write()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("empty-repo", _ => { });

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["-c", "alias.slow=!sleep 2; echo finished", "slow"],
            WorkingDirectory = repo.Path,
            CancelPolicy = GitCliCancelPolicy.NeverAfterStart,
            Timeout = TimeSpan.FromMilliseconds(300),
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(GitErrorKind.None, result.ErrorKind);
        Assert.Contains("finished", result.StandardOutput);
    }

    [Fact]
    public async Task A_child_process_holding_the_pipes_does_not_hold_the_call_hostage()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("empty-repo", _ => { });

        // The backgrounded sleep inherits git's stdout and stderr and outlives git by far.
        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["-c", "alias.bg=!sleep 25 & echo hi", "bg"],
            WorkingDirectory = repo.Path,
            Timeout = Timeout.InfiniteTimeSpan,
        }).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(result.IsSuccess, result.StandardError);
        Assert.Contains("hi", result.StandardOutput);
        Assert.True(result.Duration >= TimeSpan.FromSeconds(4), $"gave up after {result.Duration}");
        Assert.True(result.Duration < TimeSpan.FromSeconds(20), $"waited {result.Duration}");
    }

    [Fact]
    public async Task Output_waiting_in_the_pipe_is_captured_in_full_even_when_the_readers_start_late()
    {
        var locator = new GitExecutableLocator();
        await locator.ResolveAsync(null);
        var cli = new GitCliService(locator) { ReaderStartDelay = TimeSpan.FromSeconds(6) };

        using var repo = TestRepositoryBuilder.FromTemplate("git-cli-seed", r =>
            r.WriteFile("seed", "x").Stage().Commit("seed"));
        var content = new byte[1024 * 1024];
        Array.Fill(content, (byte)'a');
        File.WriteAllBytes(Path.Combine(repo.Path, "big.bin"), content);
        var blob = repo.Git("hash-object", "-w", "big.bin").Trim();

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["cat-file", "blob", blob],
            WorkingDirectory = repo.Path,
            RawStandardOutput = true,
        });

        Assert.True(result.IsSuccess, result.StandardError);
        Assert.Equal(content, result.StandardOutputBytes);
    }

    [Fact]
    public async Task Stderr_over_one_megabyte_keeps_the_tail()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("empty-repo", _ => { });

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["-c", "alias.noisy=!seq 1 40000 | sed 's/^/stderr-line-padded-so-the-total-passes-the-cap-/' >&2", "noisy"],
            WorkingDirectory = repo.Path,
        });

        Assert.True(result.IsSuccess);
        Assert.True(result.StandardError.Length <= 1024 * 1024 + 200, $"kept {result.StandardError.Length} chars");
        Assert.EndsWith("-40000" + Environment.NewLine, result.StandardError);
        Assert.DoesNotContain("passes-the-cap-1" + Environment.NewLine, result.StandardError);
    }

    [Fact]
    public async Task A_large_stdin_payload_is_fully_consumed()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("empty-repo", _ => { });

        var payload = string.Concat(Enumerable.Repeat("0123456789abcdef\n", 100 * 1024 / 17 + 1))[..(100 * 1024)];

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["hash-object", "--stdin"],
            WorkingDirectory = repo.Path,
            StandardInput = payload,
        });

        Assert.True(result.IsSuccess, result.StandardError);
        var bytes = Encoding.ASCII.GetBytes(payload);
        var expected = Convert.ToHexString(SHA1.HashData(
            [.. Encoding.ASCII.GetBytes($"blob {bytes.Length}\0"), .. bytes])).ToLowerInvariant();
        Assert.Equal(expected, result.StandardOutput.Trim());
    }

    [Fact]
    public async Task Raw_mode_decodes_stdout_text_on_demand_and_keeps_the_result()
    {
        var (cli, repo, blob) = await AwkwardBlobAsync();
        using var _ = repo;

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["cat-file", "blob", blob],
            WorkingDirectory = repo.Path,
            RawStandardOutput = true,
        });

        var text = result.StandardOutput;
        Assert.Equal("a\r\nb\rc\0d\n", text);
        Assert.Same(text, result.StandardOutput);
    }

    [Fact]
    public async Task A_throwing_line_callback_fails_the_call_instead_of_crashing_the_process()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("git-cli-seed", r =>
            r.WriteFile("seed", "x").Stage().Commit("seed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => cli.RunAsync(new GitCliRequest
        {
            Arguments = ["log", "--oneline"],
            WorkingDirectory = repo.Path,
        }, progress: null, onStdoutLine: _ => throw new InvalidOperationException("callback")));
    }

    /// <summary>A blob whose bytes line mode would rewrite: a CRLF, a lone CR and a NUL.</summary>
    private static readonly byte[] AwkwardBytes = "a\r\nb\rc\0d\n"u8.ToArray();

    private static async Task<(GitCliService Cli, TestRepositoryBuilder Repo, string Blob)> AwkwardBlobAsync()
    {
        var cli = await MakeAsync();
        var repo = TestRepositoryBuilder.FromTemplate("git-cli-seed", r =>
            r.WriteFile("seed", "x").Stage().Commit("seed"));
        File.WriteAllBytes(Path.Combine(repo.Path, "awkward.bin"), AwkwardBytes);
        var blob = repo.Git("hash-object", "-w", "awkward.bin").Trim();
        return (cli, repo, blob);
    }

    [Fact]
    public async Task Raw_stdout_keeps_crlf_lone_cr_and_nul()
    {
        var (cli, repo, blob) = await AwkwardBlobAsync();
        using var _ = repo;

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["cat-file", "blob", blob],
            WorkingDirectory = repo.Path,
            RawStandardOutput = true,
        });

        Assert.True(result.IsSuccess, result.StandardError);
        Assert.Equal(AwkwardBytes, result.StandardOutputBytes);
        Assert.Equal("a\r\nb\rc\0d\n", result.StandardOutput);
    }

    [Fact]
    public async Task Line_mode_output_is_unchanged()
    {
        var (cli, repo, blob) = await AwkwardBlobAsync();
        using var _ = repo;

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["cat-file", "blob", blob],
            WorkingDirectory = repo.Path,
        });

        Assert.True(result.IsSuccess, result.StandardError);
        Assert.Null(result.StandardOutputBytes);
        var nl = Environment.NewLine;
        Assert.Equal($"a{nl}b{nl}c\0d{nl}", result.StandardOutput);
    }

    [Fact]
    public async Task Raw_mode_rejects_a_line_callback()
    {
        var cli = await MakeAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => cli.RunAsync(new GitCliRequest
        {
            Arguments = ["version"],
            WorkingDirectory = Path.GetTempPath(),
            RawStandardOutput = true,
        }, progress: null, onStdoutLine: _ => { }));
    }

    [Fact]
    public async Task Raw_mode_honours_the_timeout()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("linear-3000", b => b.LinearHistory(3_000));

        var run = cli.RunAsync(new GitCliRequest
        {
            Arguments = ["log", "--all", "-p"],
            WorkingDirectory = repo.Path,
            RawStandardOutput = true,
            Timeout = TimeSpan.FromMilliseconds(1),
        });

        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30))));
        Assert.Equal(GitErrorKind.TimedOut, (await run).ErrorKind);
    }

    [Fact]
    public async Task RunAsync_before_resolution_waits_for_resolution_then_runs()
    {
        var locator = new DelayedLocator();
        var cli = new GitCliService(locator);

        using var repo = TestRepositoryBuilder.FromTemplate("git-cli-seed", r =>
            r.WriteFile("seed", "x").Stage().Commit("seed"));

        var runTask = cli.RunAsync(new GitCliRequest
        {
            Arguments = ["status", "--porcelain"],
            WorkingDirectory = repo.Path,
        });

        // The task should be waiting for locator resolution.
        Assert.False(runTask.IsCompleted);

        // Now resolve git using the real locator.
        var realLocator = new GitExecutableLocator();
        var resolved = await realLocator.ResolveAsync(null);
        locator.Complete(resolved);

        var result = await runTask;
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task RunAsync_fails_classified_when_resolution_finds_no_git()
    {
        var locator = new DelayedLocator();
        var cli = new GitCliService(locator);
        locator.Complete(GitExecutableInfo.NotFound);

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["status"],
            WorkingDirectory = Path.GetTempPath(),
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(GitErrorKind.Unknown, result.ErrorKind);
        Assert.Contains("not found", result.StandardError);
    }

    [Fact]
    public async Task Output_budget_stops_the_process_and_flags_it()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("linear-3000", b => b.LinearHistory(3_000));

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["log", "--all", "-p"],
            WorkingDirectory = repo.Path,
            MaxStandardOutputBytes = 1_000,
        });

        Assert.True(result.OutputTruncated);
        Assert.True(result.StandardOutput.Length <= 5_000);
        Assert.Equal(GitErrorKind.None, result.ErrorKind);
    }

    [Fact]
    public async Task Output_budget_stops_the_process_and_flags_it_in_raw_mode()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("linear-3000", b => b.LinearHistory(3_000));

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["log", "--all", "-p"],
            WorkingDirectory = repo.Path,
            RawStandardOutput = true,
            MaxStandardOutputBytes = 1_000,
        });

        Assert.True(result.OutputTruncated);
        Assert.NotNull(result.StandardOutputBytes);
        Assert.True(result.StandardOutputBytes.Length <= 1_000);
        Assert.Equal(GitErrorKind.None, result.ErrorKind);
    }

    [Fact]
    public async Task Output_budget_does_not_kill_a_process_that_must_never_be_killed()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("linear-3000", b => b.LinearHistory(3_000));

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["log", "--all", "-p"],
            WorkingDirectory = repo.Path,
            MaxStandardOutputBytes = 1_000,
            CancelPolicy = GitCliCancelPolicy.NeverAfterStart,
        });

        // The overflow is flagged and the rest is drained, but git is left to finish on its own.
        Assert.True(result.OutputTruncated);
        Assert.True(result.StandardOutput.Length <= 5_000);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void Results_with_the_same_content_are_equal_whether_or_not_stdout_was_decoded()
    {
        byte[] bytes = [(byte)'h', (byte)'i'];
        var decoded = new GitCliResult(0, bytes, "", GitErrorKind.None, TimeSpan.Zero);
        var undecoded = new GitCliResult(0, (byte[])bytes.Clone(), "", GitErrorKind.None, TimeSpan.Zero);
        Assert.Equal("hi", decoded.StandardOutput);

        Assert.Equal(decoded, undecoded);
        Assert.Equal(decoded.GetHashCode(), undecoded.GetHashCode());
        Assert.NotEqual(decoded, new GitCliResult(0, "hi", "", GitErrorKind.None, TimeSpan.Zero));
        Assert.NotEqual(decoded, undecoded with { OutputTruncated = true });
        Assert.DoesNotContain("hi", undecoded.ToString());
    }

    private sealed class DelayedLocator : IGitExecutableLocator
    {
        private readonly TaskCompletionSource<GitExecutableInfo> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private GitExecutableInfo _current = GitExecutableInfo.NotFound;

        public GitExecutableInfo Current => _current;

        public Task<GitExecutableInfo> ResolveAsync(string? userOverridePath, CancellationToken ct = default) => _tcs.Task;

        public Task<GitExecutableInfo> WhenResolvedAsync(CancellationToken ct = default) => _tcs.Task;

        public void Complete(GitExecutableInfo info)
        {
            _current = info;
            _tcs.TrySetResult(info);
        }
    }
}
