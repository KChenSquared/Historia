namespace HistoriaApp.Tests;

/// <summary><c>BranchMenu.BuildDivergenceItemsAsync</c> — the push/pull/fast-forward menu
/// item(s) for each (row kind, ahead, behind) combination, driven through a stub
/// <see cref="IGitGateway"/> rather than a real repository.</summary>
[Trait("tier", "unit")]
public sealed class BranchMenuDivergenceTests
{
    private sealed class StubGateway : IGitGateway
    {
        public AheadBehind? Result { get; set; }
        public (string Local, string Remote)? LastArgs { get; private set; }
        public string RepositoryPath => "/repo";

        public Task<AheadBehind?> GetDivergenceAsync(string local, string remote, CancellationToken ct = default)
        {
            LastArgs = (local, remote);
            return Task.FromResult(Result);
        }

        private static T No<T>() => throw new NotSupportedException("not exercised by this test");
        public Task<RepositoryInfo> GetRepositoryInfoAsync(CancellationToken ct = default) => No<Task<RepositoryInfo>>();
        public Task<Historia.Models.RepositoryStatus> GetStatusAsync(CancellationToken ct = default) => No<Task<Historia.Models.RepositoryStatus>>();
        public Task<CommitDetail> GetCommitDetailAsync(GitObjectId id, CancellationToken ct = default) => No<Task<CommitDetail>>();
        public Task<IReadOnlyList<FileStatusEntry>> GetCommitFilesAsync(GitObjectId id, int parentIndex, CancellationToken ct = default) => No<Task<IReadOnlyList<FileStatusEntry>>>();
        public Task<IReadOnlyList<BranchInfo>> GetBranchesAsync(CancellationToken ct = default) => No<Task<IReadOnlyList<BranchInfo>>>();
        public Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken ct = default) => No<Task<IReadOnlyList<TagInfo>>>();
        public Task<IReadOnlyList<RemoteInfo>> GetRemotesAsync(CancellationToken ct = default) => No<Task<IReadOnlyList<RemoteInfo>>>();
        public Task<FileDiff> GetFileDiffAsync(DiffRequest request, CancellationToken ct = default) => No<Task<FileDiff>>();
        public Task<IReadOnlyList<(string Path, string? Patch)>> GetStagedPatchesAsync(bool againstHeadParent, long? maxPatchBytes = null, CancellationToken ct = default) => No<Task<IReadOnlyList<(string Path, string? Patch)>>>();
        public Task<(string? Name, string? Email)> GetConfiguredIdentityAsync(CancellationToken ct = default) => No<Task<(string?, string?)>>();
        public Task<(string? Name, string? Email)> GetGlobalIdentityAsync(CancellationToken ct = default) => No<Task<(string?, string?)>>();
        public Task<bool> GetGpgSignConfiguredAsync(CancellationToken ct = default) => No<Task<bool>>();
        public Task StageAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => No<Task>();
        public Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => No<Task>();
        public Task DiscardAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => No<Task>();
        public Task<GitObjectId> CommitAsync(CommitRequest request, CancellationToken ct = default) => No<Task<GitObjectId>>();
        public Task MergeAsync(string refish, bool noFastForward, CancellationToken ct = default) => No<Task>();
        public Task ResetAsync(string target, GitResetMode mode, CancellationToken ct = default) => No<Task>();
        public Task<int> CountCommitsLeavingAsync(string target, CancellationToken ct = default) => No<Task<int>>();
        public Task CheckoutAsync(string refName, GitCheckoutOptions options, CancellationToken ct = default) => No<Task>();
        public Task<BranchInfo> CreateBranchAsync(string name, string startPoint, bool checkout, CancellationToken ct = default) => No<Task<BranchInfo>>();
        public Task DeleteBranchAsync(string name, bool force, CancellationToken ct = default) => No<Task>();
        public Task FastForwardBranchAsync(string branch, string target, CancellationToken ct = default) => No<Task>();
        public Task RenameBranchAsync(string oldName, string newName, CancellationToken ct = default) => No<Task>();
        public Task SetUpstreamAsync(string branchName, string upstreamRef, CancellationToken ct = default) => No<Task>();
        public Task<TagInfo> CreateTagAsync(string name, string target, string? message, CancellationToken ct = default) => No<Task<TagInfo>>();
        public Task DeleteTagAsync(string name, CancellationToken ct = default) => No<Task>();
        public Task AddRemoteAsync(string name, string url, CancellationToken ct = default) => No<Task>();
        public Task RemoveRemoteAsync(string name, CancellationToken ct = default) => No<Task>();
        public Task SetRemoteUrlAsync(string name, string url, CancellationToken ct = default) => No<Task>();
        public Task<IReadOnlyList<string>> RenameRemoteAsync(string oldName, string newName, CancellationToken ct = default) => No<Task<IReadOnlyList<string>>>();
        public Task SetRemotePushUrlAsync(string name, string? pushUrl, CancellationToken ct = default) => No<Task>();
        public Task FetchAsync(string remote, GitFetchOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => No<Task>();
        public Task PullAsync(string remote, string branch, GitPullOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => No<Task>();
        public Task PushAsync(string remote, PushSpec spec, IProgress<GitProgress>? progress, CancellationToken ct = default) => No<Task>();
        public Task DeleteRemoteBranchAsync(string remote, string branch, IProgress<GitProgress>? progress, CancellationToken ct = default) => No<Task>();
        public Task PruneRemoteAsync(string name, IProgress<GitProgress>? progress, CancellationToken ct = default) => No<Task>();
    }

    private sealed class CapturingActionBus : IRepoActionBus
    {
        public RepoNetworkRequest? LastRequest { get; private set; }
        public event EventHandler<RepoNetworkRequest>? NetworkActionRequested;

        public void RequestNetworkAction(RepoNetworkAction action) =>
            RequestNetworkAction(new RepoNetworkRequest(action, null, null));

        public void RequestNetworkAction(RepoNetworkRequest request)
        {
            LastRequest = request;
            NetworkActionRequested?.Invoke(this, request);
        }
    }

    private static BranchMenu.Target LocalTarget(string name, string currentBranch, string? counterpart, string? remoteAlias) =>
        new(Name: name, CopyName: name, TrackingRef: name, IsLocal: true, TipSha: null, CurrentBranch: currentBranch,
            CounterpartRef: counterpart, RemoteAlias: remoteAlias);

    private static BranchMenu.Target RemoteTarget(string shortName, string trackingRef, string currentBranch, string? counterpart, string? remoteAlias) =>
        new(Name: shortName, CopyName: trackingRef, TrackingRef: trackingRef, IsLocal: false, TipSha: null, CurrentBranch: currentBranch,
            CounterpartRef: counterpart, RemoteAlias: remoteAlias);

    private static string? Header(Control c) => (c as MenuItem)?.Header as string;

    [Fact]
    public async Task Local_behind_current_branch_pulls_the_upstream_name()
    {
        var gw = new StubGateway { Result = new AheadBehind(0, 2) };
        var t = LocalTarget("feature", currentBranch: "feature", counterpart: "origin/feat-x", remoteAlias: "origin");
        var bus = new CapturingActionBus();

        var items = await BranchMenu.BuildDivergenceItemsAsync(t, gw, bus, CancellationToken.None);

        var item = Assert.Single(items);
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

        Assert.NotNull(bus.LastRequest);
        Assert.Equal("feature", bus.LastRequest.Value.Branch);
        Assert.Equal("feat-x", bus.LastRequest.Value.RemoteBranch);
    }

    [Fact]
    public async Task Local_ahead_pushes_to_the_upstream_name()
    {
        var gw = new StubGateway { Result = new AheadBehind(2, 0) };
        var t = LocalTarget("feature", currentBranch: "feature", counterpart: "origin/feat-x", remoteAlias: "origin");
        var bus = new CapturingActionBus();

        var items = await BranchMenu.BuildDivergenceItemsAsync(t, gw, bus, CancellationToken.None);

        var item = Assert.Single(items);
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

        Assert.NotNull(bus.LastRequest);
        Assert.Equal("feature", bus.LastRequest.Value.Branch);
        Assert.Equal("feat-x", bus.LastRequest.Value.RemoteBranch);
    }

    [Fact]
    public async Task A_remote_alias_with_a_slash_is_stripped_once()
    {
        var gw = new StubGateway { Result = new AheadBehind(0, 1) };
        var t = LocalTarget("main", currentBranch: "main", counterpart: "team/infra/main", remoteAlias: "team/infra");
        var bus = new CapturingActionBus();

        var items = await BranchMenu.BuildDivergenceItemsAsync(t, gw, bus, CancellationToken.None);

        var item = Assert.Single(items);
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

        Assert.NotNull(bus.LastRequest);
        Assert.Equal("main", bus.LastRequest.Value.RemoteBranch);
    }

    [Fact]
    public async Task Remote_row_ahead_and_behind_offers_no_push()
    {
        var gw = new StubGateway { Result = new AheadBehind(2, 1) };
        var t = RemoteTarget("main", "origin/main", currentBranch: "main", counterpart: "main", remoteAlias: "origin");
        var bus = new CapturingActionBus();

        var items = await BranchMenu.BuildDivergenceItemsAsync(t, gw, bus, CancellationToken.None);

        Assert.DoesNotContain(items, i => (Header(i) ?? "").StartsWith("Push", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Local_ahead_offers_push_regardless_of_current_branch()
    {
        var gw = new StubGateway { Result = new AheadBehind(2, 0) };
        var t = LocalTarget("feature", currentBranch: "main", counterpart: "origin/feature", remoteAlias: "origin");

        var items = await BranchMenu.BuildDivergenceItemsAsync(t, gw, new RepoActionBus(), CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("Push 2 commit(s) to origin", Header(item));
    }

    [Fact]
    public async Task Local_behind_current_branch_offers_fast_forward()
    {
        var gw = new StubGateway { Result = new AheadBehind(0, 3) };
        var t = LocalTarget("main", currentBranch: "main", counterpart: "origin/main", remoteAlias: "origin");

        var items = await BranchMenu.BuildDivergenceItemsAsync(t, gw, new RepoActionBus(), CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("Fast-forward from origin/main", Header(item));
    }

    [Fact]
    public async Task Local_diverged_current_branch_offers_pull_not_fast_forward()
    {
        var gw = new StubGateway { Result = new AheadBehind(1, 3) };
        var t = LocalTarget("main", currentBranch: "main", counterpart: "origin/main", remoteAlias: "origin");

        var items = await BranchMenu.BuildDivergenceItemsAsync(t, gw, new RepoActionBus(), CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("Pull from origin/main", Header(item));
    }

    [Fact]
    public async Task Local_behind_a_non_current_branch_offers_nothing()
    {
        // git pull always merges into whatever is checked out — offering Pull/Fast-forward on a
        // branch that isn't current would silently target the wrong branch.
        var gw = new StubGateway { Result = new AheadBehind(0, 3) };
        var t = LocalTarget("feature", currentBranch: "main", counterpart: "origin/feature", remoteAlias: "origin");

        var items = await BranchMenu.BuildDivergenceItemsAsync(t, gw, new RepoActionBus(), CancellationToken.None);

        Assert.Empty(items);
    }

    [Fact]
    public async Task Remote_row_whose_local_counterpart_is_ahead_offers_push()
    {
        // GetDivergenceAsync(local, remote): local ahead -> remote behind.
        var gw = new StubGateway { Result = new AheadBehind(2, 0) };
        var t = RemoteTarget("main", "origin/main", currentBranch: "main", counterpart: "main", remoteAlias: "origin");

        var items = await BranchMenu.BuildDivergenceItemsAsync(t, gw, new RepoActionBus(), CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("Push main to origin", Header(item));
        Assert.Equal(("main", "origin/main"), gw.LastArgs);
    }

    [Fact]
    public async Task Remote_row_whose_local_counterpart_is_behind_offers_pull()
    {
        var gw = new StubGateway { Result = new AheadBehind(0, 2) };
        var t = RemoteTarget("main", "origin/main", currentBranch: "main", counterpart: "main", remoteAlias: "origin");

        var items = await BranchMenu.BuildDivergenceItemsAsync(t, gw, new RepoActionBus(), CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("Pull origin/main", Header(item));
    }

    [Fact]
    public async Task A_branch_with_no_upstream_produces_the_unchanged_base_menu()
    {
        var gw = new StubGateway();
        var t = LocalTarget("scratch", currentBranch: "scratch", counterpart: null, remoteAlias: null);

        var items = await BranchMenu.BuildDivergenceItemsAsync(t, gw, new RepoActionBus(), CancellationToken.None);

        Assert.Empty(items);
        Assert.Null(gw.LastArgs); // never shells out when there's nothing to compare
    }

    [Fact]
    public async Task A_remote_with_no_local_counterpart_produces_the_unchanged_base_menu()
    {
        var gw = new StubGateway();
        var t = RemoteTarget("feature", "origin/feature", currentBranch: "main", counterpart: null, remoteAlias: "origin");

        var items = await BranchMenu.BuildDivergenceItemsAsync(t, gw, new RepoActionBus(), CancellationToken.None);

        Assert.Empty(items);
        Assert.Null(gw.LastArgs);
    }

    [Fact]
    public async Task No_combination_ever_offers_a_force_push_item()
    {
        foreach (var ab in new[] { new AheadBehind(5, 0), new AheadBehind(0, 5), new AheadBehind(3, 3) })
        {
            var gw = new StubGateway { Result = ab };
            var local = LocalTarget("main", "main", "origin/main", "origin");
            var remote = RemoteTarget("main", "origin/main", "main", "main", "origin");

            var localItems = await BranchMenu.BuildDivergenceItemsAsync(local, gw, new RepoActionBus(), CancellationToken.None);
            var remoteItems = await BranchMenu.BuildDivergenceItemsAsync(remote, gw, new RepoActionBus(), CancellationToken.None);

            Assert.DoesNotContain(localItems, i => (Header(i) ?? "").Contains("force", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(remoteItems, i => (Header(i) ?? "").Contains("force", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Identical_refs_produce_no_items()
    {
        var gw = new StubGateway { Result = new AheadBehind(0, 0) };
        var t = LocalTarget("main", "main", "origin/main", "origin");

        var items = await BranchMenu.BuildDivergenceItemsAsync(t, gw, new RepoActionBus(), CancellationToken.None);

        Assert.Empty(items);
    }
}
