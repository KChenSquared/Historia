namespace HistoriaApp.Tests;

/// <summary>The runner's <see cref="RepoNetworkAction.FastForwardBranch"/>: it moves a branch that
/// is not checked out to its remote-tracking ref and refreshes; a refusal is a warning card that
/// says why, never an error.</summary>
[Trait("tier", "ui")]
public sealed class RepoNetworkFastForwardTests
{
    [AvaloniaFact]
    public async Task FastForward_moves_non_current_branch_and_refreshes()
    {
        using var fixture = new BranchSyncFixture().AdvanceRemoteOnly().CheckoutNew("feature");
        await using var rig = new BranchSyncRig();
        var repo = await rig.OpenAsync(fixture.LocalPath);
        var target = fixture.Rev(fixture.LocalPath, "refs/remotes/origin/main");

        await rig.RunToIdleAsync(() => rig.Bus.RequestNetworkAction(
            new RepoNetworkRequest(RepoNetworkAction.FastForwardBranch, "origin", "main", "main", Repo: repo)));

        Assert.Equal(target, fixture.Rev(fixture.LocalPath, "refs/heads/main"));
        Assert.Equal("feature", fixture.CurrentBranch());
        var main = Assert.Single(repo.State.Branches, b => !b.IsRemote && b.Name == "main");
        Assert.Equal(target, main.TipCommitId?.ToString());
        Assert.Contains(rig.Notify.Published,
            n => n.Severity == NotificationSeverity.Success && n.Message == "Fast-forwarded main to origin/main.");
        Assert.Empty(rig.Dialogs.Confirms);
    }

    [AvaloniaFact]
    public async Task FastForward_refusal_is_a_warning_card()
    {
        using var fixture = new BranchSyncFixture().Diverge().CheckoutNew("feature");
        await using var rig = new BranchSyncRig();
        var repo = await rig.OpenAsync(fixture.LocalPath);
        var before = fixture.Rev(fixture.LocalPath, "refs/heads/main");

        await rig.RunToIdleAsync(() => rig.Bus.RequestNetworkAction(
            new RepoNetworkRequest(RepoNetworkAction.FastForwardBranch, "origin", "main", "main", Repo: repo)));

        var warning = Assert.Single(rig.Notify.Published);
        Assert.Equal(NotificationSeverity.Warning, warning.Severity);
        Assert.Contains("can't be fast-forwarded", warning.Message);
        Assert.Equal(before, fixture.Rev(fixture.LocalPath, "refs/heads/main"));
    }

    [AvaloniaFact]
    public async Task FastForward_without_a_target_is_refused_before_running()
    {
        using var fixture = new BranchSyncFixture();
        await using var rig = new BranchSyncRig();
        var repo = await rig.OpenAsync(fixture.LocalPath);

        rig.Bus.RequestNetworkAction(
            new RepoNetworkRequest(RepoNetworkAction.FastForwardBranch, "origin", "main", Repo: repo));
        await rig.WaitUntilAsync(() => rig.Notify.Published.Count == 1);

        var warning = Assert.Single(rig.Notify.Published);
        Assert.Equal(NotificationSeverity.Warning, warning.Severity);
        Assert.Equal("No branch to fast-forward.", warning.Message);
    }
}
