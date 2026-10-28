namespace Historia.Tests;

/// <summary>A background fetch (auto-fetch) tries only the configured transport, never prompts,
/// gives up on a stalled connection and runs at background-network priority; a user-started
/// network command keeps the transport fallback and interactive authentication. A recording
/// <see cref="IGitCliService"/> stands in for git, so no remote is ever contacted.</summary>
[Trait("tier", "git")]
public sealed class GitGatewayNetworkTests : IAsyncDisposable
{
    private const string Unreachable = "ssh: connect to host example.invalid port 22: Connection timed out\nfatal: Could not read from remote repository.";
    private const string AuthFailed = "fatal: Authentication failed for 'https://example.invalid/o/r.git/'";

    private static readonly string[] BackgroundConfig =
    [
        "gc.auto=0", "maintenance.auto=false", "http.lowSpeedLimit=1000", "http.lowSpeedTime=60",
    ];

    private static readonly GitFetchOptions Background = new() { Background = true };
    private static readonly GitFetchOptions User = new();

    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _repos) r.Dispose();
        await _host.DisposeAsync();
    }

    private RepositoryContext Open(string remoteUrl) => _host.Open(NewRepository(remoteUrl, null).Path);

    private TestRepositoryBuilder NewRepository(string remoteUrl, string? insteadOfKey)
    {
        var name = "gateway-network-" + Math.Abs(StringComparer.Ordinal.GetHashCode(remoteUrl + insteadOfKey));
        var repo = TestRepositoryBuilder.FromTemplate(name, r =>
        {
            r.WriteFile("f.txt", "x").Stage().Commit("seed");
            r.Git("remote", "add", "origin", remoteUrl);
            if (insteadOfKey is not null)
                r.Git("config", insteadOfKey, "https://example.invalid/");
        });
        _repos.Add(repo);
        return repo;
    }

    private GitGateway Gateway(RepositoryContext ctx, IGitCliService cli, ISshHostResolver? hosts = null) => new(
        ctx,
        cli,
        _host.Provider.GetRequiredService<IRepoLayoutService>(),
        _host.Provider.GetRequiredService<ICredentialVaultService>(),
        hosts ?? new FakeHosts());

    private static GitCliResult Fail(string stderr) =>
        new(1, "", stderr, GitErrorClassifier.Classify(1, stderr), TimeSpan.Zero);

    private static GitCliResult Ok() => new(0, "", "", GitErrorKind.None, TimeSpan.Zero);

    /// <summary>The values of every <c>-c</c> option that precedes the git subcommand.</summary>
    private static List<string> ConfigOptions(GitCliRequest request)
    {
        var values = new List<string>();
        for (var i = 0; i < request.Arguments.Count - 1 && request.Arguments[i] == "-c"; i += 2)
            values.Add(request.Arguments[i + 1]);
        return values;
    }

    [Fact]
    public async Task A_background_fetch_over_ssh_makes_one_quiet_attempt_and_does_not_fall_back()
    {
        await using var ctx = Open("git@example.invalid:o/r.git");
        var cli = new RecordingCli(Fail(Unreachable), Ok());
        var hosts = new FakeHosts();

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(() => Gateway(ctx, cli, hosts).FetchAsync("origin", Background, null));

        var call = Assert.Single(cli.NetworkCalls);
        Assert.Equal(GitErrorKind.NetworkUnreachable, ex.Kind);
        Assert.Equal(0, hosts.Calls);
        Assert.Equal(GitCliPriority.BackgroundNetwork, call.Priority);
        Assert.Equal("never", call.ExtraEnvironment!["GCM_INTERACTIVE"]);

        var configs = ConfigOptions(call);
        foreach (var expected in BackgroundConfig)
            Assert.Contains(expected, configs);
        Assert.Contains("fetch", call.Arguments);
        Assert.DoesNotContain(call.Arguments, a => a.StartsWith("remote.origin.url=https", StringComparison.Ordinal));

        var ssh = call.ExtraEnvironment["GIT_SSH_COMMAND"];
        Assert.Contains("-o BatchMode=yes", ssh);
        Assert.Contains("-o ServerAliveInterval=15", ssh);
        Assert.Contains("-o ServerAliveCountMax=4", ssh);
        Assert.Contains("-o ConnectTimeout=20", ssh);
        Assert.DoesNotContain("StrictHostKeyChecking", ssh);
    }

    [Fact]
    public async Task A_background_fetch_over_https_makes_one_attempt_and_does_not_fall_back_to_ssh()
    {
        await using var ctx = Open("https://example.invalid/o/r.git");
        var cli = new RecordingCli(Fail(AuthFailed), Ok());

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(() => Gateway(ctx, cli).FetchAsync("origin", Background, null));

        var call = Assert.Single(cli.NetworkCalls);
        Assert.Equal(GitErrorKind.AuthenticationFailed, ex.Kind);
        Assert.Equal(GitCliPriority.BackgroundNetwork, call.Priority);
        Assert.Equal("never", call.ExtraEnvironment!["GCM_INTERACTIVE"]);
        Assert.DoesNotContain(call.Arguments, a => a.StartsWith("remote.origin.url=", StringComparison.Ordinal));
        foreach (var expected in BackgroundConfig)
            Assert.Contains(expected, ConfigOptions(call));
    }

    [Fact]
    public async Task A_background_fetch_of_an_https_remote_that_insteadof_sends_to_ssh_runs_ssh_quietly()
    {
        var repo = NewRepository("https://example.invalid/o/r.git", "url.git@example.invalid:.insteadOf");
        await using var ctx = _host.Open(repo.Path);
        var cli = new RecordingCli(Fail(Unreachable), Ok());

        await Assert.ThrowsAsync<HistoriaGitException>(() => Gateway(ctx, cli).FetchAsync("origin", Background, null));

        var call = Assert.Single(cli.NetworkCalls);
        var ssh = call.ExtraEnvironment!["GIT_SSH_COMMAND"];
        Assert.Contains("-o BatchMode=yes", ssh);
        Assert.Contains("-o ServerAliveInterval=15", ssh);
        Assert.Contains("-o ServerAliveCountMax=4", ssh);
        Assert.Contains("-o ConnectTimeout=20", ssh);
        Assert.Equal("never", call.ExtraEnvironment["GCM_INTERACTIVE"]);
    }

    [Fact]
    public async Task A_background_fetch_of_a_local_remote_is_one_attempt_with_the_quiet_settings()
    {
        await using var ctx = Open(new Uri(Path.Combine(TestScratch.Root, "no-such-remote.git")).AbsoluteUri);
        var cli = new RecordingCli(Fail("fatal: '/x' does not appear to be a git repository"), Ok());

        await Assert.ThrowsAsync<HistoriaGitException>(() => Gateway(ctx, cli).FetchAsync("origin", Background, null));

        var call = Assert.Single(cli.NetworkCalls);
        Assert.Equal(GitCliPriority.BackgroundNetwork, call.Priority);
        Assert.Equal("never", call.ExtraEnvironment!["GCM_INTERACTIVE"]);
        Assert.Contains("BatchMode=yes", call.ExtraEnvironment["GIT_SSH_COMMAND"]);
    }

    [Fact]
    public async Task A_background_fetch_with_tags_keeps_the_tags_flag()
    {
        await using var ctx = Open("git@example.invalid:o/r.git");
        var cli = new RecordingCli(Ok());

        await Gateway(ctx, cli).FetchAsync("origin", Background with { Tags = true }, null);

        var call = Assert.Single(cli.NetworkCalls);
        Assert.Contains("--tags", call.Arguments);
    }

    [Fact]
    public async Task A_user_fetch_falls_back_to_https_with_interactive_authentication()
    {
        await using var ctx = Open("git@example.invalid:o/r.git");
        var cli = new RecordingCli(Fail(Unreachable), Ok());

        await Gateway(ctx, cli).FetchAsync("origin", User, null);

        Assert.Equal(2, cli.NetworkCalls.Count);
        Assert.Contains("remote.origin.url=https://example.invalid/o/r.git", cli.NetworkCalls[1].Arguments);
        Assert.All(cli.NetworkCalls, call =>
        {
            Assert.Equal(GitCliPriority.Normal, call.Priority);
            Assert.DoesNotContain(ConfigOptions(call), c => BackgroundConfig.Contains(c));
            Assert.False(call.ExtraEnvironment?.ContainsKey("GCM_INTERACTIVE") ?? false);
        });
    }

    [Fact]
    public async Task A_user_fetch_over_https_falls_back_to_ssh_without_batch_mode()
    {
        await using var ctx = Open("https://example.invalid/o/r.git");
        var cli = new RecordingCli(Fail(AuthFailed), Ok());

        await Gateway(ctx, cli).FetchAsync("origin", User, null);

        Assert.Equal(2, cli.NetworkCalls.Count);
        var ssh = cli.NetworkCalls[1].ExtraEnvironment!["GIT_SSH_COMMAND"];
        Assert.Contains("-o ConnectTimeout=20", ssh);
        Assert.DoesNotContain("BatchMode", ssh);
        Assert.DoesNotContain("ServerAlive", ssh);
    }

    [Fact]
    public async Task A_user_push_and_pull_keep_the_fallback_and_normal_priority()
    {
        await using var ctx = Open("git@example.invalid:o/r.git");
        var pushCli = new RecordingCli(Fail(Unreachable), Ok());
        var pullCli = new RecordingCli(Fail(Unreachable), Ok());

        await Gateway(ctx, pushCli).PushAsync("origin", new PushSpec { RefSpec = "main" }, null);
        await Gateway(ctx, pullCli).PullAsync("origin", "main", new GitPullOptions(), null);

        Assert.Equal(2, pushCli.NetworkCalls.Count);
        Assert.Equal(2, pullCli.NetworkCalls.Count);
        Assert.All(pushCli.NetworkCalls.Concat(pullCli.NetworkCalls), call => Assert.Equal(GitCliPriority.Normal, call.Priority));
    }

    [Theory]
    [InlineData("git@example.invalid:o/r.git", false)]
    [InlineData("git@example.invalid:o/r.git", true)]
    [InlineData("https://example.invalid/o/r.git", false)]
    [InlineData("https://example.invalid/o/r.git", true)]
    public async Task An_ssh_only_repo_never_tries_https(string remoteUrl, bool background)
    {
        await using var ctx = Open(remoteUrl);
        await _host.Provider.GetRequiredService<IRepoLayoutService>().UpdateAsync(ctx.RepositoryPath, l => l with { SshOnly = true });
        var cli = new RecordingCli(Fail(Unreachable), Ok());
        var hosts = new FakeHosts { ["example.invalid"] = "real.invalid" };

        await Assert.ThrowsAsync<HistoriaGitException>(
            () => Gateway(ctx, cli, hosts).FetchAsync("origin", new GitFetchOptions { Background = background }, null));

        var call = Assert.Single(cli.NetworkCalls);
        Assert.Equal(0, hosts.Calls);
        Assert.Contains("GIT_SSH_COMMAND", call.ExtraEnvironment!.Keys);
        Assert.DoesNotContain(call.Arguments, a => a.StartsWith("remote.origin.url=https://", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_background_fetch_of_every_remote_gives_each_one_background_priority()
    {
        await using var ctx = Open("git@example.invalid:o/r.git");
        var cli = new RecordingCli(Ok(), Ok());

        await Gateway(ctx, cli).FetchAsync("origin", Background with { All = true }, null);

        Assert.NotEmpty(cli.NetworkCalls);
        Assert.All(cli.NetworkCalls, call => Assert.Equal(GitCliPriority.BackgroundNetwork, call.Priority));
    }

    [Fact]
    public async Task The_network_path_starts_git_off_a_pumping_synchronization_context()
    {
        var repo = NewRepository("git@example.invalid:o/r.git", null);
        var threads = new ConcurrentQueue<(string Verb, int Thread, bool HadContext)>();
        var cli = new InterceptingGitCliService(_host.Cli)
        {
            Intercept = request =>
            {
                var verb = request.Arguments.Contains("fetch") ? "fetch" : request.Arguments[0];
                threads.Enqueue((verb, Environment.CurrentManagedThreadId, SynchronizationContext.Current is not null));
                return verb == "fetch" ? Ok() : null;
            },
        };
        await using var ctx = _host.Open(repo.Path, cli);
        var gateway = Gateway(ctx, cli);
        using var pump = new PumpingContext();

        // Starts on the pump thread, the way a UI handler starts it. A call that did not leave that
        // thread would run its first git command (the remote lookup) on it.
        var fetch = await pump.InvokeAsync(() => gateway.FetchAsync("origin", User, null));
        await fetch.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains(threads, t => t.Verb == "fetch");
        Assert.All(threads, t => Assert.NotEqual(pump.ThreadId, t.Thread));
        Assert.DoesNotContain(threads, t => t.HadContext);
    }

    [Theory]
    [InlineData("Fetch", SelfOperationKind.GitDirectoryOnly)]
    [InlineData("Push", SelfOperationKind.GitDirectoryOnly)]
    [InlineData("Pull", SelfOperationKind.Full)]
    public async Task Each_network_command_runs_in_its_own_self_operation_scope(string command, SelfOperationKind expected)
    {
        await using var ctx = Open("git@example.invalid:o/r.git");
        var observed = new List<SelfOperationKind?>();
        var cli = new RecordingCli(Ok()) { OnNetworkCall = () => observed.Add(ScopeOf(ctx)) };
        var gateway = Gateway(ctx, cli);

        switch (command)
        {
            case "Fetch": await gateway.FetchAsync("origin", User, null); break;
            case "Push": await gateway.PushAsync("origin", new PushSpec { RefSpec = "main" }, null); break;
            default: await gateway.PullAsync("origin", "main", new GitPullOptions(), null); break;
        }

        Assert.Equal(expected, Assert.Single(observed));
        Assert.Null(ScopeOf(ctx));
    }

    [Fact]
    public async Task Prune_and_remote_branch_delete_run_in_a_git_directory_scope()
    {
        await using var ctx = Open("git@example.invalid:o/r.git");
        var observed = new List<SelfOperationKind?>();
        var cli = new RecordingCli(Ok(), Ok()) { OnNetworkCall = () => observed.Add(ScopeOf(ctx)) };
        var gateway = Gateway(ctx, cli);

        await gateway.DeleteRemoteBranchAsync("origin", "topic", null);
        await gateway.PruneRemoteAsync("origin", null);

        Assert.Equal(2, observed.Count);
        Assert.All(observed, kind => Assert.Equal(SelfOperationKind.GitDirectoryOnly, kind));
    }

    private static SelfOperationKind? ScopeOf(RepositoryContext ctx) =>
        ctx.IsSelfOperationInProgress ? SelfOperationKind.Full
        : ctx.IsGitDirectoryOperationInProgress ? SelfOperationKind.GitDirectoryOnly
        : null;

    /// <summary>A single-threaded synchronization context like the UI thread's: work posted to it
    /// runs one item at a time on its own dedicated thread.</summary>
    private sealed class PumpingContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;

        public PumpingContext()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "test-pump" };
            _thread.Start();
            ThreadId = _thread.ManagedThreadId;
        }

        public int ThreadId { get; }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        /// <summary>Runs <paramref name="start"/> on the pump thread and returns the task it started.</summary>
        public Task<Task> InvokeAsync(Func<Task> start)
        {
            var started = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ =>
            {
                try { started.SetResult(start()); }
                catch (Exception ex) { started.SetException(ex); }
            }, null);
            return started.Task;
        }

        private void Run()
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                callback(state);
        }

        public void Dispose() => _queue.CompleteAdding();
    }

    /// <summary>Answers each network invocation (push / pull / fetch) with the next scripted
    /// result and records it; answers <c>remote get-url</c> with the override URL.</summary>
    private sealed class RecordingCli(params GitCliResult[] results) : IGitCliService
    {
        private int _next;

        public List<GitCliRequest> NetworkCalls { get; } = [];

        /// <summary>Runs as each network command arrives, before it is answered.</summary>
        public Action? OnNetworkCall { get; init; }
        public bool IsGitAvailable => true;

        public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
            => RunAsync(request, null, null, ct);

        public Task<GitCliResult> RunAsync(
            GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default)
        {
            var args = request.Arguments;
            if (args.Contains("remote") && args.Contains("get-url"))
            {
                var url = args.Last(a => a.StartsWith("remote.", StringComparison.Ordinal) && !a.EndsWith('=')).Split('=', 2)[1];
                return Task.FromResult(new GitCliResult(0, url + "\n", "", GitErrorKind.None, TimeSpan.Zero));
            }
            if (!args.Any(a => a is "push" or "pull" or "fetch" or "prune"))
                return Task.FromResult(Ok());

            lock (NetworkCalls)
            {
                NetworkCalls.Add(request);
                OnNetworkCall?.Invoke();
                var result = _next < results.Length ? results[_next] : Ok();
                _next++;
                return Task.FromResult(result);
            }
        }
    }

    private sealed class FakeHosts : ISshHostResolver
    {
        private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

        public int Calls { get; private set; }

        public string this[string host] { set => _map[host] = value; }

        public Task<string?> ResolveHostNameAsync(string host, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_map.TryGetValue(host, out var name) ? name : null);
        }
    }
}
