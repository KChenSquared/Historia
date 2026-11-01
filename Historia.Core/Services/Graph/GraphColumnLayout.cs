
namespace Historia.Services.Graph;

/// <summary>One resolved column: its kind and its position in the scrolling band.</summary>
public readonly record struct ResolvedColumn(GraphColumnKind Kind, double Left, double Width);

/// <summary>
/// Pure column-layout resolution for the graph panel. No UI-framework types — geometry in,
/// geometry out — so it is unit-testable like <c>DagLayoutEngine</c>. Computed once per arrange
/// pass into a caller-owned scratch array.
/// </summary>
public static class GraphColumnLayout
{
    /// <summary>auto lane-sizing (and Graph's share of the co-grow) stops here. Deliberately a
    /// tighter cap than the Message ceiling, so this is not == CoGrowCeiling.</summary>
    public const double GraphAutoMax = 250;

    /// <summary>Message co-grows with Graph to this ceiling, then Message alone absorbs
    /// the remainder. Graph's own share of the co-grow is capped at <see cref="GraphAutoMax"/>.
    /// Only consulted on the legacy equal-rate path (when the sequence opts out).</summary>
    public const double CoGrowCeiling = 320;

    /// <summary>when neither Graph nor Message is user-sized, growing width fills
    /// Message to here first, then Graph to <see cref="GraphCoGrowTarget"/>, then Message again.
    /// Shrinking mirrors it: Graph → 220, then Message → 250, then Graph → 150, then Message → 150.</summary>
    public const double MessageFirstStop = 250;

    /// <summary>Graph's co-grow target and its first shrink stop. On a busy repo the
    /// lane-count auto-size may hold Graph above this (up to <see cref="GraphAutoMax"/>) — that
    /// excess is the first thing the mirrored shrink gives back.</summary>
    public const double GraphCoGrowTarget = 220;

    /// <summary>Shrink priority (step 4): the first column to lose width is last here.</summary>
    private static readonly GraphColumnKind[] ShrinkOrder =
    [
        GraphColumnKind.Message, GraphColumnKind.Graph, GraphColumnKind.Date,
        GraphColumnKind.Author, GraphColumnKind.Sha, GraphColumnKind.Refs,
    ];

    public sealed record Result(IReadOnlyList<ResolvedColumn> Columns, double BandWidth);

    /// <param name="columns">Ordered column list; the order is the column order.</param>
    /// <param name="viewportWidth">Width available to the band.</param>
    /// <param name="laneWidth">Current lane width (scales with Ctrl+Scroll zoom).</param>
    /// <param name="totalLaneCount">Lanes the current layout uses.</param>
    /// <param name="graphWidthPinned">True once the user has dragged the Graph divider.</param>
    /// <param name="refsBaseWidth">Measured Refs base width; the static floor is fine
    /// before a control exists.</param>
    /// <param name="manuallySized">Column kinds the user has resized this session — excluded
    /// from the shrink pass.</param>
    public static Result Resolve(
        IReadOnlyList<GraphColumn> columns,
        double viewportWidth,
        double laneWidth,
        int totalLaneCount,
        bool graphWidthPinned,
        double refsBaseWidth = GraphColumnDefaults.RefsStaticFloor,
        IReadOnlySet<GraphColumnKind>? manuallySized = null)
    {
        manuallySized ??= System.Collections.Immutable.ImmutableHashSet<GraphColumnKind>.Empty;

        var visible = columns.Where(c => c.Visible).ToList();
        if (visible.Count == 0)
            visible = GraphColumnDefaults.All.ToList();

        var refsFloor = Math.Max(GraphColumnDefaults.RefsStaticFloor, refsBaseWidth);

        var graphIdx = visible.FindIndex(c => c.Kind == GraphColumnKind.Graph);
        var msgIdx = visible.FindIndex(c => c.Kind == GraphColumnKind.Message && c.IsFlex);
        if (msgIdx < 0)
            msgIdx = visible.FindIndex(c => c.IsFlex);
        if (msgIdx < 0)
            msgIdx = visible.Count - 1;

        // the Message→Graph→Message staged sequence only runs when the flex column is
        // Message and NEITHER Graph nor Message carries a user-set width (not pinned, not
        // manually sized). Computed before the width pass because it caps Graph's base.
        var newSequenceApplies =
            graphIdx >= 0
            && msgIdx >= 0
            && graphIdx != msgIdx
            && visible[msgIdx].Kind == GraphColumnKind.Message
            && !graphWidthPinned
            && !manuallySized.Contains(GraphColumnKind.Message)
            && !manuallySized.Contains(GraphColumnKind.Graph);

        // ── 1. Base widths ──
        var widths = new double[visible.Count];
        for (var i = 0; i < visible.Count; i++)
        {
            var col = visible[i];
            double w;
            if (col.Kind == GraphColumnKind.Refs)
            {
                w = Math.Max(col.Width, refsFloor);
            }
            else if (col.Kind == GraphColumnKind.Graph)
            {
                var graphFloor = GraphColumnDefaults.MinWidthFor(GraphColumnKind.Graph);
                if (newSequenceApplies)
                {
                    // the staged sequence owns Graph's width — it grows from its floor
                    // to exactly GraphCoGrowTarget (220) and no further; the lane-count
                    // auto-size does not apply.
                    w = graphFloor;
                }
                else if (graphWidthPinned)
                {
                    // a pinned Graph width is never wider than the lanes actually in use,
                    // so a width pinned while a busy repo was open does not show as empty space
                    // after a refs change shrinks the layout. Before the first layout lands
                    // (totalLaneCount == 0) leave the pinned width alone so it isn't crushed to
                    // the floor while loading.
                    var naturalMax = Math.Max(graphFloor, (totalLaneCount + 1) * laneWidth);
                    w = totalLaneCount > 0
                        ? Math.Clamp(col.Width, graphFloor, naturalMax)
                        : Math.Max(col.Width, graphFloor);
                }
                else
                {
                    w = Math.Clamp((totalLaneCount + 1) * laneWidth, graphFloor, GraphAutoMax);
                }
            }
            else if (i == msgIdx)
            {
                // The flex column's stored width only counts once the user has dragged it;
                // otherwise it grows from its minimum so Graph can co-grow with it.
                w = manuallySized.Contains(col.Kind)
                    ? Math.Max(col.Width, GraphColumnDefaults.MinWidthFor(col.Kind))
                    : GraphColumnDefaults.MinWidthFor(col.Kind);
            }
            else
            {
                w = Math.Max(col.Width, GraphColumnDefaults.MinWidthFor(col.Kind));
            }
            widths[i] = w;
        }

        // ── 2. Slack ──
        var slack = viewportWidth - widths.Sum();

        if (slack > 0 && newSequenceApplies)
        {
            // ── 3. Grow: Message → 250, then Graph → 220, then Message absorbs the
            //        rest. (Graph keeps any lane-count auto-size above 220 — stage B is then a
            //        no-op and that excess is the first thing shrink gives back.) ──
            var a = Math.Min(slack, Math.Max(0, MessageFirstStop - widths[msgIdx]));
            widths[msgIdx] += a;
            slack -= a;

            var b = Math.Min(slack, Math.Max(0, GraphCoGrowTarget - widths[graphIdx]));
            widths[graphIdx] += b;
            slack -= b;

            widths[msgIdx] += slack;
        }
        else if (slack > 0)
        {
            // ── 3. Grow (legacy): Graph and Message at an equal rate until Graph reaches
            //        GraphAutoMax, then Message alone absorbs the remainder. Once the
            //        Graph divider is pinned it is excluded from the co-grow, so a manual shrink
            //        to MinWidthFor(Graph) actually sticks. ──
            if (graphIdx >= 0 && graphIdx != msgIdx && !graphWidthPinned)
            {
                var needG = Math.Max(0, GraphAutoMax - widths[graphIdx]);
                var needM = Math.Max(0, CoGrowCeiling - widths[msgIdx]);
                var eq = Math.Min(slack / 2, Math.Min(needG, needM));
                widths[graphIdx] += eq;
                widths[msgIdx] += eq;
                slack -= 2 * eq;
            }
            widths[msgIdx] += slack;
        }
        else if (slack < 0)
        {
            // ── 4. Shrink: take the deficit from non-manually-sized columns, in priority order,
            //        each down to its minimum. Under the sequence, Graph and Message are already
            //        at their 150 floor (step 1 gave Graph its floor, not the auto-size), so the
            //        whole deficit runs the priority pass — Message and Graph first, then
            //        Date/Author/Sha. Any residue becomes band overflow. ──
            var deficit = -slack;
            ShrinkByPriority(ref deficit, widths, visible, manuallySized, refsFloor);
        }

        // ── 5. Positions ──
        var resolved = new ResolvedColumn[visible.Count];
        double left = 0;
        for (var i = 0; i < visible.Count; i++)
        {
            resolved[i] = new ResolvedColumn(visible[i].Kind, left, widths[i]);
            left += widths[i];
        }

        return new Result(resolved, left);
    }

    /// <summary>step 4: draw <paramref name="deficit"/> off non-manually-sized columns
    /// in <see cref="ShrinkOrder"/>, each down to its minimum. Leftover stays as band overflow.</summary>
    private static void ShrinkByPriority(
        ref double deficit,
        double[] widths,
        List<GraphColumn> visible,
        IReadOnlySet<GraphColumnKind> manuallySized,
        double refsFloor)
    {
        foreach (var kind in ShrinkOrder)
        {
            if (deficit <= 0)
                break;
            if (manuallySized.Contains(kind))
                continue;
            var idx = visible.FindIndex(c => c.Kind == kind);
            if (idx < 0)
                continue;
            var min = kind == GraphColumnKind.Refs ? refsFloor : GraphColumnDefaults.MinWidthFor(kind);
            var give = Math.Min(deficit, widths[idx] - min);
            if (give <= 0)
                continue;
            widths[idx] -= give;
            deficit -= give;
        }
    }
}
