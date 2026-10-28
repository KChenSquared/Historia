namespace Historia.Tests;

/// <summary>A fetch or push opens a git-directory-only self-operation scope: ref writes under the
/// git directory publish nothing while it runs, working-tree edits still do, and one refresh runs
/// when the last scope ends. A full scope (pull) drops both.</summary>
[Trait("tier", "git")]
public sealed class NetworkWatcherScopeTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    public NetworkWatcherScopeTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("network-watcher-scope-seed", b =>
        {
            b.WriteFile("seed.txt", "x").Stage().Commit("seed");
        });
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(700);

    private static void RaiseRawEvent(Historia.Services.Watching.FileWatcherService watcher, string fullPath, bool isGitDir)
        => typeof(Historia.Services.Watching.FileWatcherService)
            .GetMethod("OnRawEvent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(watcher, [fullPath, isGitDir]);

    private static int StatusRuns(InterceptingGitCliService cli) => cli.Requests.Count(r => r.Arguments[0] == "status");

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.True(condition(), what);
    }

    /// <summary>Runs the first refresh, which loads the refs and may follow itself with a second
    /// status, so a test measures a steady-state refresh. Returns the status runs so far.</summary>
    private static async Task<int> SettleAsync(RepositoryContext ctx, InterceptingGitCliService cli)
    {
        ctx.BeginSelfOperation().Dispose();
        await WaitUntilAsync(() => StatusRuns(cli) > 0, "the priming refresh did not run");
        await Task.Delay(Quiet);
        return StatusRuns(cli);
    }

    private (RepositoryContext Ctx, InterceptingGitCliService Cli, FakeWorkspace Workspace, Historia.Services.Watching.FileWatcherService Watcher) Open()
    {
        var cli = new InterceptingGitCliService(_host.Cli);
        var ctx = _host.Open(_repo.Path, cli);
        var workspace = new FakeWorkspace();
        // No operating-system watchers: only the events a test injects reach the debounce.
        var watcher = new Historia.Services.Watching.FileWatcherService(ctx, ctx.State, _host.Settings, workspace, watchFilesystem: false);
        return (ctx, cli, workspace, watcher);
    }

    private string RemoteRef => Path.Combine(_repo.Path, ".git", "refs", "remotes", "origin", "main");

    private string WorkingFile => Path.Combine(_repo.Path, "src", "edited.txt");

    [Fact]
    public async Task A_git_directory_scope_keeps_working_tree_events_and_drops_git_directory_events()
    {
        var (ctx, _, ws, watcher) = Open();
        await using var ctxScope = ctx;
        await using var watcherScope = watcher;

        using (ctx.BeginSelfOperation(SelfOperationKind.GitDirectoryOnly))
        {
            Assert.False(ctx.IsSelfOperationInProgress);
            Assert.True(ctx.IsGitDirectoryOperationInProgress);

            RaiseRawEvent(watcher, RemoteRef, isGitDir: true);
            RaiseRawEvent(watcher, Path.Combine(ctx.GitDirectory, "FETCH_HEAD"), isGitDir: true);
            await Task.Delay(Quiet);
            Assert.Equal(0, ws.RefreshCalls);

            RaiseRawEvent(watcher, WorkingFile, isGitDir: false);
            await WaitUntilAsync(() => ws.RefreshCalls == 1, "the working-tree edit did not publish a refresh");
            Assert.False(ws.LastFull);

            await Task.Delay(Quiet);
            Assert.Equal(1, ws.RefreshCalls);
        }

        Assert.False(ctx.IsGitDirectoryOperationInProgress);
    }

    [Fact]
    public async Task An_index_event_inside_a_git_directory_scope_publishes_one_status_only_refresh()
    {
        var (ctx, _, ws, watcher) = Open();
        await using var ctxScope = ctx;
        await using var watcherScope = watcher;

        using (ctx.BeginSelfOperation(SelfOperationKind.GitDirectoryOnly))
        {
            RaiseRawEvent(watcher, RemoteRef, isGitDir: true);
            RaiseRawEvent(watcher, Path.Combine(ctx.GitDirectory, "index"), isGitDir: true);
            RaiseRawEvent(watcher, Path.Combine(ctx.GitDirectory, "index"), isGitDir: true);

            await WaitUntilAsync(() => ws.RefreshCalls == 1, "the index event did not publish a refresh");
            Assert.False(ws.LastFull);

            await Task.Delay(Quiet);
            Assert.Equal(1, ws.RefreshCalls);
        }
    }

    [Fact]
    public async Task A_full_scope_drops_working_tree_and_git_directory_events()
    {
        var (ctx, _, ws, watcher) = Open();
        await using var ctxScope = ctx;
        await using var watcherScope = watcher;

        using (ctx.BeginSelfOperation(SelfOperationKind.Full))
        {
            Assert.True(ctx.IsSelfOperationInProgress);
            Assert.False(ctx.IsGitDirectoryOperationInProgress);

            RaiseRawEvent(watcher, WorkingFile, isGitDir: false);
            RaiseRawEvent(watcher, RemoteRef, isGitDir: true);
            await Task.Delay(Quiet);

            Assert.Equal(0, ws.RefreshCalls);
        }
    }

    [Fact]
    public async Task The_default_scope_is_full()
    {
        var (ctx, _, _, watcher) = Open();
        await using var ctxScope = ctx;
        await using var watcherScope = watcher;

        using (ctx.BeginSelfOperation())
        {
            Assert.True(ctx.IsSelfOperationInProgress);
            Assert.False(ctx.IsGitDirectoryOperationInProgress);
        }

        Assert.False(ctx.IsSelfOperationInProgress);
    }

    [Fact]
    public async Task The_refresh_after_a_git_directory_scope_fires_once_when_it_ends()
    {
        var (ctx, cli, _, watcher) = Open();
        await using var ctxScope = ctx;
        await using var watcherScope = watcher;

        var before = await SettleAsync(ctx, cli);

        var scope = ctx.BeginSelfOperation(SelfOperationKind.GitDirectoryOnly);
        await Task.Delay(Quiet);
        Assert.Equal(before, StatusRuns(cli));

        scope.Dispose();
        await WaitUntilAsync(() => StatusRuns(cli) > before, "no refresh ran after the scope ended");
        await Task.Delay(Quiet);

        Assert.Equal(before + 1, StatusRuns(cli));
    }

    [Fact]
    public async Task The_refresh_waits_for_the_last_scope_of_either_kind()
    {
        var (ctx, cli, _, watcher) = Open();
        await using var ctxScope = ctx;
        await using var watcherScope = watcher;

        var before = await SettleAsync(ctx, cli);

        var gitDir = ctx.BeginSelfOperation(SelfOperationKind.GitDirectoryOnly);
        var full = ctx.BeginSelfOperation(SelfOperationKind.Full);

        gitDir.Dispose();
        await Task.Delay(Quiet);
        Assert.Equal(before, StatusRuns(cli));
        Assert.True(ctx.IsSelfOperationInProgress);

        full.Dispose();
        await WaitUntilAsync(() => StatusRuns(cli) > before, "no refresh ran after the last scope ended");
        await Task.Delay(Quiet);

        Assert.Equal(before + 1, StatusRuns(cli));
    }

    [Fact]
    public async Task Scopes_of_mixed_kinds_ending_on_parallel_threads_fire_one_refresh()
    {
        var (ctx, cli, _, watcher) = Open();
        await using var ctxScope = ctx;
        await using var watcherScope = watcher;

        var before = await SettleAsync(ctx, cli);

        const int count = 8;
        var scopes = Enumerable.Range(0, count)
            .Select(i => ctx.BeginSelfOperation(i % 2 == 0 ? SelfOperationKind.Full : SelfOperationKind.GitDirectoryOnly))
            .ToArray();
        using var barrier = new Barrier(count);
        var threads = scopes.Select(scope => Task.Factory.StartNew(() =>
        {
            barrier.SignalAndWait();
            scope.Dispose();
        }, TaskCreationOptions.LongRunning)).ToArray();
        await Task.WhenAll(threads);

        await WaitUntilAsync(() => StatusRuns(cli) > before, "no refresh ran after the last scope ended");
        await Task.Delay(Quiet);

        Assert.Equal(before + 1, StatusRuns(cli));
        Assert.False(ctx.IsSelfOperationInProgress);
        Assert.False(ctx.IsGitDirectoryOperationInProgress);
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
}
