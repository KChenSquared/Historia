namespace Historia.Controls;

public sealed partial class CommitGraphControl
{
    // ── Render ──

    private readonly Dictionary<int, List<GraphDecoration>> _decoIndex = [];
    private object? _decoIndexKey;
    private readonly List<(Rect Rect, int Row, List<GraphDecoration> Hidden)> _overflowChips = [];

    /// <summary>
    /// Row → decorations, built once per layout rather than re-scanned every frame. Keyed on
    /// the <see cref="GraphLayout.Decorations"/> array identity: Compute and Recompute build a
    /// new array, and an Extend builds one whenever it added decorations (an Extend that added
    /// none reuses the array, whose contents are unchanged). Frequent repaints (hover,
    /// selection, scroll) reuse the index untouched.
    /// </summary>
    private Dictionary<int, List<GraphDecoration>> DecorationsByRow(GraphLayout layout)
    {
        if (ReferenceEquals(_decoIndexKey, layout.Decorations))
            return _decoIndex;

        _decoIndex.Clear();
        foreach (var d in layout.Decorations)
        {
            if (!_decoIndex.TryGetValue(d.RowIndex, out var list))
                _decoIndex[d.RowIndex] = list = [];
            list.Add(d);
        }
        _decoIndexKey = layout.Decorations;
        return _decoIndex;
    }

    private double RowY(int row) => RowsTop + row * _rowHeight - _scrollOffset;

    public override void Render(DrawingContext dc)
    {
        if (FrameBench.Active)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                RenderGraph(dc);
            }
            finally
            {
                FrameBench.RecordGraphRender(start);
            }
            return;
        }
        RenderGraph(dc);
    }

    private void RenderGraph(DrawingContext dc)
    {
        var size = Bounds.Size;
        dc.DrawRectangle(_bg, null, new Rect(size));

        // Resolved in ArrangeOverride each frame; re-resolve here defensively (idempotent) so a
        // Render that somehow precedes an arrange still has a band.
        ResolveColumnBand(size.Width);

        DrawHeaderBand(dc, size.Width);

        if (RevealBlanking)
        {
            // Graph body is blanked while walking toward an unrendered commit — show only the
            // target, centred. The pump InvalidateVisual()s each batch to refresh the count.
            DrawRevealPlaceholder(dc, size);
            return;
        }

        if (_layout is not { } result)
        {
            // nothing painted yet — the optimistic snapshot missed and the real walk
            // is still priming (on a large, never-before-opened repo `git log --date-order` can
            // take a couple of seconds to yield its first row; see
            // GraphLayoutService.PrimeCliWalkAsync).
            DrawInitialLoadingPlaceholder(dc, size);
            return;
        }

        try
        {
            RenderRows(dc, size, result);
            _renderFailStreak = 0;

            if (!_firstPaintRenderLogged && _firstPaintStartTimestamp != 0)
            {
                _firstPaintRenderLogged = true;
                var elapsedMs = Stopwatch.GetElapsedTime(_firstPaintStartTimestamp).TotalMilliseconds;
                var kind = _firstPaintKind ?? "layout";
                Historia.Diagnostics.PerfLog.Mark(
                    $"graph first paint rendered: {kind} ({result.Layout.RowCount} rows, {elapsedMs:F1}ms total)");
                _firstPaintStartTimestamp = 0;
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or InvalidDataException
                                   or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // The disk-backed commit store was mid-rebuild (a spill restart rewrites the cache
            // files) while this frame was indexing it. Drop the frame and repaint — the store
            // settles within a batch or two, and the alternative is an exception thrown off the
            // render thread, which Avalonia propagates straight past Dispatcher.UnhandledException
            // and terminates the process. Layer 1 (the store lease) keeps the handle from being
            // closed under us; this is the belt-and-braces for a concurrent rewrite.
            //
            // A transient cause clears within a few frames. Cap the auto-repaints so a
            // *deterministic* fault in here (a stale scroll window, a lane-index bug) surfaces
            // once and leaves a blank frame instead of spinning a core forever on Background.
            if (++_renderFailStreak <= MaxRenderRetries)
            {
                Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
            }
            else if (_renderFailStreak == MaxRenderRetries + 1)
            {
                Historia.Diagnostics.PerfLog.Mark(
                    $"graph Render giving up after {MaxRenderRetries} retries: {ex.GetType().Name}");
                _notifications.Publish(NotificationSeverity.Error,
                    "The commit graph hit a rendering error and stopped repainting. "
                    + "Refresh the repository to retry.",
                    ex.ToString());
            }
        }
    }

    private void RenderRows(DrawingContext dc, Size size, GraphLayoutResult result)
    {
        var layout = result.Layout;
        var rowArea = size.Height - RowsTop;
        var first = Math.Max(0, (int)(_scrollOffset / _rowHeight) - 2);
        var last = Math.Min(layout.RowCount - 1, (int)((_scrollOffset + rowArea) / _rowHeight) + 2);
        if (last < first)
            return;

        // Fault the visible block of a disk-backed store in with one read before the column
        // passes start indexing it row by row. A no-op for the in-memory store.
        layout.Commits.Prefetch(first, last);

        // Derive the lane segments for the whole drawn block up front, with a tight window after
        // a jump such as a scrollbar drag.
        layout.EnsureRows(first, last);

        _pillRects.Clear();
        _iconRects.Clear();
        _overflowChips.Clear();
        _badgeRightEdge.Clear();

        var currentMatchRow = _matches.CurrentRow;
        var matchSet = _matches.RowSet;
        var gx = _graphColLeft - _hScrollOffset;
        var washRight = gx + Math.Max(0, _graphColWidth - TickWidth);
        // Clip the row-background band to the row area: the first drawn row starts two rows
        // above the viewport top, so its fill would otherwise bleed up into the header band.
        using (dc.PushClip(new Rect(0, RowsTop, size.Width, Math.Max(0, rowArea))))
            for (var row = first; row <= last; row++)
            {
                var y = RowY(row);
                if (matchSet is not null && matchSet.Contains(row))
                    dc.DrawRectangle(_matchFill, null, new Rect(0, y, size.Width, _rowHeight));

                var selected = row == _selectedRow || _multiSelect.Contains(row);
                if (selected)
                    dc.DrawRectangle(_selectionFill, null, new Rect(0, y, size.Width, _rowHeight));
                else if (row == _hoverRow)
                    dc.DrawRectangle(_hoverFill, null, new Rect(0, y, size.Width, _rowHeight));

                if (_graphColWidth > 0)
                {
                    var gr = layout.Rows[row];
                    var colorIndex = gr.ColorIndex;
                    // the wash starts at the commit's own node — the graph line it sits on —
                    // and runs right to the column edge, rather than beginning half a lane to its
                    // left and covering the whole lane band.
                    var fillLeft = Math.Max(gx, NodeX(gr.LaneIndex, out _));
                    // Inset half the gap top and bottom so adjacent rows' washes and right-edge ticks
                    // are separated by WashRowGap px; the lane lines stay continuous.
                    if (washRight > fillLeft)
                        dc.DrawRectangle(LaneFill(colorIndex), null,
                            new Rect(fillLeft, y + WashRowGap / 2, washRight - fillLeft, _rowHeight - WashRowGap));
                    // The solid right-edge tick is drawn in DrawGraphColumn, AFTER the edges, so a
                    // lane line routed near the column's right edge can't paint over it.
                }

                if (selected)
                    dc.DrawRectangle(_selectionBar, null, new Rect(0, y, 2, _rowHeight));

                if (row == currentMatchRow)
                    dc.DrawRectangle(_selectionBar, null, new Rect(0, y, 3, _rowHeight));
            }

        var decosByRow = DecorationsByRow(layout);

        // Badge→node connectors: after the lane wash (drawn in the row loop above) but BEFORE
        // the column loop, so the graph's lane lines paint over them and the dots (a later
        // pass) stay on top.
        DrawBadgeConnectors(dc, layout, decosByRow, first, last, rowArea);

        foreach (var col in _resolvedColumns)
        {
            var x = col.Left - _hScrollOffset;
            if (x + col.Width < 0 || x > size.Width)
                continue;

            using (dc.PushClip(new Rect(x, RowsTop, Math.Max(0, col.Width), Math.Max(0, rowArea))))
            {
                switch (col.Kind)
                {
                    case GraphColumnKind.Graph:
                        DrawGraphColumn(dc, layout, first, last, x, size.Width);
                        break;
                    case GraphColumnKind.Refs:
                        DrawRefsColumn(dc, decosByRow, first, last, x, col.Width);
                        break;
                    case GraphColumnKind.Message:
                    case GraphColumnKind.Author:
                    case GraphColumnKind.Date:
                    case GraphColumnKind.Sha:
                        DrawTextColumn(dc, layout, first, last, x, col.Width, col.Kind);
                        break;
                }
            }
        }

        // Commit dots last, clipped to the Graph column, so the badge→node connectors sit
        // behind them.
        foreach (var col in _resolvedColumns)
        {
            if (col.Kind != GraphColumnKind.Graph)
                continue;
            var gcx = col.Left - _hScrollOffset;
            if (gcx + col.Width >= 0 && gcx <= size.Width)
                using (dc.PushClip(new Rect(gcx, RowsTop, Math.Max(0, col.Width), Math.Max(0, rowArea))))
                    DrawGraphNodes(dc, layout, first, last);
            break;
        }
    }

    /// <summary>when the Branch/Tags column sits immediately left of the Graph column,
    /// draw a short horizontal line from each row's primary badge to its commit node. The
    /// checked-out branch's row gets the full lane line (colour + <see cref="LaneStrokeWidth"/>);
    /// every other decorated row gets a faint 1 DIP hairline in the same lane colour. Runs
    /// BEFORE the column loop (after the lane wash) so the graph's lane lines paint over the
    /// connectors and the dots stay on top; its own clip spans only the Refs↔Graph gap. Cheap
    /// enough not to cache; reads <c>_graphColLeft</c> fresh.</summary>
    private void DrawBadgeConnectors(DrawingContext dc, GraphLayout layout,
        Dictionary<int, List<GraphDecoration>> decosByRow, int first, int last, double rowArea)
    {
        if (RevealBlanking || _graphColWidth <= 0 || _resolvedColumns.Length == 0)
            return;

        var refsIdx = Array.FindIndex(_resolvedColumns, static c => c.Kind == GraphColumnKind.Refs);
        var graphIdx = Array.FindIndex(_resolvedColumns, static c => c.Kind == GraphColumnKind.Graph);
        if (refsIdx < 0 || graphIdx != refsIdx + 1)
            return;

        var refsCol = _resolvedColumns[refsIdx];
        var refsLeft = refsCol.Left - _hScrollOffset;
        var refsRightFallback = refsCol.Left + refsCol.Width - 8 - _hScrollOffset;
        var clipRight = _graphColLeft + _graphColWidth - _hScrollOffset;
        if (clipRight <= refsLeft)
            return;

        // The Refs column has not painted yet this frame, so measure where each row's primary
        // badge will end (the connector start) now.
        MeasureBadgeRightEdges(decosByRow, first, last, refsLeft, refsCol.Width);

        using (dc.PushClip(new Rect(refsLeft, RowsTop, clipRight - refsLeft, Math.Max(0, rowArea))))
        {
            for (var row = first; row <= last; row++)
            {
                if (!decosByRow.TryGetValue(row, out var decos) || decos.Count == 0)
                    continue;

                var gr = layout.Rows[row];
                var startX = _badgeRightEdge.GetValueOrDefault(row, refsRightFallback);
                var endX = NodeX(gr.LaneIndex, out _);
                if (endX <= startX + 0.5)
                    continue;

                var y = RowY(row) + _rowHeight / 2;
                var isHead = (gr.Flags & RowFlags.IsHead) != 0 || AnyHead(decos);
                var pen = isHead ? LanePen(gr.ColorIndex) : FaintConnectorPen(gr.ColorIndex);
                dc.DrawLine(pen, new Point(startX, y), new Point(endX, y));
            }
        }
    }

    private void DrawHeaderBand(DrawingContext dc, double width)
    {
        dc.DrawRectangle(_chrome, null, new Rect(0, 0, width, HeaderHeight));
        dc.DrawLine(_borderPen, new Point(0, HeaderHeight - 0.5), new Point(width, HeaderHeight - 0.5));

        foreach (var col in _resolvedColumns)
        {
            var x = col.Left - _hScrollOffset;
            if (x + col.Width < 0 || x > width)
                continue;

            var label = col.Kind switch
            {
                GraphColumnKind.Refs => "BRANCH / TAGS",
                GraphColumnKind.Graph => "GRAPH",
                GraphColumnKind.Message => "MESSAGE",
                GraphColumnKind.Author => "AUTHOR",
                GraphColumnKind.Date => "DATE",
                GraphColumnKind.Sha => "SHA",
                _ => "",
            };
            HeaderIconRect? iconRect = null;
            HeaderIconRect? secondIconRect = null;
            if (col.Kind is GraphColumnKind.Refs or GraphColumnKind.Message)
            {
                iconRect = GraphColumnInteraction.HeaderIconRectFor(_resolvedColumns, col.Kind, _hScrollOffset, HeaderHeight, HeaderIconSize, HeaderIconInset);
            }
            else if (col.Kind == GraphColumnKind.Graph)
            {
                iconRect = GraphColumnInteraction.HeaderIconRectFor(_resolvedColumns, col.Kind, _hScrollOffset, HeaderHeight, HeaderIconSize, HeaderIconInset, slot: 0);
                secondIconRect = GraphColumnInteraction.HeaderIconRectFor(_resolvedColumns, col.Kind, _hScrollOffset, HeaderHeight, HeaderIconSize, HeaderIconInset, slot: 1);
            }

            var leftmostIconX = secondIconRect?.X ?? iconRect?.X;
            var labelMaxWidth = leftmostIconX is { } lx ? Math.Max(10, lx - x - 12) : Math.Max(10, col.Width - 12);
            var ft = Text(label, _headerText, labelMaxWidth, HeaderTextSize);
            ft.Draw(dc, new Point(x + 8, (HeaderHeight - ft.Height) / 2));

            if (col.Left > 0.5)
                dc.DrawLine(_borderPen, new Point(x, 4), new Point(x, HeaderHeight - 4));

            if (col.Kind is GraphColumnKind.Refs or GraphColumnKind.Message)
            {
                if (iconRect is { } ir)
                {
                    if (_hoverHeaderIcon == (col.Kind, 0))
                        dc.DrawRectangle(_hoverFill, null, new Rect(ir.X - 2, ir.Y - 2, ir.Width + 4, ir.Height + 4), 3, 3);
                    DrawBadgeIcon(dc, col.Kind == GraphColumnKind.Refs ? _icoFilter : _icoSearch, ir.X, ir.Y, _headerText);
                }
            }
            else if (col.Kind == GraphColumnKind.Graph)
            {
                if (iconRect is { } cog)
                {
                    if (_hoverHeaderIcon == (GraphColumnKind.Graph, 0))
                        dc.DrawRectangle(_hoverFill, null, new Rect(cog.X - 2, cog.Y - 2, cog.Width + 4, cog.Height + 4), 3, 3);
                    DrawBadgeIcon(dc, _icoSettings, cog.X, cog.Y, _headerText);
                }
                if (secondIconRect is { } reset)
                {
                    if (_hoverHeaderIcon == (GraphColumnKind.Graph, 1))
                        dc.DrawRectangle(_hoverFill, null, new Rect(reset.X - 2, reset.Y - 2, reset.Width + 4, reset.Height + 4), 3, 3);
                    DrawBadgeIcon(dc, _icoReset, reset.X, reset.Y, _headerText);
                }
            }
        }

        if (_reorderKind is { } rk)
        {
            var ghost = Text(rk.ToString().ToUpperInvariant(), _text, size: HeaderTextSize);
            dc.DrawRectangle(_hoverFill, _borderPen, new Rect(_reorderGhostX - 40, 2, 80, HeaderHeight - 4));
            ghost.Draw(dc, new Point(_reorderGhostX - 36, (HeaderHeight - ghost.Height) / 2));

            if (_reorderCaret >= 0)
            {
                double caretX = _reorderCaret < _resolvedColumns.Length
                    ? _resolvedColumns[_reorderCaret].Left - _hScrollOffset
                    : _bandWidth - _hScrollOffset;
                dc.DrawRectangle(_selectionBar, null, new Rect(caretX - 1, 0, 2, HeaderHeight));
            }
        }
    }

    /// <summary>shown instead of a blank graph body while this context has no layout
    /// at all yet — the optimistic snapshot missed and the real walk hasn't produced its first
    /// batch. Distinct from <see cref="DrawRevealPlaceholder"/> (which has a target commit and a
    /// live row count to show); here there is nothing to report yet beyond "still working".</summary>
    private void DrawInitialLoadingPlaceholder(DrawingContext dc, Size size)
    {
        var rowAreaH = Math.Max(0, size.Height - RowsTop);
        var text = Text("Loading history…", _textMuted, size.Width - 80, HeaderTextSize);
        text.Draw(dc, new Point((size.Width - text.Width) / 2, RowsTop + rowAreaH * 0.45));
    }

    /// <summary>The blanked-graph placeholder: a stand-in ref badge centred in the Branch/Tags
    /// column, and a borderless "Loading history… N rows" line just below it. Shown while the
    /// reveal pump walks toward an as-yet-unrendered commit.</summary>
    private void DrawRevealPlaceholder(DrawingContext dc, Size size)
    {
        const double h = 20;
        var rowAreaH = Math.Max(0, size.Height - RowsTop);
        var midY = RowsTop + rowAreaH / 2;
        var top = midY - h / 2;

        // Keep the column boundaries visible so the placeholder badge reads as sitting in the
        // Branch/Tags column.
        foreach (var c in _resolvedColumns)
        {
            var lx = c.Left - _hScrollOffset;
            if (c.Left > 0.5 && lx >= 0 && lx <= size.Width)
                dc.DrawLine(_borderPen, new Point(lx, RowsTop), new Point(lx, size.Height));
        }

        if (_revealDeco is { } deco)
        {
            // Centre it in the Refs column when that column is shown, else on the whole width.
            double refLeft = 0, refWidth = size.Width;
            foreach (var c in _resolvedColumns)
                if (c.Kind == GraphColumnKind.Refs) { refLeft = c.Left - _hScrollOffset; refWidth = c.Width; }

            var (_, natural, min) = BadgeMetrics(deco);
            var w = Math.Clamp(natural, min, Math.Max(min, refWidth - 16));
            var x = refLeft + (refWidth - w) / 2;

            DrawBadge(dc, deco, x, top, w, h, midY, row: -1);
            _pillRects.Clear();   // the placeholder badge is decorative — no hit-testing
            _iconRects.Clear();
        }

        // ~20% below the vertical centre, so it clears the badge even when the Refs column is
        // in the middle of the window.
        var sub = Text($"Loading history…  {(_layout?.Layout.RowCount ?? 0):N0} rows",
            _textMuted, size.Width - 80, HeaderTextSize);
        sub.Draw(dc, new Point((size.Width - sub.Width) / 2, RowsTop + rowAreaH * 0.70));
    }

    /// <summary>The row HEAD sits on, or -1. A lookup that missed (HEAD's commit is not among the
    /// loaded rows) is remembered for that layout snapshot and HEAD's commit, so a frame does not
    /// take the layout's row-map lock again for an answer that cannot have changed.</summary>
    private int ResolveHeadRow(GraphLayout layout)
    {
        if (layout.HeadRow >= 0 && layout.HeadRow < layout.RowCount)
            return layout.HeadRow;

        if (_state?.CurrentBranch?.TipCommitId is not { } tip)
            return -1;
        if (_headRowMissed && ReferenceEquals(_headRowMissLayout, layout) && _headRowMissTip.Equals(tip))
            return -1;

        _headRowLookups++;
        if (layout.TryGetRow(tip, out var r))
            return r;

        _headRowMissed = true;
        _headRowMissLayout = layout;
        _headRowMissTip = tip;
        return -1;
    }

    private void DrawGraphColumn(DrawingContext dc, GraphLayout layout, int first, int last, double colX, double viewportWidth)
    {
        var hoverLane = _hoverRow >= 0 && _hoverRow < layout.RowCount ? layout.Rows[_hoverRow].LaneIndex : -1;

        // foreach uses HashSet's struct enumerator; LINQ First() would box it every frame.
        var selectedRow = _selectedRow;
        if (selectedRow < 0)
        {
            foreach (var r in _multiSelect)
            {
                selectedRow = r;
                break;
            }
        }
        int focusLane = -1;
        if (selectedRow >= 0 && selectedRow < layout.RowCount)
        {
            focusLane = layout.Rows[selectedRow].LaneIndex;
        }
        else
        {
            var headRow = ResolveHeadRow(layout);
            if (headRow >= 0 && headRow < layout.RowCount)
                focusLane = layout.Rows[headRow].LaneIndex;
        }

        // cull lane geometry against the visible span — the Graph column intersected with
        // the viewport — with a small margin for the pen width. A stretched or panned column
        // "contains" every lane, so culling against the column edge alone would skip nothing;
        // this stops issuing draw calls for lines nobody can see.
        var minVisibleX = Math.Max(colX, 0) - 4;
        var maxVisibleX = Math.Min(colX + _graphColWidth, viewportWidth) + 4;

        // Plain elbows go BEFORE the lane runs, so every vertical lane line paints over the
        // horizontals that cross it. Emphasised elbows are queued and drawn after, so the
        // emphasis stays unbroken where they cross other lanes.
        DrawRowElbows(dc, layout, first, last, minVisibleX, maxVisibleX, focusLane, hoverLane);

        // Vertical lane pieces of the visible rows are merged into maximal same-colour runs and
        // drawn as one line each — about lanes plus lane breaks, not rows × lanes. Where several
        // pieces share a lane in one row, the builder keeps the last one listed, which is the one
        // that would cover the rest on screen (same x, same pen width). Pens use a square cap, so
        // where two runs meet (a colour change, an elbow) they overlap rather than leave a seam.
        _laneRuns.Begin(layout.TotalLaneCount);
        var piecesSeen = 0;
        for (var row = first; row <= last; row++)
        {
            var segments = layout.GetRowSegments(row);
            piecesSeen += segments.Length;
            _laneRuns.AddRow(segments);
        }

        var yBase = RowY(first);
        var halfRow = _rowHeight / 2;
        var runsDrawn = 0;
        foreach (var run in _laneRuns.Finish())
        {
            var x = LaneX(run.Lane);
            if (x < minVisibleX || x > maxVisibleX)
                continue;

            var isEmph = (focusLane >= 0 && run.Lane == focusLane) || (hoverLane >= 0 && run.Lane == hoverLane);
            var pen = isEmph ? EmphPen(run.ColorIndex) : LanePen(run.ColorIndex);
            dc.DrawLine(pen, new Point(x, yBase + run.StartHalf * halfRow), new Point(x, yBase + run.EndHalf * halfRow));
            runsDrawn++;
        }
        _lanePiecesSeen = piecesSeen;
        _laneRunsDrawn = runsDrawn;

        DrawQueuedEmphasisedElbows(dc);

        // The right-edge lane tick sits ON TOP of the lines, broken per-row with WashRowGap
        // to match the row wash beside it.
        if (_graphColWidth > 0)
        {
            var tickX = colX + _graphColWidth - TickWidth;
            for (var row = first; row <= last; row++)
            {
                var color = layout.Rows[row].ColorIndex;
                var y = RowY(row);
                dc.DrawRectangle(LaneTick(color), null,
                    new Rect(tickX, y + WashRowGap / 2, TickWidth, _rowHeight - WashRowGap));
            }
        }
    }

    /// <summary>The commit dots — a pass after <see cref="DrawBadgeConnectors"/> so the
    /// connector lines render behind them. Every dot is the same size and solid-filled in its
    /// lane colour.</summary>
    private void DrawGraphNodes(DrawingContext dc, GraphLayout layout, int first, int last)
    {
        for (var row = first; row <= last; row++)
        {
            var gr = layout.Rows[row];
            var cx = NodeX(gr.LaneIndex, out _);
            var cy = RowY(row) + _rowHeight / 2;

            dc.DrawEllipse(LaneBrush(gr.ColorIndex), null,
                new Point(cx, cy), NodeRadius, NodeRadius);
            if (row == _hoverRow)
                dc.DrawEllipse(null, _hoverPen, new Point(cx, cy), NodeRadius + 2, NodeRadius + 2);
        }
    }

    // _hScrollOffset scrolls the whole column band; _graphPanOffset scrolls only the
    // lanes inside the Graph column. Culling, NodeX edge-clamping, the lane wash, badge
    // connectors and hover emphasis all derive from LaneX, so they follow the pan automatically.
    private double LaneX(int lane) => _graphColLeft + (lane + 0.5) * LaneWidth - _hScrollOffset - _graphPanOffset;

    /// <summary>pan just enough to bring <paramref name="lane"/> inside the visible part
    /// of the Graph column, leaving one lane of margin. A no-op when the lane already shows or
    /// nothing is hidden. Never called from hover.</summary>
    private void EnsureLaneInView(int lane)
    {
        if (MaxPan <= 0)
        {
            if (_graphPanOffset != 0) { _graphPanOffset = 0; InvalidateVisual(); }
            return;
        }

        var left = lane * LaneWidth;                    // lane's left edge in pan space
        var right = left + LaneWidth;
        var view = _graphColWidth - TickWidth;
        if (left < _graphPanOffset)
            _graphPanOffset = left - LaneWidth;         // one lane of margin
        else if (right > _graphPanOffset + view)
            _graphPanOffset = right - view + LaneWidth;
        ClampPan();
        InvalidateVisual();
    }

    /// <summary>screen x of a commit node — always its plain lane centre. The single
    /// source of a node's x: the drawn dot, its hit area, its lane wash and its badge connector
    /// all read from here so they always agree.
    /// <para>Panned-out dots and lines clip against the Graph column together, exactly like
    /// narrowing the column with the divider. Trap: do not edge-clamp the dot while the lanes
    /// are panned (<c>_graphPanOffset != 0</c>). Clamping pins the dot to the column edge while
    /// the lane segments / elbows keep reading <see cref="LaneX"/>, so a shift+scroll pan
    /// visibly detaches the dots from their lines.</para></summary>
    private double NodeX(int laneIndex, out bool clamped)
    {
        clamped = false;
        return LaneX(laneIndex);
    }

    private const double IconBox = 12;
    private const double IconGap = 4;
    // A numbered remote cloud is wider than the other badge icons so its digit stays legible.
    private const double NumberedCloudBox = 16;
    private const double BadgePadX = 2;
    private const double BadgeClusterGap = 4;
    /// <summary>Ref-badge / overflow-chip corner radius — near-square.</summary>
    private const double BadgeCornerRadius = 2;
    private const double BadgeMinTextWidth = 10;
    private const double BadgeTextSlack = 1;
    private const double BadgeMinBranchWidth = 130;

    /// <summary>True when a branch badge draws one numbered cloud per remote rather than a
    /// single plain cloud.</summary>
    private bool NumberedClouds(GraphDecoration d) => _remoteNumbering.IsActive && d.Remotes.Length > 0;

    private static bool IsBranchBadge(GraphDecoration d) =>
        d.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch or DecorationType.Head;

    /// <summary>The width of the badge's right-hand icon cluster. Measuring and drawing both read
    /// this, so a badge's measured width always matches what is drawn.</summary>
    private double IconClusterWidth(GraphDecoration d)
    {
        double width = 0;
        var icons = 0;
        if (d.Type == DecorationType.Tag)
        {
            width = IconBox;
            icons = 1;
        }
        else if (d.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch)
        {
            if (d.IsLocal)
            {
                width += IconBox;
                icons++;
            }
            if (NumberedClouds(d))
            {
                width += d.Remotes.Length * NumberedCloudBox;
                icons += d.Remotes.Length;
            }
            else if (d.Remotes.Length > 0)
            {
                width += IconBox;
                icons++;
            }
        }
        return icons == 0 ? 0 : width + (icons - 1) * IconGap;
    }

    private static string? BadgePlusText(GraphDecoration d) =>
        d.SiblingsOrNull is { Length: > 0 } sibs ? PlusLabel(sibs.Length + 1) : null;

    private static readonly string[] PlusLabels =
        Enumerable.Range(0, 256).Select(i => "+" + i.ToString(CultureInfo.InvariantCulture)).ToArray();

    /// <summary>The "+N" overflow text; a table lookup, so a badge costs no string per frame.</summary>
    private static string PlusLabel(int n) =>
        (uint)n < (uint)PlusLabels.Length ? PlusLabels[n] : "+" + n.ToString(CultureInfo.InvariantCulture);

    private static bool AnyHead(List<GraphDecoration> decos)
    {
        foreach (var d in decos)
            if (d.IsHead)
                return true;
        return false;
    }

    private (double RightExtras, double Natural, double Min) BadgeMetrics(GraphDecoration d)
    {
        var label = d.Type == DecorationType.Head ? "HEAD" : d.Label;
        var labelW = Text(label, _pillText, size: BadgeLabelSize).Width;

        var cluster = IconClusterWidth(d);
        var plus = BadgePlusText(d);
        var plusW = plus is null ? 0 : Text(plus, _pillText, size: BadgeLabelSize).Width;

        var rightExtras =
            (cluster > 0 ? BadgeClusterGap + cluster : 0) +
            (plus is not null ? BadgeClusterGap + plusW : 0);

        var content = BadgePadX + labelW + BadgeTextSlack + rightExtras + BadgePadX;
        var natural = IsBranchBadge(d) ? Math.Max(BadgeMinBranchWidth, content) : content;
        var min = BadgePadX + BadgeMinTextWidth + rightExtras + BadgePadX;
        return (rightExtras, natural, min);
    }

    /// <summary>record, per row, the screen-X right edge of the primary badge — the
    /// start point for <see cref="DrawBadgeConnectors"/>, which runs before the Refs column
    /// paints. Mirrors the primary-badge width calc in <see cref="DrawRefsColumn"/> exactly.</summary>
    private void MeasureBadgeRightEdges(Dictionary<int, List<GraphDecoration>> decosByRow,
        int first, int last, double colX, double colWidth)
    {
        var startX = colX + 8;
        var rightLimit = colX + colWidth - 8;
        for (var row = first; row <= last; row++)
        {
            if (!decosByRow.TryGetValue(row, out var decos) || decos.Count == 0)
                continue;
            var (_, natural, min) = BadgeMetrics(decos[0]);
            var reserve = decos.Count > 1 ? 32.0 : 0.0;
            var room = rightLimit - startX - reserve;
            _badgeRightEdge[row] = startX + Math.Min(natural, Math.Max(min, room));
        }
    }

    private void DrawRefsColumn(DrawingContext dc, Dictionary<int, List<GraphDecoration>> decosByRow,
        int first, int last, double colX, double colWidth)
    {
        var h = Math.Min(20, _rowHeight - 6);
        var startX = colX + 8;
        var rightLimit = colX + colWidth - 8;

        for (var row = first; row <= last; row++)
        {
            if (!decosByRow.TryGetValue(row, out var decos) || decos.Count == 0)
                continue;

            var top = RowY(row) + (_rowHeight - h) / 2;
            var midY = top + h / 2;

            var drawn = _drawnBadges;
            drawn.Clear();
            var x = startX;
            for (var i = 0; i < decos.Count; i++)
            {
                var (_, natural, min) = BadgeMetrics(decos[i]);
                var reserve = i < decos.Count - 1 ? 32 : 0;
                var room = rightLimit - x - reserve;
                if (i == 0)
                {
                    var w0 = Math.Min(natural, Math.Max(min, room));
                    drawn.Add((decos[i], w0));
                    x += w0 + 4;
                    continue;
                }
                if (natural > room)
                    break;
                drawn.Add((decos[i], natural));
                x += natural + 4;
            }

            var bx = startX;
            foreach (var (d, w) in drawn)
            {
                DrawBadge(dc, d, bx, top, w, h, midY, row);
                bx += w + 4;
            }

            // the primary badge's right edge (the connector start) is measured up front
            // in MeasureBadgeRightEdges — DrawBadgeConnectors runs before this column.

            if (drawn.Count < decos.Count)
            {
                var hidden = RentOverflowList(_overflowChips.Count);
                for (var i = drawn.Count; i < decos.Count; i++)
                    hidden.Add(decos[i]);
                var ft = Text(PlusLabel(hidden.Count), _textMuted, size: BadgeLabelSize);
                var chipW = ft.Width + 10;
                var chipX = colX + colWidth - 4 - chipW;
                var chipRect = new Rect(chipX, top, chipW, h);
                dc.DrawRectangle(_hoverFill, _borderPen, chipRect, BadgeCornerRadius, BadgeCornerRadius);
                ft.Draw(dc, new Point(chipX + 5, CenteredTextY(ft, top, h, BadgeLabelSize)));
                _overflowChips.Add((chipRect, row, hidden));
            }
        }
    }

    private const int CellTextCapacity = 2048;

    private readonly record struct DateCellKey(GitObjectId Id, long Minute, bool AuthorDate);

    // Date and SHA cell strings, built once per commit (and per minute for the relative date)
    // instead of once per row per frame. Each miss is counted.
    private readonly CellTextCache<DateCellKey> _dateCells = new(CellTextCapacity);
    private readonly CellTextCache<GitObjectId> _shaCells = new(CellTextCapacity);
    private int _cellTextMisses;

    private string DateCellText(CommitMetadata meta, long minute)
    {
        var key = new DateCellKey(meta.Id, minute, _showAuthorDate);
        if (_dateCells.TryGet(key, out var text))
            return text;
        _cellTextMisses++;
        text = DateCell(CommitDateDisplay.Pick(meta.AuthorWhen, meta.CommitWhen, _showAuthorDate));
        _dateCells.Add(key, text);
        return text;
    }

    private string ShaCellText(GitObjectId id)
    {
        if (_shaCells.TryGet(id, out var text))
            return text;
        _cellTextMisses++;
        text = id.ToShortString(7);
        _shaCells.Add(id, text);
        return text;
    }

    /// <summary>A two-generation string cache: a lookup that misses the current generation but
    /// hits the previous one is promoted, and once the current generation is full it becomes the
    /// previous one. Whatever the last frames drew survives a rollover, and nothing is allocated
    /// when the generations swap.</summary>
    private sealed class CellTextCache<TKey>(int capacity) where TKey : notnull
    {
        private Dictionary<TKey, string> _current = [];
        private Dictionary<TKey, string> _previous = [];

        public bool TryGet(TKey key, out string text)
        {
            if (_current.TryGetValue(key, out text!))
                return true;
            if (_previous.TryGetValue(key, out text!))
            {
                Add(key, text);
                return true;
            }
            return false;
        }

        public void Add(TKey key, string text)
        {
            if (_current.Count >= capacity)
            {
                (_previous, _current) = (_current, _previous);
                _current.Clear();
            }
            _current[key] = text;
        }
    }

    /// <summary>Top-left Y for drawing <paramref name="ft"/> so the text's cap band is optically
    /// centred in a <paramref name="bandH"/>-tall band at <paramref name="bandTop"/> — independent
    /// of the font's line-box padding, which <c>(bandH - ft.Height) / 2</c> ignores.</summary>
    private static double CenteredTextY(TextLayout ft, double bandTop, double bandH, double size)
        => bandTop + bandH / 2 + size * 0.35 - ft.Baseline;

    private void DrawBadge(DrawingContext dc, GraphDecoration d, double x, double top, double w,
        double h, double midY, int row)
    {
        var rect = new Rect(x, top, w, h);
        var headRing = d.IsHead || d.Type == DecorationType.Head;
        // solid opaque lane-colour fill + same-hue border.
        dc.DrawRectangle(
            LaneBrush(d.ColorIndex),
            headRing ? _hoverPen : PillBorder(d.ColorIndex),
            rect, BadgeCornerRadius, BadgeCornerRadius);

        // every badge label and icon uses the theme's ref-label ink, whatever the lane colour.
        var ink = _pillText;
        var (rightExtras, _, _) = BadgeMetrics(d);
        var plus = BadgePlusText(d);
        var plusW = plus is null ? 0 : Text(plus, ink, size: BadgeLabelSize).Width;

        var label = d.Type == DecorationType.Head ? "HEAD" : d.Label;
        var textBudget = Math.Max(4, w - BadgePadX * 2 - rightExtras);
        var ft = Text(label, ink, textBudget, BadgeLabelSize);
        ft.Draw(dc, new Point(x + BadgePadX, CenteredTextY(ft, top, h, BadgeLabelSize)));
        _pillRects.Add((rect, row, d));

        if (plus is not null)
        {
            var pf = Text(plus, ink, size: BadgeLabelSize);
            pf.Draw(dc, new Point(x + w - BadgePadX - plusW, CenteredTextY(pf, top, h, BadgeLabelSize)));
        }

        // The icon cluster and the +N counter are always laid out from the badge's
        // right edge with a fixed order and a guaranteed gap, so the cloud glyph can never be
        // clipped or hidden behind +N. If the badge is pathologically narrow, the label is what
        // yields (its budget already floors low), never the right-edge cluster.
        var ix = x + w - BadgePadX - (plus is not null ? plusW + BadgeClusterGap : 0) - IconClusterWidth(d);
        ix = Math.Max(ix, x + BadgePadX + Math.Min(ft.Width, BadgeMinTextWidth));
        var iy = midY - 6;
        if (d.Type == DecorationType.Tag)
        {
            DrawBadgeIcon(dc, _icoTag, ix, iy, ink);
        }
        else if (d.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch)
        {
            if (d.IsLocal)
            {
                DrawBadgeIcon(dc, _icoLocal, ix, iy, ink);
                _iconRects.Add((new Rect(ix, iy, IconBox, IconBox), "local"));
                ix += IconBox + IconGap;
            }
            if (NumberedClouds(d))
                DrawNumberedClouds(dc, d, ix, midY, ink);
            else if (d.Remotes.Length > 0)
            {
                DrawBadgeIcon(dc, _icoRemote, ix, iy, ink);
                _iconRects.Add((new Rect(ix, iy, IconBox, IconBox), RemotesTip(d.RemotesOrNull!)));
            }
        }
    }

    /// <summary>One filled cloud per remote in ascending number order, unnumbered (stale) remotes
    /// last, each with its number knocked out in the badge's lane colour and its own tooltip.</summary>
    private void DrawNumberedClouds(DrawingContext dc, GraphDecoration d, double ix, double midY, IBrush ink)
    {
        var remotes = d.Remotes;
        Span<int> order = remotes.Length <= 32 ? stackalloc int[remotes.Length] : new int[remotes.Length];
        for (var i = 0; i < order.Length; i++)
        {
            var key = SortKey(remotes[i]);
            var j = i;
            for (; j > 0 && SortKey(remotes[order[j - 1]]) > key; j--)
                order[j] = order[j - 1];
            order[j] = i;
        }

        var iy = midY - NumberedCloudBox / 2;
        var band = RemoteCloudGlyph.DigitBand(NumberedCloudBox);
        var knockout = LaneBrush(d.ColorIndex);
        foreach (var k in order)
        {
            var name = remotes[k];
            DrawBadgeIcon(dc, _icoRemoteFilled, ix, iy, ink);
            var n = _remoteNumbering.NumberOf(name);
            if (n > 0)
            {
                var size = RemoteCloudGlyph.DigitFontSize(n, NumberedCloudBox);
                var digit = _textCache.Get(n < NumberStrings.Length ? NumberStrings[n] : n.ToString(CultureInfo.InvariantCulture),
                    _digitTypeface, size, knockout, double.PositiveInfinity, TextTrimming.CharacterEllipsis, maxLines: 1);
                digit.Draw(dc, new Point(ix + band.X + (band.Width - digit.Width) / 2,
                    CenteredTextY(digit, iy + band.Y, band.Height, size)));
            }
            _iconRects.Add((new Rect(ix, iy, NumberedCloudBox, NumberedCloudBox), _remoteNumbering.TooltipOf(name)));
            ix += NumberedCloudBox + IconGap;
        }

        int SortKey(string name) => _remoteNumbering.NumberOf(name) is > 0 and var n ? n : int.MaxValue;
    }

    /// <summary>The single plain cloud's tooltip: the remote's name, or every name joined
    /// (computed once per decoration array).</summary>
    private string RemotesTip(string[] remotes)
    {
        if (remotes.Length == 1)
            return remotes[0];
        if (_joinedRemoteTips.TryGetValue(remotes, out var tip))
            return tip;
        _poolMisses++;
        tip = string.Join(", ", remotes);
        _joinedRemoteTips.AddOrUpdate(remotes, tip);
        return tip;
    }

    private void DrawBadgeIcon(DrawingContext dc, Geometry g, double x, double y, IBrush ink)
    {
        using (dc.PushTransform(Matrix.CreateTranslation(x, y)))
            dc.DrawGeometry(ink, null, g);
    }

    // Badges drawn on the row being laid out, and the per-chip hidden-badge lists: reused every
    // frame, so a row with overflow allocates nothing.
    private readonly List<(GraphDecoration D, double W)> _drawnBadges = [];
    private readonly List<List<GraphDecoration>> _overflowLists = [];

    private List<GraphDecoration> RentOverflowList(int index)
    {
        if (index < _overflowLists.Count)
        {
            var list = _overflowLists[index];
            list.Clear();
            return list;
        }
        _poolMisses++;
        var created = new List<GraphDecoration>();
        _overflowLists.Add(created);
        return created;
    }

    private void DrawTextColumn(DrawingContext dc, GraphLayout layout, int first, int last,
        double colX, double colWidth, GraphColumnKind kind)
    {
        var maxW = Math.Max(20, colWidth - 12);
        var brush = kind == GraphColumnKind.Message ? _text : _textMuted;
        // The relative date changes with the minute, so a cached date cell is valid for one. The
        // bucket is the UTC minute, so a cell can lag the commit's true age by up to 59 seconds.
        var minute = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;
        for (var row = first; row <= last; row++)
        {
            var meta = layout.Commits[row];
            var text = kind switch
            {
                GraphColumnKind.Message => meta.MessageShort,
                GraphColumnKind.Author => meta.AuthorName,
                GraphColumnKind.Date => DateCellText(meta, minute),
                GraphColumnKind.Sha => ShaCellText(meta.Id),
                _ => "",
            };
            var ft = Text(text, brush, maxW);
            var midY = RowY(row) + (_rowHeight - ft.Height) / 2;
            ft.Draw(dc, new Point(colX + 8, midY));
        }
    }

    /// <summary>Draws the plain elbows of the visible rows and queues the emphasised ones, in
    /// paint order, for <see cref="DrawQueuedEmphasisedElbows"/>.</summary>
    private void DrawRowElbows(DrawingContext dc, GraphLayout layout, int first, int last,
        double minVisibleX, double maxVisibleX, int focusLane, int hoverLane)
    {
        _emphElbowCount = 0;
        for (var row = first; row <= last; row++)
        {
            var y = RowY(row);
            var rowElbows = layout.GetRowElbows(row);
            if (rowElbows.IsEmpty)
                continue;

            // Elbows on one row share the node-height horizontal, so paint order decides
            // which colour shows where they overlap: longest first, so each stretch shows
            // the nearest branch that still runs past it. Emphasised ones go last.
            if (_elbowScratch.Length < rowElbows.Length)
                _elbowScratch = new RowElbow[Math.Max(16, rowElbows.Length)];
            var elbows = _elbowScratch.AsSpan(0, rowElbows.Length);
            OrderElbowsForPaint(rowElbows, elbows, focusLane, hoverLane);

            foreach (var elbow in elbows)
            {
                var fromX = LaneX(elbow.FromLane);
                var toX = LaneX(elbow.ToLane);
                // An elbow spans [min(fromX, toX), max(fromX, toX)]; skip it only when that
                // whole span is outside the visible span.
                if (Math.Max(fromX, toX) < minVisibleX || Math.Min(fromX, toX) > maxVisibleX)
                    continue;

                if (IsElbowEmphasised(elbow, focusLane, hoverLane))
                {
                    if (_emphElbowCount == _emphElbows.Length)
                        Array.Resize(ref _emphElbows, Math.Max(16, _emphElbows.Length * 2));
                    _emphElbows[_emphElbowCount++] = new QueuedElbow(elbow, fromX, y);
                    continue;
                }

                DrawElbow(dc, LanePen(elbow.ColorIndex), elbow.FromLane, elbow.ToLane, fromX, y, elbow.Kind);
            }
        }
    }

    /// <summary>Draws the elbows <see cref="DrawRowElbows"/> queued, in the order queued: rows
    /// top to bottom, and within a row the order <see cref="OrderElbowsForPaint"/> gave.</summary>
    private void DrawQueuedEmphasisedElbows(DrawingContext dc)
    {
        for (var i = 0; i < _emphElbowCount; i++)
        {
            ref readonly var q = ref _emphElbows[i];
            DrawElbow(dc, EmphPen(q.Elbow.ColorIndex), q.Elbow.FromLane, q.Elbow.ToLane, q.FromX, q.Y, q.Elbow.Kind);
        }
    }

    private readonly record struct QueuedElbow(RowElbow Elbow, double FromX, double Y);

    private RowElbow[] _elbowScratch = Array.Empty<RowElbow>();
    private QueuedElbow[] _emphElbows = Array.Empty<QueuedElbow>();
    private int _emphElbowCount;

    /// <summary>The lane an elbow's vertical leg runs in: the child's lane for a branch-out
    /// (falls from the row top), the merged-in parent's lane for a merge (rises from the row
    /// bottom). The other end is the horizontal leg into a node.</summary>
    internal static int ElbowVerticalLane(in RowElbow e) =>
        e.Kind == ElbowKind.EnterTop ? e.FromLane : e.ToLane;

    /// <summary>An elbow belongs to an emphasised line only when its vertical leg runs in that
    /// lane — it is that line turning into another commit. An elbow that merely ends its
    /// horizontal leg on an emphasised lane belongs to the other line and stays plain, so the
    /// bold stroke never stops at a bend into a thin vertical.</summary>
    internal static bool IsElbowEmphasised(in RowElbow e, int focusLane, int hoverLane)
    {
        var lane = ElbowVerticalLane(e);
        return (focusLane >= 0 && lane == focusLane) || (hoverLane >= 0 && lane == hoverLane);
    }

    internal static bool IsElbowEmphasised(in RowElbow e, int hoverLane) =>
        IsElbowEmphasised(e, focusLane: -1, hoverLane);

    /// <summary>Copies <paramref name="source"/> into <paramref name="dest"/> in paint order:
    /// elbows whose vertical leg is in <paramref name="focusLane"/> or <paramref name="hoverLane"/>
    /// last (hover last among emphasised), and within each group the widest lane span first.
    /// Stable, so equal spans keep their layout order.</summary>
    internal static void OrderElbowsForPaint(ReadOnlySpan<RowElbow> source, Span<RowElbow> dest, int focusLane, int hoverLane)
    {
        static int Key(in RowElbow e, int focusLane, int hoverLane)
        {
            var vertical = ElbowVerticalLane(e);
            var isHover = hoverLane >= 0 && vertical == hoverLane;
            var isFocus = focusLane >= 0 && vertical == focusLane;
            var priority = isHover ? 2 << 20 : isFocus ? 1 << 20 : 0;
            return priority - Math.Abs(e.ToLane - e.FromLane);
        }

        source.CopyTo(dest);
        for (var i = 1; i < dest.Length; i++)
        {
            var item = dest[i];
            var key = Key(item, focusLane, hoverLane);
            var j = i - 1;
            while (j >= 0 && Key(dest[j], focusLane, hoverLane) > key)
            {
                dest[j + 1] = dest[j];
                j--;
            }
            dest[j + 1] = item;
        }
    }

    internal static void OrderElbowsForPaint(ReadOnlySpan<RowElbow> source, Span<RowElbow> dest, int hoverLane)
        => OrderElbowsForPaint(source, dest, focusLane: -1, hoverLane);

    private readonly Dictionary<(int LaneDelta, ElbowKind Kind), StreamGeometry> _elbowGeometryCache = new();

    private StreamGeometry GetElbowGeometry(int laneDelta, ElbowKind kind)
    {
        if (_elbowGeometryCache.TryGetValue((laneDelta, kind), out var cached))
            return cached;
        _poolMisses++;

        var geo = new StreamGeometry();
        var dx = laneDelta * _laneWidth;
        var midY = _rowHeight / 2.0;
        var sx = dx >= 0 ? 1.0 : -1.0;
        // One rounded corner joining a vertical run in one lane to a horizontal run at node
        // height. The vertical run on this row is midY long, the horizontal at most |dx|.
        var r = Math.Min(CornerRadius, Math.Min(Math.Abs(dx) / 2.0, midY));

        using (var ctx = geo.Open())
        {
            if (kind == ElbowKind.LeaveBottom)
            {
                // Merge edge, drawn on the merge commit's own row. The merged-in branch's line
                // rises up its own lane (ToLane, at x = dx) from the row below, turns at node
                // height, and runs horizontally into the merge commit's node in FromLane (x = 0).
                // Up from the source, then toward the target.
                ctx.BeginFigure(new Point(dx, _rowHeight), isFilled: false);
                ctx.LineTo(new Point(dx, midY + r));
                ctx.QuadraticBezierTo(new Point(dx, midY), new Point(dx - sx * r, midY));
                ctx.LineTo(new Point(0, midY));
            }
            else
            {
                // Branch-out, drawn on the parent commit's row. The new commit's line falls down
                // its own lane (FromLane, at x = 0) from the row above, turns at node height, and
                // runs horizontally into the parent commit's node in ToLane (x = dx). Down from
                // the new commit, then toward the parent.
                ctx.BeginFigure(new Point(0, 0), isFilled: false);
                ctx.LineTo(new Point(0, midY - r));
                ctx.QuadraticBezierTo(new Point(0, midY), new Point(sx * r, midY));
                ctx.LineTo(new Point(dx, midY));
            }
            ctx.EndFigure(false);
        }

        _elbowGeometryCache[(laneDelta, kind)] = geo;
        return geo;
    }

    private void DrawElbow(DrawingContext dc, Pen pen, int fromLane, int toLane, double fromX, double topY, ElbowKind kind)
    {
        var geo = GetElbowGeometry(toLane - fromLane, kind);
        using (dc.PushTransform(Matrix.CreateTranslation(fromX, topY)))
            dc.DrawGeometry(null, pen, geo);
    }

    /// <summary>
    /// Shaped-text layouts kept for the graph: the visible rows' text columns, badge labels and
    /// <c>+N</c> chips, header labels and placeholders. On a 100k-row graph with every column
    /// shown, a 1,400 DIP viewport (53 rows) draws 174 distinct strings, so this holds several
    /// viewports even on a tall display: a scroll step shapes only the rows it reveals. Each
    /// shaped message costs a few kilobytes, which is what keeps the bound this low.
    /// </summary>
    internal const int TextCacheCapacity = 1024;

    /// <summary>A single-line, end-ellipsised layout of <paramref name="s"/>, shaped once and
    /// cached. Do not dispose it or keep it past a theme change.</summary>
    private TextLayout Text(string s, IBrush brush, double maxWidth = double.PositiveInfinity, double size = RowTextSize)
        => _textCache.Get(s, _typeface, size, brush,
            double.IsPositiveInfinity(maxWidth) ? maxWidth : Math.Max(4, maxWidth),
            TextTrimming.CharacterEllipsis, maxLines: 1);

    private static string Relative(DateTimeOffset when)
    {
        var d = DateTimeOffset.Now - when;
        if (d.TotalMinutes < 60) return $"{Math.Max(1, (int)d.TotalMinutes)}m";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays}d";
        return when.ToString("yyyy-MM-dd");
    }

    // The Date column shows the full local wall-clock timestamp plus the "how long ago"
    // relative form in parentheses — e.g. "09/05/2026 - 02:30 PM (17h)".
    private static string DateCell(DateTimeOffset when)
        => when.ToLocalTime().ToString("MM/dd/yyyy - hh:mm tt", CultureInfo.InvariantCulture)
           + $" ({Relative(when)})";
}
