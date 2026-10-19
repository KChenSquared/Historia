namespace Historia.Services.Graph;

/// <summary>
/// Keeps the graph files in the machine-local cache directory bounded. One pass per launch
/// (<see cref="GraphLayoutService.SweepDiskCache"/>), behind the background-work gate:
/// <list type="bullet">
/// <item>Orphaned <c>graph-*.tmp</c> files (a write that crashed before its rename) older than
/// an hour, and leftover <c>.retired</c> files, are deleted.</item>
/// <item>The files of the mode that is off are deleted: every <c>.meta</c> store when commit
/// details are kept in memory, every <c>.json.gz</c> walk cache when they are kept on disk.</item>
/// <item>In disk mode, <c>.meta</c> stores are evicted least recently used first, past
/// <see cref="MaxStores"/> stores or <see cref="MaxTotalBytes"/> in all. The stores of open
/// tabs and the most recently used store are always kept.</item>
/// </list>
/// A store's last use is its header's write time: every header write sets it, and
/// <see cref="GraphMetaStore.TryOpen"/> refreshes it at most hourly.
/// </summary>
internal static class GraphCacheJanitor
{
    internal const int MaxStores = 40;
    internal const long MaxTotalBytes = 4L * 1024 * 1024 * 1024;
    internal static readonly TimeSpan TempFileGrace = TimeSpan.FromHours(1);

    internal readonly record struct SweepResult(int StoresEvicted, int FilesDeleted, long BytesFreed);

    public static SweepResult Sweep(
        IReadOnlyCollection<string> openRepositoryPaths, bool metaFromDisk, DateTime nowUtc,
        int maxStores = MaxStores, long maxTotalBytes = MaxTotalBytes)
    {
        var dir = new DirectoryInfo(AppPaths.CacheDirectory);
        if (!dir.Exists)
            return default;

        var keep = openRepositoryPaths.Select(GraphCachePaths.Stem).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = 0;
        var bytes = 0L;
        void Deleted(long length)
        {
            files++;
            bytes += length;
        }

        var stores = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in dir.EnumerateFiles("graph-*"))
        {
            var name = f.Name;
            var length = f.Length; // a deleted FileInfo no longer reports it
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                if (nowUtc - f.LastWriteTimeUtc > TempFileGrace && TryDelete(f))
                    Deleted(length);
                continue;
            }
            if (name.EndsWith(".retired", StringComparison.OrdinalIgnoreCase))
            {
                if (TryDelete(f))
                    Deleted(length);
                continue;
            }
            if (name.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase))
            {
                if (metaFromDisk && TryDelete(f))
                    Deleted(length);
                continue;
            }
            var meta = name.IndexOf(".meta.", StringComparison.OrdinalIgnoreCase);
            if (meta <= 0)
                continue;
            var stem = name[..meta];
            if (!stores.TryGetValue(stem, out var group))
                stores[stem] = group = [];
            group.Add(f);
        }

        var evicted = 0;
        var ranked = stores
            .Select(kv => (
                Stem: kv.Key,
                Kept: metaFromDisk && keep.Contains(kv.Key),
                LastUsed: LastUsed(kv.Key, kv.Value),
                Bytes: kv.Value.Sum(f => f.Length)))
            .OrderByDescending(s => s.Kept)
            .ThenByDescending(s => s.LastUsed);
        var keptStores = 0;
        var keptBytes = 0L;
        var keptMostRecent = false;
        foreach (var s in ranked)
        {
            // Open stores rank first; the first store after them is the most recently used one.
            var fits = !keptMostRecent || (keptStores < maxStores && keptBytes + s.Bytes <= maxTotalBytes);
            if (s.Kept || (metaFromDisk && fits))
            {
                keptStores++;
                keptBytes += s.Bytes;
                keptMostRecent |= !s.Kept;
                continue;
            }
            var freed = Evict(dir, s.Stem);
            if (freed.Files > 0)
            {
                evicted++;
                files += freed.Files;
                bytes += freed.Bytes;
            }
        }

        return new SweepResult(evicted, files, bytes);
    }

    private static DateTime LastUsed(string stem, List<FileInfo> group)
    {
        var hdr = group.Find(f => f.Name.Equals(stem + GraphMetaStore.HdrExt, StringComparison.OrdinalIgnoreCase));
        return hdr?.LastWriteTimeUtc ?? group.Max(f => f.LastWriteTimeUtc);
    }

    /// <summary>Deletes every file of one store under its writer lock, so no append or reset can
    /// interleave. Each file is renamed before it is deleted: a reader holding it open keeps
    /// reading, and the name is free at once.</summary>
    private static (int Files, long Bytes) Evict(DirectoryInfo dir, string stem)
    {
        var files = 0;
        var bytes = 0L;
        lock (GraphCacheLocks.ForStem(stem))
        {
            foreach (var f in dir.EnumerateFiles(stem + ".meta.*"))
            {
                var length = f.Length;
                var retired = $"{f.FullName}.{Guid.NewGuid():N}.retired";
                try
                {
                    f.MoveTo(retired);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                files++;
                bytes += length;
                try { File.Delete(retired); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return (files, bytes);
    }

    private static bool TryDelete(FileInfo f)
    {
        try
        {
            f.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
