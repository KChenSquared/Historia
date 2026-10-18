namespace Historia.Services.Graph;

/// <summary>
/// Shared naming for the machine-local graph cache files. A repo maps to one stem
/// <c>graph-&lt;sha1(normalised store root)&gt;</c>; <see cref="GraphWalkCache"/> owns
/// <c>.json.gz</c> and <see cref="GraphMetaStore"/> owns the <c>.meta.*</c> siblings.
/// </summary>
internal static class GraphCachePaths
{
    private static readonly ConcurrentDictionary<string, string> Stems = new(StringComparer.Ordinal);

    public static string Stem(string repoPath) => Stems.GetOrAdd(repoPath, static p => HashStem(StoreRoot(p)));

    public static string File(string repoPath, string ext)
        => Path.Combine(AppPaths.CacheDirectory, Stem(repoPath) + ext);

    private static string HashStem(string root)
    {
        var norm = root.Replace('\\', '/').TrimEnd('/');
        // Case-fold only where the filesystem does (cheap 99%-correct proxy — see GraphWalkCache).
        if (OperatingSystem.IsWindows())
            norm = norm.ToLowerInvariant();
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(norm)));
        return $"graph-{hash}";
    }

    /// <summary>
    /// The directory a repository's cache is named after. Linked worktrees share their main
    /// repository's refs, so every one walks the same tips and they share one store: a linked
    /// worktree resolves to its main worktree (the parent of a common directory named
    /// <c>.git</c>) or, for a bare repository's worktree, to the bare repository itself. Any
    /// other path, a submodule included, is its own root. The walk key in each store's header
    /// still guards what a store may serve, so a wrong guess costs a re-walk, never wrong rows.
    /// </summary>
    internal static string StoreRoot(string repoPath)
    {
        try
        {
            var dotGit = Path.Combine(repoPath, ".git");
            if (!System.IO.File.Exists(dotGit))
                return repoPath;
            var line = System.IO.File.ReadAllText(dotGit).Trim();
            if (!line.StartsWith("gitdir:", StringComparison.Ordinal))
                return repoPath;
            var gitDir = line["gitdir:".Length..].Trim();
            gitDir = Path.GetFullPath(Path.IsPathRooted(gitDir) ? gitDir : Path.Combine(repoPath, gitDir));
            if (!System.IO.File.Exists(Path.Combine(gitDir, "commondir")))
                return repoPath; // a submodule's checkout, not a linked worktree
            var common = Path.TrimEndingDirectorySeparator(RefStateToken.ResolveCommonDir(gitDir));
            return Path.GetFileName(common).Equals(".git", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(common) ?? repoPath
                : common;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return repoPath;
        }
    }
}
