namespace Historia.Services.Git;

/// <summary>
/// The only Git surface the UI and higher-level services talk to. Routes all Git operations
/// through the Git CLI (canonical semantics, credential-aware, network-capable).
/// One instance per repository tab, owned by <c>RepositoryContext</c>.
/// </summary>
public interface IGitGateway
{
    string RepositoryPath { get; }

    // ── Reads ──
    Task<RepositoryInfo> GetRepositoryInfoAsync(CancellationToken ct = default);
    Task<RepositoryStatus> GetStatusAsync(CancellationToken ct = default);

    Task<CommitDetail> GetCommitDetailAsync(GitObjectId id, CancellationToken ct = default);
    Task<IReadOnlyList<FileStatusEntry>> GetCommitFilesAsync(GitObjectId id, int parentIndex, CancellationToken ct = default);
    Task<IReadOnlyList<BranchInfo>> GetBranchesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RemoteInfo>> GetRemotesAsync(CancellationToken ct = default);
    Task<FileDiff> GetFileDiffAsync(DiffRequest request, CancellationToken ct = default);

    /// <summary>The staged diff split into one patch per file, in path order. With
    /// <paramref name="againstHeadParent"/> it is taken against <c>HEAD^</c> (the empty tree for a root
    /// commit), so an amend sees the amended commit's own changes too. With
    /// <paramref name="maxPatchBytes"/>, git's output is read only that far: every file whose patch
    /// lies past it is still listed, with a null patch. Without it, a diff over 64 MB fails.</summary>
    Task<IReadOnlyList<(string Path, string? Patch)>> GetStagedPatchesAsync(bool againstHeadParent, long? maxPatchBytes = null, CancellationToken ct = default);

    /// <summary>Ahead/behind between any two refs via <c>git rev-list --left-right --count</c>.
    /// Returns <see langword="null"/> when either ref cannot be resolved, e.g. a branch with no
    /// upstream — that is an ordinary case for a caller asking "is this ahead?", not an error.</summary>
    Task<AheadBehind?> GetDivergenceAsync(string local, string remote, CancellationToken ct = default);

    /// <summary>The repository's currently configured <c>user.name</c>/<c>user.email</c>, read fresh.</summary>
    Task<(string? Name, string? Email)> GetConfiguredIdentityAsync(CancellationToken ct = default);

    /// <summary>The user's <b>global</b> <c>~/.gitconfig</c> <c>user.name</c>/<c>user.email</c>, read
    /// fresh via <c>git config --global --get</c>. Used as the conflict-banner baseline when no
    /// primary profile is explicitly set.</summary>
    Task<(string? Name, string? Email)> GetGlobalIdentityAsync(CancellationToken ct = default);

    /// <summary>The effective <c>commit.gpgsign</c> (local → global), read fresh. Used under
    /// <i>Local config</i> where Historia manages no signing settings of its own.</summary>
    Task<bool> GetGpgSignConfiguredAsync(CancellationToken ct = default);

    // ── Working tree mutations ──
    Task StageAsync(IReadOnlyList<string> paths, CancellationToken ct = default);
    Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct = default);
    /// <summary>
    /// Reverts only unstaged changes for the specified paths. Tracked paths are restored from
    /// the index via <c>git restore --worktree</c>, untracked and intent-to-add paths are sent
    /// to the Recycle Bin, and staged changes are left untouched.
    /// </summary>
    Task DiscardAsync(IReadOnlyList<string> paths, CancellationToken ct = default);
    Task<GitObjectId> CommitAsync(CommitRequest request, CancellationToken ct = default);

    /// <summary>Refreshes the index's cached stat data (<c>update-index --refresh</c>) so a later
    /// status does not re-hash files whose timestamps changed but whose content did not. Never
    /// changes staged content, unmerged entries or the reported modifications. Skipped while
    /// <c>index.lock</c> exists or a merge, rebase, cherry-pick, revert or bisect is in progress. The default
    /// does nothing.</summary>
    Task RefreshIndexStatAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Merges <paramref name="refish"/> into the current branch (CLI-routed). Conflicts surface as <see cref="Models.GitErrorKind.MergeConflict"/>.</summary>
    Task MergeAsync(string refish, bool noFastForward, CancellationToken ct = default);

    /// <summary>Moves the current branch to <paramref name="target"/> (CLI-routed). <c>Hard</c> is destructive.</summary>
    Task ResetAsync(string target, GitResetMode mode, CancellationToken ct = default);

    /// <summary>Commits reachable from <paramref name="target"/>..HEAD — i.e. how many commits
    /// would leave the branch if it were reset to <paramref name="target"/> — via
    /// <c>git rev-list --count</c>.</summary>
    Task<int> CountCommitsLeavingAsync(string target, CancellationToken ct = default);

    // ── Ref mutations ──
    Task CheckoutAsync(string refName, GitCheckoutOptions options, CancellationToken ct = default);
    Task<BranchInfo> CreateBranchAsync(string name, string startPoint, bool checkout, CancellationToken ct = default);
    Task DeleteBranchAsync(string name, bool force, CancellationToken ct = default);
    Task RenameBranchAsync(string oldName, string newName, CancellationToken ct = default);
    Task SetUpstreamAsync(string branchName, string upstreamRef, CancellationToken ct = default);
    Task<TagInfo> CreateTagAsync(string name, string target, string? message, CancellationToken ct = default);
    Task DeleteTagAsync(string name, CancellationToken ct = default);

    // ── Remotes ──
    Task AddRemoteAsync(string name, string url, CancellationToken ct = default);
    Task RemoveRemoteAsync(string name, CancellationToken ct = default);
    Task SetRemoteUrlAsync(string name, string url, CancellationToken ct = default);

    /// <summary>Renames a remote's alias. A config + refspec edit, never a network call.
    /// Returns the non-default fetch refspecs git could not rewrite automatically,
    /// if any — the caller should surface those to the user rather than drop them silently.</summary>
    Task<IReadOnlyList<string>> RenameRemoteAsync(string oldName, string newName, CancellationToken ct = default);

    /// <summary>Sets a remote's separate push URL, or removes it when <paramref name="pushUrl"/>
    /// is <see langword="null"/> so pushes go to the fetch URL again. A config edit, never a
    /// network call; clearing a push URL that is not set succeeds.</summary>
    Task SetRemotePushUrlAsync(string name, string? pushUrl, CancellationToken ct = default);

    // ── Network (always CLI; never background in SSH-Only mode) ──
    Task FetchAsync(string remote, GitFetchOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default);
    Task PullAsync(string remote, string branch, GitPullOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default);
    Task PushAsync(string remote, PushSpec spec, IProgress<GitProgress>? progress, CancellationToken ct = default);

    /// <summary>Deletes the branch <paramref name="branch"/> on <paramref name="remote"/> via
    /// <c>push --delete</c>. Git drops the matching remote-tracking ref.</summary>
    Task DeleteRemoteBranchAsync(string remote, string branch, IProgress<GitProgress>? progress, CancellationToken ct = default);

    /// <summary>Deletes the remote-tracking branches of <paramref name="name"/> whose branch no
    /// longer exists on the remote. Destructive — a pruned ref can be the last pointer to
    /// commits — so callers confirm first.</summary>
    Task PruneRemoteAsync(string name, IProgress<GitProgress>? progress, CancellationToken ct = default);
}
