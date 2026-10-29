namespace Historia.Tests;

[Trait("tier", "unit")]
public sealed class GitGatewayProfileFallbackTests
{
    [Theory]
    [InlineData(GitErrorKind.AuthenticationFailed)]
    [InlineData(GitErrorKind.NetworkUnreachable)]
    [InlineData(GitErrorKind.TimedOut)]
    [InlineData(GitErrorKind.Unknown)]
    public void CredentialOrReachabilityFailures_OfferLocalConfig(GitErrorKind kind) =>
        Assert.True(GitGateway.IsProfileFixable(kind));

    [Theory]
    [InlineData(GitErrorKind.RefNotFound)]
    [InlineData(GitErrorKind.RemoteRejected)]
    [InlineData(GitErrorKind.RemoteDeclined)]
    [InlineData(GitErrorKind.UncommittedChanges)]
    [InlineData(GitErrorKind.UnrelatedHistories)]
    [InlineData(GitErrorKind.MergeConflict)]
    [InlineData(GitErrorKind.HostKeyVerificationFailed)]
    public void FailuresAnotherProfileCannotFix_KeepTheirOwnHandling(GitErrorKind kind) =>
        Assert.False(GitGateway.IsProfileFixable(kind));

    [Theory]
    [InlineData(GitErrorKind.AuthenticationFailed, true)]
    [InlineData(GitErrorKind.NetworkUnreachable, true)]
    [InlineData(GitErrorKind.TimedOut, true)]
    [InlineData(GitErrorKind.HostKeyVerificationFailed, true)]
    [InlineData(GitErrorKind.Unknown, true)]
    [InlineData(GitErrorKind.None, false)]
    [InlineData(GitErrorKind.RemoteRejected, false)]
    [InlineData(GitErrorKind.RemoteDeclined, false)]
    [InlineData(GitErrorKind.TagConflict, false)]
    [InlineData(GitErrorKind.MergeConflict, false)]
    [InlineData(GitErrorKind.UncommittedChanges, false)]
    [InlineData(GitErrorKind.LockedIndex, false)]
    [InlineData(GitErrorKind.NotAGitRepository, false)]
    [InlineData(GitErrorKind.RefNotFound, false)]
    [InlineData(GitErrorKind.InvalidRefName, false)]
    [InlineData(GitErrorKind.DetachedHeadOperation, false)]
    [InlineData(GitErrorKind.HookFailed, false)]
    [InlineData(GitErrorKind.LfsMissing, false)]
    [InlineData(GitErrorKind.UnrelatedHistories, false)]
    [InlineData(GitErrorKind.Cancelled, false)]
    [InlineData(GitErrorKind.UnsafeRepository, false)]
    [InlineData(GitErrorKind.SigningToolMissing, false)]
    public void Only_transport_failures_try_the_other_transport(GitErrorKind kind, bool transport) =>
        Assert.Equal(transport, GitGateway.IsTransportFailure(kind));

    [Fact]
    public void Every_kind_is_covered_by_the_transport_theory() =>
        Assert.Equal(22, Enum.GetValues<GitErrorKind>().Length);
}
