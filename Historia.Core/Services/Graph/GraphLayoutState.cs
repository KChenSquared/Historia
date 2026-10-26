namespace Historia.Services.Graph;

/// <summary>
/// Opaque allocator state carried between layout calls so incremental extension produces the
/// same result as a full recomputation. Never renumbers lanes already assigned to
/// existing rows.
/// </summary>
public sealed class GraphLayoutState
{
    internal int MaxLanes { get; init; } = int.MaxValue;

    /// <summary>When false, <see cref="Commits"/> is left empty — the owning service is
    /// spilling per-row metadata to a disk-backed store instead of keeping it resident.</summary>
    internal bool RetainCommits { get; init; } = true;

    /// <summary>Next sequential row index to assign.</summary>
    internal int NextRowIndex;

    /// <summary>Next never-used raw lane.</summary>
    internal int LaneCeiling;

    /// <summary>Raw lanes released and available for reuse (always the lowest is taken first).</summary>
    internal readonly SortedSet<int> FreeLanes = [];

    /// <summary>raw lane → the last row anything occupied it: a node, or a line that ended there.
    /// Lanes past the end, or never used, read as -1. A merge edge may run down a lane only when
    /// nothing occupied it after the merge row.</summary>
    internal int[] LaneLastBusyRow = [];

    /// <summary>parent id → the child reservations expecting it.</summary>
    internal readonly Dictionary<GitObjectId, List<Reservation>> Reservations = [];

    /// <summary>
    /// raw lane → how many pending reservations name it as their parent lane (i.e. entries with
    /// <see cref="Reservation.RawLane"/> ≥ 0). Lets <c>ReleaseLane</c> answer "is this lane still
    /// spoken for?" in O(1) instead of scanning every reservation of every parent.
    /// </summary>
    internal readonly Dictionary<int, int> ReservedLaneCount = [];

    /// <summary>raw lane → compacted lane, assigned on first row placement and never changed.</summary>
    internal readonly Dictionary<int, int> CompactionMap = [];
    internal int NextCompactedLane;

    /// <summary>The trunk tip the pre-pass chose, placed on raw lane 0. Set once by
    /// <see cref="DagLayoutEngine.Compute"/>, carried unchanged by
    /// <see cref="DagLayoutEngine.Extend"/>, and null when the window has fewer than two
    /// leaf branch tips.</summary>
    internal GitObjectId? TrunkTip;

    /// <summary>compacted lane → color index, seeded once from the lane's topmost commit.</summary>
    internal readonly Dictionary<int, int> LaneColor = [];

    /// <summary>compacted lane → the stable key its colour was seeded from: the lane's branch
    /// name if it has one, else the SHA of the commit that first opened the lane. a pure
    /// function of the walk, so incremental and full layouts colour identically and a repo
    /// reopens in the same shades. Never renumbered.</summary>
    internal readonly Dictionary<int, string> LaneKey = [];

    // Accumulators — real edges only append; OffWindow edges are regenerated each build.
    // Rows is chunked (ChunkedAppendList), not a plain List<T> — see its own doc
    // comment. This is what lets GraphLayout.Rows be a RowCount-bounded VIEW over this same
    // instance instead of a fresh array copy on every Build/Extend.
    internal readonly ChunkedAppendList<GraphRow> Rows = new();
    // Commits is chunked too: BoundedCommitStore snapshots read it with no lock (Find searches it
    // from a background thread) while an extend appends, which ChunkedAppendList's contract
    // allows and a List<T> reallocating its array does not.
    internal readonly ChunkedAppendList<CommitMetadata> Commits = new();
    internal readonly List<GraphEdge> RealEdges = [];
    internal readonly List<GraphDecoration> Decorations = [];
    internal readonly Dictionary<GitObjectId, int> RowByCommitId = [];
    internal int HeadRow = -1;

    /// <summary>How many threads are blocked in <see cref="EnterAsReader"/> waiting for this
    /// state's lock. The monitor is not fair, so a chunked extend that takes the lock straight
    /// back would beat a waiting reader to it every time; the extend reads this between chunks and
    /// stands aside while it is above zero.</summary>
    internal int LockWaiters;

    /// <summary>How many times a chunked extend stood aside for a waiting reader. Tests only.</summary>
    internal int ExtendYields;

    /// <summary>Takes <c>lock (this)</c> for a reader, announcing itself in <see
    /// cref="LockWaiters"/> while it has to wait. Same mutual exclusion as the <c>lock</c>
    /// statement; dispose the result to release.</summary>
    internal ReaderHold EnterAsReader()
    {
        if (!Monitor.TryEnter(this))
        {
            Interlocked.Increment(ref LockWaiters);
            try { Monitor.Enter(this); }
            finally { Interlocked.Decrement(ref LockWaiters); }
        }
        return new ReaderHold(this);
    }

    /// <summary>Releases the lock taken by <see cref="EnterAsReader"/>.</summary>
    internal readonly struct ReaderHold(GraphLayoutState state) : IDisposable
    {
        public void Dispose() => Monitor.Exit(state);
    }

    /// <summary>Crossing index: boundary rows are multiples of this stride. Sized so a
    /// window's checkpoint lookup costs about (concurrent lanes) work, not (total rows) — linux
    /// at 300k rows / 637 lanes gives ~1,172 checkpoints of a few hundred entries each, versus
    /// re-deriving 106M segment entries per <c>Build</c>.</summary>
    internal const int CheckpointStride = 256;

    /// <summary>checkpoint index (boundary row = index × <see cref="CheckpointStride"/>) → the
    /// indices into <see cref="RealEdges"/> of every real edge spanning that boundary
    /// (<c>FromRow &lt; boundary ≤ ToRow</c>). Append-only: populated once, when the edge is
    /// created, for every boundary it will ever cross — a long-lived branch's edge is written
    /// into potentially many checkpoints at creation time, but each checkpoint is then never
    /// touched again.</summary>
    internal readonly Dictionary<int, List<int>> CheckpointCrossing = [];

    /// <summary>(FromRow ÷ <see cref="CheckpointStride"/>) → the indices into <see
    /// cref="RealEdges"/> of every real edge whose <c>FromRow</c> falls in that block. Lets a
    /// window derivation find "edges starting in [checkpoint, b]" without scanning every edge.</summary>
    internal readonly Dictionary<int, List<int>> EdgesByFromBlock = [];

    /// <param name="RawLane">The raw lane reserved for the PARENT.</param>
    /// <param name="ChildLane">The raw lane the CHILD occupies — equal to <paramref name="RawLane"/>
    /// for a first parent; for a second-or-later parent it is the merge commit's own raw lane, so
    /// the emitted edge starts at the merge node rather than floating free of it.</param>
    internal readonly record struct Reservation(int RawLane, int ChildLane, int ChildRow, bool IsFirstParent);
}
