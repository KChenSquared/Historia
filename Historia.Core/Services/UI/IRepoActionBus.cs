namespace Historia.Services.UI;

/// <summary>Network actions requested against the focused repository, from the shortcuts bar
/// or from menus that target one remote.</summary>
public enum RepoNetworkAction
{
    Fetch,
    Pull,
    Push,
    Prune,
    DeleteRemoteBranch,

    /// <summary>Pushes one local tag to one remote. The tag menu is the only surface that pushes
    /// a tag; branch pushes never carry tags along.</summary>
    PushTag,

    /// <summary>Moves a local branch that is not checked out forward to its remote-tracking ref,
    /// without a fetch. Local only: no network call.</summary>
    FastForwardBranch,

    /// <summary>Merges a remote-tracking branch into a local branch that has diverged from it,
    /// then pushes the local branch, checking it out first when it is not current.</summary>
    MergeAndPush,
}

/// <summary>A network action request, optionally targeted at a specific remote/branch pair
/// (e.g. a branch menu's "Push to origin"). <paramref name="Branch"/> is the local branch;
/// <paramref name="RemoteBranch"/> is its name on <paramref name="Remote"/> (null means the
/// same name as <paramref name="Branch"/>). <see langword="null"/> targets fall back to the
/// runner's usual resolution — <c>origin</c> if present, else the first remote, and the current
/// branch. For <see cref="RepoNetworkAction.DeleteRemoteBranch"/>, <paramref name="Remote"/> and
/// <paramref name="RemoteBranch"/> are both required, and <paramref name="Branch"/> is unused.
/// For <see cref="RepoNetworkAction.PushTag"/>, <paramref name="Remote"/> and <paramref name="Tag"/>
/// are both required and never fall back.
/// For <see cref="RepoNetworkAction.FastForwardBranch"/>, <paramref name="Branch"/> is the local
/// branch to move and <paramref name="Remote"/> + <paramref name="RemoteBranch"/> name the target
/// <c>&lt;remote&gt;/&lt;remoteBranch&gt;</c>; all three are required and never fall back.
/// For <see cref="RepoNetworkAction.MergeAndPush"/>, <paramref name="Branch"/> is the local branch
/// to merge into and push, <paramref name="Remote"/> + <paramref name="RemoteBranch"/> the branch
/// merged into it and pushed to; all three are required and never fall back.
/// <paramref name="Repo"/> pins the request to one repository (e.g. the staging panel's
/// own, whichever tab is focused by the time it runs); null means the focused repository.</summary>
public readonly record struct RepoNetworkRequest(
    RepoNetworkAction Action,
    string? Remote,
    string? Branch,
    string? RemoteBranch = null,
    RepositoryContext? Repo = null,
    string? Tag = null);

/// <summary>
/// A thin app-wide bus so any surface — the shortcuts bar, branch menus, the staging panel —
/// can hand a Fetch / Pull / Push to the one network-action runner, which owns the progress
/// box, cancellation, the busy guard and the recovery flows. Singleton.
/// </summary>
public interface IRepoActionBus
{
    event EventHandler<RepoNetworkRequest>? NetworkActionRequested;

    void RequestNetworkAction(RepoNetworkAction action);
    void RequestNetworkAction(RepoNetworkRequest request);
}

/// <inheritdoc cref="IRepoActionBus"/>
public sealed class RepoActionBus : IRepoActionBus
{
    public event EventHandler<RepoNetworkRequest>? NetworkActionRequested;

    public void RequestNetworkAction(RepoNetworkAction action) => RequestNetworkAction(new RepoNetworkRequest(action, null, null));

    public void RequestNetworkAction(RepoNetworkRequest request) => NetworkActionRequested?.Invoke(this, request);
}
