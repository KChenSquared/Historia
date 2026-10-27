namespace Historia.Services.Git;

/// <summary>Reads which multi-step operation a repository is in from the marker files git leaves in
/// its directory.</summary>
internal static class OperationMarkers
{
    /// <summary>Derives <see cref="OperationState"/> from on-disk sequencer markers.</summary>
    public static OperationState Detect(string gitDir)
    {
        if (System.IO.Directory.Exists(System.IO.Path.Combine(gitDir, "rebase-merge")) ||
            System.IO.Directory.Exists(System.IO.Path.Combine(gitDir, "rebase-apply")))
            return OperationState.Rebasing;
        if (System.IO.File.Exists(System.IO.Path.Combine(gitDir, "CHERRY_PICK_HEAD")))
            return OperationState.CherryPicking;
        if (System.IO.File.Exists(System.IO.Path.Combine(gitDir, "REVERT_HEAD")))
            return OperationState.Reverting;
        if (System.IO.File.Exists(System.IO.Path.Combine(gitDir, "MERGE_HEAD")))
            return OperationState.Merging;
        if (System.IO.File.Exists(System.IO.Path.Combine(gitDir, "BISECT_LOG")))
            return OperationState.Bisecting;
        return OperationState.None;
    }
}
