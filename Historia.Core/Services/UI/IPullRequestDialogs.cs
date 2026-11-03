namespace Historia.Services.UI;

/// <summary>
/// What the Create pull request dialog needs. <paramref name="InitialSource"/> is the display key
/// (<c>local.&lt;name&gt;</c> or <c>&lt;remote&gt;.&lt;name&gt;</c>) the source starts on.
/// <paramref name="Push"/> pushes a local branch to a remote (branch, remote) and returns whether the push
/// happened; <paramref name="RefreshBranches"/> returns the repository's branches as they are after a push.
/// </summary>
public sealed record CreatePullRequestRequest(
    IReadOnlyList<BranchInfo> Branches,
    IReadOnlyList<RemoteInfo> Remotes,
    string InitialSource,
    Func<string, string, CancellationToken, Task<bool>> Push,
    Func<IReadOnlyList<BranchInfo>> RefreshBranches);

/// <summary>
/// The Create pull request dialog, behind an interface so the branch menus can open it without a
/// window. The task completes when the dialog closes.
/// </summary>
public interface IPullRequestDialogs
{
    Task ShowAsync(CreatePullRequestRequest request);
}

/// <summary>A pull request dialog that never opens. The default where none is supplied.</summary>
public sealed class NullPullRequestDialogs : IPullRequestDialogs
{
    public static NullPullRequestDialogs Instance { get; } = new();

    private NullPullRequestDialogs()
    {
    }

    public Task ShowAsync(CreatePullRequestRequest request) => Task.CompletedTask;
}
