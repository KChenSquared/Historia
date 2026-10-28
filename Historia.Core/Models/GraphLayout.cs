namespace Historia.Models;

/// <summary>
/// Complete layout for a contiguous row window. Arrays are index-aligned:
/// <c>Rows[i]</c> and <c>Commits[i]</c> describe the same commit.
/// </summary>
public sealed class GraphLayout : IDisposable
{
    /// <summary>A <see cref="RowCount"/>-bounded VIEW over <see cref="SourceState"/>'s
    /// own chunked row accumulator — never a fresh array copy. Computed from <see
    /// cref="SourceState"/>/<see cref="RowCount"/> (both set before this is ever read), so it
    /// needs no initializer of its own. Indexer, <c>Count</c> and <c>Length</c> (array-style
    /// access) are all safe to call at any time; see
    /// <see cref="GraphRowsView"/>'s own doc comment for the concurrency contract.</summary>
    public GraphRowsView Rows => new(SourceState, RowCount);

    /// <summary>The shared, append-only accumulator that <see cref="DagLayoutEngine"/>
    /// derives lines from on demand. Null only for a layout with no engine behind it (<see
    /// cref="Empty"/>) — there are no rows to derive lines for either way.</summary>
    internal GraphLayoutState? SourceState { get; init; }

    /// <summary>How many of <see cref="SourceState"/>'s real edges existed when this snapshot
    /// was built. Later <c>Extend</c> calls on the same state only append — capping reads here
    /// gives an older snapshot the same lines forever, even while the shared accumulator keeps
    /// growing underneath it (snapshot isolation).</summary>
    internal int RealEdgeCap { get; init; }

    /// <summary>This snapshot's own off-window stub edges, regenerated fresh at build time from
    /// whichever reservations were still open then — never shared with another snapshot.</summary>
    internal GraphEdge[] OffWindowEdges { get; init; } = [];

    /// <summary>Rows pulled either side of a requested row into one cached derivation, so
    /// scrolling a screenful at a time rarely re-derives. Only scrolling past the
    /// window's edge gets this overscan. A jump (scrollbar drag, search, reveal) derives just the
    /// requested rows ± <see cref="JumpOverscan"/>, because a drag leaves the window on every
    /// frame and a derivation costs window rows × every lane, on screen or not.</summary>
    private const int WindowOverscan = 1_000;

    /// <summary>Rows either side of the requested range in the tight window a jump derives.</summary>
    private const int JumpOverscan = 128;

    /// <summary>A request more than this many rows beyond the current window is a jump; within
    /// it, a scroll. A slow drag of a few hundred rows per frame still counts as a scroll and gets
    /// a full window that lasts several frames.</summary>
    private const int JumpDistance = 256;

    private (int A, int B, RowLaneSegment[] Segs, int[] SegStart, int[] SegCount,
        RowElbow[] Elbs, int[] ElbStart, int[] ElbCount)? _window;

    /// <summary>Per-row commit metadata. Usually an <see cref="ArrayCommitStore"/>; a
    /// disk-backed store when the graph-from-disk setting is on. Settable within Core
    /// so <c>GraphLayoutService</c> can swap the array for the paged store after the engine
    /// has built the layout — <c>required</c> is dropped only because an internal setter and a
    /// public required member cannot coexist; <see cref="DagLayoutEngine"/> always sets it.</summary>
    public ICommitStore Commits { get; internal set; } = ArrayCommitStore.Empty;

    /// <summary>Materialized lazily on first access, from <see cref="SourceState"/>'s
    /// real-edge accumulator (capped at <see cref="RealEdgeCap"/>, for the same snapshot-isolation
    /// reason <see cref="Rows"/> is capped at <see cref="RowCount"/>) plus this snapshot's own
    /// <see cref="OffWindowEdges"/>. Nothing in App or Core code actually reads this — only tests
    /// do — so <c>DagLayoutEngine.Build</c> doesn't pay to build it on every single Build/Extend
    /// call; <see cref="EdgeCount"/> is a plain field computed without ever materializing it.</summary>
    public GraphEdge[] Edges
    {
        get
        {
            if (_edges is { } cached)
                return cached;
            if (SourceState is not { } state)
                return _edges = OffWindowEdges;

            GraphEdge[] real;
            using (state.EnterAsReader())
            {
                var n = Math.Min(RealEdgeCap, state.RealEdges.Count);
                real = new GraphEdge[n];
                for (var i = 0; i < n; i++)
                    real[i] = state.RealEdges[i];
            }
            var combined = new GraphEdge[real.Length + OffWindowEdges.Length];
            real.CopyTo(combined, 0);
            OffWindowEdges.CopyTo(combined, real.Length);
            return _edges = combined;
        }
    }

    private GraphEdge[]? _edges;

    public required GraphDecoration[] Decorations { get; init; }

    public required int RowCount { get; init; }
    public required int EdgeCount { get; init; }
    public required int TotalLaneCount { get; init; }
    public required bool IsComplete { get; init; }

    /// <summary>The row index carrying HEAD, or -1 when HEAD has not been placed in this layout.</summary>
    public int HeadRow { get; init; } = -1;

    /// <summary>
    /// Maps <see cref="GitObjectId"/> → row index. Built once; used for selection sync only.
    /// <see cref="DagLayoutEngine.Build"/> hands this the accumulator's own, still-growing
    /// dictionary rather than a per-snapshot copy (see its comment) — safe for this snapshot's
    /// own <see cref="Consume"/> writer, but not for an unsynchronized reader while a background
    /// <c>ExtendLayoutAsync</c> is inserting into the very same instance under
    /// <c>lock (existing.State)</c> (<c>CommitGraphControl
    /// .ResolveRow</c> reads it from the render thread with no lock at all —
    /// an unsynchronized <see cref="Dictionary{TKey,TValue}"/> read concurrent with an insert
    /// that resizes it can return a wrong row or throw). The value stored here is wrapped so
    /// every lookup — through this property's own <c>TryGetValue</c>/indexer, or through <see
    /// cref="TryGetRow"/> (the preferred call for new code) — takes <c>lock (SourceState)</c>
    /// when this snapshot still has one, and ignores any row at or past this snapshot's own
    /// <see cref="RowCount"/> (an older snapshot must not resolve a row a later extend appended),
    /// so callers using only the dictionary interface are safe too.
    /// </summary>
    public required IReadOnlyDictionary<GitObjectId, int> RowByCommitId
    {
        get => _rowByCommitIdView;
        init { _rawRowByCommitId = value; _rowByCommitIdView = new RowByCommitIdView(this); }
    }

    private IReadOnlyDictionary<GitObjectId, int> _rawRowByCommitId = EmptyRowMap;
    private IReadOnlyDictionary<GitObjectId, int> _rowByCommitIdView = EmptyRowMap;
    private static readonly IReadOnlyDictionary<GitObjectId, int> EmptyRowMap = new Dictionary<GitObjectId, int>();

    /// <summary>Locked, bounds-checked row lookup. Takes
    /// <c>lock (SourceState)</c> when this snapshot has one (every layout except <see
    /// cref="Empty"/>), so it never races the accumulator's writer; ignores any row at or past
    /// this snapshot's own <see cref="RowCount"/>. Preferred over <see cref="RowByCommitId"/>'s
    /// <c>TryGetValue</c> for new code — that property routes through this same method.</summary>
    public bool TryGetRow(GitObjectId id, out int row)
    {
        bool found;
        int value;
        if (SourceState is { } state)
        {
            using (state.EnterAsReader())
                found = _rawRowByCommitId.TryGetValue(id, out value);
        }
        else
        {
            found = _rawRowByCommitId.TryGetValue(id, out value);
        }

        if (found && value < RowCount)
        {
            row = value;
            return true;
        }

        row = -1;
        return false;
    }

    /// <summary>Thin <see cref="IReadOnlyDictionary{TKey,TValue}"/> facade over <see
    /// cref="RowByCommitId"/> that routes every lookup through <see cref="TryGetRow"/> (see that
    /// property's doc comment). Only <see cref="TryGetValue"/> and the indexer sit on any real
    /// call path; <see cref="Count"/>/<see cref="Keys"/>/<see cref="Values"/>/enumeration exist
    /// for interface completeness (exercised by tests, not by App code) and take the same lock
    /// for a single consistent snapshot.</summary>
    private sealed class RowByCommitIdView(GraphLayout owner) : IReadOnlyDictionary<GitObjectId, int>
    {
        public int this[GitObjectId key] =>
            owner.TryGetRow(key, out var row) ? row : throw new KeyNotFoundException();

        public bool ContainsKey(GitObjectId key) => owner.TryGetRow(key, out _);
        public bool TryGetValue(GitObjectId key, out int value) => owner.TryGetRow(key, out value);

        public IEnumerable<GitObjectId> Keys => Snapshot().Select(kv => kv.Key);
        public IEnumerable<int> Values => Snapshot().Select(kv => kv.Value);
        public int Count => Snapshot().Count;

        public IEnumerator<KeyValuePair<GitObjectId, int>> GetEnumerator() => Snapshot().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private List<KeyValuePair<GitObjectId, int>> Snapshot()
        {
            if (owner.SourceState is { } state)
                using (state.EnterAsReader())
                    return Filter();
            return Filter();
        }

        private List<KeyValuePair<GitObjectId, int>> Filter()
        {
            var rowCount = owner.RowCount;
            var list = new List<KeyValuePair<GitObjectId, int>>();
            foreach (var kv in owner._rawRowByCommitId)
                if (kv.Value < rowCount)
                    list.Add(kv);
            return list;
        }
    }

    public ReadOnlySpan<RowLaneSegment> GetRowSegments(int row)
    {
        if ((uint)row >= (uint)RowCount) return [];
        EnsureWindow(row);
        var w = _window!.Value;
        var i = row - w.A;
        return w.SegCount[i] > 0 ? w.Segs.AsSpan(w.SegStart[i], w.SegCount[i]) : [];
    }

    public ReadOnlySpan<RowElbow> GetRowElbows(int row)
    {
        if ((uint)row >= (uint)RowCount) return [];
        EnsureWindow(row);
        var w = _window!.Value;
        var i = row - w.A;
        return w.ElbCount[i] > 0 ? w.Elbs.AsSpan(w.ElbStart[i], w.ElbCount[i]) : [];
    }

    private void EnsureWindow(int row) => EnsureRows(row, row);

    /// <summary>
    /// Makes sure rows <paramref name="first"/>..<paramref name="last"/> are inside the cached
    /// derivation window. A renderer calls this once per frame for the rows it will draw, so the
    /// whole block is derived up front instead of from whichever row is read first.
    /// Scrolling just past the window's edge (within <see cref="JumpDistance"/> rows), or a
    /// layout with no window yet, derives the full ± <see cref="WindowOverscan"/>. A jump further
    /// than that derives only the range ± <see cref="JumpOverscan"/>, so a scrollbar drag that
    /// leaves the window every frame stays cheap however many lanes the history has. Allocates
    /// nothing when the range is already covered.
    /// </summary>
    public void EnsureRows(int first, int last)
    {
        if (RowCount == 0) return;
        first = Math.Max(0, first);
        last = Math.Min(RowCount - 1, last);
        if (last < first) return;

        var cur = _window;
        if (cur is { } covered && first >= covered.A && last <= covered.B) return;

        if (SourceState is null)
        {
            var len = last - first + 1;
            _window = (first, last, [], new int[len], new int[len], [], new int[len], new int[len]);
            return;
        }

        int a, b;
        var jump = false;
        if (cur is { } w)
        {
            var gap = first > w.B ? first - w.B : w.A - last;
            jump = gap > JumpDistance;
        }

        if (jump)
        {
            a = first - JumpOverscan;
            b = last + JumpOverscan;
        }
        else
        {
            a = first - WindowOverscan;
            b = last + WindowOverscan;
        }
        a = Math.Max(0, a);
        b = Math.Min(RowCount - 1, b);

        var (segs, segStart, segCount, elbs, elbStart, elbCount) =
            DagLayoutEngine.DeriveWindow(SourceState, RealEdgeCap, OffWindowEdges, RowCount, a, b);
        _window = (a, b, segs, segStart, segCount, elbs, elbStart, elbCount);
        WindowDerivations++;
    }

    /// <summary>How many times this snapshot has derived a window. Tests only.</summary>
    internal int WindowDerivations { get; private set; }

    /// <summary>The cached window's row range, or null before first use or after <see
    /// cref="Dispose"/>. Tests only.</summary>
    internal (int A, int B)? WindowRange => _window is { } w ? (w.A, w.B) : null;

    public static GraphLayout Empty { get; } = new()
    {
        Commits = ArrayCommitStore.Empty,
        Decorations = [],
        RowCount = 0,
        EdgeCount = 0,
        TotalLaneCount = 0,
        IsComplete = true,
        RowByCommitId = new Dictionary<GitObjectId, int>(),
    };

    /// <summary>Drops this snapshot's cached derivation window. The shared <see
    /// cref="SourceState"/> is owned by the layout service, not this snapshot, so it is never
    /// disposed here.</summary>
    public void Dispose() { _window = null; }
}

/// <summary>
/// A <see cref="GraphLayout.RowCount"/>-bounded, read-only view over a
/// <see cref="GraphLayoutState"/>'s own chunked row accumulator — what
/// <see cref="GraphLayout.Rows"/> hands back instead of a fresh <c>GraphRow[]</c> copy on every
/// Build/Extend call. Deliberately a plain class with no custom <c>Equals</c>/<c>GetHashCode</c>
/// override (and NOT a record type, which would compare its private fields — including the
/// shared, mutable <c>SourceState</c> reference — rather than the rows themselves): callers that
/// compare two layouts' rows for value equality (xUnit's <c>Assert.Equal</c> included) fall
/// through to a sequence comparison via <see cref="IEnumerable{T}"/>, exactly as they would for
/// a plain array — verified against xUnit's <c>AssertEqualityComparer</c>, which
/// only tries <c>IEquatable&lt;T&gt;</c>/<c>IStructuralEquatable</c> before falling back to an
/// element-wise <c>IEnumerable</c> comparison. <c>Length</c> sits alongside <c>Count</c> so
/// callers can use it like a <c>GraphRow[]</c>.
/// <para>
/// Every member here is safe to call from any thread at any time with no lock — see
/// <see cref="ChunkedAppendList{T}"/>'s own concurrency contract, which this is a thin,
/// <see cref="GraphLayout.RowCount"/>-bounded wrapper around.
/// </para>
/// </summary>
public sealed class GraphRowsView(GraphLayoutState? source, int count) : IReadOnlyList<GraphRow>
{
    public int Count => count;

    /// <summary>Same as <see cref="Count"/>, for callers that use the view like a <c>GraphRow[]</c>.</summary>
    public int Length => count;

    public GraphRow this[int index]
    {
        get
        {
            if ((uint)index >= (uint)count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return source!.Rows[index];
        }
    }

    public IEnumerator<GraphRow> GetEnumerator()
    {
        for (var i = 0; i < count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
