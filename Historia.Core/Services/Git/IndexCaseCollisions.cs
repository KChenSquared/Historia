namespace Historia.Services.Git;

/// <summary>A stage-0 index entry whose path differs from another entry's only in letter case.</summary>
internal readonly record struct CaseTwin(string Path, string Mode, string BlobId);

/// <summary>
/// Detects index paths that differ only in letter case and share a single on-disk file on
/// case-insensitive filesystems, caching collision groups and worktree hashes against index and
/// config timestamps, and remembering which name last held the disk content. Staging lists the
/// whole index under its own gate; status asks only about the paths it is showing, and hashes
/// worktree files under a gate of its own, so neither waits on the other.
/// </summary>
internal sealed class IndexCaseCollisions(IGitCliService cli, string gitDirectory, string workingDirectory)
{
    // Above this many candidates the targeted query (cost grows with candidates times index size)
    // is slower than one whole-index listing, which is also cached against the index timestamp.
    internal const int MaxTargetedCandidates = 100;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _hashGate = new(1, 1);
    private readonly string _cwd = string.IsNullOrWhiteSpace(workingDirectory) ? gitDirectory : workingDirectory;

    private sealed record IgnoreCaseCache(((long Mtime, long Length) Config, (long Mtime, long Length) Worktree) Stamp, bool Value);
    private IgnoreCaseCache? _ignoreCaseCache;

    // Keyed case-insensitively, so every member of a group maps to the same owner.
    private readonly Dictionary<string, string> _owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _ownersGate = new();

    private sealed record GroupsCache(
        (long Mtime, long Length) Stamp,
        IReadOnlyDictionary<string, IReadOnlyList<CaseTwin>> Groups);
    private GroupsCache? _groupsCache;

    private sealed record CandidateCache(
        (long Mtime, long Length) Stamp,
        string Key,
        IReadOnlyDictionary<string, IReadOnlyList<CaseTwin>> Groups);
    private CandidateCache? _candidateCache;

    private readonly Dictionary<string, (long Length, long Mtime, string Hash)> _worktreeHashCache = new(StringComparer.Ordinal);

    public async Task<bool> IgnoreCaseAsync(CancellationToken ct = default)
    {
        // The stamp is read before git config runs, so a config write during it invalidates the entry.
        var stamp = GetConfigStamp();

        var cached = Volatile.Read(ref _ignoreCaseCache);
        if (cached is not null && cached.Stamp == stamp)
            return cached.Value;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _ignoreCaseCache);
            if (cached is not null && cached.Stamp == stamp)
                return cached.Value;

            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["config", "--type=bool", "--get", "core.ignorecase"],
                WorkingDirectory = _cwd,
            }, ct).ConfigureAwait(false);

            bool ignoreCase;
            if (result.IsSuccess)
            {
                ignoreCase = string.Equals(result.StandardOutput.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            }
            else if (result.ExitCode == 1 && result.ErrorKind is not (GitErrorKind.Cancelled or GitErrorKind.TimedOut))
            {
                ignoreCase = false;
            }
            else
            {
                throw HistoriaGitException.FromCli(result, "Read core.ignorecase config");
            }

            Volatile.Write(ref _ignoreCaseCache, new IgnoreCaseCache(stamp, ignoreCase));
            return ignoreCase;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Records that <paramref name="owner"/>'s index blob is the content of the shared disk file.
    /// </summary>
    public void RecordOwner(string owner)
    {
        lock (_ownersGate)
            _owners[owner] = owner;
    }

    /// <summary>The group member last seen holding the disk content, or null when never seen.</summary>
    public string? OwnerOf(string path)
    {
        lock (_ownersGate)
            return _owners.TryGetValue(path, out var owner) ? owner : null;
    }

    /// <summary>
    /// Collision groups for a stage operation. Returns the empty map without listing the index when
    /// the last listing found no collisions, so staging in an ordinary repository spawns nothing.
    /// </summary>
    public Task<IReadOnlyDictionary<string, IReadOnlyList<CaseTwin>>> GroupsForStagingAsync(CancellationToken ct = default)
    {
        var cached = Volatile.Read(ref _groupsCache);
        return cached is not null && cached.Groups.Count == 0
            ? Task.FromResult(cached.Groups)
            : GroupsAsync(ct);
    }

    /// <summary>The index mode of <paramref name="path"/>, read from its peers' entries.</summary>
    internal static string? ModeOf(IReadOnlyDictionary<string, IReadOnlyList<CaseTwin>> groups, string path)
    {
        if (!groups.TryGetValue(path, out var peers) || peers.Count == 0
            || !groups.TryGetValue(peers[0].Path, out var peersOfPeer))
            return null;

        foreach (var twin in peersOfPeer)
        {
            if (string.Equals(twin.Path, path, StringComparison.Ordinal))
                return twin.Mode;
        }
        return null;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<CaseTwin>>> GroupsAsync(CancellationToken ct = default)
    {
        // The stamp is read before the listing, so an index write during it invalidates the entry.
        var stamp = GetIndexStamp();
        if (stamp == (0L, 0L))
            return ImmutableDictionary<string, IReadOnlyList<CaseTwin>>.Empty;

        var cached = Volatile.Read(ref _groupsCache);
        if (cached is not null && cached.Stamp == stamp)
            return cached.Groups;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _groupsCache);
            if (cached is not null && cached.Stamp == stamp)
                return cached.Groups;

            var sw = Stopwatch.StartNew();
            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["ls-files", "-s", "-z"],
                WorkingDirectory = _cwd,
                RawStandardOutput = true,
                Timeout = Timeout.InfiniteTimeSpan,
            }, ct).ConfigureAwait(false);

            if (!result.IsSuccess)
                throw HistoriaGitException.FromCli(result, "List index entries");

            var groups = ParseGroups(result.StandardOutput, out var totalEntries, out var groupCount);
            sw.Stop();
            PerfLog.Mark($"IndexCaseCollisions.GroupsAsync: {sw.ElapsedMilliseconds} ms, {totalEntries} entries, {groupCount} groups");

            Volatile.Write(ref _groupsCache, new GroupsCache(stamp, groups));
            return groups;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The pathspec that matches <paramref name="path"/> and any case-twin of it.</summary>
    internal static string IcaseLiteralPathspec(string path) => ":(literal,icase)" + path;

    /// <summary>
    /// Collision groups among <paramref name="paths"/> only: each path and every index entry that
    /// differs from it only in letter case, found with literal case-insensitive pathspecs
    /// (<paramref name="chunk"/> splits the pathspecs to fit a command line). Cached against the
    /// index timestamp and the path list. Falls back to the whole-index listing when there are too
    /// many paths for the targeted query to pay off or when the query fails.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<CaseTwin>>> GroupsForCandidatesAsync(
        IReadOnlyList<string> paths,
        Func<IReadOnlyList<string>, IEnumerable<IReadOnlyList<string>>> chunk,
        CancellationToken ct = default)
    {
        if (paths.Count == 0)
            return ImmutableDictionary<string, IReadOnlyList<CaseTwin>>.Empty;
        if (paths.Count > MaxTargetedCandidates)
            return await GroupsAsync(ct).ConfigureAwait(false);

        // The stamp is read before the query, so an index write during it invalidates the entry.
        var stamp = GetIndexStamp();
        if (stamp == (0L, 0L))
            return ImmutableDictionary<string, IReadOnlyList<CaseTwin>>.Empty;

        var key = string.Join(' ', paths);
        var cached = Volatile.Read(ref _candidateCache);
        if (cached is not null && cached.Stamp == stamp && cached.Key == key)
            return cached.Groups;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new StringBuilder();
        foreach (var specs in chunk(paths.Select(IcaseLiteralPathspec).ToList()))
        {
            var arguments = new List<string>(specs.Count + 4) { "ls-files", "-s", "-z", "--" };
            arguments.AddRange(specs);

            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = arguments,
                WorkingDirectory = _cwd,
                RawStandardOutput = true,
                Timeout = Timeout.InfiniteTimeSpan,
            }, ct).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                ct.ThrowIfCancellationRequested();
                AppLog.Write(AppLogLevel.Warn, "git",
                    $"Case collision candidate query failed (exit {result.ExitCode}, {result.ErrorKind}); listing the whole index.");
                return await GroupsAsync(ct).ConfigureAwait(false);
            }

            // A twin pair split across chunks is reported by both; keep each entry once.
            foreach (var entry in result.StandardOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (seen.Add(entry))
                    merged.Append(entry).Append(' ');
            }
        }

        var groups = ParseGroups(merged.ToString());
        Volatile.Write(ref _candidateCache, new CandidateCache(stamp, key, groups));

        // A staging listing that found no collisions is reused without a stamp check; a twin seen
        // here makes it stale.
        if (groups.Count > 0 && Volatile.Read(ref _groupsCache) is { Groups.Count: 0 })
            Volatile.Write(ref _groupsCache, null);

        return groups;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<CaseTwin>> ParseGroups(string lsFilesStageOutput)
        => ParseGroups(lsFilesStageOutput, out _, out _);

    internal static IReadOnlyDictionary<string, IReadOnlyList<CaseTwin>> ParseGroups(
        string lsFilesStageOutput,
        out int totalEntries,
        out int groupCount)
    {
        totalEntries = 0;
        groupCount = 0;

        if (string.IsNullOrEmpty(lsFilesStageOutput))
            return ImmutableDictionary<string, IReadOnlyList<CaseTwin>>.Empty;

        var entries = lsFilesStageOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        totalEntries = entries.Length;

        var caseGroups = new Dictionary<string, List<CaseTwin>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var tabIndex = entry.IndexOf('\t');
            if (tabIndex < 0)
                continue;

            var meta = entry[..tabIndex];
            var path = entry[(tabIndex + 1)..];

            var space1 = meta.IndexOf(' ');
            if (space1 < 0)
                continue;
            var space2 = meta.IndexOf(' ', space1 + 1);
            if (space2 < 0)
                continue;

            var mode = meta[..space1];
            var oid = meta.Substring(space1 + 1, space2 - space1 - 1);
            var stage = meta[(space2 + 1)..];

            if (stage != "0")
                continue;

            if (!caseGroups.TryGetValue(path, out var list))
            {
                list = [];
                caseGroups[path] = list;
            }
            list.Add(new CaseTwin(path, mode, oid));
        }

        var result = new Dictionary<string, IReadOnlyList<CaseTwin>>(StringComparer.Ordinal);

        foreach (var group in caseGroups.Values)
        {
            if (group.Count < 2)
                continue;

            groupCount++;

            for (var i = 0; i < group.Count; i++)
            {
                var member = group[i];
                var peers = new List<CaseTwin>(group.Count - 1);
                for (var j = 0; j < group.Count; j++)
                {
                    if (i != j)
                    {
                        peers.Add(group[j]);
                    }
                }
                result[member.Path] = peers;
            }
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<string, string>> HashWorktreeAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0)
            return ImmutableDictionary<string, string>.Empty;

        await _hashGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var candidateSet = new HashSet<string>(paths, StringComparer.Ordinal);
            var toRemove = new List<string>();
            foreach (var cachedPath in _worktreeHashCache.Keys)
            {
                if (!candidateSet.Contains(cachedPath))
                    toRemove.Add(cachedPath);
            }
            foreach (var p in toRemove)
                _worktreeHashCache.Remove(p);

            var toHash = new List<(string Path, long Length, long Mtime)>();
            foreach (var path in paths)
            {
                var fullPath = Path.Combine(_cwd, path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath))
                    continue;

                // Stat before hashing, so a write during the hash leaves a stale stamp, not a stale hash.
                var fi = new FileInfo(fullPath);
                var length = fi.Length;
                var mtime = fi.LastWriteTimeUtc.Ticks;

                if (_worktreeHashCache.TryGetValue(path, out var cached)
                    && cached.Length == length
                    && cached.Mtime == mtime)
                {
                    continue;
                }

                toHash.Add((path, length, mtime));
            }

            if (toHash.Count > 0)
            {
                var stdin = string.Join("\n", toHash.Select(x => x.Path)) + "\n";
                var result = await cli.RunAsync(new GitCliRequest
                {
                    Arguments = ["hash-object", "--stdin-paths"],
                    WorkingDirectory = _cwd,
                    StandardInput = stdin,
                    Timeout = Timeout.InfiniteTimeSpan,
                }, ct).ConfigureAwait(false);

                if (result.IsSuccess)
                {
                    var lines = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length == toHash.Count)
                    {
                        for (var i = 0; i < toHash.Count; i++)
                        {
                            var item = toHash[i];
                            _worktreeHashCache[item.Path] = (item.Length, item.Mtime, lines[i].Trim());
                        }
                    }
                    else
                    {
                        PerfLog.Mark($"IndexCaseCollisions.HashWorktreeAsync count mismatch: got {lines.Length}, expected {toHash.Count}");
                    }
                }
                else
                {
                    ct.ThrowIfCancellationRequested();
                    PerfLog.Mark($"IndexCaseCollisions.HashWorktreeAsync failed: exit={result.ExitCode}, kind={result.ErrorKind}");
                }
            }

            var dict = new Dictionary<string, string>(_worktreeHashCache.Count, StringComparer.Ordinal);
            foreach (var (p, entry) in _worktreeHashCache)
            {
                dict[p] = entry.Hash;
            }
            return dict;
        }
        finally
        {
            _hashGate.Release();
        }
    }

    internal static bool SharesOneDiskEntry(string workDir, string a, string b)
        => TryGetSharedDiskEntry(workDir, a, b, out _, out _);

    /// <summary>
    /// True when <paramref name="path"/> and <paramref name="twin"/> share one disk file whose name,
    /// at the segment where the two paths differ, is spelled exactly as in <paramref name="path"/>.
    /// Checkout recreates the file under the name it writes, so the spelling follows the last writer.
    /// </summary>
    internal static bool IsDiskNameOf(string workDir, string path, string twin)
        => TryGetSharedDiskEntry(workDir, path, twin, out var segment, out var diskName)
           && string.Equals(segment, diskName, StringComparison.Ordinal);

    private static bool TryGetSharedDiskEntry(string workDir, string a, string b, out string segmentOfA, out string diskName)
    {
        segmentOfA = "";
        diskName = "";
        try
        {
            var fullA = Path.Combine(workDir, a.Replace('/', Path.DirectorySeparatorChar));
            var fullB = Path.Combine(workDir, b.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(fullA) || !File.Exists(fullB))
                return false;

            var segsA = a.Split('/');
            var segsB = b.Split('/');

            if (segsA.Length != segsB.Length)
                return false;

            int diffIndex = -1;
            for (var i = 0; i < segsA.Length; i++)
            {
                if (!string.Equals(segsA[i], segsB[i], StringComparison.Ordinal))
                {
                    if (!string.Equals(segsA[i], segsB[i], StringComparison.OrdinalIgnoreCase))
                        return false;

                    diffIndex = i;
                    break;
                }
            }

            if (diffIndex < 0)
                return false;

            var parentDir = diffIndex == 0
                ? workDir
                : Path.Combine(workDir, string.Join(Path.DirectorySeparatorChar, segsA[..diffIndex]));

            if (!Directory.Exists(parentDir))
                return false;

            var targetSeg = segsA[diffIndex];
            var count = 0;
            var match = "";
            foreach (var entry in Directory.EnumerateFileSystemEntries(parentDir))
            {
                var name = Path.GetFileName(entry);
                if (string.Equals(name, targetSeg, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                    match = name;
                }
            }

            if (count != 1)
                return false;

            segmentOfA = targetSeg;
            diskName = match;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The worktree file is read as well, because extensions.worktreeConfig lets it override the
    // shared config. Global and included config files are not watched.
    private ((long Mtime, long Length) Config, (long Mtime, long Length) Worktree) GetConfigStamp()
        => (GetFileStamp(Path.Combine(RepoConfigReader.ResolveCommonDir(gitDirectory), "config")),
            GetFileStamp(Path.Combine(gitDirectory, "config.worktree")));

    private (long Mtime, long Length) GetIndexStamp() => GetFileStamp(Path.Combine(gitDirectory, "index"));

    private static (long Mtime, long Length) GetFileStamp(string path)
    {
        if (File.Exists(path))
        {
            var fi = new FileInfo(path);
            return (fi.LastWriteTimeUtc.Ticks, fi.Length);
        }
        return (0L, 0L);
    }
}
