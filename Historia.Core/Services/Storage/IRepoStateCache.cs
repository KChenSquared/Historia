
namespace Historia.Services.Storage;

/// <summary>
/// Last-known enumerated state of one repository — local &amp; remote branches, tags, stashes,
/// submodules, worktrees. Any list is null when that half has not been cached yet.
/// </summary>
public sealed record RepoStateSnapshot(
    IReadOnlyList<BranchInfo>? Branches,
    IReadOnlyList<TagInfo>? Tags,
    IReadOnlyList<RemoteInfo>? Remotes,
    IReadOnlyList<StashEntry>? Stashes,
    IReadOnlyList<SubmoduleInfo>? Submodules,
    IReadOnlyList<WorktreeInfo>? Worktrees);

/// <summary>
/// On-disk cache of a repository's enumerated state so the sidebar can paint immediately on the
/// next launch, before the real <c>git</c> enumeration completes. There is no
/// fingerprint: the always-following async refresh overwrites any staleness within a second.
/// A no-op when the "Cache sidebar between launches" setting is off (but <see cref="WipeAll"/>
/// still runs, so turning it off cleans up).
/// </summary>
public interface IRepoStateCache
{
    /// <summary>Merged snapshot for <paramref name="repoPath"/>, or null when nothing is cached.</summary>
    RepoStateSnapshot? TryLoad(string repoPath);

    /// <summary>Like <see cref="TryLoad"/> but only the stashes, submodules and worktrees; the
    /// branch, tag and remote lists of the snapshot are null. The default projects
    /// <see cref="TryLoad"/>.</summary>
    RepoStateSnapshot? TryLoadPowerTools(string repoPath)
        => TryLoad(repoPath) is { } s ? new RepoStateSnapshot(null, null, null, s.Stashes, s.Submodules, s.Worktrees) : null;

    void SaveRefs(string repoPath, IReadOnlyList<BranchInfo> branches, IReadOnlyList<TagInfo> tags, IReadOnlyList<RemoteInfo> remotes);

    void SavePowerTools(string repoPath, IReadOnlyList<StashEntry>? stashes, IReadOnlyList<SubmoduleInfo>? submodules, IReadOnlyList<WorktreeInfo>? worktrees);

    /// <summary>Deletes every persisted repo-state file. Returns the count removed.</summary>
    int WipeAll();
}
