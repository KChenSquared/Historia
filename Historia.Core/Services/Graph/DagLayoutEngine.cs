namespace Historia.Services.Graph;

/// <summary>
/// Pure C# DAG layout algorithm. Converts a topologically ordered commit stream
/// (children before parents) into visual rows, lanes, and edges in three conceptual passes:
/// ordered walk → greedy lane assignment + edge emission → stable compaction + color.
/// Input is <see cref="RawCommit"/> records.
/// </summary>
public sealed class DagLayoutEngine
{
    /// <summary>Computes a fresh layout for up to <paramref name="maxRows"/> commits.</summary>
    /// <param name="retainCommits">When false, the per-row <see cref="CommitMetadata"/> is not
    /// accumulated in the layout state — the caller is spilling it to a disk-backed store and
    /// will swap that in. The built <see cref="GraphLayout.Commits"/> is then an empty
    /// <see cref="ArrayCommitStore"/> until the swap.</param>
    /// <param name="isAncestor"> checked-out-branch exception: a full-history ancestry
    /// oracle — <c>isAncestor(a, b)</c> is true when commit <paramref name="a"/> is an ancestor of
    /// (or equal to) commit <c>b</c>. Supplied by <see cref="GraphLayoutService"/> from the live
    /// repo; null on the optimistic stale first paint, where the exception is skipped and the
    /// plain newest-tip-wins order stands until the verified recompute.</param>
    public GraphLayoutResult Compute(
        IReadOnlyList<RawCommit> commits,
        IReadOnlyDictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>>? decorations,
        int maxRows,
        int maxLanes = int.MaxValue,
        CancellationToken ct = default,
        bool retainCommits = true,
        Func<GitObjectId, GitObjectId, bool>? isAncestor = null)
    {
        var state = new GraphLayoutState { MaxLanes = Math.Max(2, maxLanes), RetainCommits = retainCommits };
        SeedTipPriority(state, commits, decorations, isAncestor);
        Consume(state, commits, 0, decorations, maxRows, ct);
        return Build(state, complete: commits.Count <= maxRows, incremental: false);
    }

    /// <summary>
    /// Resumes the walk from where <paramref name="previous"/> left off, appending up to
    /// <paramref name="additionalRows"/> more rows. Lane indices already assigned are never
    /// renumbered — extension is byte-identical to a full recomputation over the same range.
    /// </summary>
    public GraphLayoutResult Extend(
        GraphLayoutResult previous,
        IReadOnlyList<RawCommit> nextCommits,
        IReadOnlyDictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>>? decorations,
        int additionalRows,
        CancellationToken ct = default)
    {
        var state = previous.State;
        Consume(state, nextCommits, 0, decorations, state.Rows.Count + additionalRows, ct);
        return Build(state, complete: nextCommits.Count == 0, incremental: true);
    }

    /// <summary>
    /// Places commits from <paramref name="nextCommits"/>, starting at index
    /// <paramref name="start"/>, until the state holds <paramref name="rowCeiling"/> rows or the
    /// list ends, without building a snapshot. Each commit is placed completely before the next
    /// one is looked at, so the state is consistent between any two calls: a long extend can be
    /// split into several calls, each under its own hold of the state's lock, and ended by one
    /// <see cref="Snapshot"/>. The caller holds <c>lock (state)</c>.
    /// </summary>
    internal static void ExtendInPlace(
        GraphLayoutState state,
        IReadOnlyList<RawCommit> nextCommits,
        int start,
        IReadOnlyDictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>>? decorations,
        int rowCeiling,
        CancellationToken ct = default)
        => Consume(state, nextCommits, start, decorations, rowCeiling, ct);

    /// <summary>Builds the immutable snapshot of <paramref name="state"/> as it stands — the
    /// closing step of a chunked <see cref="ExtendInPlace"/>. The caller holds
    /// <c>lock (state)</c>.</summary>
    internal static GraphLayoutResult Snapshot(GraphLayoutState state, bool complete)
        => Build(state, complete, incremental: true);

    private static void Consume(
        GraphLayoutState s,
        IReadOnlyList<RawCommit> commits,
        int start,
        IReadOnlyDictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>>? decorations,
        int rowCeiling,
        CancellationToken ct)
    {
        for (var ci = start; ci < commits.Count; ci++)
        {
            if (s.Rows.Count >= rowCeiling)
                break;
            ct.ThrowIfCancellationRequested();
            var commit = commits[ci];

            var row = s.NextRowIndex++;
            var deco = decorations is not null && decorations.TryGetValue(commit.Id, out var d) ? d : null;

            // ── Determine this commit's raw lane ──
            int rawLane;
            List<Res>? incoming = null;
            if (s.Reservations.TryGetValue(commit.Id, out incoming) && incoming.Count > 0)
            {
                // A deferred merge-parent reservation carries RawLane -1 (its column is
                // not claimed until the parent is actually placed here). Take the lowest real
                // lane; if every incoming reservation is deferred, allocate now.
                var known = int.MaxValue;
                foreach (var res in incoming)
                    if (res.RawLane >= 0 && res.RawLane < known)
                        known = res.RawLane;
                rawLane = known == int.MaxValue ? AllocLane(s) : known;
                RemoveReservationsFor(s, commit.Id, incoming);
            }
            else if (s.TrunkTip is { } trunk && trunk.Equals(commit.Id))
            {
                // The trunk tip is placed on raw lane 0 by the pre-pass reservation. Lane 0 is
                // below LaneCeiling and was never put in FreeLanes, so it is ours to take
                // directly; ReleaseLane recycles it normally once the trunk ends or merges.
                rawLane = 0;
            }
            else
            {
                rawLane = AllocLane(s); // branch tip / orphan root
            }

            var compacted = Compact(s, rawLane);
            var laneLastBusy = LastBusyRow(s, rawLane);
            MarkBusy(s, rawLane, row);

            // ── Color (seed once per compacted lane) ──
            var color = ResolveColor(s, compacted, deco, commit.Id);

            // ── Emit incoming edges (child → this commit) ──
            if (incoming is not null)
            {
                // A commit that lists the same parent id twice (a self-merge, a malformed
                // octopus) reserves it once per slot; the walk should draw one edge from that
                // child, not one per reservation. One child == one row, so ChildRow dedupes.
                HashSet<int>? seenChildRows = incoming.Count > 1 ? [] : null;
                foreach (var res in incoming)
                {
                    // Any extra (real) lane this commit was reserved in is now free. A deferred
                    // reservation (RawLane -1) never claimed one. Done for every reservation,
                    // including a de-duplicated one.
                    if (res.RawLane >= 0 && res.RawLane != rawLane)
                    {
                        MarkBusy(s, res.RawLane, row);
                        ReleaseLane(s, res.RawLane);
                    }

                    if (seenChildRows is not null && !seenChildRows.Add(res.ChildRow))
                        continue;

                    // The edge always leaves the CHILD's own lane. For a first parent
                    // that lane equals this commit's lane and the line is straight; when the child
                    // sat in a different lane it is a branch-out. A second-or-later parent is a
                    // merge edge into this (merge) commit, and it too starts at the child.
                    var fromLane = Compact(s, res.ChildLane);
                    var type = res.IsFirstParent
                        ? (fromLane == compacted ? EdgeType.Straight : EdgeType.BranchOut)
                        : EdgeType.MergeIn;
                    // a first-parent edge keeps the child's lane colour. A
                    // MergeIn edge is drawn in the SOURCE (merged-in) branch's colour — this
                    // commit's own lane colour — not the destination (merge commit's) lane.
                    var fromColor = s.LaneColor.GetValueOrDefault(fromLane, color);

                    // A merge edge runs down this commit's lane from the merge row. When that lane
                    // held other commits in between, the line would pass over them and read as
                    // though they were merged, so it takes a free lane that stayed empty over the
                    // whole span instead, or a new lane when none did.
                    var viaLane = -1;
                    if (type == EdgeType.MergeIn && fromLane != compacted && laneLastBusy > res.ChildRow)
                    {
                        var (viaRaw, via) = FindClearLane(s, res.ChildRow, fromLane, compacted);
                        if (viaRaw < 0 && s.LaneCeiling < s.MaxLanes - 1)
                        {
                            // The new lane carries only this line, which ends here, so it goes
                            // straight to the free list for the next branch to reuse.
                            viaRaw = s.LaneCeiling++;
                            via = Compact(s, viaRaw);
                            ReleaseLane(s, viaRaw);
                        }
                        if (viaRaw >= 0)
                        {
                            MarkBusy(s, viaRaw, row);
                            viaLane = via;
                        }
                    }

                    var newEdge = new GraphEdge
                    {
                        FromRow = res.ChildRow,
                        FromLane = fromLane,
                        ToRow = row,
                        ToLane = compacted,
                        Type = type,
                        ColorIndex = type == EdgeType.MergeIn ? color : fromColor,
                        // the lead-in (NodeToBottom/Full, before the elbow) always
                        // matches fromColor — for MergeIn that's the merge commit's OWN lane
                        // colour, which is what's genuinely still drawn there for this row span
                        // (the first-parent edge continues straight through the same lane).
                        // Only the elbow at ToRow switches to the merged-in branch's colour.
                        LeadColorIndex = fromColor,
                        ViaLane = viaLane,
                    };
                    s.RealEdges.Add(newEdge);
                    IndexNewRealEdge(s, s.RealEdges.Count - 1, newEdge);
                }
            }

            // ── Row + metadata + decorations ──
            var flags = RowFlags.None;
            if (commit.Metadata.ParentCount == 0) flags |= RowFlags.IsRoot;
            if (commit.Metadata.ParentCount >= 2) flags |= RowFlags.IsMerge;
            if (deco is { Count: > 0 })
            {
                flags |= RowFlags.HasDecorations;
                // HEAD is per-badge (IsHead), but a standalone detached-HEAD badge
                // still uses DecorationType.Head. Either marks the row.
                if (deco.Any(x => x.Type == DecorationType.Head || x.IsHead))
                {
                    flags |= RowFlags.IsHead;
                    s.HeadRow = row;
                }
            }

            s.Rows.Add(new GraphRow
            {
                RowIndex = row,
                LaneIndex = compacted,
                ColorIndex = color,
                ParentCount = commit.Metadata.ParentCount,
                Flags = flags,
            });
            if (s.RetainCommits)
                s.Commits.Add(commit.Metadata);
            s.RowByCommitId[commit.Id] = row;

            if (deco is not null)
                foreach (var x in deco)
                    s.Decorations.Add(new GraphDecoration
                    {
                        RowIndex = row,
                        Type = x.Type,
                        Label = x.Label,
                        ColorIndex = color,
                        IsLocal = x.IsLocal,
                        IsHead = x.IsHead,
                        RemotesOrNull = x.Remotes, // copied through, no merging here
                        SiblingsOrNull = x.Siblings,
                    });

            // ── Reserve lanes for parents ──
            if (commit.ParentIds.Count == 0)
            {
                ReleaseLane(s, rawLane);
                continue;
            }

            for (var pi = 0; pi < commit.ParentIds.Count; pi++)
            {
                var parent = commit.ParentIds[pi];
                if (pi == 0)
                {
                    AddReservation(s, parent, new Res(rawLane, rawLane, row, IsFirstParent: true));
                }
                else if (s.Reservations.TryGetValue(parent, out var existing) && existing.Count > 0)
                {
                    // Parent already reserved elsewhere — point the merge edge at that lane if it
                    // is known, else stay deferred. ChildLane is this merge commit's own raw lane.
                    var known = int.MaxValue;
                    foreach (var e in existing)
                        if (e.RawLane >= 0 && e.RawLane < known)
                            known = e.RawLane;
                    AddReservation(s, parent, new Res(known == int.MaxValue ? -1 : known, rawLane, row, IsFirstParent: false));
                }
                else
                {
                    // do NOT allocate a column for a merge parent now. A parent that is far
                    // down the walk — or that turns out to sit on an existing spine — should not
                    // hold a lane in the meantime. -1 = "allocate when the parent is placed".
                    AddReservation(s, parent, new Res(-1, rawLane, row, IsFirstParent: false));
                }
            }
        }
    }

    /// <param name="incremental">True when the state already backed an earlier snapshot, so the
    /// per-row metadata is shared with it instead of copied again.</param>
    private static GraphLayoutResult Build(GraphLayoutState s, bool complete, bool incremental)
    {
        var offWindowList = RegenerateOffWindow(s);
        var offWindow = offWindowList.ToArray();

        // Neither Rows nor Edges is materialized here — GraphLayout.Rows is
        // a RowCount-bounded VIEW straight over s.Rows (a ChunkedAppendList, never copied),
        // and GraphLayout.Edges is computed lazily on first access (nothing in App or Core code
        // actually reads it). EdgeCount is just the sum of what those two arrays' lengths would
        // be.
        //
        // Rows carry no Segment/Elbow slice indices — GraphLayout derives lines
        // for a row window on demand from the shared crossing index (DeriveWindow), rather than
        // materialising flat Segments/Elbows arrays sized to every resident row on
        // every Build/Extend call.
        var layout = new GraphLayout
        {
            SourceState = s,
            RealEdgeCap = s.RealEdges.Count,
            OffWindowEdges = offWindow,
            Commits = BuildCommitStore(s, incremental),
            Decorations = DecorationsOf(s),
            RowCount = s.Rows.Count,
            EdgeCount = s.RealEdges.Count + offWindow.Length,
            TotalLaneCount = s.NextCompactedLane,
            IsComplete = complete && s.Reservations.Count == 0,
            HeadRow = s.HeadRow,
            // share the accumulator's dictionary instead of copying it wholesale
            // on every Build — it only ever grows, and a lookup against an older snapshot is
            // bounds-checked by the caller against that snapshot's own RowCount.
            RowByCommitId = s.RowByCommitId,
        };

        return new GraphLayoutResult(layout, s);
    }

    // Decorations only ever append, so the array last built for a state is still exact while its
    // length matches the accumulator's: an extend that places no decorated row reuses it, and a
    // reader's per-array index (keyed on the array's identity) stays valid across the extend.
    private sealed record DecorationSnapshot(int Count, GraphDecoration[] Items);

    private static readonly ConditionalWeakTable<GraphLayoutState, DecorationSnapshot> DecorationSnapshots = [];

    private static GraphDecoration[] DecorationsOf(GraphLayoutState s)
    {
        if (DecorationSnapshots.TryGetValue(s, out var last) && last.Count == s.Decorations.Count)
            return last.Items;
        var items = s.Decorations.ToArray();
        DecorationSnapshots.AddOrUpdate(s, new DecorationSnapshot(items.Length, items));
        return items;
    }

    private static ICommitStore BuildCommitStore(GraphLayoutState s, bool incremental)
    {
        if (!s.RetainCommits || s.Commits.Count == 0)
            return new ArrayCommitStore([]);
        return incremental
            ? new BoundedCommitStore(s.Commits, s.Commits.Count)
            : new ArrayCommitStore(s.Commits.ToArray());
    }

    /// <summary>An in-memory <see cref="ICommitStore"/> over the first <c>count</c> entries of a
    /// state's append-only metadata list. The list is shared with every later snapshot of the same
    /// state instead of being copied per extend; an entry below <c>count</c> is never rewritten,
    /// so reading it needs no lock while the list keeps growing behind it.</summary>
    private sealed class BoundedCommitStore(List<CommitMetadata> source, int count) : ICommitStore
    {
        public CommitMetadata this[int index]
        {
            get
            {
                if ((uint)index >= (uint)count)
                    throw new ArgumentOutOfRangeException(nameof(index));
                return source[index];
            }
        }

        public int Count => count;

        public void Prefetch(int firstRow, int lastRow) { }

        // In-memory: no handle to lease, nothing to release.
        public ICommitStore? AddRef() => this;

        public void Dispose() { }

        public IReadOnlyList<int> Search(string term)
        {
            var hits = new List<int>();
            var t = term.Trim();
            if (t.Length == 0)
                return hits;
            for (var i = 0; i < count; i++)
            {
                var m = source[i];
                if (m.MessageShort.Contains(t, StringComparison.OrdinalIgnoreCase)
                    || m.AuthorName.Contains(t, StringComparison.OrdinalIgnoreCase)
                    || m.Id.ToString().StartsWith(t, StringComparison.OrdinalIgnoreCase))
                    hits.Add(i);
            }
            return hits;
        }

        public IEnumerator<CommitMetadata> GetEnumerator()
        {
            for (var i = 0; i < count; i++)
                yield return source[i];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Indexes a newly created real edge into the crossing checkpoints it
    /// will span and the from-row block it starts in, so a later window derivation can find it
    /// without scanning every edge ever created.</summary>
    private static void IndexNewRealEdge(GraphLayoutState s, int index, GraphEdge edge)
    {
        const int stride = GraphLayoutState.CheckpointStride;

        var block = edge.FromRow / stride;
        if (!s.EdgesByFromBlock.TryGetValue(block, out var blockList))
            s.EdgesByFromBlock[block] = blockList = [];
        blockList.Add(index);

        var firstCheckpoint = edge.FromRow / stride + 1; // first boundary strictly after FromRow
        var lastCheckpoint = edge.ToRow / stride;         // last boundary at or before ToRow
        for (var cp = firstCheckpoint; cp <= lastCheckpoint; cp++)
        {
            if (!s.CheckpointCrossing.TryGetValue(cp, out var list))
                s.CheckpointCrossing[cp] = list = [];
            list.Add(index);
        }
    }

    /// <summary>
    /// Derives <see cref="RowLaneSegment"/>/<see cref="RowElbow"/> data for rows
    /// <paramref name="a"/>..<paramref name="b"/> only, using <paramref name="state"/>'s crossing
    /// index to gather just the edges that can touch that range instead of every edge ever laid
    /// out. Produces byte-identical per-row content to <see cref="DeriveWindow"/> over
    /// the whole row range, because edges outside the gathered set cannot contribute to any row in
    /// [a, b] (a real edge spans a contiguous row range; an edge not selected either never
    /// touches [a, b] or was excluded from a checkpoint it doesn't actually cross), and the
    /// gathered edges are processed in the same relative order (ascending <c>RealEdges</c> index,
    /// then off-window edges) as the full derivation would visit them.
    /// </summary>
    internal static (RowLaneSegment[] Segments, int[] SegStart, int[] SegCount,
        RowElbow[] Elbows, int[] ElbStart, int[] ElbCount) DeriveWindow(
        GraphLayoutState state, int realEdgeCap, GraphEdge[] offWindowEdges, int rowCount, int a, int b)
    {
        a = Math.Max(0, a);
        b = Math.Min(rowCount - 1, b);
        if (b < a)
            return ([], [], [], [], [], []);

        const int stride = GraphLayoutState.CheckpointStride;
        var checkpoint = a / stride;

        // GraphLayoutService.ExtendLayoutAsync mutates this same state object (appending
        // to CheckpointCrossing/EdgesByFromBlock/RealEdges via IndexNewRealEdge) under
        // `lock (existing.State)` while a background walk streams in more commits. This method
        // runs on the render thread against that same live state, so it must take the same lock
        // or a scroll-triggered extend can mutate a List<int> mid-enumeration here
        // (InvalidOperationException: "Collection was modified").
        lock (state)
        {
            var candidates = new SortedSet<int>();
            if (state.CheckpointCrossing.TryGetValue(checkpoint, out var crossing))
                foreach (var i in crossing)
                    if (i < realEdgeCap)
                        candidates.Add(i);

            var startBlock = checkpoint;
            var endBlock = b / stride;
            for (var blk = startBlock; blk <= endBlock; blk++)
            {
                if (!state.EdgesByFromBlock.TryGetValue(blk, out var blockList))
                    continue;
                foreach (var i in blockList)
                {
                    if (i >= realEdgeCap)
                        continue;
                    if (state.RealEdges[i].FromRow <= b)
                        candidates.Add(i);
                }
            }

            var edges = new List<GraphEdge>(candidates.Count + offWindowEdges.Length);
            foreach (var i in candidates)
                edges.Add(state.RealEdges[i]);
            edges.AddRange(offWindowEdges);

            return ComputeWindowVisuals(edges, rowCount, a, b);
        }
    }

    /// <summary>Per-edge visual rules, restricted to emitting rows
    /// in [<paramref name="a"/>, <paramref name="b"/>] — <paramref name="rowCount"/> is the
    /// layout's true total row count, used to decide whether
    /// an edge's endpoint is a real row at all, which is independent of the render window.</summary>
    private static (RowLaneSegment[] Segments, int[] SegStart, int[] SegCount,
        RowElbow[] Elbows, int[] ElbStart, int[] ElbCount) ComputeWindowVisuals(
        List<GraphEdge> edges, int rowCount, int a, int b)
    {
        var winLen = b - a + 1;
        var segCounts = new int[winLen];
        var elbCounts = new int[winLen];

        bool InWin(int r) => r >= a && r <= b;

        foreach (var edge in edges)
        {
            var fromIsRealRow = edge.FromRow >= 0 && edge.FromRow < rowCount;
            var mergeTurn = edge.Type == EdgeType.MergeIn && edge.FromLane != edge.ToLane && fromIsRealRow;

            if (fromIsRealRow && InWin(edge.FromRow))
            {
                if (mergeTurn) elbCounts[edge.FromRow - a]++;
                else segCounts[edge.FromRow - a]++;
            }

            var passEnd = Math.Min(edge.ToRow, rowCount);
            var passFrom = Math.Max(edge.FromRow + 1, a);
            var passTo = Math.Min(passEnd - 1, b);
            for (var r = passFrom; r <= passTo; r++)
                segCounts[r - a]++;

            if (edge.ToRow >= 0 && edge.ToRow < rowCount && edge.Type != EdgeType.OffWindow && InWin(edge.ToRow))
            {
                var viaTurn = mergeTurn && edge.ViaLane >= 0;
                if (!viaTurn && (edge.FromLane == edge.ToLane || mergeTurn)) segCounts[edge.ToRow - a]++;
                else elbCounts[edge.ToRow - a]++;
            }
        }

        var totalSegs = 0;
        var totalElbs = 0;
        var segStarts = new int[winLen];
        var elbStarts = new int[winLen];
        for (var r = 0; r < winLen; r++)
        {
            segStarts[r] = totalSegs;
            totalSegs += segCounts[r];
            elbStarts[r] = totalElbs;
            totalElbs += elbCounts[r];
        }

        var allSegments = new RowLaneSegment[totalSegs];
        var allElbows = new RowElbow[totalElbs];
        var segHeads = (int[])segStarts.Clone();
        var elbHeads = (int[])elbStarts.Clone();

        foreach (var edge in edges)
        {
            var leadColor = edge.LeadColorIndex;
            var color = edge.ColorIndex;
            var fromIsRealRow = edge.FromRow >= 0 && edge.FromRow < rowCount;
            var mergeTurn = edge.Type == EdgeType.MergeIn && edge.FromLane != edge.ToLane && fromIsRealRow;
            // A merge edge routed around a busy parent lane runs down its via lane and turns into
            // the parent's node at ToRow; otherwise it runs down the parent's lane itself.
            var viaTurn = mergeTurn && edge.ViaLane >= 0;
            var mergeLane = viaTurn ? edge.ViaLane : edge.ToLane;
            var passLane = mergeTurn ? mergeLane : edge.FromLane;
            var passColor = mergeTurn ? color : leadColor;

            if (fromIsRealRow && InWin(edge.FromRow))
            {
                if (mergeTurn)
                {
                    allElbows[elbHeads[edge.FromRow - a]++] = new RowElbow
                    {
                        FromLane = edge.FromLane,
                        ToLane = mergeLane,
                        ColorIndex = color,
                        Kind = ElbowKind.LeaveBottom,
                    };
                }
                else
                {
                    allSegments[segHeads[edge.FromRow - a]++] = new RowLaneSegment
                    {
                        Lane = edge.FromLane,
                        ColorIndex = leadColor,
                        Type = LaneSegmentType.NodeToBottom
                    };
                }
            }

            var passEnd = Math.Min(edge.ToRow, rowCount);
            var passFrom = Math.Max(edge.FromRow + 1, a);
            var passTo = Math.Min(passEnd - 1, b);
            for (var r = passFrom; r <= passTo; r++)
            {
                allSegments[segHeads[r - a]++] = new RowLaneSegment
                {
                    Lane = passLane,
                    ColorIndex = passColor,
                    Type = LaneSegmentType.Full
                };
            }

            if (edge.ToRow >= 0 && edge.ToRow < rowCount && edge.Type != EdgeType.OffWindow && InWin(edge.ToRow))
            {
                if (!viaTurn && (edge.FromLane == edge.ToLane || mergeTurn))
                {
                    allSegments[segHeads[edge.ToRow - a]++] = new RowLaneSegment
                    {
                        Lane = edge.ToLane,
                        ColorIndex = color,
                        Type = LaneSegmentType.TopToNode
                    };
                }
                else
                {
                    allElbows[elbHeads[edge.ToRow - a]++] = new RowElbow
                    {
                        FromLane = viaTurn ? edge.ViaLane : edge.FromLane,
                        ToLane = edge.ToLane,
                        ColorIndex = color,
                        Kind = ElbowKind.EnterTop,
                    };
                }
            }
        }

        return (allSegments, segStarts, segCounts, allElbows, elbStarts, elbCounts);
    }

    private static List<GraphEdge> RegenerateOffWindow(GraphLayoutState s)
    {
        var offWindow = new List<GraphEdge>();
        var boundaryRow = s.Rows.Count;
        foreach (var (_, list) in s.Reservations)
        {
            foreach (var res in list)
            {
                var fromLane = Compact(s, res.ChildLane);
                var toLane = res.RawLane >= 0 ? Compact(s, res.RawLane) : fromLane;
                var stubColor = res.IsFirstParent || res.RawLane < 0
                    ? s.LaneColor.GetValueOrDefault(fromLane, fromLane % LanePalette.Size)
                    : s.LaneColor.GetValueOrDefault(toLane, toLane % LanePalette.Size);
                offWindow.Add(new GraphEdge
                {
                    FromRow = res.ChildRow,
                    FromLane = fromLane,
                    ToRow = boundaryRow,
                    ToLane = toLane,
                    Type = EdgeType.OffWindow,
                    ColorIndex = stubColor,
                    // An off-window stub never reaches an elbow (derivation skips ToRow for
                    // EdgeType.OffWindow) — its only visible segments are the lead-in ones, which
                    // must match the lane they're actually drawn in, same as a real edge's lead-in.
                    LeadColorIndex = s.LaneColor.GetValueOrDefault(fromLane, fromLane % LanePalette.Size),
                });
            }
        }
        return offWindow;
    }

    // ── Lane allocation ──

    /// <summary>How many commits from the top of the list <see cref="SeedTipPriority"/> scans for
    /// candidate branch tips.</summary>
    private const int TipPriorityScanHorizon = 200;

    /// <summary>
    /// "newest tip wins the trunk, with a checked-out-branch exception". When the walk
    /// window holds two or more branch tips that are leaves (nothing in the window builds on
    /// them), rank them to choose the trunk. Lane 0 is reserved for the trunk tip only; every
    /// other lineage takes the lowest free lane when the walk reaches it, so lanes open
    /// left-to-right in row order. Row order is untouched — the newest commit is still row 0,
    /// a non-trunk tip just takes a higher lane when reached.
    ///
    /// Base order: <b>newest tip first</b>, by committer date (SHA breaks an exact-timestamp tie
    /// so the order is stable per run).
    ///
    /// Exception: the currently checked-out local branch stays on the trunk when its work has
    /// been built on. Let <c>H</c> be that branch's tip among the candidates. If <c>H</c> is not
    /// already newest, scan the tips newer than <c>H</c> newest-first for the first one <c>H</c>
    /// is an ancestor of:
    /// <list type="bullet">
    ///   <item>ancestor of the newest tip → order is unchanged.</item>
    ///   <item>ancestor of a later (non-newest) tip <c>T</c> → <c>T</c> takes the trunk and every
    ///     tip ahead of it shifts one rank right.</item>
    ///   <item>ancestor of none of the newer tips → <c>H</c> takes the trunk and every other tip
    ///     shifts one rank right.</item>
    /// </list>
    /// The exception is skipped when no ancestry oracle is supplied (the optimistic stale first
    /// paint); the verified recompute that follows applies it.
    ///
    /// It ranks only tips within the first <see cref="TipPriorityScanHorizon"/> commits of the
    /// list the engine is handed, and is a pure function of that prefix (plus the oracle) — never
    /// of <c>maxRows</c>. So a partial <see cref="Compute"/> + <see cref="Extend"/> seeds the same
    /// trunk as a full compute over the same list and incremental extension equivalence still holds.
    /// A checked-out branch whose tip is beyond the horizon is not a candidate, so the exception
    /// does not fire for it.
    ///
    /// <para><b>Trap:</b> reserving lanes for non-trunk tips (first every tip in the 200-commit scan,
    /// then tips in the first 16 rows) held a column empty from row 0 down to each tip, so any
    /// lineage opening above them — a merged pull request's own commit — was pushed right of all
    /// of them.</para>
    /// </summary>
    private static void SeedTipPriority(
        GraphLayoutState s,
        IReadOnlyList<RawCommit> commits,
        IReadOnlyDictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>>? decorations,
        Func<GitObjectId, GitObjectId, bool>? isAncestor)
    {
        if (decorations is null || decorations.Count == 0 || commits.Count < 2)
            return;

        var limit = Math.Min(commits.Count, TipPriorityScanHorizon);

        var hasChild = new HashSet<GitObjectId>();
        for (var i = 0; i < limit; i++)
            foreach (var p in commits[i].ParentIds)
                hasChild.Add(p);

        var tips = new List<RawCommit>();
        for (var i = 0; i < limit; i++)
        {
            var c = commits[i];
            if (hasChild.Contains(c.Id))
                continue; // a ref pointing mid-history — it gets its lane by reservation, not here
            if (decorations.TryGetValue(c.Id, out var d)
                && d.Any(x => x.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch))
            {
                tips.Add(c);
            }
        }
        if (tips.Count < 2)
            return;

        // newest tip first, by committer date; the SHA breaks an exact timestamp tie so
        // the order is stable per run.
        static int ByNewest(RawCommit a, RawCommit b)
        {
            var t = b.Metadata.CommitWhen.CompareTo(a.Metadata.CommitWhen);
            return t != 0 ? t : a.Id.CompareTo(b.Id);
        }
        tips.Sort(ByNewest);

        // checked-out-branch exception. Locate the checked-out local branch's tip anywhere
        // in the scan window — it need NOT be a leaf: the whole point is that newer work has been
        // built on it. A detached HEAD carries no such badge, so nothing fires.
        RawCommit? head = null;
        for (var i = 0; i < limit; i++)
            if (decorations.TryGetValue(commits[i].Id, out var hd)
                && hd.Any(x => x.IsHead && x.Type == DecorationType.LocalBranch))
            {
                head = commits[i];
                break;
            }

        if (head is { } h && !tips[0].Id.Equals(h.Id))
        {
            // Every real caller passes isAncestor: null and gets the
            // exact in-window check below for free (no repo access). The parameter stays
            // only so tests can inject a stand-in oracle against a synthetic DAG.
            Dictionary<GitObjectId, int>? rowById = null;
            if (isAncestor is null)
            {
                rowById = new Dictionary<GitObjectId, int>(limit);
                for (var i = 0; i < limit; i++)
                    rowById[commits[i].Id] = i;
            }
            bool Ancestor(GitObjectId a, GitObjectId b) =>
                isAncestor is not null ? isAncestor(a, b) : IsAncestorInWindow(commits, rowById!, a, b);

            // The tips strictly newer than HEAD are the front run of the sorted list.
            var newerCount = 0;
            while (newerCount < tips.Count && ByNewest(tips[newerCount], h) < 0)
                newerCount++;

            // Newest-first, the first newer tip HEAD is an ancestor of.
            var promote = -1;
            for (var i = 0; i < newerCount; i++)
                if (!tips[i].Id.Equals(h.Id) && Ancestor(h.Id, tips[i].Id))
                {
                    promote = i;
                    break;
                }

            if (promote > 0)
            {
                // That tip takes the trunk; the tips ahead of it shift one rank right.
                var t = tips[promote];
                tips.RemoveAt(promote);
                tips.Insert(0, t);
            }
            else if (promote < 0)
            {
                // HEAD is an ancestor of no newer tip → HEAD itself takes the trunk. Only a leaf
                // HEAD reaches here (a non-leaf HEAD always has a newer descendant tip it is an
                // ancestor of), so it is already one of the candidates — just move it to front.
                var hi = tips.FindIndex(x => x.Id.Equals(h.Id));
                if (hi > 0)
                {
                    var t = tips[hi];
                    tips.RemoveAt(hi);
                    tips.Insert(0, t);
                }
            }
            // promote == 0 → HEAD is an ancestor of the current newest tip: order is unchanged.
        }

        // Reserve lane 0 for the trunk tip (rank 0). Compacted lane 0 is mapped so the trunk
        // renders in the leftmost column even when the walk emits a different tip on row 0.
        if (s.MaxLanes - 1 > 0)
        {
            s.TrunkTip = tips[0].Id;
            s.CompactionMap[0] = 0;
            s.NextCompactedLane = Math.Max(s.NextCompactedLane, 1);
            s.LaneCeiling = Math.Max(s.LaneCeiling, 1);
        }
    }

    /// <summary>
    /// Exact in-window ancestry check — <c>isAncestor(a, b)</c> is true when commit <paramref
    /// name="h"/> is an ancestor of (or equal to) <paramref name="t"/> — using ONLY the already
    /// scanned topo-ordered prefix (<paramref name="commits"/>[0, <see cref="TipPriorityScanHorizon"/>)),
    /// with no repo access.
    /// <para>Exact because every commit on a path from <paramref name="t"/> to <paramref name="h"/>
    /// is itself a descendant of <paramref name="h"/> and so precedes it in the topological
    /// (children-before-parents) walk order — the whole path lies inside <c>[row(t), row(h)]</c>.
    /// Mark <paramref name="t"/>, then walk rows <c>row(t)..row(h)</c> forward, marking the
    /// parents of every already-marked row; <paramref name="h"/> is an ancestor of <paramref
    /// name="t"/> iff it ends up marked.</para>
    /// <para>Returns false — a safe negative, never a crash — when either commit falls outside the
    /// scanned window, or when <c>row(t) &gt; row(h)</c> (impossible for a true ancestor: a parent
    /// always sits at a later row than its child in this sort). Caveat inherited from the analysis:
    /// a path-filtered / single-branch graph can omit commits, so here a false negative is
    /// possible — the only effect is which branch holds the trunk lane.</para>
    /// </summary>
    private static bool IsAncestorInWindow(
        IReadOnlyList<RawCommit> commits, IReadOnlyDictionary<GitObjectId, int> rowById, GitObjectId h, GitObjectId t)
    {
        if (h.Equals(t))
            return true;
        if (!rowById.TryGetValue(t, out var rowT) || !rowById.TryGetValue(h, out var rowH) || rowT > rowH)
            return false;

        var marked = new HashSet<GitObjectId> { t };
        for (var i = rowT; i <= rowH; i++)
        {
            if (!marked.Contains(commits[i].Id))
                continue;
            foreach (var p in commits[i].ParentIds)
                marked.Add(p);
        }
        return marked.Contains(h);
    }

    private static int AllocLane(GraphLayoutState s)
    {
        if (s.FreeLanes.Count > 0)
        {
            var min = s.FreeLanes.Min;
            s.FreeLanes.Remove(min);
            return min;
        }

        if (s.LaneCeiling < s.MaxLanes - 1)
            return s.LaneCeiling++;

        // with MaxLanes == int.MaxValue this branch is unreachable — kept only as an
        // overflow-safe guard. It does not describe a rendered lane; nothing collapses here.
        return s.MaxLanes - 1;
    }

    private static void ReleaseLane(GraphLayoutState s, int rawLane)
    {
        if (rawLane >= s.MaxLanes - 1)
            return; // never free the overflow lane

        // Do not free a lane still expected by a pending parent reservation.
        if (s.ReservedLaneCount.GetValueOrDefault(rawLane) > 0)
            return;

        if (rawLane < s.LaneCeiling)
            s.FreeLanes.Add(rawLane);
    }

    private static int LastBusyRow(GraphLayoutState s, int rawLane) =>
        rawLane < s.LaneLastBusyRow.Length ? s.LaneLastBusyRow[rawLane] : -1;

    private static void MarkBusy(GraphLayoutState s, int rawLane, int row)
    {
        if (rawLane >= s.LaneLastBusyRow.Length)
        {
            var old = s.LaneLastBusyRow.Length;
            var size = Math.Max(rawLane + 1, Math.Max(16, old * 2));
            Array.Resize(ref s.LaneLastBusyRow, size);
            Array.Fill(s.LaneLastBusyRow, -1, old, size - old);
        }
        s.LaneLastBusyRow[rawLane] = row;
    }

    /// <summary>
    /// The free lane, already on screen, that nothing occupied after <paramref name="fromRow"/>,
    /// closest to <paramref name="toLane"/> so the merge line's horizontal legs stay short
    /// (the lower lane wins a tie). Only lanes in <see cref="GraphLayoutState.FreeLanes"/> are
    /// considered and none is taken from it, so later lane allocation, the lane count and lane
    /// colours are unchanged when one qualifies. Returns (-1, -1) when none does.
    /// </summary>
    private static (int RawLane, int Lane) FindClearLane(GraphLayoutState s, int fromRow, int fromLane, int toLane)
    {
        var bestRaw = -1;
        var best = -1;
        var bestDistance = int.MaxValue;
        foreach (var raw in s.FreeLanes)
        {
            if (LastBusyRow(s, raw) >= fromRow || !s.CompactionMap.TryGetValue(raw, out var lane))
                continue;
            if (lane == fromLane || lane == toLane)
                continue;
            var distance = Math.Abs(lane - toLane);
            if (distance < bestDistance || (distance == bestDistance && lane < best))
            {
                bestRaw = raw;
                best = lane;
                bestDistance = distance;
            }
        }
        return (bestRaw, best);
    }

    private static int Compact(GraphLayoutState s, int rawLane)
    {
        if (s.CompactionMap.TryGetValue(rawLane, out var c))
            return c;
        c = s.NextCompactedLane++;
        s.CompactionMap[rawLane] = c;
        return c;
    }

    private static void AddReservation(GraphLayoutState s, GitObjectId parent, Res res)
    {
        if (!s.Reservations.TryGetValue(parent, out var list))
            s.Reservations[parent] = list = [];
        list.Add(res);
        if (res.RawLane >= 0)
            s.ReservedLaneCount[res.RawLane] = s.ReservedLaneCount.GetValueOrDefault(res.RawLane) + 1;
    }

    private static void RemoveReservationsFor(GraphLayoutState s, GitObjectId id, List<Res> removed)
    {
        foreach (var res in removed)
            if (res.RawLane >= 0)
            {
                var n = s.ReservedLaneCount.GetValueOrDefault(res.RawLane) - 1;
                if (n > 0)
                    s.ReservedLaneCount[res.RawLane] = n;
                else
                    s.ReservedLaneCount.Remove(res.RawLane);
            }
        s.Reservations.Remove(id);
    }

    // ── Color assignment ──

    private static int ResolveColor(GraphLayoutState s, int compactedLane,
        IReadOnlyList<GraphDecorationInput>? deco, GitObjectId birthCommit)
    {
        if (s.LaneColor.TryGetValue(compactedLane, out var existing))
            return existing;

        // 1. Seed from a STABLE per-lane key.
        // The key is the lane's
        // branch name when it has one — the lexicographically smallest, so several refs on the
        // birth commit cannot flip the choice between runs — otherwise the SHA of the commit
        // that first opened the lane. It never depends on the compacted-lane ordinal or on how
        // far the walk has progressed, so an incremental Extend and a full recompute seed every
        // lane identically and a repo reopens in the same shades.
        var branch = deco?
            .Where(x => x.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch && x.Label.Length > 0)
            .Select(x => x.Label)
            .OrderBy(l => l, StringComparer.Ordinal)
            .FirstOrDefault();
        var laneKey = branch ?? birthCommit.ToString();
        s.LaneKey[compactedLane] = laneKey;

        var seed = (int)(StableHash.Fnv1a(laneKey) % LanePalette.Size);

        // 2. Deterministic ±4 de-collision: step +1 (mod palette) past the colours of nearby
        // lanes already coloured. Lane birth order is a pure function of the (stable) walk, and
        // a lane born later that lands on the same colour steps when it is itself coloured — so
        // the outcome does not depend on which side of a pair is processed first.
        // with up to hundreds of lanes and 9 palette colours a ±2 window would let
        // identical colours sit only 3 lanes apart everywhere. ±4 is the widest window that
        // still guarantees a free colour exists — its 8 neighbours can hold at most 8 of the 9.
        var taken = new HashSet<int>();
        for (var l = compactedLane - 4; l <= compactedLane + 4; l++)
            if (l != compactedLane && s.LaneColor.TryGetValue(l, out var nearColor))
                taken.Add(nearColor);

        if (taken.Contains(seed))
        {
            for (var step = 1; step <= LanePalette.Size; step++)
            {
                var candidate = (seed + step) % LanePalette.Size;
                if (!taken.Contains(candidate))
                {
                    seed = candidate;
                    break;
                }
            }
        }

        s.LaneColor[compactedLane] = seed;
        return seed;
    }
}
