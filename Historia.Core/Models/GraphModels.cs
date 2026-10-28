namespace Historia.Models;

/// <summary>
/// Immutable per-commit metadata, projected from the git CLI log stream.
/// One instance per commit; stored once in <see cref="GraphLayout.Commits"/>
/// and referenced everywhere else by row index.
/// </summary>
public sealed record CommitMetadata
{
    public required GitObjectId Id { get; init; }
    public required string MessageShort { get; init; }
    public required string AuthorName { get; init; }
    public required string AuthorEmail { get; init; }
    public required DateTimeOffset AuthorWhen { get; init; }
    public required DateTimeOffset CommitWhen { get; init; }
    public required int ParentCount { get; init; }
}

public enum LaneSegmentType : byte
{
    Full = 0,
    TopToNode = 1,
    NodeToBottom = 2,
}

public readonly record struct RowLaneSegment
{
    public required int Lane { get; init; }
    public required int ColorIndex { get; init; }
    public required LaneSegmentType Type { get; init; }
}

/// <summary>Which way an elbow bends through its row.</summary>
public enum ElbowKind : byte
{
    /// <summary>Enters vertically from the row's top edge in <see cref="RowElbow.FromLane"/>,
    /// turns at node height, leaves horizontally toward <see cref="RowElbow.ToLane"/>. The bend
    /// a branch-out makes at the commit it rejoins.</summary>
    EnterTop = 0,

    /// <summary>Rises vertically in <see cref="RowElbow.ToLane"/> from the row's bottom edge,
    /// turns at node height, and runs horizontally into the merge commit's node in
    /// <see cref="RowElbow.FromLane"/>. The bend a merge edge makes on the merge commit's
    /// row.</summary>
    LeaveBottom = 1,
}

public readonly record struct RowElbow
{
    public required int FromLane { get; init; }
    public required int ToLane { get; init; }
    public required int ColorIndex { get; init; }
    public required ElbowKind Kind { get; init; }
}

/// <summary>
/// The hot layout record: one per visual row. Holds layout position and row-local
/// visual slice indices into flat <see cref="GraphLayout.Segments"/> and <see cref="GraphLayout.Elbows"/>
/// for instant, zero-allocation rendering with zero GC tracing overhead.
/// </summary>
public readonly record struct GraphRow
{
    public required int RowIndex { get; init; }
    public required int LaneIndex { get; init; }
    public required int ColorIndex { get; init; }
    public required int ParentCount { get; init; }
    public required RowFlags Flags { get; init; }
}

[Flags]
public enum RowFlags
{
    None = 0,
    IsHead = 1,
    IsMerge = 2,
    IsRoot = 4,
    HasDecorations = 8,
}

/// <summary>
/// Visual edge connecting two rows. Rows, not commit ids: the renderer works purely in
/// row/lane space and never resolves an <see cref="GitObjectId"/> during a frame.
/// </summary>
public readonly record struct GraphEdge
{
    public required int FromRow { get; init; }
    public required int FromLane { get; init; }
    public required int ToRow { get; init; }
    public required int ToLane { get; init; }
    public required EdgeType Type { get; init; }

    /// <summary>Colour of the segment/elbow drawn at <see cref="ToRow"/> — the terminal end of
    /// the edge (where it arrives at its parent's node). For a <see cref="EdgeType.MergeIn"/>
    /// edge this is the merged-in branch's own colour, which can differ from
    /// <see cref="LeadColorIndex"/>.</summary>
    public required int ColorIndex { get; init; }

    /// <summary>Colour of the lead-in portion drawn from <see cref="FromRow"/> down to (but not
    /// including) <see cref="ToRow"/> — i.e. the <c>NodeToBottom</c> and <c>Full</c> segments.
    /// For a first-parent edge (<see cref="EdgeType.Straight"/>/<see cref="EdgeType.BranchOut"/>)
    /// this always equals <see cref="ColorIndex"/>. For <see cref="EdgeType.MergeIn"/> it is the
    /// colour already occupying the merge commit's OWN lane, not the merged-in branch's colour —
    /// that lane keeps carrying its own line for this same row span, so the merge stub must match
    /// it or the two would visibly clash as two differently-coloured lines sharing one lane.</summary>
    public required int LeadColorIndex { get; init; }

    private readonly int _viaLaneBiased;

    /// <summary>For a <see cref="EdgeType.MergeIn"/> edge whose parent's lane holds other commits
    /// between the merge row and the parent, the empty lane the edge runs down instead: it leaves
    /// the merge node into this lane and turns into the parent's node at <see cref="ToRow"/>.
    /// -1 (the default) when the edge runs down <see cref="ToLane"/> itself.</summary>
    public int ViaLane
    {
        get => _viaLaneBiased - 1;
        init => _viaLaneBiased = value + 1;
    }
}

public enum EdgeType
{
    /// <summary>Parent continues in the same lane — rendered as a straight line.</summary>
    Straight,

    /// <summary>A second-or-later parent joining from another lane into a merge commit.</summary>
    MergeIn,

    /// <summary>A commit's lane diverging to a parent already placed in a different lane.</summary>
    BranchOut,

    /// <summary>
    /// The parent lies outside the currently laid-out row window. Rendered as a short stub
    /// fading at the window boundary — never as a dangling line to nowhere.
    /// </summary>
    OffWindow,
}

/// <summary>Decoration label attached to a commit row. Named to avoid a clash with the
/// <see cref="CommitDecoration"/> used by the plain history list.</summary>
public readonly record struct GraphDecoration
{
    public required int RowIndex { get; init; }
    public required DecorationType Type { get; init; }
    public required string Label { get; init; }
    public required int ColorIndex { get; init; }

    /// <summary>the badge has a local branch of this name.</summary>
    public bool IsLocal { get; init; }

    /// <summary>HEAD points at this badge's branch. Per-badge, unlike <see cref="RowFlags.IsHead"/>.</summary>
    public bool IsHead { get; init; }

    /// <summary>the remotes carrying this branch, or null when the badge is local-only.</summary>
    public string[]? RemotesOrNull { get; init; }

    /// <summary>Never null. A struct's default instance bypasses initializers, so a nullable
    /// backing field with a non-null accessor is the only shape correct for both paths (Trap).</summary>
    public ReadOnlySpan<string> Remotes => RemotesOrNull ?? [];

    /// <summary>the other refs sitting on this same commit that folded into this badge —
    /// the further branches (default-first, then alphabetical) followed by any tags. Null unless
    /// this badge stands in for a group; shown in the interactive hover list. The <c>+N</c>
    /// counter on the badge is <c>1 + SiblingsOrNull.Length</c>.</summary>
    public GraphDecorationInput[]? SiblingsOrNull { get; init; }

    /// <summary>Never null (default-struct safe, like <see cref="Remotes"/>).</summary>
    public ReadOnlySpan<GraphDecorationInput> Siblings => SiblingsOrNull ?? [];
}

/// <summary>Input decoration before a row index is known. Also used for one entry of a folded
/// badge's <see cref="GraphDecoration.SiblingsOrNull"/> list.</summary>
public readonly record struct GraphDecorationInput(
    DecorationType Type,
    string Label,
    bool IsLocal = false,
    string[]? Remotes = null,
    bool IsHead = false,
    GraphDecorationInput[]? Siblings = null);

/// <summary>One commit fed to the layout engine, in topological (children-before-parents) order.</summary>
public readonly record struct RawCommit(GitObjectId Id, IReadOnlyList<GitObjectId> ParentIds, CommitMetadata Metadata);

/// <summary>
/// Row-indexed access to the per-commit metadata for a laid-out window. The default
/// implementation (<see cref="ArrayCommitStore"/>) is a plain in-memory array; when the
/// "Read commit details from disk" setting is on, <c>DiskCommitStore</c> serves the same
/// shape by paging a random-access cache file, so a huge history keeps only the fixed-size
/// layout arrays resident. Consumers only ever index a bounded visible-row window plus
/// one-off interaction lookups, so a paged read is transparent to them.
/// <para>
/// The store is <b>lease-counted</b> (<see cref="AddRef"/> / <see cref="IDisposable.Dispose"/>).
/// A disk-backed store holds an open file handle; <c>GraphLayoutService</c> keeps one lease for
/// its walk cache, and every consumer that parks a <see cref="GraphLayout.Commits"/> reference it
/// will read from later (the graph control's current layout) must take its own. The handle
/// closes only when the last lease is released — without this, a RefsChanged re-walk that drops
/// the service's cache could close the handle while the control was still rendering from it.
/// A consumer re-taking a lease it had dropped (a control leaving and re-entering the visual
/// tree) must treat a <c>null</c> from <see cref="AddRef"/> as "this layout is dead, recompute".
/// </para>
/// </summary>
public interface ICommitStore : IReadOnlyList<CommitMetadata>, IDisposable
{
    /// <summary>Hint that rows <paramref name="firstRow"/>..<paramref name="lastRow"/>
    /// (inclusive, clamped by the implementation) are about to be read — lets a disk-backed
    /// store fault the block in with one read. A no-op for the in-memory store.</summary>
    void Prefetch(int firstRow, int lastRow);

    /// <summary>find: ascending row indices whose message or author contains
    /// <paramref name="term"/> (case-insensitive), or whose SHA starts with it. Cancelling
    /// <paramref name="ct"/> stops the scan early by throwing <see cref="OperationCanceledException"/>;
    /// a partial result is never returned.</summary>
    IReadOnlyList<int> Search(string term, CancellationToken ct = default);

    /// <summary>Take an extra lease on the store's backing resources. Every successful call must
    /// be balanced by exactly one <see cref="IDisposable.Dispose"/>; the handle a disk-backed
    /// store holds closes only when the final lease is released. Returns <c>this</c> for call
    /// chaining, or <c>null</c> when the store's handle has already been released (a concurrent
    /// final <see cref="IDisposable.Dispose"/> won the race) — the caller must then discard the
    /// layout that pointed at it and recompute rather than render stubs. The in-memory store
    /// never returns null.</summary>
    ICommitStore? AddRef();
}

/// <summary>The match rule the in-memory stores share for <see cref="ICommitStore.Search"/>.</summary>
internal static class CommitSearch
{
    /// <summary>Rows scanned between cancellation checks; a power of two.</summary>
    internal const int CancelCheckInterval = 4_096;

    /// <summary>Ascending indices of the rows of <paramref name="rows"/> whose message or author
    /// contains the trimmed <paramref name="term"/> (case-insensitive) or whose SHA starts with it;
    /// none for an empty term. The token is checked every <see cref="CancelCheckInterval"/> rows
    /// and cancelling it throws <see cref="OperationCanceledException"/>.</summary>
    internal static List<int> Matching(IReadOnlyList<CommitMetadata> rows, string term, CancellationToken ct = default)
    {
        var hits = new List<int>();
        var t = term.Trim();
        if (t.Length == 0)
            return hits;
        for (var i = 0; i < rows.Count; i++)
        {
            if ((i & (CancelCheckInterval - 1)) == 0)
                ct.ThrowIfCancellationRequested();
            var m = rows[i];
            if (m.MessageShort.Contains(t, StringComparison.OrdinalIgnoreCase)
                || m.AuthorName.Contains(t, StringComparison.OrdinalIgnoreCase)
                || m.Id.ToString().StartsWith(t, StringComparison.OrdinalIgnoreCase))
                hits.Add(i);
        }
        return hits;
    }
}

/// <summary>In-memory <see cref="ICommitStore"/> — the array the layout engine builds.</summary>
public sealed class ArrayCommitStore(CommitMetadata[] items) : ICommitStore
{
    public static ArrayCommitStore Empty { get; } = new([]);

    public CommitMetadata this[int index] => items[index];
    public int Count => items.Length;
    public void Prefetch(int firstRow, int lastRow) { }

    // In-memory: no handle to lease, nothing to release — a lease can never fail.
    public ICommitStore? AddRef() => this;
    public void Dispose() { }

    public IReadOnlyList<int> Search(string term, CancellationToken ct = default) => CommitSearch.Matching(this, term, ct);

    public IEnumerator<CommitMetadata> GetEnumerator() => ((IEnumerable<CommitMetadata>)items).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => items.GetEnumerator();
}

public enum GraphRefScope { AllRefs, CurrentBranch, Selected }

public enum InvalidationReason { CommitsAppended, RefsChanged, HistoryRewritten, FetchCompleted }

/// <summary>Which refs the graph should include, and how deep.</summary>
public sealed record GraphQuery
{
    public GraphRefScope RefScope { get; init; } = GraphRefScope.AllRefs;
    public IReadOnlyList<string>? SelectedRefs { get; init; }
    public bool IncludeRemoteBranches { get; init; } = true;
    public string? PathFilter { get; init; }
    /// <summary>lanes are uncapped — every lineage gets its own track. A lane cap would
    /// collapse every lane past it onto a single dashed overflow lane, which on a busy history
    /// obscures genuine topology. Far lanes are reached by the in-graph pan or
    /// by stretching the Graph column, not by a cap.</summary>
    public int MaxLanes { get; init; } = int.MaxValue;
}
