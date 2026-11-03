namespace Historia.Tests;

[Trait("tier", "unit")]

public sealed class LoggingGitGatewayTests : IDisposable
{
    private const string DemoRepo = "repos/demo";

    private readonly StateDir _dir = new();
    private AppLog.AppLogSession? _session;

    public void Dispose()
    {
        _session?.Dispose();
        _dir.Dispose();
    }

    /// <summary>Logs this test's flow only, so the suite can run in parallel without turning on
    /// the process-wide log for every other test.</summary>
    private void StartLogging() => _session = AppLog.StartSessionForTesting(_dir.Path);

    /// <summary>Closes the session writer first — it holds the file open for the whole session,
    /// so the handle must be closed before a second reader can open the same file.</summary>
    private string SessionText()
    {
        _session!.Dispose();
        return File.ReadAllText(_session.SessionPath);
    }

    [Fact]
    public async Task Delegates_and_returns_the_inner_result()
    {
        var inner = new FakeGateway
        {
            Branches = [Branch("main"), Branch("dev")],
        };
        var gw = new LoggingGitGateway(inner, DemoRepo);

        var result = await gw.GetBranchesAsync();

        Assert.Equal(2, result.Count);
        Assert.Equal(1, inner.GetBranchesCalls);
    }

    [Fact]
    public async Task Propagates_exceptions_unchanged()
    {
        var boom = new InvalidOperationException("detached HEAD");
        var gw = new LoggingGitGateway(new FakeGateway { CheckoutThrows = boom }, DemoRepo);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gw.CheckoutAsync("main", new GitCheckoutOptions()));
        Assert.Same(boom, thrown);
    }

    [Fact]
    public async Task Logs_the_operation_with_arguments_and_outcome()
    {
        StartLogging();
        var gw = new LoggingGitGateway(new FakeGateway { Branches = [Branch("main")] }, DemoRepo);

        await gw.GetBranchesAsync();

        var text = SessionText();
        Assert.Contains("[demo] GetBranchesAsync", text);
        Assert.Contains("1 branches", text);
    }

    [Fact]
    public async Task Logs_a_failed_operation()
    {
        StartLogging();
        var gw = new LoggingGitGateway(
            new FakeGateway { CheckoutThrows = new InvalidOperationException("nope") }, DemoRepo);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gw.CheckoutAsync("main", new GitCheckoutOptions()));

        var text = SessionText();
        Assert.Contains("CheckoutAsync(main)", text);
        Assert.Contains("FAILED: InvalidOperationException: nope", text);
    }

    [Fact]
    public async Task CountCommitsLeavingAsync_delegates_and_logs_the_count()
    {
        StartLogging();
        var gw = new LoggingGitGateway(new FakeGateway { CountCommitsLeavingResult = 3 }, DemoRepo);

        var n = await gw.CountCommitsLeavingAsync("origin/main");

        Assert.Equal(3, n);
        var text = SessionText();
        Assert.Contains("CountCommitsLeavingAsync(origin/main)", text);
        Assert.Contains("(3,", text);
    }

    [Fact]
    public async Task GetDivergenceAsync_logs_arguments_and_outcome()
    {
        StartLogging();
        var gw = new LoggingGitGateway(new FakeGateway { DivergenceResult = new AheadBehind(2, 1) }, DemoRepo);

        var ab = await gw.GetDivergenceAsync("refs/heads/main", "refs/remotes/origin/main");

        Assert.Equal(new AheadBehind(2, 1), ab);
        var text = SessionText();
        Assert.Contains("GetDivergenceAsync(refs/heads/main...refs/remotes/origin/main)", text);
        Assert.Contains("+2/-1", text);
    }

    [Fact]
    public async Task RenameRemoteAsync_logs_arguments_and_outcome()
    {
        StartLogging();
        var gw = new LoggingGitGateway(new FakeGateway { RenameRemoteResult = ["+refs/heads/*:refs/custom/*"] }, DemoRepo);

        var untouched = await gw.RenameRemoteAsync("origin", "upstream");

        Assert.Single(untouched);
        var text = SessionText();
        Assert.Contains("RenameRemoteAsync(origin -> upstream)", text);
        Assert.Contains("1 unrewritten refspec(s)", text);
    }

    [Fact]
    public async Task SetRemotePushUrlAsync_logs_arguments()
    {
        StartLogging();
        var gw = new LoggingGitGateway(new FakeGateway(), DemoRepo);

        await gw.SetRemotePushUrlAsync("origin", "ssh://push.example/o/r.git");
        await gw.SetRemotePushUrlAsync("origin", null);

        var text = SessionText();
        Assert.Contains("SetRemotePushUrlAsync(origin, ssh://push.example/o/r.git)", text);
        Assert.Contains("SetRemotePushUrlAsync(origin, <clear>)", text);
    }

    [Fact]
    public async Task PruneRemoteAsync_logs_outcome()
    {
        StartLogging();
        var gw = new LoggingGitGateway(new FakeGateway(), DemoRepo);

        await gw.PruneRemoteAsync("origin", progress: null);

        var text = SessionText();
        Assert.Contains("[demo] PruneRemoteAsync(origin)", text);
    }

    [Fact]
    public async Task FastForwardBranchAsync_delegates_and_logs_arguments()
    {
        StartLogging();
        var inner = new FakeGateway();
        var gw = new LoggingGitGateway(inner, DemoRepo);

        await gw.FastForwardBranchAsync("main", "refs/remotes/origin/main");

        Assert.Equal([("main", "refs/remotes/origin/main")], inner.FastForwardCalls);
        var text = SessionText();
        Assert.Contains("[demo] FastForwardBranchAsync(main, refs/remotes/origin/main)", text);
    }

    [Fact]
    public async Task Pure_passthrough_when_logging_is_disabled()
    {
        var inner = new FakeGateway { Branches = [Branch("main")] };
        var gw = new LoggingGitGateway(inner, DemoRepo);

        await gw.GetBranchesAsync();

        Assert.False(Directory.Exists(AppPaths.LogsDirectory)
            && Directory.GetFiles(AppPaths.LogsDirectory, "dev-*.log").Length > 0);
    }

    private static BranchInfo Branch(string name) => new()
    {
        Name = name,
        CanonicalName = "refs/heads/" + name,
        IsRemote = false,
        IsCurrent = name == "main",
        TipCommitId = null,
    };

    /// <summary>Every member throws unless a test opts one in; enough of the interface is filled to compile.</summary>
    private sealed class FakeGateway : IGitGateway
    {
        public IReadOnlyList<BranchInfo> Branches { get; set; } = [];
        public Exception? CheckoutThrows { get; set; }
        public int GetBranchesCalls { get; private set; }
        public List<(string Branch, string Target)> FastForwardCalls { get; } = [];

        public string RepositoryPath => DemoRepo;

        public Task<IReadOnlyList<BranchInfo>> GetBranchesAsync(CancellationToken ct = default)
        {
            GetBranchesCalls++;
            return Task.FromResult(Branches);
        }

        public Task CheckoutAsync(string refName, GitCheckoutOptions options, CancellationToken ct = default)
            => CheckoutThrows is { } ex ? Task.FromException(ex) : Task.CompletedTask;

        // ── unused members ──
        private static T No<T>() => throw new NotSupportedException("not exercised by this test");

        public Task<RepositoryInfo> GetRepositoryInfoAsync(CancellationToken ct = default) => No<Task<RepositoryInfo>>();
        public Task<Historia.Models.RepositoryStatus> GetStatusAsync(CancellationToken ct = default) => No<Task<Historia.Models.RepositoryStatus>>();
        public Task<CommitDetail> GetCommitDetailAsync(GitObjectId id, CancellationToken ct = default) => No<Task<CommitDetail>>();
        public Task<IReadOnlyList<FileStatusEntry>> GetCommitFilesAsync(GitObjectId id, int parentIndex, CancellationToken ct = default) => No<Task<IReadOnlyList<FileStatusEntry>>>();
        public Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken ct = default) => No<Task<IReadOnlyList<TagInfo>>>();
        public Task<IReadOnlyList<RemoteInfo>> GetRemotesAsync(CancellationToken ct = default) => No<Task<IReadOnlyList<RemoteInfo>>>();
        public Task<FileDiff> GetFileDiffAsync(DiffRequest request, CancellationToken ct = default) => No<Task<FileDiff>>();
        public Task<IReadOnlyList<(string Path, string? Patch)>> GetStagedPatchesAsync(bool againstHeadParent, long? maxPatchBytes = null, CancellationToken ct = default) => No<Task<IReadOnlyList<(string Path, string? Patch)>>>();
        public AheadBehind? DivergenceResult { get; set; }
        public Task<AheadBehind?> GetDivergenceAsync(string local, string remote, CancellationToken ct = default)
            => DivergenceResult is { } ab ? Task.FromResult<AheadBehind?>(ab) : No<Task<AheadBehind?>>();
        public Task<(string? Name, string? Email)> GetConfiguredIdentityAsync(CancellationToken ct = default) => No<Task<(string?, string?)>>();
        public Task<(string? Name, string? Email)> GetGlobalIdentityAsync(CancellationToken ct = default) => No<Task<(string?, string?)>>();
        public Task<bool> GetGpgSignConfiguredAsync(CancellationToken ct = default) => No<Task<bool>>();
        public Task StageAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => No<Task>();
        public Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => No<Task>();
        public Task DiscardAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => No<Task>();
        public Task<GitObjectId> CommitAsync(CommitRequest request, CancellationToken ct = default) => No<Task<GitObjectId>>();
        public Task MergeAsync(string refish, bool noFastForward, CancellationToken ct = default) => No<Task>();
        public Task ResetAsync(string target, GitResetMode mode, CancellationToken ct = default) => No<Task>();
        public int? CountCommitsLeavingResult { get; set; }
        public Task<int> CountCommitsLeavingAsync(string target, CancellationToken ct = default)
            => CountCommitsLeavingResult is { } n ? Task.FromResult(n) : No<Task<int>>();
        public Task<BranchInfo> CreateBranchAsync(string name, string startPoint, bool checkout, CancellationToken ct = default) => No<Task<BranchInfo>>();
        public Task DeleteBranchAsync(string name, bool force, CancellationToken ct = default) => No<Task>();
        public Task FastForwardBranchAsync(string branch, string target, CancellationToken ct = default)
        {
            FastForwardCalls.Add((branch, target));
            return Task.CompletedTask;
        }
        public Task RenameBranchAsync(string oldName, string newName, CancellationToken ct = default) => No<Task>();
        public Task SetUpstreamAsync(string branchName, string upstreamRef, CancellationToken ct = default) => No<Task>();
        public Task<TagInfo> CreateTagAsync(string name, string target, string? message, CancellationToken ct = default) => No<Task<TagInfo>>();
        public Task DeleteTagAsync(string name, CancellationToken ct = default) => No<Task>();
        public Task AddRemoteAsync(string name, string url, CancellationToken ct = default) => No<Task>();
        public Task RemoveRemoteAsync(string name, CancellationToken ct = default) => No<Task>();
        public Task SetRemoteUrlAsync(string name, string url, CancellationToken ct = default) => No<Task>();
        public IReadOnlyList<string>? RenameRemoteResult { get; set; }
        public Task<IReadOnlyList<string>> RenameRemoteAsync(string oldName, string newName, CancellationToken ct = default)
            => RenameRemoteResult is { } r ? Task.FromResult(r) : No<Task<IReadOnlyList<string>>>();
        public Task SetRemotePushUrlAsync(string name, string? pushUrl, CancellationToken ct = default) => Task.CompletedTask;
        public Task PruneRemoteAsync(string name, IProgress<GitProgress>? progress, CancellationToken ct = default) => Task.CompletedTask;
        public Task FetchAsync(string remote, GitFetchOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => No<Task>();
        public Task PullAsync(string remote, string branch, GitPullOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => No<Task>();
        public Task PushAsync(string remote, PushSpec spec, IProgress<GitProgress>? progress, CancellationToken ct = default) => No<Task>();
        public Task DeleteRemoteBranchAsync(string remote, string branch, IProgress<GitProgress>? progress, CancellationToken ct = default) => No<Task>();
    }
}
