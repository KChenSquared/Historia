namespace Historia.Services.Git;

/// <summary>
/// <see cref="IGitGateway"/> decorator that records every operation to <see cref="AppLog"/>
/// — the method, its key arguments, the outcome or the exception, and how long it took.
/// Wraps the real <see cref="GitGateway"/> in <c>RepositoryContext</c>. A pure pass-through when
/// logging is disabled (one bool check per call, no allocation).
/// </summary>
public sealed class LoggingGitGateway(IGitGateway inner, string repositoryPath) : IGitGateway
{
    private readonly string _repo = Path.GetFileName(repositoryPath.TrimEnd('/', '\\'));

    public string RepositoryPath => inner.RepositoryPath;

    // ── plumbing ──

    private async Task<T> Run<T>(string op, Func<Task<T>> call, Func<T, string>? outcome = null)
    {
        if (!AppLog.Enabled)
            return await call().ConfigureAwait(false);

        using var scope = AppLog.Operation("git", $"[{_repo}] {op}");
        try
        {
            var result = await call().ConfigureAwait(false);
            scope.Outcome(outcome is not null ? outcome(result) : "ok");
            return result;
        }
        catch (Exception ex)
        {
            scope.Fail(ex);
            throw;
        }
    }

    private async Task Run(string op, Func<Task> call)
    {
        if (!AppLog.Enabled)
        {
            await call().ConfigureAwait(false);
            return;
        }

        using var scope = AppLog.Operation("git", $"[{_repo}] {op}");
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            scope.Fail(ex);
            throw;
        }
    }

    private static string Count<T>(IReadOnlyList<T> list, string noun) => $"{list.Count} {noun}";

    // ── Reads ──

    public Task<RepositoryInfo> GetRepositoryInfoAsync(CancellationToken ct = default)
        => Run("GetRepositoryInfoAsync", () => inner.GetRepositoryInfoAsync(ct));

    public Task<RepositoryStatus> GetStatusAsync(CancellationToken ct = default)
        => Run("GetStatusAsync", () => inner.GetStatusAsync(ct));

    public Task<CommitDetail> GetCommitDetailAsync(GitObjectId id, CancellationToken ct = default)
        => Run($"GetCommitDetailAsync({Short(id)})", () => inner.GetCommitDetailAsync(id, ct));

    public Task<IReadOnlyList<FileStatusEntry>> GetCommitFilesAsync(GitObjectId id, int parentIndex, CancellationToken ct = default)
        => Run($"GetCommitFilesAsync({Short(id)}, parent={parentIndex})", () => inner.GetCommitFilesAsync(id, parentIndex, ct), r => Count(r, "files"));

    public Task<IReadOnlyList<BranchInfo>> GetBranchesAsync(CancellationToken ct = default)
        => Run("GetBranchesAsync", () => inner.GetBranchesAsync(ct), r => Count(r, "branches"));

    public Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken ct = default)
        => Run("GetTagsAsync", () => inner.GetTagsAsync(ct), r => Count(r, "tags"));

    public Task<IReadOnlyList<RemoteInfo>> GetRemotesAsync(CancellationToken ct = default)
        => Run("GetRemotesAsync", () => inner.GetRemotesAsync(ct), r => Count(r, "remotes"));

    public Task<FileDiff> GetFileDiffAsync(DiffRequest request, CancellationToken ct = default)
        => Run("GetFileDiffAsync", () => inner.GetFileDiffAsync(request, ct));

    public Task<IReadOnlyList<(string Path, string? Patch)>> GetStagedPatchesAsync(bool againstHeadParent, long? maxPatchBytes = null, CancellationToken ct = default)
        => Run($"GetStagedPatchesAsync(amend={againstHeadParent})", () => inner.GetStagedPatchesAsync(againstHeadParent, maxPatchBytes, ct), r => Count(r, "files"));

    public Task<AheadBehind?> GetDivergenceAsync(string local, string remote, CancellationToken ct = default)
        => Run($"GetDivergenceAsync({local}...{remote})", () => inner.GetDivergenceAsync(local, remote, ct),
            r => r is { } ab ? $"+{ab.Ahead}/-{ab.Behind}" : "none");

    public Task<(string? Name, string? Email)> GetConfiguredIdentityAsync(CancellationToken ct = default)
        => Run("GetConfiguredIdentityAsync", () => inner.GetConfiguredIdentityAsync(ct));

    public Task<(string? Name, string? Email)> GetGlobalIdentityAsync(CancellationToken ct = default)
        => Run("GetGlobalIdentityAsync", () => inner.GetGlobalIdentityAsync(ct));

    public Task<bool> GetGpgSignConfiguredAsync(CancellationToken ct = default)
        => Run("GetGpgSignConfiguredAsync", () => inner.GetGpgSignConfiguredAsync(ct), r => r.ToString());

    // ── Working tree mutations ──

    public Task StageAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
        => Run($"StageAsync({paths.Count} path(s))", () => inner.StageAsync(paths, ct));

    public Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
        => Run($"UnstageAsync({paths.Count} path(s))", () => inner.UnstageAsync(paths, ct));

    public Task RefreshIndexStatAsync(CancellationToken ct = default)
        => Run("RefreshIndexStatAsync()", () => inner.RefreshIndexStatAsync(ct));

    public Task DiscardAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
        => Run($"DiscardAsync({paths.Count} path(s))", () => inner.DiscardAsync(paths, ct));

    public Task<GitObjectId> CommitAsync(CommitRequest request, CancellationToken ct = default)
        => Run("CommitAsync", () => inner.CommitAsync(request, ct), id => id.ToString());

    public Task MergeAsync(string refish, bool noFastForward, CancellationToken ct = default)
        => Run($"MergeAsync({refish}, noFastForward={noFastForward})", () => inner.MergeAsync(refish, noFastForward, ct));

    public Task ResetAsync(string target, GitResetMode mode, CancellationToken ct = default)
        => Run($"ResetAsync({target}, {mode})", () => inner.ResetAsync(target, mode, ct));

    public Task<int> CountCommitsLeavingAsync(string target, CancellationToken ct = default)
        => Run($"CountCommitsLeavingAsync({target})", () => inner.CountCommitsLeavingAsync(target, ct), n => n.ToString());

    // ── Ref mutations ──

    public Task CheckoutAsync(string refName, GitCheckoutOptions options, CancellationToken ct = default)
        => Run($"CheckoutAsync({refName})", () => inner.CheckoutAsync(refName, options, ct));

    public Task<BranchInfo> CreateBranchAsync(string name, string startPoint, bool checkout, CancellationToken ct = default)
        => Run($"CreateBranchAsync({name}, start={startPoint}, checkout={checkout})", () => inner.CreateBranchAsync(name, startPoint, checkout, ct), b => b.Name);

    public Task DeleteBranchAsync(string name, bool force, CancellationToken ct = default)
        => Run($"DeleteBranchAsync({name}, force={force})", () => inner.DeleteBranchAsync(name, force, ct));

    public Task RenameBranchAsync(string oldName, string newName, CancellationToken ct = default)
        => Run($"RenameBranchAsync({oldName} -> {newName})", () => inner.RenameBranchAsync(oldName, newName, ct));

    public Task SetUpstreamAsync(string branchName, string upstreamRef, CancellationToken ct = default)
        => Run($"SetUpstreamAsync({branchName} -> {upstreamRef})", () => inner.SetUpstreamAsync(branchName, upstreamRef, ct));

    public Task<TagInfo> CreateTagAsync(string name, string target, string? message, CancellationToken ct = default)
        => Run($"CreateTagAsync({name}, target={target}, annotated={message is not null})", () => inner.CreateTagAsync(name, target, message, ct), t => t.Name);

    public Task DeleteTagAsync(string name, CancellationToken ct = default)
        => Run($"DeleteTagAsync({name})", () => inner.DeleteTagAsync(name, ct));

    // ── Remotes ──

    public Task AddRemoteAsync(string name, string url, CancellationToken ct = default)
        => Run($"AddRemoteAsync({name}, {url})", () => inner.AddRemoteAsync(name, url, ct));

    public Task RemoveRemoteAsync(string name, CancellationToken ct = default)
        => Run($"RemoveRemoteAsync({name})", () => inner.RemoveRemoteAsync(name, ct));

    public Task SetRemoteUrlAsync(string name, string url, CancellationToken ct = default)
        => Run($"SetRemoteUrlAsync({name}, {url})", () => inner.SetRemoteUrlAsync(name, url, ct));

    public Task<IReadOnlyList<string>> RenameRemoteAsync(string oldName, string newName, CancellationToken ct = default)
        => Run($"RenameRemoteAsync({oldName} -> {newName})", () => inner.RenameRemoteAsync(oldName, newName, ct), r => Count(r, "unrewritten refspec(s)"));

    public Task SetRemotePushUrlAsync(string name, string? pushUrl, CancellationToken ct = default)
        => Run($"SetRemotePushUrlAsync({name}, {pushUrl ?? "<clear>"})", () => inner.SetRemotePushUrlAsync(name, pushUrl, ct));

    // ── Network ──

    public Task FetchAsync(string remote, GitFetchOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default)
        => Run($"FetchAsync({remote})", () => inner.FetchAsync(remote, options, progress, ct));

    public Task PullAsync(string remote, string branch, GitPullOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default)
        => Run($"PullAsync({remote}/{branch})", () => inner.PullAsync(remote, branch, options, progress, ct));

    public Task PushAsync(string remote, PushSpec spec, IProgress<GitProgress>? progress, CancellationToken ct = default)
        => Run($"PushAsync({remote})", () => inner.PushAsync(remote, spec, progress, ct));

    public Task DeleteRemoteBranchAsync(string remote, string branch, IProgress<GitProgress>? progress, CancellationToken ct = default)
        => Run($"DeleteRemoteBranchAsync({remote}, {branch})", () => inner.DeleteRemoteBranchAsync(remote, branch, progress, ct));

    public Task PruneRemoteAsync(string name, IProgress<GitProgress>? progress, CancellationToken ct = default)
        => Run($"PruneRemoteAsync({name})", () => inner.PruneRemoteAsync(name, progress, ct));

    private static string Short(GitObjectId id) => id.ToShortString(10);
}
