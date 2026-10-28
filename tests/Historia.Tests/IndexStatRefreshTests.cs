namespace Historia.Tests;

/// <summary>
/// <c>RefreshIndexStatAsync</c> only rewrites the cached stat data of files whose content did not
/// change. These tests run it against real repositories and check that staged blobs, unmerged
/// entries and real modifications — including the same-size, copied-mtime and racily-clean cases —
/// come out exactly as they went in, and that the state service starts it at most once a minute
/// after a slow status.
/// </summary>
[Trait("tier", "git")]
public sealed class IndexStatRefreshTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static readonly DateTime Past = new(2024, 3, 1, 10, 0, 0, DateTimeKind.Utc);

    private static TestRepositoryBuilder NewRepo()
    {
        var repo = TestRepositoryBuilder.Create();
        repo.Git("config", "core.autocrlf", "false");
        return repo;
    }

    private static void Write(TestRepositoryBuilder repo, string path, string content, DateTime mtimeUtc)
    {
        repo.WriteFile(path, content);
        File.SetLastWriteTimeUtc(Path.Combine(repo.Path, path), mtimeUtc);
    }

    private static string IndexPath(TestRepositoryBuilder repo) => Path.Combine(repo.Path, ".git", "index");

    private static string LockPath(TestRepositoryBuilder repo) => Path.Combine(repo.Path, ".git", "index.lock");

    private static bool IsRefresh(GitCliRequest request) =>
        request.Arguments[0] == "update-index" && request.Arguments.Contains("--refresh");

    private static int RefreshCount(InterceptingGitCliService cli) => cli.Requests.Count(IsRefresh);

    private (RepositoryContext Context, InterceptingGitCliService Cli) Open(TestRepositoryBuilder repo)
    {
        var cli = new InterceptingGitCliService(_host.Cli);
        return (_host.Open(repo.Path, cli), cli);
    }

    /// <summary>The mtime, in whole seconds, that the index records for <paramref name="path"/>.</summary>
    private static long IndexedMtimeSeconds(TestRepositoryBuilder repo, string path)
    {
        var output = repo.Git("ls-files", "--debug", "--", path);
        var match = Regex.Match(output, @"mtime:\s*(\d+):");
        Assert.True(match.Success, output);
        return long.Parse(match.Groups[1].Value);
    }

    private static long FileMtimeSeconds(TestRepositoryBuilder repo, string path) =>
        new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(repo.Path, path))).ToUnixTimeSeconds();

    private static bool IsModified(Historia.Models.RepositoryStatus status, string path) =>
        status.Unstaged.Any(e => e.Path == path && e.Kind == FileChangeKind.Modified);

    /// <summary>A repository with a merge stopped on a conflict in <c>f.txt</c> and a clean tracked
    /// <c>other.txt</c>.</summary>
    private static TestRepositoryBuilder NewConflictedRepo()
    {
        var repo = NewRepo();
        Write(repo, "f.txt", "base\n", Past);
        Write(repo, "other.txt", "other\n", Past);
        repo.Stage();
        repo.Commit("base");
        repo.Git("checkout", "-b", "side");
        repo.WriteFile("f.txt", "side\n");
        repo.Stage();
        repo.Commit("side");
        repo.Git("checkout", "main");
        repo.WriteFile("f.txt", "main\n");
        repo.Stage();
        repo.Commit("main");
        var merge = TestGit.Run(repo.Path, ["merge", "side"]);
        Assert.Equal(1, merge.ExitCode);
        return repo;
    }

    [Fact(Timeout = 60000)]
    public async Task Staged_blobs_and_the_reported_status_are_identical_after_a_refresh()
    {
        using var repo = NewRepo();
        Write(repo, "a.txt", "one\n", Past);
        Write(repo, "b.txt", "two\n", Past.AddMinutes(1));
        Write(repo, "c.txt", "three\n", Past.AddMinutes(2));
        repo.Stage();
        repo.Commit("init");

        repo.WriteFile("a.txt", "staged version\n");
        repo.Git("add", "a.txt");
        repo.WriteFile("a.txt", "working copy differs from the staged blob\n");
        repo.WriteFile("d.txt", "new staged file\n");
        repo.Git("add", "d.txt");
        repo.WriteFile("e.txt", "untracked\n");
        File.SetLastWriteTimeUtc(Path.Combine(repo.Path, "b.txt"), Past.AddHours(1));
        File.SetLastWriteTimeUtc(Path.Combine(repo.Path, "c.txt"), Past.AddHours(2));

        var (ctx, cli) = Open(repo);
        var blobsBefore = repo.Git("ls-files", "-s");
        var statusBefore = await ctx.Gateway.GetStatusAsync();
        Assert.NotEqual(FileMtimeSeconds(repo, "b.txt"), IndexedMtimeSeconds(repo, "b.txt"));

        await ctx.Gateway.RefreshIndexStatAsync();

        Assert.Equal(1, RefreshCount(cli));
        Assert.Equal(blobsBefore, repo.Git("ls-files", "-s"));
        Assert.True(RepositoryStatusEquivalence.Equivalent(statusBefore, await ctx.Gateway.GetStatusAsync()));
        Assert.Equal(FileMtimeSeconds(repo, "b.txt"), IndexedMtimeSeconds(repo, "b.txt"));
        Assert.Equal(FileMtimeSeconds(repo, "c.txt"), IndexedMtimeSeconds(repo, "c.txt"));
    }

    [Fact(Timeout = 60000)]
    public async Task A_same_size_edit_with_an_mtime_copied_from_another_file_stays_modified()
    {
        using var repo = NewRepo();
        Write(repo, "f.txt", "aaaa\n", Past);
        Write(repo, "u.txt", "uuuu\n", Past.AddHours(1));
        repo.Stage();
        repo.Commit("init");
        var blobsBefore = repo.Git("ls-files", "-s");

        repo.WriteFile("f.txt", "bbbb\n");
        File.SetLastWriteTimeUtc(
            Path.Combine(repo.Path, "f.txt"),
            File.GetLastWriteTimeUtc(Path.Combine(repo.Path, "u.txt")));

        var (ctx, _) = Open(repo);
        var before = await ctx.Gateway.GetStatusAsync();
        Assert.True(IsModified(before, "f.txt"));

        await ctx.Gateway.RefreshIndexStatAsync();

        var after = await ctx.Gateway.GetStatusAsync();
        Assert.True(IsModified(after, "f.txt"));
        Assert.False(IsModified(after, "u.txt"));
        Assert.Equal(blobsBefore, repo.Git("ls-files", "-s"));
        Assert.True(RepositoryStatusEquivalence.Equivalent(before, after));
    }

    [Fact(Timeout = 60000)]
    public async Task A_touched_but_unchanged_file_is_clean_afterwards_with_unchanged_status_output()
    {
        using var repo = NewRepo();
        Write(repo, "t.txt", "touched\n", Past);
        Write(repo, "keep.txt", "kept\n", Past.AddMinutes(1));
        repo.Stage();
        repo.Commit("init");
        File.SetLastWriteTimeUtc(Path.Combine(repo.Path, "t.txt"), Past.AddHours(3));

        var (ctx, _) = Open(repo);
        var before = await ctx.Gateway.GetStatusAsync();
        var plainBefore = repo.Git("--no-optional-locks", "status", "--porcelain=v2", "--branch");
        Assert.NotEqual(FileMtimeSeconds(repo, "t.txt"), IndexedMtimeSeconds(repo, "t.txt"));

        await ctx.Gateway.RefreshIndexStatAsync();

        var after = await ctx.Gateway.GetStatusAsync();
        Assert.Empty(after.Unstaged);
        Assert.Empty(after.Staged);
        Assert.True(RepositoryStatusEquivalence.Equivalent(before, after));
        Assert.Equal(plainBefore, repo.Git("--no-optional-locks", "status", "--porcelain=v2", "--branch"));
        Assert.Equal(FileMtimeSeconds(repo, "t.txt"), IndexedMtimeSeconds(repo, "t.txt"));
    }

    // An index entry is racily clean when its file's mtime is not older than the index file itself.
    // Git then compares content instead of trusting the stat data, and smudges the recorded size
    // whenever it writes such an entry (Documentation/technical/racy-git). The refresh rewrites
    // the index, so it must leave an edit made right after it detectable. An mtime a minute ahead
    // makes the entry racy whatever the clock does; offset 0 is the entry written in the same
    // second as the refresh. When a second boundary passes between choosing that mtime and git
    // writing the index, the entry is not racy and the scenario does not apply; it starts again,
    // and a loaded machine can need many starts.
    [Theory(Timeout = 180000)]
    [InlineData(0)]
    [InlineData(60)]
    public async Task An_edit_made_in_the_same_second_as_the_refresh_is_still_reported_modified(int offsetSeconds)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            using var repo = NewRepo();
            Write(repo, "f.txt", "aaaa\n", Past);
            Write(repo, "u.txt", "uuuu\n", Past.AddMinutes(1));
            repo.Stage();
            repo.Commit("init");
            var blobsBefore = repo.Git("ls-files", "-s");

            var racy = DateTime.UtcNow.AddSeconds(offsetSeconds);
            File.SetLastWriteTimeUtc(Path.Combine(repo.Path, "f.txt"), racy);

            var (ctx, _) = Open(repo);
            await ctx.Gateway.RefreshIndexStatAsync();

            var racySeconds = new DateTimeOffset(racy).ToUnixTimeSeconds();
            var indexSeconds = new DateTimeOffset(File.GetLastWriteTimeUtc(IndexPath(repo))).ToUnixTimeSeconds();
            if (offsetSeconds == 0 && indexSeconds != racySeconds)
                continue;

            Assert.Equal(FileMtimeSeconds(repo, "f.txt"), IndexedMtimeSeconds(repo, "f.txt"));
            Assert.Empty((await ctx.Gateway.GetStatusAsync()).Unstaged);

            repo.WriteFile("f.txt", "bbbb\n");
            File.SetLastWriteTimeUtc(Path.Combine(repo.Path, "f.txt"), racy);

            var status = await ctx.Gateway.GetStatusAsync();
            Assert.True(IsModified(status, "f.txt"));
            Assert.Equal(blobsBefore, repo.Git("ls-files", "-s"));
            return;
        }

        Assert.Fail("The refresh never wrote the index in the second of the edit.");
    }

    [Fact(Timeout = 60000)]
    public async Task An_edit_made_just_before_the_refresh_is_still_reported_modified()
    {
        using var repo = NewRepo();
        Write(repo, "f.txt", "aaaa\n", Past);
        Write(repo, "u.txt", "uuuu\n", Past.AddMinutes(1));
        repo.Stage();
        repo.Commit("init");
        File.SetLastWriteTimeUtc(Path.Combine(repo.Path, "u.txt"), Past.AddHours(2));

        var now = DateTime.UtcNow;
        repo.WriteFile("f.txt", "bbbb\n");
        File.SetLastWriteTimeUtc(Path.Combine(repo.Path, "f.txt"), now);

        var (ctx, _) = Open(repo);
        await ctx.Gateway.RefreshIndexStatAsync();

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.True(IsModified(status, "f.txt"));
        Assert.False(IsModified(status, "u.txt"));
        Assert.Equal(FileMtimeSeconds(repo, "u.txt"), IndexedMtimeSeconds(repo, "u.txt"));
    }

    [Fact(Timeout = 60000)]
    public async Task The_refresh_is_skipped_during_a_merge_conflict()
    {
        using var repo = NewConflictedRepo();
        File.SetLastWriteTimeUtc(Path.Combine(repo.Path, "other.txt"), Past.AddHours(5));
        var unmergedBefore = repo.Git("ls-files", "-u");
        var indexBefore = File.ReadAllBytes(IndexPath(repo));
        Assert.NotEmpty(unmergedBefore);

        var (ctx, cli) = Open(repo);
        await ctx.Gateway.RefreshIndexStatAsync();

        Assert.Equal(0, RefreshCount(cli));
        Assert.Equal(unmergedBefore, repo.Git("ls-files", "-u"));
        Assert.Equal(indexBefore, File.ReadAllBytes(IndexPath(repo)));
    }

    [Fact(Timeout = 60000)]
    public async Task Unmerged_entries_without_a_sequencer_marker_are_left_as_they_are()
    {
        using var repo = NewConflictedRepo();
        foreach (var marker in new[] { "MERGE_HEAD", "MERGE_MODE", "MERGE_MSG" })
            File.Delete(Path.Combine(repo.Path, ".git", marker));
        Assert.Equal(OperationState.None, RepositoryStateService.DetectOperationState(Path.Combine(repo.Path, ".git")));
        File.SetLastWriteTimeUtc(Path.Combine(repo.Path, "other.txt"), Past.AddHours(5));
        var unmergedBefore = repo.Git("ls-files", "-u");
        var conflictedFile = File.ReadAllText(Path.Combine(repo.Path, "f.txt"));

        var (ctx, cli) = Open(repo);
        await ctx.Gateway.RefreshIndexStatAsync();

        Assert.Equal(1, RefreshCount(cli));
        Assert.Equal(unmergedBefore, repo.Git("ls-files", "-u"));
        Assert.Equal(conflictedFile, File.ReadAllText(Path.Combine(repo.Path, "f.txt")));
        Assert.Equal(FileMtimeSeconds(repo, "other.txt"), IndexedMtimeSeconds(repo, "other.txt"));
        Assert.Single((await ctx.Gateway.GetStatusAsync()).Conflicted);
    }

    [Fact(Timeout = 60000)]
    public async Task An_existing_index_lock_skips_the_refresh_and_stays_untouched()
    {
        using var repo = NewRepo();
        Write(repo, "t.txt", "touched\n", Past);
        repo.Stage();
        repo.Commit("init");
        File.SetLastWriteTimeUtc(Path.Combine(repo.Path, "t.txt"), Past.AddHours(3));
        File.WriteAllText(LockPath(repo), "held by another process");
        var lockTime = File.GetLastWriteTimeUtc(LockPath(repo));
        var indexBefore = File.ReadAllBytes(IndexPath(repo));

        var (ctx, cli) = Open(repo);
        await ctx.Gateway.RefreshIndexStatAsync();

        Assert.Equal(0, RefreshCount(cli));
        Assert.True(File.Exists(LockPath(repo)));
        Assert.Equal("held by another process", File.ReadAllText(LockPath(repo)));
        Assert.Equal(lockTime, File.GetLastWriteTimeUtc(LockPath(repo)));
        Assert.Equal(indexBefore, File.ReadAllBytes(IndexPath(repo)));
    }

    [Fact(Timeout = 60000)]
    public async Task The_refresh_runs_at_normal_priority_without_a_wall_clock_inside_the_write_lock()
    {
        using var repo = NewRepo();
        Write(repo, "t.txt", "touched\n", Past);
        repo.Stage();
        repo.Commit("init");

        var (ctx, cli) = Open(repo);
        var duringRefresh = new List<(bool InWriteScope, bool SelfOperation)>();
        cli.Intercept = r =>
        {
            if (IsRefresh(r))
                duringRefresh.Add((GitWriteScope.IsActive, ctx.IsSelfOperationInProgress));
            return null;
        };

        await ctx.Gateway.RefreshIndexStatAsync();

        var request = Assert.Single(cli.Requests, IsRefresh);
        Assert.Equal(GitCliPriority.Normal, request.Priority);
        Assert.Equal(Timeout.InfiniteTimeSpan, request.Timeout);
        Assert.Equal(GitCliCancelPolicy.NeverAfterStart, request.CancelPolicy);
        Assert.Equal(["update-index", "-q", "--refresh"], request.Arguments);
        var observed = Assert.Single(duringRefresh);
        Assert.True(observed.InWriteScope);
        Assert.True(observed.SelfOperation);
    }

    [Fact(Timeout = 60000)]
    public async Task The_refresh_does_nothing_in_a_repository_without_a_work_tree()
    {
        using var source = NewRepo();
        Write(source, "t.txt", "touched\n", Past);
        source.Stage();
        source.Commit("init");
        var barePath = Path.Combine(source.Path, "..", Path.GetFileName(source.Path) + "-bare.git");
        source.Git("clone", "--bare", source.Path, barePath);

        try
        {
            var cli = new InterceptingGitCliService(_host.Cli);
            var ctx = _host.Open(barePath, cli);

            await ctx.Gateway.RefreshIndexStatAsync();

            Assert.Equal(0, RefreshCount(cli));
        }
        finally
        {
            if (Directory.Exists(barePath))
            {
                foreach (var file in Directory.EnumerateFiles(barePath, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(barePath, recursive: true);
            }
        }
    }

    // ── The state service's trigger ──

    /// <summary>A clock the test advances by hand, in milliseconds.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ms;
        public void Advance(long ms) => Interlocked.Add(ref _ms, ms);
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref _ms);
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(Interlocked.Read(ref _ms));
    }

    /// <summary>Forwards every call to the real gateway, taking a hook on status reads (to move the
    /// test clock) and on index stat refreshes (to count them or make them fail).</summary>
    public class GatewayProxy : DispatchProxy
    {
        public IGitGateway Inner { get; set; } = null!;
        public Action? OnStatusRead { get; set; }
        public Func<Task>? OnRefresh { get; set; }
        public int Refreshes => Volatile.Read(ref _refreshes);
        private int _refreshes;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IGitGateway.GetStatusAsync))
                OnStatusRead?.Invoke();
            if (targetMethod.Name == nameof(IGitGateway.RefreshIndexStatAsync))
            {
                Interlocked.Increment(ref _refreshes);
                if (OnRefresh is { } replaced)
                    return replaced();
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException!;
            }
        }
    }

    private (RepositoryStateService State, GatewayProxy Proxy, ManualTime Time, Action<long> SetStatusMs) NewService(TestRepositoryBuilder repo)
    {
        var (ctx, _) = Open(repo);
        var time = new ManualTime();
        long statusMs = 0;
        var proxy = DispatchProxy.Create<IGitGateway, GatewayProxy>();
        var handle = (GatewayProxy)(object)proxy;
        handle.Inner = ctx.Gateway;
        handle.OnStatusRead = () => time.Advance(Interlocked.Read(ref statusMs));
        var state = new RepositoryStateService(ctx, gateway: proxy, time: time);
        return (state, handle, time, ms => Interlocked.Exchange(ref statusMs, ms));
    }

    [Fact(Timeout = 60000)]
    public async Task The_first_status_of_a_tab_starts_no_refresh_however_slow_it_is()
    {
        using var repo = NewRepo();
        Write(repo, "t.txt", "touched\n", Past);
        repo.Stage();
        repo.Commit("init");
        var (state, proxy, _, setStatusMs) = NewService(repo);

        // A cold first run is slow whatever the index holds; only a slow run after it counts.
        setStatusMs(30_000);
        await state.RefreshStatusAsync();
        await state.IndexStatRefreshTask;
        Assert.Equal(0, proxy.Refreshes);

        await state.RefreshStatusAsync();
        await state.IndexStatRefreshTask;
        Assert.Equal(1, proxy.Refreshes);
    }

    [Fact(Timeout = 60000)]
    public async Task A_slow_status_starts_one_refresh_per_minute()
    {
        using var repo = NewRepo();
        Write(repo, "t.txt", "touched\n", Past);
        repo.Stage();
        repo.Commit("init");
        var (state, proxy, time, setStatusMs) = NewService(repo);

        // Not slow: at the threshold or under it.
        setStatusMs(1999);
        await state.RefreshStatusAsync();
        setStatusMs(2000);
        await state.RefreshStatusAsync();
        await state.IndexStatRefreshTask;
        Assert.Equal(0, proxy.Refreshes);

        // The first slow status starts a refresh.
        setStatusMs(2500);
        await state.RefreshStatusAsync();
        await state.IndexStatRefreshTask;
        Assert.Equal(1, proxy.Refreshes);

        // More slow statuses within the minute do not.
        time.Advance(10_000);
        await state.RefreshStatusAsync();
        time.Advance(40_000);
        await state.RefreshStatusAsync();
        await state.IndexStatRefreshTask;
        Assert.Equal(1, proxy.Refreshes);

        // Past the minute the next slow status does, once.
        time.Advance(20_000);
        await state.RefreshStatusAsync();
        await state.IndexStatRefreshTask;
        Assert.Equal(2, proxy.Refreshes);
        await state.RefreshStatusAsync();
        await state.IndexStatRefreshTask;
        Assert.Equal(2, proxy.Refreshes);
    }

    [Fact(Timeout = 60000)]
    public async Task A_slow_status_does_not_start_a_second_refresh_while_one_runs()
    {
        using var repo = NewRepo();
        Write(repo, "t.txt", "touched\n", Past);
        repo.Stage();
        repo.Commit("init");
        var (state, proxy, time, setStatusMs) = NewService(repo);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.OnRefresh = () => release.Task;

        await state.RefreshStatusAsync();
        setStatusMs(3000);
        await state.RefreshStatusAsync();
        time.Advance(120_000);
        await state.RefreshStatusAsync();

        Assert.False(state.IndexStatRefreshTask.IsCompleted);
        Assert.Equal(1, proxy.Refreshes);

        release.SetResult();
        await state.IndexStatRefreshTask;
    }

    [Fact(Timeout = 60000)]
    public async Task A_failing_refresh_is_swallowed_and_does_not_fail_the_status_refresh()
    {
        using var repo = NewRepo();
        Write(repo, "t.txt", "touched\n", Past);
        repo.Stage();
        repo.Commit("init");
        var (state, proxy, _, setStatusMs) = NewService(repo);
        proxy.OnRefresh = () => Task.FromException(new InvalidOperationException("boom"));

        await state.RefreshStatusAsync();
        setStatusMs(3000);
        await state.RefreshStatusAsync();
        await state.IndexStatRefreshTask;

        Assert.True(state.IndexStatRefreshTask.IsCompletedSuccessfully);
        Assert.Equal(1, proxy.Refreshes);
    }
}
