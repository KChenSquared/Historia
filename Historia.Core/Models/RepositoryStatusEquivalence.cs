namespace Historia.Models;

/// <summary>
/// Decides whether two working-directory statuses show the user the same thing, so a consumer can
/// skip a refresh that would redraw identical lists. The computation time is not part of the answer.
/// </summary>
public static class RepositoryStatusEquivalence
{
    /// <summary>
    /// True when both statuses list the same entries in the same sections — path, kind, old path,
    /// binary and submodule flags and size — and agree on dirtiness, ahead/behind counts and the
    /// case-collision lists. A changed size makes the statuses different: the panel shows it.
    /// </summary>
    public static bool Equivalent(RepositoryStatus a, RepositoryStatus b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (ReferenceEquals(a, b))
            return true;

        return a.IsDirty == b.IsDirty
            && a.AheadBy == b.AheadBy
            && a.BehindBy == b.BehindBy
            && SameEntries(a.Staged, b.Staged)
            && SameEntries(a.Unstaged, b.Unstaged)
            && SameEntries(a.Untracked, b.Untracked)
            && SameEntries(a.Conflicted, b.Conflicted)
            && SamePaths(a.HiddenCaseCollisions, b.HiddenCaseCollisions)
            && SamePaths(a.AmbiguousCaseCollisions, b.AmbiguousCaseCollisions);
    }

    /// <summary>
    /// <see cref="Equivalent(RepositoryStatus, RepositoryStatus)"/> and the repository info read
    /// with each status agree too: current branch, detached or unborn HEAD, and HEAD commit.
    /// </summary>
    public static bool Equivalent(RepositoryStatus a, RepositoryInfo? infoA, RepositoryStatus b, RepositoryInfo? infoB)
        => Equivalent(a, b) && Equals(infoA, infoB);

    private static bool SameEntries(IReadOnlyList<FileStatusEntry> a, IReadOnlyList<FileStatusEntry> b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
        {
            // FileStatusEntry is a record: the comparison covers every field, size included.
            if (a[i] != b[i])
                return false;
        }

        return true;
    }

    private static bool SamePaths(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
