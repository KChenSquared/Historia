namespace Historia.Services.Graph;

/// <summary>
/// Read side of <see cref="GraphMetaStore"/>. Keeps the <c>.idx</c> offset table resident
/// (~8 bytes/row) and pages <c>.dat</c> on demand behind a bounded decode cache. Grows in
/// place via <see cref="SetCount"/> after an <see cref="GraphMetaWriter.Append"/>.
/// </summary>
internal sealed class DiskCommitStore : ICommitStore, IDisposable
{
    private const int DecodeCacheCap = 8192;

    // Every file this reader touches is held as a handle opened together at Open, on the
    // incarnation the header named then. Reading by path instead would follow a reset onto a
    // different store's files while the offsets and records already read belong to the old one.
    private readonly SafeFileHandle _dat;
    private readonly SafeFileHandle _idx;
    private readonly SafeFileHandle? _search;   // null when the store has no search file
    private long[] _offsets;            // [record] -> start byte in .dat; capacity may exceed _offsetCount
    private int _offsetCount;           // records held in _offsets
    private long _datReads;             // reads issued against .dat (test hook)
    private long _datLength;
    private int _count;                 // row count (== record count until the first reorder)
    private readonly int _hashSize;

    /// <summary>[row] -&gt; record index, or null for the identity order (row IS
    /// record — every store before its first reorder, and the overwhelming majority always).
    /// Loaded once at <see cref="Open"/> and never mutated in place — a reorder always produces a
    /// freshly-opened store (see <c>GraphLayoutService</c>'s reorder job), so a reader never needs
    /// to see an order file change under it.</summary>
    private readonly int[]? _order;

    private readonly Dictionary<int, CommitMetadata> _decoded = new(DecodeCacheCap);
    private readonly Queue<int> _decodedOrder = new(DecodeCacheCap);
    private readonly object _sync = new();

    // Lease count: the RepoCache that opened the store holds the first lease; every
    // consumer that parks a reference to read from later takes another via AddRef(). The file
    // handle closes only when the last lease is released, so a RefsChanged re-walk dropping the
    // cache's lease can't pull the handle out from under an in-flight Render. Reads no-op once
    // closed rather than throwing ObjectDisposedException off the render thread.
    // Both fields are only mutated under _sync so AddRef and Dispose can't race a lease back in
    // after the count has already dropped to zero and committed to closing (that race would hand a
    // caller a live-looking lease on a store whose handle was gone, blanking every row).
    private int _leases = 1;
    private volatile bool _closed;

    private static readonly UTF8Encoding Utf8 = new(false);

    private static readonly System.Buffers.ArrayPool<byte> Pool = System.Buffers.ArrayPool<byte>.Shared;

    private static readonly CommitMetadata ClosedStoreStub = new()
    {
        Id = GitObjectId.Zero,
        MessageShort = "",
        AuthorName = "",
        AuthorEmail = "",
        AuthorWhen = default,
        CommitWhen = default,
        ParentCount = 0,
    };

    private DiskCommitStore(
        SafeFileHandle dat, SafeFileHandle idx, SafeFileHandle? search, long[] offsets, long datLength, int count,
        int[]? order, int orderVersion, string generation, int hashSize)
    {
        _dat = dat;
        _idx = idx;
        _search = search;
        _offsets = offsets;
        _offsetCount = offsets.Length;
        _datLength = datLength;
        _count = count;
        _order = order;
        OrderVersion = orderVersion;
        Generation = generation;
        _hashSize = hashSize > 0 ? hashSize : 20;
    }

    /// <summary>The store incarnation these files belong to (<see cref="GraphMetaStore.Header.Generation"/>
    /// at open; <c>""</c> for a store written before generations existed).</summary>
    public string Generation { get; }

    /// <summary>The order version this store was opened with; <c>0</c> is the identity order.</summary>
    public int OrderVersion { get; }

    /// <summary>Opens a store at the IDENTITY order — row <c>i</c> IS record <c>i</c>. Every
    /// caller, and the only shape <see cref="SetCount"/>
    /// supports growing in place.</summary>
    public static DiskCommitStore Open(string repoPath, int rowCount) => Open(repoPath, rowCount, rowCount, orderVersion: 0, hashSize: 0);

    /// <summary>Opens a store whose current header names an explicit order version.
    /// <paramref name="recordCount"/> sizes the <c>.idx</c>/offset table (records, not rows);
    /// <paramref name="orderVersion"/> 0 means identity (no order file to load).</summary>
    public static DiskCommitStore Open(string repoPath, int rowCount, int recordCount, int orderVersion) => Open(repoPath, rowCount, recordCount, orderVersion, hashSize: 0);

    public static DiskCommitStore Open(string repoPath, int rowCount, int recordCount, int orderVersion, int hashSize)
        => Open(repoPath, rowCount, recordCount, orderVersion, hashSize, generation: null);

    /// <summary>Opens the store and records the incarnation it belongs to. <paramref name="generation"/>
    /// is the header's incarnation the caller already read; <c>null</c> reads the header here, before
    /// any file is opened — so a reset that lands afterwards makes this store look older than the
    /// files, never newer.</summary>
    internal static DiskCommitStore Open(
        string repoPath, int rowCount, int recordCount, int orderVersion, int hashSize, string? generation)
    {
        if (hashSize <= 0 || generation is null)
        {
            var hdrPath = GraphCachePaths.File(repoPath, GraphMetaStore.HdrExt);
            if (File.Exists(hdrPath))
            {
                try
                {
                    var hdr = JsonSerializer.Deserialize<GraphMetaStore.Header>(GraphMetaStore.ReadAllBytesShared(hdrPath), GraphMetaStore.Json);
                    if (hdr is not null && hdr.HashSize > 0 && hashSize <= 0)
                        hashSize = hdr.HashSize;
                    if (hdr is not null)
                        generation ??= hdr.Generation;
                }
                catch { }
            }
            if (hashSize <= 0)
                hashSize = 20;
        }

        const FileShare share = FileShare.ReadWrite | FileShare.Delete;
        var datPath = GraphCachePaths.File(repoPath, GraphMetaStore.DatExt);
        var idxPath = GraphCachePaths.File(repoPath, GraphMetaStore.IdxExt);
        var searchPath = GraphCachePaths.File(repoPath, GraphMetaStore.SearchExt);
        SafeFileHandle? dat = null, idx = null, search = null;
        try
        {
            dat = File.OpenHandle(datPath, FileMode.Open, FileAccess.Read, share);
            idx = File.OpenHandle(idxPath, FileMode.Open, FileAccess.Read, share);
            try { search = File.OpenHandle(searchPath, FileMode.Open, FileAccess.Read, share); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }

            var offsets = ReadOffsets(idx, 0, recordCount);
            var len = RandomAccess.GetLength(dat);
            int[]? order = null;
            if (orderVersion > 0)
            {
                var orderPath = GraphMetaStore.OrderFile(repoPath, orderVersion);
                order = ReadOrder(orderPath, rowCount);
            }
            ValidateCoverage(offsets, len, order, rowCount, recordCount);
            var count = order is not null ? Math.Min(rowCount, order.Length) : Math.Min(rowCount, offsets.Length);
            var store = new DiskCommitStore(dat, idx, search, offsets, len, count, order, orderVersion, generation ?? "", hashSize);
            if (!store.LastRecordIsReal())
            {
                store.Dispose();
                throw new InvalidDataException("graph meta store is torn: its last record is blank");
            }
            return store;
        }
        catch
        {
            dat?.Dispose();
            idx?.Dispose();
            search?.Dispose();
            throw;
        }
    }

    /// <summary>Throws <see cref="InvalidDataException"/> when the files do not hold what the
    /// header names: fewer offsets than records, offsets that do not climb through <c>.dat</c>,
    /// or an order file shorter than the rows or naming a record that is not there. A header
    /// renamed into place survives a crash that its unflushed data did not, and reading such a
    /// store would serve blank rows as a complete history. The checks run over arrays already in
    /// memory, about a millisecond per million records.</summary>
    private static void ValidateCoverage(long[] offsets, long datLength, int[]? order, int rowCount, int recordCount)
    {
        if (offsets.Length < recordCount || (order is null && offsets.Length < rowCount))
            throw new InvalidDataException("graph meta store is torn: .idx holds fewer records than its header names");
        var previous = -1L;
        foreach (var offset in offsets)
        {
            if (offset <= previous)
                throw new InvalidDataException("graph meta store is torn: .idx offsets do not climb");
            previous = offset;
        }
        if (offsets.Length > 0 && offsets[^1] >= datLength)
            throw new InvalidDataException("graph meta store is torn: .dat ends before its last record");
        if (order is null)
            return;
        if (order.Length < rowCount)
            throw new InvalidDataException("graph meta store is torn: the order file is shorter than its rows");
        foreach (var record in order)
        {
            if ((uint)record >= (uint)offsets.Length)
                throw new InvalidDataException("graph meta store is torn: the order names a missing record");
        }
    }

    /// <summary>Whether the last record holds real bytes. A crash can leave the tail of
    /// <c>.dat</c> zero-filled, which decodes without error as a commit with a zero id and a
    /// zero timestamp. Reads a bounded window from the record's start, since <c>.dat</c> past it can
    /// hold a batch appended but not yet committed; only a record longer than the window reads the
    /// rest.</summary>
    private bool LastRecordIsReal()
    {
        const int Window = 64 * 1024;
        if (_offsetCount == 0)
            return true;
        var start = _offsets[_offsetCount - 1];
        var tail = _datLength - start;
        if (tail <= 0)
            return false;
        CommitMetadata last;
        try
        {
            last = DecodeTail(start, (int)Math.Min(tail, Window));
        }
        catch (InvalidDataException) when (tail > Window)
        {
            last = DecodeTail(start, (int)Math.Min(tail, int.MaxValue));
        }
        return !(last.Id.IsZero && last.CommitWhen.UtcTicks == 0);
    }

    private CommitMetadata DecodeTail(long start, int len)
    {
        var buf = new byte[len];
        if (ReadDat(buf, start) < len)
            throw new InvalidDataException("graph meta store is torn: .dat ends before its last record");
        return DecodeFrom(buf, _hashSize).Meta;
    }

    private static int[] ReadOrder(string orderPath, int rowCount)
    {
        var bytes = GraphMetaStore.ReadAllBytesShared(orderPath);
        var n = Math.Min(rowCount, bytes.Length / 4);
        var order = new int[n];
        Buffer.BlockCopy(bytes, 0, order, 0, n * 4);
        return order;
    }

    /// <summary>Adopt rows appended since <see cref="Open"/> (called after an Extend spill).
    /// Reads only the <c>.idx</c> bytes appended since the last call instead of
    /// re-reading the whole file. The offset table grows by capacity doubling, so a long run of
    /// small appends copies it a logarithmic number of times rather than once per call.
    /// Only valid for an identity-order store (<see cref="_order"/> null) — a store
    /// opened at an explicit order version never grows in place; the layout service reopens a
    /// fresh store after a reorder instead.</summary>
    public void SetCount(int rowCount)
    {
        lock (_sync)
        {
            if (_closed || _order is not null || rowCount <= _count)
                return;
            var startByte = (long)_offsetCount * 8;
            var length = RandomAccess.GetLength(_idx);
            if (rowCount > _offsetCount && startByte < length)
            {
                var tail = (int)Math.Min((length - startByte) / 8, (long)rowCount - _offsetCount);
                if (tail > 0)
                {
                    var buf = Pool.Rent(tail * 8);
                    try
                    {
                        var readTotal = 0;
                        while (readTotal < tail * 8)
                        {
                            var r = RandomAccess.Read(_idx, buf.AsSpan(readTotal, tail * 8 - readTotal), startByte + readTotal);
                            if (r <= 0)
                                break;
                            readTotal += r;
                        }
                        var n = readTotal / 8;
                        if (n > 0)
                        {
                            if (_offsetCount + n > _offsets.Length)
                            {
                                var grown = new long[Math.Max(_offsetCount + n, _offsets.Length * 2)];
                                Array.Copy(_offsets, grown, _offsetCount);
                                _offsets = grown;
                            }
                            for (var i = 0; i < n; i++)
                                _offsets[_offsetCount + i] = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(i * 8, 8));
                            Volatile.Write(ref _offsetCount, _offsetCount + n);
                        }
                    }
                    finally
                    {
                        Pool.Return(buf);
                    }
                }
            }
            _datLength = RandomAccess.GetLength(_dat);
            _count = Math.Min(rowCount, _offsetCount);
        }
    }

    /// <summary>Reads issued against <c>.dat</c> since open; lets a test prove that a warm
    /// <see cref="Prefetch"/> performs no IO.</summary>
    internal long DatReadCount => Interlocked.Read(ref _datReads);

    private int ReadDat(Span<byte> buffer, long offset)
    {
        Interlocked.Increment(ref _datReads);
        return RandomAccess.Read(_dat, buffer, offset);
    }

    public int HashSize => _hashSize;

    public int Count => Volatile.Read(ref _count);

    /// <summary>Total distinct commit records in <c>.meta.dat</c> right now (can exceed
    /// <see cref="Count"/> once history has been reordered — old records superseded by a reorder
    /// stay in the file, unreferenced, until compaction).</summary>
    public int RecordCount => Volatile.Read(ref _offsetCount);

    /// <summary>Row <paramref name="row"/>'s record index — identity unless an order file is
    /// loaded.</summary>
    private int RecordOf(int row) => _order is null ? row : _order[row];

    /// <summary>The full row -&gt; record order this store was opened with, as a
    /// fresh array (safe for a caller to hand to <see cref="GraphMetaSplice.Build"/>, which never
    /// mutates <see cref="_order"/> in place — see that field's own doc comment on why a reorder
    /// always opens a freshly-loaded store instead). Identity (<c>row == record</c>) when this
    /// store has no order file (<c>OrderVersion</c> 0).</summary>
    public int[] ToOrderArray() => _order is not null ? [.. _order] : [.. Enumerable.Range(0, _count)];

    /// <summary>Builds a SHA -&gt; record-index table by reading just the leading
    /// SHA of every existing record (skips message/author/parent bytes entirely) — used
    /// by the reorder job to find which commits in a freshly walked history already have a
    /// durable record, so it only appends genuinely new ones. Records are read through a large
    /// sequential window that is refilled when the next record's SHA falls outside it, so a
    /// million records cost a few hundred reads instead of a million. This is a background,
    /// gated, lowest-priority job (never on the render thread).
    /// <para>
    /// Keyed by <see cref="CompactShaIndex"/> (an 8-byte prefix per entry, verified
    /// against the full id on any hit) rather than the 40-char hex string — at
    /// 1.5M records on linux a hex-string table holds ~150-180 MB; this holds meaningfully
    /// less while staying exactly as correct (every lookup still verifies the full id).
    /// </para>
    /// </summary>
    public CompactShaIndex BuildShaIndex()
    {
        const int Window = 1 << 20;
        var index = new CompactShaIndex(RecordCount);
        lock (_sync)
        {
            if (_closed)
                return index;
            var buf = Pool.Rent(Window);
            try
            {
                var windowStart = 0L;
                var windowLen = 0;
                for (var rec = 0; rec < _offsetCount; rec++)
                {
                    var offset = _offsets[rec];
                    if (offset < windowStart || offset + _hashSize > windowStart + windowLen)
                    {
                        windowStart = offset;
                        windowLen = ReadDat(buf.AsSpan(0, Window), offset);
                        if (windowLen < _hashSize)
                        {
                            windowLen = 0;
                            continue;
                        }
                    }
                    index.Add(new GitObjectId(buf.AsSpan((int)(offset - windowStart), _hashSize)), rec);
                }
            }
            finally
            {
                Pool.Return(buf);
            }
        }
        return index;
    }

    public CommitMetadata this[int index]
    {
        get
        {
            lock (_sync)
            {
                if (_closed)
                    return ClosedStoreStub;
                if (_decoded.TryGetValue(index, out var hit))
                    return hit;
                try
                {
                    var (m, _) = Decode(index);
                    Cache(index, m);
                    return m;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException
                                           or ArgumentOutOfRangeException or IndexOutOfRangeException)
                {
                    // A background spill restart (SpillRange from == 0) is rewriting .dat/.idx
                    // under us. Serve a stub for this frame rather than throw off the render
                    // thread; the stub is not cached, so a later read of this row decodes it again
                    // once the files settle.
                    return ClosedStoreStub;
                }
            }
        }
    }

    /// <summary>Reads rows [<paramref name="first"/>, <paramref name="first"/> +
    /// <paramref name="count"/>) with full parent SHAs and timestamps — the batch form of
    /// <see cref="ReadRaw"/>, used to serve a whole already-spilled range straight from disk
    /// instead of keeping it resident in <c>RepoCache.Commits</c> (see
    /// <c>GraphLayoutService.ReadWindow</c>). One <see cref="RandomAccess.Read"/> covering every
    /// record in range, then per-row decode from the buffer — same shape as
    /// <see cref="Prefetch"/>. Faults the same way as every other reader here: a concurrent
    /// from-row-0 re-spill returns whatever was already decoded rather than throwing.</summary>
    public IReadOnlyList<RawCommit> ReadRawRange(int first, int count)
    {
        if (count <= 0)
            return [];
        lock (_sync)
        {
            if (_closed)
                return [];
            first = Math.Max(0, first);
            var last = Math.Min(first + count, _count) - 1;
            if (last < first)
                return [];
            try
            {
                // An explicit order means consecutive ROWS need not be consecutive
                // RECORDS (or even increasing ones) — the one-big-read trick below only holds for
                // the identity order. Reordered stores fall back to one read per row; simpler and
                // correct, at the cost of the batching this method exists for. A reorder is rare
                // (a fetch/branch-create/branch-delete, not every extend), so this only bites the
                // rows read right after one, before the layout's own decode cache warms up.
                if (_order is not null)
                {
                    // ReadRaw re-enters this same lock (Monitor is reentrant per-thread) — safe.
                    var perRow = new List<RawCommit>(last - first + 1);
                    for (var row = first; row <= last; row++)
                        perRow.Add(ReadRaw(row));
                    return perRow;
                }

                var start = _offsets[first];
                var end = last + 1 < _offsetCount ? _offsets[last + 1] : _datLength;
                var span = (int)(end - start);
                if (span <= 0)
                    return [];
                var buf = Pool.Rent(span);
                try
                {
                    var read = ReadDat(buf.AsSpan(0, span), start);
                    var result = new List<RawCommit>(last - first + 1);
                    for (var row = first; row <= last; row++)
                    {
                        var rs = _offsets[row] - start;
                        var re = (row + 1 < _offsetCount ? _offsets[row + 1] : _datLength) - start;
                        if (rs < 0 || re > read || re <= rs)
                            break;
                        var (meta, parents) = DecodeFrom(buf.AsSpan((int)rs, (int)(re - rs)), _hashSize);
                        Cache(row, meta);
                        result.Add(new RawCommit(meta.Id, parents, meta));
                    }
                    return result;
                }
                finally
                {
                    Pool.Return(buf);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException
                                       or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                return [];
            }
        }
    }

    /// <summary>Decodes row <paramref name="index"/> WITH its parent SHAs (persisted
    /// since <see cref="GraphMetaStore.Version"/> 2) — used by <c>TryOptimisticSnapshot</c> to
    /// seed a first paint straight from this store. Same fault handling as the indexer: a
    /// concurrent spill rewrite serves a childless stub rather than throwing off the render
    /// thread's caller.</summary>
    public RawCommit ReadRaw(int index)
    {
        lock (_sync)
        {
            if (_closed)
                return new RawCommit(ClosedStoreStub.Id, [], ClosedStoreStub);
            try
            {
                var (m, parents) = Decode(index);
                Cache(index, m);
                return new RawCommit(m.Id, parents, m);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException
                                       or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                return new RawCommit(ClosedStoreStub.Id, [], ClosedStoreStub);
            }
        }
    }

    public void Prefetch(int firstRow, int lastRow)
    {
        firstRow = Math.Max(0, firstRow);
        lastRow = Math.Min(Count - 1, lastRow);
        if (lastRow < firstRow)
            return;
        lock (_sync)
        {
            if (_closed)
                return;
            try
            {
                // Same "identity order only" restriction as ReadRawRange — a reordered
                // store falls back to one decode per row via the indexer's own path.
                if (_order is not null)
                {
                    for (var row = firstRow; row <= lastRow; row++)
                    {
                        if (_decoded.ContainsKey(row))
                            continue;
                        Cache(row, Decode(row).Meta);
                    }
                    return;
                }

                // A window whose rows are all decoded already costs no read and no allocation:
                // only the span between the first and last missing row is fetched.
                while (firstRow <= lastRow && _decoded.ContainsKey(firstRow))
                    firstRow++;
                while (lastRow >= firstRow && _decoded.ContainsKey(lastRow))
                    lastRow--;
                if (lastRow < firstRow)
                    return;

                var start = _offsets[firstRow];
                var end = lastRow + 1 < _offsetCount ? _offsets[lastRow + 1] : _datLength;
                var span = (int)Math.Min(end - start, 1 << 22); // cap a pathological single read at 4 MB
                if (span <= 0)
                    return;
                var buf = Pool.Rent(span);
                try
                {
                    var read = ReadDat(buf.AsSpan(0, span), start);
                    for (var row = firstRow; row <= lastRow; row++)
                    {
                        if (_decoded.ContainsKey(row))
                            continue;
                        var rs = _offsets[row] - start;
                        var re = (row + 1 < _offsetCount ? _offsets[row + 1] : _datLength) - start;
                        if (rs < 0 || re > read || re <= rs)
                            break;
                        Cache(row, DecodeFrom(buf.AsSpan((int)rs, (int)(re - rs)), _hashSize).Meta);
                    }
                }
                finally
                {
                    Pool.Return(buf);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException
                                       or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                // Concurrent .dat/.idx rewrite (see the indexer) — the prefetch is only a hint,
                // so drop it; the per-row indexer path serves stubs until the store settles.
            }
        }
    }

    public IEnumerator<CommitMetadata> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return this[i];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Ctrl+F backing scan: streams <c>.meta.search</c>, one line per RECORD (
    /// the file is record-indexed, like <c>.dat</c>/<c>.idx</c>, so it is never rewritten by a
    /// reorder). Matches the in-memory find exactly — substring on message or author, prefix on
    /// the SHA. The file is already lowercased, so the lowercased term is compared as UTF-8 bytes
    /// against each line in place: no line is decoded, and nothing is allocated per record. For an
    /// identity-order store a record index IS the row; otherwise the hits are
    /// mapped back to rows via the inverse of the current order, built once per call (no
    /// persisted reverse map), and a record no longer referenced
    /// by the current order (superseded by a reorder, not yet compacted) is dropped. The token is
    /// checked before every read block; cancelling it throws <see cref="OperationCanceledException"/>
    /// after the pooled buffer and the lease are returned.
    /// </summary>
    public IReadOnlyList<int> Search(string term, CancellationToken ct = default)
    {
        var t = term.Trim().ToLowerInvariant();
        if (t.Length == 0 || _search is null)
            return [];
        // A lease keeps the handle open for the whole scan, however long a concurrent tab switch
        // or re-walk takes to release the store's owner.
        if (AddRef() is null)
            return [];

        var hits = new List<int>();
        byte[]? buf = null;
        try
        {
            var needle = Utf8.GetBytes(t);
            Dictionary<int, int>? recordToRow = null;
            if (_order is not null)
            {
                recordToRow = new Dictionary<int, int>(_order.Length);
                for (var row = 0; row < _order.Length; row++)
                    recordToRow[_order[row]] = row;
            }

            var recordCount = RecordCount;
            var record = -1;
            var stop = false;

            // Scans the handle's own file, so a store opened before a reset keeps searching its own
            // rows. Lines end at a '\n' byte, which never occurs inside a multi-byte UTF-8
            // sequence, so lines are cut before matching; an unterminated tail is a line still
            // being appended and is left alone.
            buf = Pool.Rent(1 << 18);
            var len = 0;
            long offset = 0;
            while (!stop)
            {
                if (len == buf.Length)
                {
                    var grown = Pool.Rent(buf.Length * 2);
                    Buffer.BlockCopy(buf, 0, grown, 0, len);
                    Pool.Return(buf);
                    buf = grown;
                }
                ct.ThrowIfCancellationRequested();
                var n = RandomAccess.Read(_search, buf.AsSpan(len), offset);
                if (n <= 0)
                    break;
                offset += n;
                len += n;

                var start = 0;
                int nl;
                while (!stop && (nl = buf.AsSpan(start, len - start).IndexOf((byte)'\n')) >= 0)
                {
                    record++;
                    if (record >= recordCount)
                    {
                        stop = true;
                        break;
                    }
                    var line = buf.AsSpan(start, nl);
                    start += nl + 1;

                    var sep = line.IndexOf((byte)GraphMetaStore.Sep);
                    if (sep < 0)
                        continue;
                    if (line[(sep + 1)..].IndexOf(needle) < 0 && !line[..sep].StartsWith(needle))
                        continue;

                    if (recordToRow is null)
                        hits.Add(record);
                    else if (recordToRow.TryGetValue(record, out var row))
                        hits.Add(row);
                }
                len -= start;
                if (start > 0 && len > 0)
                    Buffer.BlockCopy(buf, start, buf, 0, len);
            }
            if (recordToRow is not null)
                hits.Sort();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return hits;
        }
        finally
        {
            if (buf is not null)
                Pool.Return(buf);
            Dispose();
        }
        return hits;
    }

    /// <inheritdoc/>
    public ICommitStore? AddRef()
    {
        lock (_sync)
        {
            if (_closed)
                return null;
            _leases++;
            return this;
        }
    }

    /// <summary>Releases one lease; closes the file handle when the last one goes. Idempotent and
    /// tolerant of an over-release (a consumer disposing twice) — the handle is never closed
    /// more than once, and never while a lease is still outstanding. Readers keep reading through
    /// their open handles even if <see cref="GraphMetaWriter.Reset"/> has retired and deleted the
    /// file on disk; the OS frees the disk allocation once the last handle closes.</summary>
    public void Dispose()
    {
        bool close;
        lock (_sync)
        {
            if (_leases <= 0 || _closed)
                return;                     // already fully released / over-released
            close = --_leases == 0;
            if (close)
                _closed = true;
        }
        if (close)
        {
            _dat.Dispose();
            _idx.Dispose();
            _search?.Dispose();
        }
    }

    // ── internals ──

    /// <summary>Reads the offsets of records [<paramref name="fromRecord"/>, <paramref name="recordCount"/>)
    /// through <paramref name="idx"/> — the whole table at open, or just the tail appended since
    /// the caller last read it. Reads only what the handle's own file holds.</summary>
    private static long[] ReadOffsets(SafeFileHandle idx, int fromRecord, int recordCount)
    {
        var startByte = (long)fromRecord * 8;
        var length = RandomAccess.GetLength(idx);
        if (recordCount <= fromRecord || startByte >= length)
            return [];
        var wantBytes = (int)Math.Min(length - startByte, (long)(recordCount - fromRecord) * 8);
        var buf = new byte[wantBytes];
        var readTotal = 0;
        while (readTotal < buf.Length)
        {
            var r = RandomAccess.Read(idx, buf.AsSpan(readTotal), startByte + readTotal);
            if (r <= 0)
                break;
            readTotal += r;
        }
        var n = readTotal / 8;
        var offsets = new long[n];
        for (var i = 0; i < n; i++)
            offsets[i] = BitConverter.ToInt64(buf, i * 8);
        return offsets;
    }

    /// <summary>Decodes ROW <paramref name="row"/> — translated to its record via
    /// <see cref="RecordOf"/> (identity unless an order file is loaded). A record's byte length
    /// is always bounded by the NEXT RECORD's offset regardless of row order, since
    /// <c>.meta.idx</c>/<c>.dat</c> are append-ordered by record, never by row.</summary>
    private (CommitMetadata Meta, GitObjectId[] Parents) Decode(int row)
    {
        if (row < 0 || row >= _count)
            throw new ArgumentOutOfRangeException(nameof(row));
        var rec = RecordOf(row);
        if (rec < 0 || rec >= _offsetCount)
            throw new InvalidDataException("graph meta order points at an unknown record");
        var start = _offsets[rec];
        var end = rec + 1 < _offsetCount ? _offsets[rec + 1] : _datLength;
        var len = (int)(end - start);
        if (len <= 0 || start < 0 || end > _datLength)
            throw new InvalidDataException("graph meta record out of range");
        var buf = Pool.Rent(len);
        try
        {
            var read = ReadDat(buf.AsSpan(0, len), start);
            if (read < len)
                throw new InvalidDataException("graph meta record truncated");
            return DecodeFrom(buf.AsSpan(0, len), _hashSize);
        }
        finally
        {
            Pool.Return(buf);
        }
    }

    /// <summary>Parses one <c>.dat</c> record straight from its bytes. Records written before <see cref="GraphMetaStore.Version"/>
    /// 2 have no parent-SHA bytes; that format is rejected wholesale by the header version check
    /// before any record is ever read, so a mismatch here would only mean real corruption.
    /// Strings use the length-prefixed UTF-8 layout of <see cref="BinaryWriter.Write(string)"/>.</summary>
    internal static (CommitMetadata Meta, GitObjectId[] Parents) DecodeFrom(ReadOnlySpan<byte> record, int hashSize)
    {
        // Trap: a record read at the wrong offset (an index and a data file from different store
        // incarnations) can make the id constructor throw ArgumentException, which no reader
        // catches; a garbage parent count could also allocate an absurd array. So every way a record
        // can fail to parse is reported as InvalidDataException, the one fault the readers and the
        // graph render already treat as "serve a stub".
        try
        {
            var pos = 0;
            var id = new GitObjectId(Take(record, ref pos, hashSize));
            var msg = ReadString(record, ref pos);
            var author = ReadString(record, ref pos);
            var email = ReadString(record, ref pos);
            var authorWhen = ReadWhen(record, ref pos);
            var commitWhen = ReadWhen(record, ref pos);
            var parentCount = BinaryPrimitives.ReadInt32LittleEndian(Take(record, ref pos, 4));
            var remaining = record.Length - pos;
            if (parentCount < 0 || parentCount > remaining / hashSize)
                throw new InvalidDataException("graph meta record has an impossible parent count");
            var parents = new GitObjectId[parentCount];
            for (var i = 0; i < parentCount; i++)
                parents[i] = new GitObjectId(Take(record, ref pos, hashSize));
            var meta = new CommitMetadata
            {
                Id = id,
                MessageShort = msg,
                AuthorName = author,
                AuthorEmail = email,
                AuthorWhen = authorWhen,
                CommitWhen = commitWhen,
                ParentCount = parentCount,
            };
            return (meta, parents);
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or EndOfStreamException or FormatException)
        {
            throw new InvalidDataException("graph meta record does not parse", ex);
        }
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> record, ref int pos, int length)
    {
        if (length < 0 || length > record.Length - pos)
            throw new EndOfStreamException();
        var slice = record.Slice(pos, length);
        pos += length;
        return slice;
    }

    private static string ReadString(ReadOnlySpan<byte> record, ref int pos)
    {
        // 7-bit encoded byte length, at most five bytes, as BinaryWriter writes it.
        var length = 0;
        for (var shift = 0; ; shift += 7)
        {
            if (shift == 35)
                throw new FormatException();
            var b = Take(record, ref pos, 1)[0];
            if (shift == 28 && b > 0x0F)
                throw new FormatException();
            length |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                break;
        }
        if (length < 0)
            throw new FormatException();
        return length == 0 ? "" : Utf8.GetString(Take(record, ref pos, length));
    }

    private static DateTimeOffset ReadWhen(ReadOnlySpan<byte> record, ref int pos)
    {
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(Take(record, ref pos, 8));
        var offsetMinutes = BinaryPrimitives.ReadInt16LittleEndian(Take(record, ref pos, 2));
        try { return new DateTimeOffset(ticks, TimeSpan.FromMinutes(offsetMinutes)); }
        catch { return DateTimeOffset.UnixEpoch; }
    }

    private void Cache(int index, CommitMetadata m)
    {
        if (_decoded.ContainsKey(index))
            return;
        if (_decoded.Count >= DecodeCacheCap && _decodedOrder.Count > 0)
            _decoded.Remove(_decodedOrder.Dequeue());
        _decoded[index] = m;
        _decodedOrder.Enqueue(index);
    }
}
