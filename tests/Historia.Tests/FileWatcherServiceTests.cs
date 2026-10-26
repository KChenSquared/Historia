namespace Historia.Tests;

/// <summary>the watcher's cheap pre-filter must not blanket-drop a tracked <c>bin</c>/
/// <c>obj</c> path — only skip it when it actually looks like build output (a project file sits
/// beside it). See <see cref="FileWatcherService.IsLikelyIgnored"/>.</summary>
[Trait("tier", "git")]
public sealed class FileWatcherServiceTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    public FileWatcherServiceTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("file-watcher-seed", b =>
        {
            b.WriteFile("seed.txt", "x").Stage().Commit("seed");
        });
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Tracked_bin_directory_with_no_adjacent_project_file_is_not_dropped()
    {
        // A common non-.NET layout: a repo-root bin/ of tracked scripts, nothing that would ever
        // build into it.
        _repo.WriteFile("bin/deploy", "#!/bin/sh\n").Stage().Commit("add bin/deploy");
        await using var ctx = _host.Open(_repo.Path);

        Assert.False(ctx.Watcher.IsLikelyIgnored("bin/deploy"));
        Assert.False(ctx.Watcher.IsLikelyIgnored("obj/fixtures/data.json"));
    }

    [Fact]
    public async Task Bin_beside_a_project_file_is_still_treated_as_build_output()
    {
        Directory.CreateDirectory(Path.Combine(_repo.Path, "src", "App", "bin", "Debug"));
        File.WriteAllText(Path.Combine(_repo.Path, "src", "App", "App.csproj"), "<Project />");
        await using var ctx = _host.Open(_repo.Path);

        Assert.True(ctx.Watcher.IsLikelyIgnored("src/App/bin/Debug/App.dll"));
        Assert.True(ctx.Watcher.IsLikelyIgnored("src/App/obj/Debug/App.dll"));
    }

    [Fact]
    public async Task Vendor_and_tooling_directories_are_always_ignored()
    {
        await using var ctx = _host.Open(_repo.Path);

        Assert.True(ctx.Watcher.IsLikelyIgnored("node_modules/left-pad/index.js"));
        Assert.True(ctx.Watcher.IsLikelyIgnored(".vs/state.json"));
        Assert.True(ctx.Watcher.IsLikelyIgnored(".git/foo")); // defence in depth; OnRawEvent routes .git elsewhere anyway
    }

    [Fact]
    public async Task Ordinary_paths_are_never_ignored()
    {
        await using var ctx = _host.Open(_repo.Path);

        Assert.False(ctx.Watcher.IsLikelyIgnored("src/App/Program.cs"));
        Assert.False(ctx.Watcher.IsLikelyIgnored("README.md"));
    }

    [Theory]
    [InlineData(".gitignore")]
    [InlineData(".gitattributes")]
    [InlineData(".github/workflows/ci.yml")]
    public async Task An_edit_beside_the_git_directory_publishes_a_status_refresh(string relPath)
    {
        await using var ctx = _host.Open(_repo.Path);
        var fakeWs = new FakeWorkspace();
        await using var watcher = new Historia.Services.Watching.FileWatcherService(ctx, ctx.State, _host.Settings, fakeWs);

        var onRawEvent = typeof(Historia.Services.Watching.FileWatcherService)
            .GetMethod("OnRawEvent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        onRawEvent.Invoke(watcher, [Path.Combine(_repo.Path, relPath.Replace('/', Path.DirectorySeparatorChar)), false]);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (fakeWs.RefreshCalls == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.Equal(1, fakeWs.RefreshCalls);
        Assert.False(fakeWs.LastFull);
    }

    private static int PendingPaths(Historia.Services.Watching.FileWatcherService watcher)
        => ((HashSet<string>)typeof(Historia.Services.Watching.FileWatcherService)
            .GetField("_pendingPaths", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(watcher)!).Count;

    [Fact]
    public async Task Ignored_and_uninteresting_git_paths_queue_no_refresh()
    {
        await using var ctx = _host.Open(_repo.Path);
        await using var watcher = new Historia.Services.Watching.FileWatcherService(ctx, ctx.State, _host.Settings, new FakeWorkspace());
        var onRawEvent = typeof(Historia.Services.Watching.FileWatcherService)
            .GetMethod("OnRawEvent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        onRawEvent.Invoke(watcher, [Path.Combine(_repo.Path, "node_modules", "x.js"), false]);
        onRawEvent.Invoke(watcher, [Path.Combine(ctx.GitDirectory, "objects", "ab", "cdef"), false]);
        Assert.Equal(0, PendingPaths(watcher));
    }

    [Theory]
    [InlineData("/r/.git", "/r/.git", true)]
    [InlineData("/r/.git/index", "/r/.git", true)]
    [InlineData(@"C:\r\.git\refs\heads\main", @"C:\r\.git", true)]
    [InlineData("/r/.gitignore", "/r/.git", false)]
    [InlineData(@"C:\r\.github\ci.yml", @"C:\r\.git", false)]
    [InlineData("/r/src/a.cs", "/r/.git", false)]
    [InlineData("/r/.git", "", false)]
    public void IsSameOrUnder_matches_only_the_directory_and_its_contents(string path, string dir, bool expected)
        => Assert.Equal(expected, Historia.Services.Watching.FileWatcherService.IsSameOrUnder(path, dir));

    [Fact]
    public async Task No_refresh_is_published_after_DisposeAsync()
    {
        await using var ctx = _host.Open(_repo.Path);
        var fakeWs = new FakeWorkspace();
        var watcher = new Historia.Services.Watching.FileWatcherService(ctx, ctx.State, _host.Settings, fakeWs);

        // Arm debounce with a working-tree change
        var onRawEvent = typeof(Historia.Services.Watching.FileWatcherService)
            .GetMethod("OnRawEvent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        onRawEvent.Invoke(watcher, [Path.Combine(_repo.Path, "new-file.txt"), false]);

        // Dispose immediately while debounce timer is armed
        await watcher.DisposeAsync();

        // Wait past debounce window (DebounceMs = 150ms)
        await Task.Delay(250);

        Assert.Equal(0, fakeWs.RefreshCalls);
    }

    [Fact]
    public async Task A_watcher_error_after_disposal_is_ignored()
    {
        await using var ctx = _host.Open(_repo.Path);
        var fakeWs = new FakeWorkspace();
        var watcher = new Historia.Services.Watching.FileWatcherService(ctx, ctx.State, _host.Settings, fakeWs);

        await watcher.DisposeAsync();

        var onError = typeof(Historia.Services.Watching.FileWatcherService)
            .GetMethod("OnWatcherError", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var errorEventArgs = new ErrorEventArgs(new Exception("buffer overflow"));

        // Must not throw
        onError.Invoke(watcher, [watcher, errorEventArgs]);

        await Task.Delay(250);
        Assert.Equal(0, fakeWs.RefreshCalls);
    }

    private static void RaiseWatcherError(Historia.Services.Watching.FileWatcherService watcher)
    {
        var onError = typeof(Historia.Services.Watching.FileWatcherService)
            .GetMethod("OnWatcherError", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        onError.Invoke(watcher, [watcher, new ErrorEventArgs(new InternalBufferOverflowException("too many changes"))]);
    }

    [Fact]
    public async Task A_burst_of_overflows_logs_one_warning_per_window_with_the_suppressed_count()
    {
        using var dir = new StateDir();
        // Flow-local: the process-wide log would also capture every test running in parallel.
        using var session = AppLog.StartSessionForTesting(dir.Path);
        await using var ctx = _host.Open(_repo.Path);
        await using var watcher = new Historia.Services.Watching.FileWatcherService(ctx, ctx.State, _host.Settings, new FakeWorkspace());

        watcher.OverflowLogWindow = TimeSpan.FromHours(1);
        RaiseWatcherError(watcher);
        RaiseWatcherError(watcher);
        RaiseWatcherError(watcher);

        watcher.OverflowLogWindow = TimeSpan.Zero;
        RaiseWatcherError(watcher);

        session.Dispose(); // close the session writer before reading the file back
        var lines = File.ReadAllLines(session.SessionPath)
            .Where(l => l.Contains("[watcher]") && l.Contains("overflowed"))
            .ToList();

        Assert.Equal(2, lines.Count);
        Assert.Contains("WARN", lines[0]);
        Assert.Contains("InternalBufferOverflowException", lines[0]);
        Assert.DoesNotContain("not logged", lines[0]);
        Assert.Contains("2 further overflow(s)", lines[1]);
    }

    [Fact]
    public async Task Entering_degraded_mode_logs_the_reason()
    {
        // Rewrite only the header's entry count: the watcher decides from the header alone.
        var index = Path.Combine(_repo.Path, ".git", "index");
        var bytes = File.ReadAllBytes(index);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 60_000);
        File.WriteAllBytes(index, bytes);

        using var dir = new StateDir();
        using var session = AppLog.StartSessionForTesting(dir.Path);
        await using var ctx = _host.Open(_repo.Path);

        Assert.True(ctx.Watcher.IsDegraded);
        session.Dispose();
        var line = Assert.Single(File.ReadAllLines(session.SessionPath), l => l.Contains("[watcher]") && l.Contains("degraded"));
        Assert.Contains("WARN", line);
        Assert.Contains(ctx.Watcher.DegradedReason!, line);
    }

    [Fact(Timeout = 30000)]
    public async Task Each_full_refresh_marks_its_trigger_in_the_perf_log()
    {
        // Flow-local: setting PerfLog.Enabled would turn on perf logging for every parallel test.
        PerfLog.OverrideEnabledForTesting(true);
        try
        {
            await using var ctx = _host.Open(_repo.Path);
            var fakeWs = new FakeWorkspace();
            // Built inside the override: the debounce timer's callback captures this flow.
            await using var watcher = new Historia.Services.Watching.FileWatcherService(ctx, ctx.State, _host.Settings, fakeWs);

            PerfLog.Flush(TimeSpan.FromSeconds(10));
            var startLineCount = File.Exists(PerfLog.LogPath) ? File.ReadAllLines(PerfLog.LogPath).Length : 0;

            var onRawEvent = typeof(Historia.Services.Watching.FileWatcherService)
                .GetMethod("OnRawEvent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            onRawEvent.Invoke(watcher, [Path.Combine(_repo.Path, ".git", "refs", "heads", "trigger-probe"), true]);
            await WaitForRefreshesAsync(fakeWs, 1);

            RaiseWatcherError(watcher);
            await WaitForRefreshesAsync(fakeWs, 2);

            Assert.True(PerfLog.Flush(TimeSpan.FromSeconds(10)));
            var lines = File.ReadAllLines(PerfLog.LogPath).Skip(startLineCount).ToList();
            Assert.Contains(lines, l => l.Contains("watcher full refresh") && l.Contains("trigger refs:refs/heads/trigger-probe"));
            Assert.Contains(lines, l => l.Contains("watcher full refresh") && l.Contains("trigger overflow"));
        }
        finally
        {
            PerfLog.OverrideEnabledForTesting(null);
        }
    }

    private static async Task WaitForRefreshesAsync(FakeWorkspace ws, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (ws.RefreshCalls < count && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.True(ws.RefreshCalls >= count, $"expected {count} refreshes, saw {ws.RefreshCalls}");
    }

    private sealed class FakeWorkspace : IWorkspaceService
    {
        public int RefreshCalls { get; private set; }
        public bool LastFull { get; private set; }
        public IReadOnlyList<RepositoryContext> OpenRepositories => [];
        public RepositoryContext? Focused => null;
        public Task<RepositoryContext> OpenAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RepositoryContext> InitAsync(string path, string? initialBranch, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RepositoryContext> CloneAsync(string url, string targetDirectory, Historia.Services.Workspace.CloneOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RepositoryContext> OpenSubmoduleAsync(RepositoryContext owner, SubmoduleInfo submodule, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CloseAsync(RepositoryContext context) => Task.CompletedTask;
        public void Focus(RepositoryContext context) { }
        public void EnsureInitialized(RepositoryContext context) { }
        public Task RequestBackgroundRefreshAsync(RepositoryContext context, bool full = false, CancellationToken ct = default)
        {
            LastFull = full;
            RefreshCalls++;
            return Task.CompletedTask;
        }
        public void Move(RepositoryContext context, int newIndex) { }
        public Task RestoreSessionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler<FocusedRepositoryChangedEventArgs>? FocusedRepositoryChanged { add { } remove { } }
        public event EventHandler<OpenRepositoriesChangedEventArgs>? OpenRepositoriesChanged { add { } remove { } }
    }

    [Fact]
    public void Tracked_file_count_comes_from_the_index_header()
    {
        _repo.WriteFile("two.txt", "2").WriteFile("three.txt", "3").Stage().Commit("more");

        Assert.Equal(3, Historia.Services.Watching.FileWatcherService.TrackedFileCount(Path.Combine(_repo.Path, ".git")));
    }

    [Fact]
    public void A_split_index_counts_the_shared_index_too()
    {
        for (var i = 0; i < 20; i++)
            _repo.WriteFile($"f{i}.txt", $"{i}");
        _repo.Stage().Commit("twenty");
        _repo.Git("update-index", "--split-index");
        _repo.WriteFile("f0.txt", "changed").Stage();

        var gitDir = Path.Combine(_repo.Path, ".git");
        Assert.NotEmpty(Directory.GetFiles(gitDir, "sharedindex.*"));
        Assert.Equal(21, Historia.Services.Watching.FileWatcherService.TrackedFileCount(gitDir));
    }

    [Fact]
    public void A_missing_or_corrupt_index_counts_as_empty()
    {
        var gitDir = TestScratch.NewDirectory("fake-git-dir-");
        Assert.Equal(0, Historia.Services.Watching.FileWatcherService.TrackedFileCount(gitDir));

        File.WriteAllBytes(Path.Combine(gitDir, "index"), [0x01, 0x02, 0x03]);
        Assert.Equal(0, Historia.Services.Watching.FileWatcherService.TrackedFileCount(gitDir));

        File.WriteAllBytes(Path.Combine(gitDir, "index"), [(byte)'N', (byte)'O', (byte)'P', (byte)'E', 0, 0, 0, 2, 0, 0, 0, 5]);
        Assert.Equal(0, Historia.Services.Watching.FileWatcherService.TrackedFileCount(gitDir));
    }

    [Fact]
    public async Task An_index_past_the_threshold_degrades_to_watching_git_only()
    {
        // Rewrite only the header's entry count: the watcher decides from the header alone.
        var index = Path.Combine(_repo.Path, ".git", "index");
        var bytes = File.ReadAllBytes(index);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 60_000);
        File.WriteAllBytes(index, bytes);

        await using var ctx = _host.Open(_repo.Path);

        Assert.True(ctx.Watcher.IsDegraded);
    }

    [Fact]
    public async Task A_small_index_watches_the_working_tree()
    {
        await using var ctx = _host.Open(_repo.Path);

        Assert.False(ctx.Watcher.IsDegraded);
    }

    [Fact]
    public async Task A_linked_worktree_reads_its_own_index()
    {
        var linked = Path.Combine(TestScratch.NewDirectory("watch-linked-"), "wt");
        _repo.Git("worktree", "add", "-q", "-b", "side", linked);

        await using var ctx = _host.Open(linked);

        Assert.EndsWith(Path.Combine("worktrees", "wt"), ctx.GitDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, Historia.Services.Watching.FileWatcherService.TrackedFileCount(ctx.GitDirectory));
        Assert.False(ctx.Watcher.IsDegraded);
    }
}
