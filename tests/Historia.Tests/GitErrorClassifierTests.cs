namespace Historia.Tests;

[Trait("tier", "unit")]

public class GitErrorClassifierTests
{
    [Theory]
    [InlineData("git@github.com: Permission denied (publickey).\nfatal: Could not read from remote repository.", GitErrorKind.AuthenticationFailed)]
    [InlineData("fatal: Authentication failed for 'https://github.com/owner/repo.git/'", GitErrorKind.AuthenticationFailed)]
    [InlineData("Host key verification failed.\nfatal: Could not read from remote repository.", GitErrorKind.HostKeyVerificationFailed)]
    [InlineData("fatal: unable to access 'https://x/': Could not resolve host: x", GitErrorKind.NetworkUnreachable)]
    [InlineData(" ! [rejected]        main -> main (non-fast-forward)", GitErrorKind.RemoteRejected)]
    // A rejected push also prints "failed to push some refs" and often a trailing
    // "fatal: unable to access … Could not resolve host" when git retries a second transport —
    // the strong rejection marker must still win over NetworkUnreachable.
    [InlineData(
        " ! [rejected]        main -> main (fetch first)\n"
        + "error: failed to push some refs to 'ssh-alias:owner/repo.git'\n"
        + "hint: Updates were rejected because the remote contains work that you do not\n"
        + "fatal: unable to access 'https://ssh-alias/owner/repo.git/': Could not resolve host: ssh-alias",
        GitErrorKind.RemoteRejected)]
    [InlineData("To github.com:owner/repo.git\n ! [remote rejected] main -> main (protected branch hook declined)", GitErrorKind.RemoteDeclined)]
    // GitHub's GH001 large-file refusal: progress noise, the server's reason, a [remote rejected]
    // ref and the generic trailer. A server decline, never a non-fast-forward.
    [InlineData(
        "Enumerating objects: 244, done.\n"
        + "Writing objects: 100% (244/244), 600.12 MiB | 12.00 MiB/s, done.\n"
        + "remote: error: Trace: 0123456789abcdef\n"
        + "remote: error: File big.bin is 361.72 MB; this exceeds GitHub's file size limit of 100.00 MB\n"
        + "remote: error: GH001: Large files detected. You may want to try Git Large File Storage - https://git-lfs.github.com.\n"
        + "To github-alias:owner/repo.git\n"
        + " ! [remote rejected] main -> main (pre-receive hook declined)\n"
        + "error: failed to push some refs to 'github-alias:owner/repo.git'",
        GitErrorKind.RemoteDeclined)]
    // One ref declined by the server and another merely behind: declined wins, so no force push
    // is ever offered against a server policy.
    [InlineData(
        " ! [remote rejected] main -> main (protected branch hook declined)\n"
        + " ! [rejected]        dev -> dev (non-fast-forward)\n"
        + "error: failed to push some refs to 'x'",
        GitErrorKind.RemoteDeclined)]
    [InlineData("remote: error: denied\n ! [remote rejected] main -> main (push declined due to repository rule violations)", GitErrorKind.RemoteDeclined)]
    // The generic push-failure trailers alone are not evidence of a rejection.
    [InlineData("fatal: the remote end hung up unexpectedly", GitErrorKind.Unknown)]
    [InlineData("error: failed to push some refs to 'x'", GitErrorKind.Unknown)]
    [InlineData(" ! [rejected]        main -> main (stale info)\nerror: failed to push some refs to 'x'", GitErrorKind.RemoteRejected)]
    // A fetch refusing to move a tag the remote re-pointed is a tag conflict, not a
    // non-fast-forward branch, though both print "! [rejected]".
    [InlineData(
        "From github.com:owner/repo\n"
        + "   d68df6d..8976b09  main       -> origin/main\n"
        + " ! [rejected]        v0.10.0    -> v0.10.0  (would clobber existing tag)",
        GitErrorKind.TagConflict)]
    [InlineData("CONFLICT (content): Merge conflict in file.txt\nAutomatic merge failed; fix conflicts and then commit the result.", GitErrorKind.MergeConflict)]
    [InlineData("error: Your local changes to the following files would be overwritten by checkout:", GitErrorKind.UncommittedChanges)]
    [InlineData("fatal: Unable to create '/repo/.git/index.lock': File exists.", GitErrorKind.LockedIndex)]
    // A fetch whose prune hits a ref lock a dead git process left behind.
    [InlineData("error: could not delete references: cannot lock ref 'refs/remotes/origin/x': Unable to create 'C:/r/.git/refs/remotes/origin/x.lock': File exists.\n\nAnother git process seems to be running in this repository, e.g.\nan editor opened by 'git commit'. Please make sure all processes\nare terminated then try again.", GitErrorKind.LockedIndex)]
    [InlineData("fatal: not a git repository (or any of the parent directories): .git", GitErrorKind.NotAGitRepository)]
    [InlineData("fatal: detected dubious ownership in repository at '/repo'\nTo add an exception for this directory, call:\n\tgit config --global --add safe.directory /repo", GitErrorKind.UnsafeRepository)]
    [InlineData("This repository is configured for Git LFS but 'git-lfs' was not found on your path.", GitErrorKind.LfsMissing)]
    [InlineData("error: cannot run gpg: No such file or directory\nfatal: failed to write commit object", GitErrorKind.SigningToolMissing)]
    [InlineData("error: gpg failed to sign the data\nfatal: failed to write commit object", GitErrorKind.SigningToolMissing)]
    [InlineData("fatal: refusing to merge unrelated histories", GitErrorKind.UnrelatedHistories)]
    [InlineData("error: unable to delete 'b': remote ref does not exist", GitErrorKind.RefNotFound)]
    public void Classify_maps_known_stderr(string stderr, GitErrorKind expected)
        => Assert.Equal(expected, GitErrorClassifier.Classify(exitCode: 128, stderr));

    [Fact]
    public void Clobbered_tags_are_read_from_the_rejected_lines_once_each()
    {
        var stderr =
            " ! [rejected]        v1         -> v1  (would clobber existing tag)\n"
            + " ! [rejected]        release/2  -> release/2  (would clobber existing tag)\n"
            + " ! [rejected]        main       -> origin/main  (non-fast-forward)\n"
            + "\n"
            + " ! [rejected]        v1         -> v1  (would clobber existing tag)";

        Assert.Equal(["v1", "release/2"], GitErrorClassifier.ClobberedTags(stderr));
        Assert.Empty(GitErrorClassifier.ClobberedTags(null));
        Assert.Empty(GitErrorClassifier.ClobberedTags("fatal: Authentication failed"));
    }

    [Fact]
    public void Unknown_stderr_is_never_silently_none()
        => Assert.Equal(GitErrorKind.Unknown, GitErrorClassifier.Classify(1, "some entirely novel failure text"));

    [Fact]
    public void Permission_denied_creating_an_ordinary_file_is_not_a_locked_index()
    {
        // "unable to create" alone is not a LockedIndex needle: git also uses that exact
        // phrase for ordinary filesystem failures with no lock involved at all — e.g. a checkout
        // hitting a file a build still has open, or a directory the user cannot write to. Only
        // "index.lock" / "another git process seems to be running" should say LockedIndex.
        Assert.Equal(
            GitErrorKind.Unknown,
            GitErrorClassifier.Classify(128, "error: unable to create file src/x.cs: Permission denied"));
    }

    [Fact]
    public void Exit_zero_with_no_stderr_is_none()
        => Assert.Equal(GitErrorKind.None, GitErrorClassifier.Classify(0, ""));

    [Fact]
    public void Unsafe_repository_message_names_path_and_safe_directory_command()
    {
        var msg = GitErrorClassifier.FormatUnsafeRepositoryMessage("/path/to/repo");
        Assert.Contains("/path/to/repo", msg);
        Assert.Contains("git config --global --add safe.directory", msg);
    }
}
