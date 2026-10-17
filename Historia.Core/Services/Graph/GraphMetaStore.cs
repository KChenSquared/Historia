namespace Historia.Services.Graph;

/// <summary>
/// On-disk, random-access store for the graph's per-commit metadata. When the
/// "Read commit details from disk" setting is on, <see cref="GraphLayoutService"/> spills the
/// laid-out rows here and points <see cref="GraphLayout.Commits"/> at a
/// <see cref="DiskCommitStore"/>, so only the fixed-size layout arrays stay resident on a
/// large history. Siblings per repo:
/// <list type="bullet">
/// <item><c>.meta.hdr</c> — JSON: version, walk key, remote-branch flag, row count, record
/// count, order version, exhausted.</item>
/// <item><c>.meta.dat</c> — one binary RECORD per distinct commit (sha, three strings, two
/// timestamps, parent count) — append-only, never rewritten by a ref change.</item>
/// <item><c>.meta.idx</c> — one <see cref="long"/> per RECORD: its start offset in
/// <c>.meta.dat</c>.</item>
/// <item><c>.meta.search</c> — one lowercased line per RECORD (<c>sha ␟ message ␟ author</c>)
/// for Ctrl+F.</item>
/// <item><c>.meta.order.&lt;n&gt;</c> (only once a ref change has actually reordered
/// history) — one <see cref="int"/> per ROW: which record occupies that row. Absent
/// (<c>OrderVersion == 0</c>) means the identity order — row <c>i</c> IS record <c>i</c>, exactly
/// the common case, and the only one before any reorder has ever run.</item>
/// <item><c>.meta.tips</c> — the sorted tip SHAs (20 or 32 bytes each) the current order was
/// built from; not versioned, always describes the CURRENT order.</item>
/// </list>
/// All writes are append-only and best-effort; any read fault downgrades the caller to the
/// in-memory path.
/// </summary>
internal sealed class GraphMetaStore
{
    // Version 6: rows stored in commit-date order (`git log --date-order`), canonical sorted tips,
    // and record count with order indirection in the header.
    internal const int Version = 6;
    internal const string HdrExt = ".meta.hdr";
    internal const string DatExt = ".meta.dat";
    internal const string IdxExt = ".meta.idx";
    internal const string SearchExt = ".meta.search";
    internal const string TipsExt = ".meta.tips";

    /// <summary>Field separator in <c>.meta.search</c> lines — US (unit separator).</summary>
    internal const char Sep = (char)0x1f;

    internal static readonly JsonSerializerOptions Json = new() { IncludeFields = false };

    /// <summary>The versioned order file for this repo — <c>version == 0</c> never
    /// exists on disk (0 means "identity order", encoded by the header alone).</summary>
    internal static string OrderFile(string repoPath, int version) =>
        GraphCachePaths.File(repoPath, $".meta.order.{version}");

    internal sealed class Header
    {
        public int Version { get; set; }
        public long Fingerprint { get; set; }
        public bool IncludeRemoteBranches { get; set; }
        public int RowCount { get; set; }
        public bool Exhausted { get; set; }
        public int WalkEngine { get; set; }

        /// <summary>Total distinct commit records ever appended to <c>.meta.dat</c> —
        /// equals <see cref="RowCount"/> until the first reorder, after which it can exceed it
        /// (new commits get new records; unreachable ones' old records just aren't referenced by
        /// the current order any more, until compaction).</summary>
        public int RecordCount { get; set; }

        /// <summary>Which <c>.meta.order.&lt;n&gt;</c> file maps rows to records.
        /// <c>0</c> means identity — row <c>i</c> is record <c>i</c>, no order file exists.</summary>
        public int OrderVersion { get; set; }

        /// <summary>How long the most recent walk-from-row-0 to
        /// exhaustion took, in milliseconds — <see cref="GraphMetaSplice.Evaluate"/>'s guard 4
        /// budget. <c>0</c> (the default for every store written before this field existed, or one
        /// whose disk cache was wiped) means "unknown", which guard 4 FAILS CLOSED on rather than
        /// skipping the check — see that method's doc comment. Additive field: an older reader
        /// ignores it (unknown JSON members are ignored by <c>System.Text.Json</c> by default), so
        /// no <see cref="Version"/> bump.</summary>
        public long LastFullWalkMs { get; set; }

        /// <summary>Names one incarnation of the store's files. <see cref="GraphMetaWriter.Reset"/>
        /// mints a new one, appends and reorders keep the current one, so a reader that recorded it
        /// at open can tell the files it holds have since been replaced. Additive: a header written
        /// before this field existed reads as <c>""</c>, and a matching key is then all a reader has
        /// to go on.</summary>
        public string Generation { get; set; } = "";

        private int _hashSize;

        /// <summary>Byte length of object ids in this store (20 for SHA-1, 32 for SHA-256).</summary>
        public int HashSize
        {
            get => _hashSize == 0 ? 20 : _hashSize;
            set => _hashSize = value;
        }
    }

    /// <summary>
    /// Opens the on-disk store for <paramref name="repoPath"/> if it is present, was written by
    /// the current walk (<see cref="GraphWalkCache.CliWalkMarker"/>), matches <paramref
    /// name="fingerprint"/> / <paramref name="includeRemoteBranches"/>, and already holds at least
    /// <paramref name="wantCount"/> rows (or is exhausted). Returns null on any miss — a fresh
    /// walk then re-spills.
    /// </summary>
    public static DiskCommitStore? TryOpen(
        string repoPath, long fingerprint, bool includeRemoteBranches, int wantCount)
    {
        // A reset between reading the header and opening the files would pair one incarnation's
        // header with another's files. The header is read again once the files are open and must
        // still name the same incarnation; one retry rides out a reset that lands mid-open.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            DiskCommitStore? store = null;
            try
            {
                var hdr = ReadHeader(repoPath);
                if (hdr is null
                    || hdr.Version != Version
                    || hdr.Fingerprint != fingerprint
                    || hdr.IncludeRemoteBranches != includeRemoteBranches
                    || hdr.WalkEngine != GraphWalkCache.CliWalkMarker)
                    return null;
                if (!hdr.Exhausted && hdr.RowCount < wantCount)
                    return null;

                var hashSize = hdr.HashSize == 0 ? 20 : hdr.HashSize;
                if (hashSize != 20 && hashSize != 32)
                    return null;

                store = DiskCommitStore.Open(
                    repoPath, hdr.RowCount, hdr.RecordCount, hdr.OrderVersion, hashSize, hdr.Generation);

                var again = ReadHeader(repoPath);
                if (again is not null
                    && again.Fingerprint == hdr.Fingerprint
                    && again.Generation == hdr.Generation
                    && again.OrderVersion == hdr.OrderVersion)
                    return store;
            }
            catch (InvalidDataException)
            {
                // The files don't hold what the header names: torn by a crash, or swapped by a
                // reset mid-open. DiscardIfTorn tells the two apart under the writer lock.
                store?.Dispose();
                DiscardIfTorn(repoPath, includeRemoteBranches);
                return null;
            }
            catch
            {
                // fall through: the files or the header moved under this open
            }
            store?.Dispose();
        }
        return null;
    }

    /// <summary>
    /// Deletes the store when its files do not hold what its header names (see
    /// <see cref="DiskCommitStore"/>'s open checks): a header that survived a crash its data did
    /// not. Checked again under the repo's writer lock, where no append or reset is in flight, so
    /// a reader that only raced a rewrite never deletes a good store. The next walk re-spills
    /// from row 0. Returns whether the store was deleted.
    /// </summary>
    internal static bool DiscardIfTorn(string repoPath, bool includeRemoteBranches)
    {
        lock (GraphCacheLocks.For(repoPath))
        {
            Header? hdr;
            try { hdr = ReadHeader(repoPath); }
            catch { return false; }
            if (hdr is null || hdr.Version != Version || hdr.IncludeRemoteBranches != includeRemoteBranches
                || hdr.WalkEngine != GraphWalkCache.CliWalkMarker)
                return false;
            try
            {
                using var store = DiskCommitStore.Open(
                    repoPath, hdr.RowCount, hdr.RecordCount, hdr.OrderVersion, hdr.HashSize, hdr.Generation);
                return false;
            }
            catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
            {
                // Torn: reset below.
            }
            catch
            {
                return false;
            }
            var discarded = new GraphMetaWriter(repoPath, hdr.Fingerprint, includeRemoteBranches).Reset();
            Diagnostics.PerfLog.Mark($"graph cache discarded a torn store ({repoPath}): {(discarded ? "deleted" : "delete blocked")}");
            return discarded;
        }
    }

    private static Header? ReadHeader(string repoPath)
    {
        var hdrPath = GraphCachePaths.File(repoPath, HdrExt);
        if (!File.Exists(hdrPath))
            return null;
        return JsonSerializer.Deserialize<Header>(ReadAllBytesShared(hdrPath), Json);
    }

    /// <summary>Header-only probe of the store's identity: the walk key, incarnation and order
    /// version a reader would pin by opening it now. Like <see cref="ProbeExisting"/> it does not
    /// check the fingerprint against anything.</summary>
    public static (bool Ok, long WalkKey, string Generation, int OrderVersion, int RowCount) ProbeGeneration(
        string repoPath, bool includeRemoteBranches)
    {
        try
        {
            var hdr = ReadHeader(repoPath);
            if (hdr is null || hdr.Version != Version || hdr.IncludeRemoteBranches != includeRemoteBranches
                || hdr.WalkEngine != GraphWalkCache.CliWalkMarker)
                return (false, 0, "", 0, 0);
            var hashSize = hdr.HashSize == 0 ? 20 : hdr.HashSize;
            if (hashSize != 20 && hashSize != 32)
                return (false, 0, "", 0, 0);
            return (true, hdr.Fingerprint, hdr.Generation, hdr.OrderVersion, hdr.RowCount);
        }
        catch
        {
            return (false, 0, "", 0, 0);
        }
    }

    /// <summary>Cheap header-only probe: does a store for <paramref name="repoPath"/> exist and
    /// match this fingerprint / remote-branch flag, written by the current walk? Returns its row
    /// count and exhausted flag. No file handle is opened (unlike <see cref="TryOpen"/>).</summary>
    public static (bool Ok, int RowCount, bool Exhausted) Probe(
        string repoPath, long fingerprint, bool includeRemoteBranches)
    {
        try
        {
            var hdrPath = GraphCachePaths.File(repoPath, HdrExt);
            if (!File.Exists(hdrPath))
                return (false, 0, false);
            var hdr = JsonSerializer.Deserialize<Header>(ReadAllBytesShared(hdrPath), Json);
            if (hdr is null || hdr.Version != Version || hdr.Fingerprint != fingerprint
                || hdr.IncludeRemoteBranches != includeRemoteBranches || hdr.WalkEngine != GraphWalkCache.CliWalkMarker)
                return (false, 0, false);
            var hashSize = hdr.HashSize == 0 ? 20 : hdr.HashSize;
            if (hashSize != 20 && hashSize != 32)
                return (false, 0, false);
            return (true, hdr.RowCount, hdr.Exhausted);
        }
        catch
        {
            return (false, 0, false);
        }
    }

    /// <summary>
    /// Probes the cache header without matching against a fingerprint, returning the full header
    /// including the WALK KEY it was written under — used by the headless per-tab pipeline to tell
    /// "no store at all" apart from "a store exists, but its walk key has moved" (a reorder opportunity)
    /// without needing to know the current walk key up front.
    /// </summary>
    public static (bool Ok, long WalkKey, int RowCount, int RecordCount, int OrderVersion, bool Exhausted, long LastFullWalkMs) ProbeExisting(
        string repoPath, bool includeRemoteBranches)
    {
        try
        {
            var hdrPath = GraphCachePaths.File(repoPath, HdrExt);
            if (!File.Exists(hdrPath))
                return (false, 0, 0, 0, 0, false, 0);
            var hdr = JsonSerializer.Deserialize<Header>(ReadAllBytesShared(hdrPath), Json);
            if (hdr is null || hdr.Version != Version || hdr.IncludeRemoteBranches != includeRemoteBranches
                || hdr.WalkEngine != GraphWalkCache.CliWalkMarker)
                return (false, 0, 0, 0, 0, false, 0);
            var hashSize = hdr.HashSize == 0 ? 20 : hdr.HashSize;
            if (hashSize != 20 && hashSize != 32)
                return (false, 0, 0, 0, 0, false, 0);
            return (true, hdr.Fingerprint, hdr.RowCount, hdr.RecordCount, hdr.OrderVersion, hdr.Exhausted, hdr.LastFullWalkMs);
        }
        catch
        {
            return (false, 0, 0, 0, 0, false, 0);
        }
    }

    /// <summary>Reads the sorted tip SHAs the CURRENT order was built from —
    /// <c>.meta.tips</c>, written by <see cref="GraphMetaWriter.WriteTips"/>. Returns null if the
    /// file is missing (a store that has never had its order committed with a known tip set — a
    /// never-reordered store) or unreadable; <see
    /// cref="GraphMetaSplice.Evaluate"/>'s guard 1 treats that as "tips don't match" (reject).</summary>
    public static IReadOnlyList<GitObjectId>? ReadTips(string repoPath, int hashSize = 0)
    {
        try
        {
            var path = GraphCachePaths.File(repoPath, TipsExt);
            if (!File.Exists(path))
                return null;
            var bytes = ReadAllBytesShared(path);
            if (hashSize <= 0)
            {
                var hdrPath = GraphCachePaths.File(repoPath, HdrExt);
                if (File.Exists(hdrPath))
                {
                    var hdr = JsonSerializer.Deserialize<Header>(ReadAllBytesShared(hdrPath), Json);
                    if (hdr is not null && hdr.HashSize > 0)
                        hashSize = hdr.HashSize;
                }
                else if (bytes.Length > 0 && bytes.Length % 32 == 0 && bytes.Length % 20 != 0)
                {
                    hashSize = 32;
                }
            }
            if (hashSize <= 0)
                hashSize = 20;

            if (bytes.Length % hashSize != 0)
                return null;
            var tips = new GitObjectId[bytes.Length / hashSize];
            for (var i = 0; i < tips.Length; i++)
                tips[i] = new GitObjectId(bytes.AsSpan(i * hashSize, hashSize));
            return tips;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Deletes every <c>graph-*.meta.*</c> file. Returns the count removed.</summary>
    public static int Wipe()
    {
        var n = 0;
        try
        {
            var dir = new DirectoryInfo(AppPaths.CacheDirectory);
            if (!dir.Exists)
                return 0;
            foreach (var f in dir.GetFiles("graph-*.meta.*"))
            {
                try { f.Delete(); n++; } catch { /* skip locked */ }
            }
        }
        catch { }
        return n;
    }

    /// <summary>Deletes every <c>.meta.order.&lt;n&gt;</c> file for this repo strictly
    /// older than <paramref name="currentVersion"/> — call right after a successful
    /// <see cref="GraphMetaWriter.TryCommitReorder"/>. A reader loads the whole order file once,
    /// at <see cref="DiskCommitStore.Open"/>, and never re-reads it later, so once the header has
    /// switched to <paramref name="currentVersion"/> no live reader can still be holding a
    /// reference to an older one.</summary>
    public static void DeleteSupersededOrderFiles(string repoPath, int currentVersion)
    {
        try
        {
            var dir = new DirectoryInfo(AppPaths.CacheDirectory);
            if (!dir.Exists)
                return;
            var stem = GraphCachePaths.Stem(repoPath);
            var prefix = $"{stem}.meta.order.";
            foreach (var f in dir.GetFiles($"{prefix}*"))
            {
                var suffix = f.Name[prefix.Length..];
                if (int.TryParse(suffix, out var version) && version < currentVersion)
                    try { f.Delete(); } catch { /* skip locked */ }
            }
        }
        catch { }
    }

    /// <summary>
    /// Reads all bytes from <paramref name="path"/> sharing both ReadWrite and Delete.
    /// Every reader of a <c>.meta.*</c> file must share Delete, or <see cref="GraphMetaWriter.Reset"/>
    /// cannot retire open files away before starting a fresh from-row-0 spill.
    /// </summary>
    internal static byte[] ReadAllBytesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[fs.Length];
        var read = 0;
        while (read < bytes.Length)
        {
            var n = fs.Read(bytes, read, bytes.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return read == bytes.Length ? bytes : bytes[..read];
    }

    /// <summary>
    /// Measures the unreachable dead prefix at the front of <c>.meta.dat</c>.
    /// Record 0's offset (the first 8 bytes of <c>.meta.idx</c>) indicates how many dead bytes
    /// precede record 0. Returns <c>(0, 0)</c> on any miss, unreadable file, or empty file.
    /// </summary>
    internal static (long DeadBytes, long DatLength) ProbeDeadPrefix(string repoPath)
    {
        try
        {
            var idxPath = GraphCachePaths.File(repoPath, IdxExt);
            var datPath = GraphCachePaths.File(repoPath, DatExt);
            if (!File.Exists(idxPath) || !File.Exists(datPath))
                return (0, 0);

            var buf = new byte[8];
            using (var fs = new FileStream(idxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var read = 0;
                while (read < 8)
                {
                    var n = fs.Read(buf, read, 8 - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < 8)
                    return (0, 0);
            }

            var deadBytes = BitConverter.ToInt64(buf, 0);
            if (deadBytes < 0)
                return (0, 0);

            long datLen;
            using (var datFs = new FileStream(datPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                datLen = datFs.Length;
            }

            return (deadBytes, datLen);
        }
        catch
        {
            return (0, 0);
        }
    }
}
