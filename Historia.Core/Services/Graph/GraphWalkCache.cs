namespace Historia.Services.Graph;

/// <summary>
/// On-disk persistence of the raw graph walk (commits + decorations) keyed by a fingerprint of
/// the repo's refs. A hit skips the whole revwalk on the next launch — only
/// <see cref="DagLayoutEngine.Compute"/> (~tens of ms) still runs. Cached only for the plain
/// all-refs, no-path-filter, no-stashes query; anything else re-walks.
/// </summary>
internal sealed class GraphWalkCache
{
    /// <summary>The value both persisted formats (this <c>.json.gz</c> and the
    /// <see cref="GraphMetaStore"/> header) store in their <c>WalkEngine</c> field, recording
    /// which walk produced the rows. Only the <c>git log --date-order</c> walk writes them. A
    /// file carrying any other value came from a walk engine that no longer exists and whose row
    /// order can differ on equal-timestamp ties, so it is never resumed (<c>skip: have</c>); an
    /// exhausted one is still a complete, valid history.</summary>
    internal const int CliWalkMarker = 1;

    // The prime's resume skip and the canonical (sorted) tip order both define the cache format
    // compatibility.
    // v5: rows in commit-date order; v4 files were --topo-order and must never be shown or resumed.
    private const int Version = 5;
    private const int MaxFiles = 40;

    private static readonly JsonSerializerOptions Json = new() { IncludeFields = false };

    // The optimistic snapshot (TryLoadStale) and the real, fingerprint-verified load (TryLoad)
    // read the same file within moments of each other during a tab's first paint -- on a large
    // repo that's a second, fully redundant multi-hundred-ms disk read + JSON deserialize of a
    // file nothing could have written to in between (the walk that would rewrite it hasn't even
    // started yet). Cache the raw DTO briefly per repo so the second reader gets it for free, and
    // separately cache the CONVERTED Loaded (ToLoaded is its own real cost -- allocating a
    // RawCommit per commit -- and both callers use the same metadataOnDisk value in the common
    // case, so converting a ~100k-commit history twice back to back is pure waste, and appears to
    // provoke extra GC pauses that make the second conversion look even slower than the first). A
    // short TTL bounds how long a result could theoretically go unnoticed if something else ever
    // reads this cache long after these two calls. Only successful reads are cached — a miss
    // isn't worth remembering and must never mask a save that lands moments later.
    private static readonly TimeSpan DefaultReadCacheTtl = TimeSpan.FromSeconds(5);

    // An entry nothing reads again is removed this long after its TTL has passed.
    private static readonly TimeSpan ExpiryGrace = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _readCacheTtl;

    public GraphWalkCache() : this(DefaultReadCacheTtl)
    {
    }

    internal GraphWalkCache(TimeSpan readCacheTtl) => _readCacheTtl = readCacheTtl;

    private sealed class CachedRead
    {
        public required CacheFile Dto;
        public required long Timestamp;
        public Loaded? ConvertedMetadataOnDisk;
        public Loaded? ConvertedFull;
    }

    private readonly Dictionary<string, CachedRead> _readCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _readCacheSync = new();

    public sealed record Loaded(
        List<RawCommit> Commits,
        Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>> Decorations,
        bool Exhausted);

    public static bool Supports(GraphQuery query) =>
        query.RefScope == GraphRefScope.AllRefs
        && string.IsNullOrEmpty(query.PathFilter);

    /// <param name="metadataOnDisk">when true the per-commit strings are served from the
    /// paged <c>DiskCommitStore</c>, so a warm load only needs id + parent count here — skip
    /// materialising the message/author/email text for a whole huge history.</param>
    public Loaded? TryLoad(string repoPath, GraphQuery query, int wantCount, long fingerprint, bool metadataOnDisk = false)
    {
        if (!Supports(query))
            return null;

        CachedRead? entry;
        using (Historia.Diagnostics.PerfLog.Scope("TryLoad ReadFile"))
            entry = GetCachedRead(repoPath);
        var dto = entry?.Dto;
        if (dto is null)
        {
            Historia.Diagnostics.PerfLog.Mark("walk cache MISS: no file / unreadable / wrong version");
            return null;
        }
        if (dto.Fingerprint != fingerprint)
        {
            Historia.Diagnostics.PerfLog.Mark("walk cache MISS: fingerprint changed (refs moved since save)");
            return null;
        }
        if (dto.IncludeRemoteBranches != query.IncludeRemoteBranches)
        {
            Historia.Diagnostics.PerfLog.Mark("walk cache MISS: IncludeRemoteBranches mismatch");
            return null;
        }
        if (!dto.Exhausted && dto.WalkEngine != CliWalkMarker)
        {
            Historia.Diagnostics.PerfLog.Mark(
                $"walk cache MISS: non-exhausted save written by a retired walk engine (marker {dto.WalkEngine})");
            return null;
        }

        // Enough rows? Either the whole history is here, or it already holds what we need.
        if (!dto.Exhausted && dto.Commits.Count < wantCount)
        {
            Historia.Diagnostics.PerfLog.Mark($"walk cache MISS: only {dto.Commits.Count} rows saved, need {wantCount}");
            return null;
        }

        using (Historia.Diagnostics.PerfLog.Scope("TryLoad ToLoaded"))
            return GetOrConvert(entry!, metadataOnDisk);
    }

    /// <summary>
    /// Loads the cached walk WITHOUT checking it still matches the repo's current refs —
    /// for an optimistic first paint the caller shows immediately, before the fresh ref-
    /// fingerprint check (which needs a ref snapshot, possibly a <c>for-each-ref</c> scan) even
    /// completes. The result may be momentarily stale: a branch moved, a new commit landed, or
    /// history was rewritten since the file was written. The caller is responsible for treating
    /// this as provisional and replacing it with the real, fingerprint-verified layout once that
    /// finishes — this method never substitutes for <see cref="TryLoad"/>, only precedes it.
    /// </summary>
    public Loaded? TryLoadStale(string repoPath, GraphQuery query, bool metadataOnDisk = false)
    {
        if (!Supports(query))
            return null;

        var entry = GetCachedRead(repoPath);
        var dto = entry?.Dto;
        if (dto is null)
        {
            Historia.Diagnostics.PerfLog.Mark("optimistic snapshot MISS: no file / unreadable / wrong version");
            return null;
        }
        if (dto.IncludeRemoteBranches != query.IncludeRemoteBranches)
        {
            Historia.Diagnostics.PerfLog.Mark("optimistic snapshot MISS: IncludeRemoteBranches mismatch");
            return null;
        }
        if (dto.Commits.Count == 0)
        {
            Historia.Diagnostics.PerfLog.Mark("optimistic snapshot MISS: saved file is empty");
            return null;
        }

        return GetOrConvert(entry!, metadataOnDisk);
    }

    /// <summary>How many repos currently hold a cached read.</summary>
    internal int CachedReadCount
    {
        get
        {
            lock (_readCacheSync)
                return _readCache.Count;
        }
    }

    /// <summary>Drops this repo's cached read
    /// entry (the raw DTO plus up to two converted <see cref="Loaded"/> commit lists) — called on
    /// <see cref="GraphLayoutService.Hibernate"/> so a warm-loaded repo's walk cache doesn't stay
    /// resident for the rest of the session after the tab is switched away from. A miss on the
    /// next read is no different from one past the read TTL — just a re-read.</summary>
    public void Evict(string repoPath)
    {
        lock (_readCacheSync)
            _readCache.Remove(repoPath);
    }

    private CachedRead? GetCachedRead(string repoPath)
    {
        lock (_readCacheSync)
        {
            if (_readCache.TryGetValue(repoPath, out var cached)
                && System.Diagnostics.Stopwatch.GetElapsedTime(cached.Timestamp) < _readCacheTtl)
                return cached;
        }

        var dto = ReadFileFromDisk(repoPath);
        if (dto is null)
            return null;

        var entry = new CachedRead { Dto = dto, Timestamp = System.Diagnostics.Stopwatch.GetTimestamp() };
        lock (_readCacheSync)
            _readCache[repoPath] = entry;
        ScheduleExpiry(repoPath, entry);
        return entry;
    }

    /// <summary>Removes <paramref name="entry"/> shortly after its TTL, so a repo nobody reads
    /// again does not keep its DTO and converted commit lists resident. The removal is skipped
    /// when a newer read, a save or an eviction has already replaced the entry.</summary>
    private void ScheduleExpiry(string repoPath, CachedRead entry)
    {
        _ = Task.Delay(_readCacheTtl + ExpiryGrace).ContinueWith(_ =>
        {
            lock (_readCacheSync)
            {
                if (_readCache.TryGetValue(repoPath, out var current) && ReferenceEquals(current, entry))
                    _readCache.Remove(repoPath);
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Converts (and caches) exactly once per distinct <paramref name="metadataOnDisk"/>
    /// value for this cached read -- the optimistic snapshot and the real load both convert the
    /// same DTO, and in the common case (metadata-from-disk paging off) with the same flag, so
    /// the second caller gets the already-built commit list for free instead of re-allocating one
    /// per commit in a multi-hundred-thousand-commit history a second time.</summary>
    private static Loaded GetOrConvert(CachedRead entry, bool metadataOnDisk)
    {
        lock (entry)
        {
            if (metadataOnDisk)
                return entry.ConvertedMetadataOnDisk ??= ToLoaded(entry.Dto, metadataOnDisk: true);
            return entry.ConvertedFull ??= ToLoaded(entry.Dto, metadataOnDisk: false);
        }
    }

    private static CacheFile? ReadFileFromDisk(string repoPath)
    {
        var path = FileFor(repoPath);
        try
        {
            if (!File.Exists(path))
                return null; // routine — nothing saved yet, not a fault

            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            var dto = JsonSerializer.Deserialize<CacheFile>(gz, Json);
            if (dto is null)
            {
                Historia.Diagnostics.PerfLog.Mark($"walk cache file unreadable: null DTO ({Path.GetFileName(path)})");
                return null;
            }
            if (dto.Version != Version)
            {
                Historia.Diagnostics.PerfLog.Mark(
                    $"walk cache file schema mismatch: file v{dto.Version}, code v{Version} ({Path.GetFileName(path)})");
                return null;
            }
            return dto;
        }
        catch (Exception ex)
        {
            // A file that exists but fails to decompress/deserialize is a real fault (e.g. an
            // interrupted write from an earlier build) — worth a loud mark, unlike "no file yet".
            Historia.Diagnostics.PerfLog.Mark($"walk cache file CORRUPT: {ex.GetType().Name}: {ex.Message} ({Path.GetFileName(path)})");
            return null;
        }
    }

    private static Loaded ToLoaded(CacheFile dto, bool metadataOnDisk)
    {
        var commits = new List<RawCommit>(dto.Commits.Count);
        foreach (var c in dto.Commits)
        {
            var parents = new GitObjectId[c.Parents.Length];
            for (var i = 0; i < parents.Length; i++)
                parents[i] = GitObjectId.Parse(c.Parents[i]);
            var id = GitObjectId.Parse(c.Id);
            commits.Add(new RawCommit(id, parents, metadataOnDisk
                // keep the timestamps even in the id/parents-only load — DagLayoutEngine's
                // tip-priority pre-pass sorts by CommitWhen, and the paged store is only consulted
                // for the heavy strings. Zeroing the dates here would pin the trunk to SHA order.
                ? new CommitMetadata
                {
                    Id = id,
                    MessageShort = "",
                    AuthorName = "",
                    AuthorEmail = "",
                    AuthorWhen = c.AuthorWhen,
                    CommitWhen = c.CommitWhen,
                    ParentCount = c.ParentCount,
                }
                : new CommitMetadata
                {
                    Id = id,
                    MessageShort = c.MessageShort,
                    AuthorName = c.AuthorName,
                    AuthorEmail = c.AuthorEmail,
                    AuthorWhen = c.AuthorWhen,
                    CommitWhen = c.CommitWhen,
                    ParentCount = c.ParentCount,
                }));
        }

        var decos = new Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>>(dto.Decorations.Count);
        foreach (var (sha, list) in dto.Decorations)
            decos[GitObjectId.Parse(sha)] = list.Select(FromDto).ToArray();

        return new Loaded(commits, decos, dto.Exhausted);
    }

    /// <summary>Deletes every persisted walk file (and any leftover temp files), plus the
    /// disk-backed metadata store. Returns the count.</summary>
    public int WipeAll()
    {
        lock (_readCacheSync)
            _readCache.Clear();

        var n = 0;
        try
        {
            var dir = new DirectoryInfo(AppPaths.CacheDirectory);
            if (!dir.Exists)
                return GraphMetaStore.Wipe();
            foreach (var f in dir.GetFiles("graph-*.json.gz").Concat(dir.GetFiles("*.tmp")))
            {
                try { f.Delete(); n++; } catch { /* skip locked */ }
            }
        }
        catch { }
        return n + GraphMetaStore.Wipe();
    }

    public void Save(
        string repoPath, GraphQuery query, long fingerprint, bool exhausted,
        IReadOnlyList<RawCommit> commits,
        IReadOnlyDictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>> decorations)
    {
        if (!Supports(query))
            return;

        try
        {
            Directory.CreateDirectory(AppPaths.CacheDirectory);

            var dto = new CacheFile
            {
                Version = Version,
                Fingerprint = fingerprint,
                IncludeRemoteBranches = query.IncludeRemoteBranches,
                Exhausted = exhausted,
                WalkEngine = CliWalkMarker,
                Commits = commits.Select(c => new CommitDto
                {
                    Id = c.Id.ToString(),
                    Parents = c.ParentIds.Select(p => p.ToString()).ToArray(),
                    MessageShort = c.Metadata.MessageShort,
                    AuthorName = c.Metadata.AuthorName,
                    AuthorEmail = c.Metadata.AuthorEmail,
                    AuthorWhen = c.Metadata.AuthorWhen,
                    CommitWhen = c.Metadata.CommitWhen,
                    ParentCount = c.Metadata.ParentCount,
                }).ToList(),
                Decorations = decorations.ToDictionary(
                    kv => kv.Key.ToString(),
                    kv => kv.Value.Select(ToDto).ToList()),
            };

            var dest = FileFor(repoPath);
            var tmp = $"{dest}.{Guid.NewGuid():N}.tmp";
            using (var fs = File.Create(tmp))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                JsonSerializer.Serialize(gz, dto, Json);
            File.Move(tmp, dest, overwrite: true);

            // The on-disk file just changed under this path -- drop the cached read so a later
            // TryLoad/TryLoadStale within the TTL window doesn't hand back the pre-save DTO.
            lock (_readCacheSync)
                _readCache.Remove(repoPath);

            Prune();
        }
        catch { /* cache write is best-effort */ }
    }

    private static void Prune()
    {
        try
        {
            var dir = new DirectoryInfo(AppPaths.CacheDirectory);

            foreach (var f in dir.GetFiles("graph-*.json.gz")
                         .OrderByDescending(f => f.LastWriteTimeUtc)
                         .Skip(MaxFiles))
                f.Delete();
        }
        catch { }
    }

    // Case-fold only where the filesystem does. A case-insensitive macOS volume would also want
    // folding; IsWindows is deliberately used as the cheap ~99%-correct proxy rather than probing
    // each volume's case sensitivity.
    private static string FileFor(string repoPath) => GraphCachePaths.File(repoPath, ".json.gz");

    private static DecoDto ToDto(GraphDecorationInput d) => new()
    {
        Type = (int)d.Type,
        Label = d.Label,
        IsLocal = d.IsLocal,
        Remotes = d.Remotes,
        IsHead = d.IsHead,
        Siblings = d.Siblings?.Select(ToDto).ToList(),
    };

    private static GraphDecorationInput FromDto(DecoDto d) => new(
        (DecorationType)d.Type,
        d.Label,
        d.IsLocal,
        d.Remotes,
        d.IsHead,
        d.Siblings?.Select(FromDto).ToArray());

    private sealed class CacheFile
    {
        public int Version { get; set; }
        public long Fingerprint { get; set; }
        public bool IncludeRemoteBranches { get; set; }
        public bool Exhausted { get; set; }
        public int WalkEngine { get; set; }
        public List<CommitDto> Commits { get; set; } = [];
        public Dictionary<string, List<DecoDto>> Decorations { get; set; } = [];
    }

    private sealed class CommitDto
    {
        public string Id { get; set; } = "";
        public string[] Parents { get; set; } = [];
        public string MessageShort { get; set; } = "";
        public string AuthorName { get; set; } = "";
        public string AuthorEmail { get; set; } = "";
        public DateTimeOffset AuthorWhen { get; set; }
        public DateTimeOffset CommitWhen { get; set; }
        public int ParentCount { get; set; }
    }

    private sealed class DecoDto
    {
        public int Type { get; set; }
        public string Label { get; set; } = "";
        public bool IsLocal { get; set; }
        public string[]? Remotes { get; set; }
        public bool IsHead { get; set; }
        public List<DecoDto>? Siblings { get; set; }
    }
}
