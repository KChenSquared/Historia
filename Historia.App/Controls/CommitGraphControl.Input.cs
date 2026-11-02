namespace Historia.Controls;

public sealed partial class CommitGraphControl
{
    // ── Input ──

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // Honour the OS natural/inverted-scroll preference (macOS & Linux) or the
        // explicit override. Windows probes false, so the sign is 1 (no inversion).
        var delta = e.Delta.Y * Historia.Services.UI.WheelInput.Sign(
            _settings.Current.WheelScrollDirection, _scrollProbe.NaturalScrolling);
        if (_revealTarget is not null && !_revealUserTookOver)
        {
            _revealUserTookOver = true; // any manual scroll/zoom hands the viewport back
            InvalidateArrange();        // un-blank the graph body
        }

        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            var factor = delta > 0 ? 1.1 : 1 / 1.1;

            // Anchor the zoom on the row under the cursor so it stays put (fall back to the
            // viewport top when the pointer is over the header).
            var anchorY = e.GetPosition(this).Y;
            var rowAtAnchor = anchorY >= RowsTop ? (_scrollOffset + anchorY - RowsTop) / _rowHeight : (double?)null;

            var laneBefore = _laneWidth;
            _rowHeight = Math.Clamp(_rowHeight * factor, MinRowHeight, MaxRowHeight);
            _laneWidth = Math.Clamp(_laneWidth * factor, MinLaneWidth, MaxLaneWidth);
            _zoomOverridesPin = true;
            _elbowGeometryCache.Clear();

            // scale the pan by the same ratio the lane width actually changed by, so the
            // same lanes stay in view; ClampPan again once the resolved column width settles.
            if (laneBefore > 0)
                _graphPanOffset *= _laneWidth / laneBefore;
            ClampPan();

            if (rowAtAnchor is { } r)
                _scrollOffset = Math.Max(0, r * _rowHeight - (anchorY - RowsTop));

            InvalidateArrange();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // Shift+wheel, or a trackpad horizontal swipe, over the Graph column's rows pans
        // the lanes. Some platforms deliver Shift+wheel as Delta.X already, so accept either.
        // With no hidden lanes (MaxPan == 0) this falls through to vertical scrolling.
        if (MaxPan > 0)
        {
            var wpos = e.GetPosition(this);
            if (wpos.Y >= RowsTop && ColumnKindAt(wpos.X) == GraphColumnKind.Graph)
            {
                var pan = e.Delta.X != 0 ? e.Delta.X
                        : (e.KeyModifiers & KeyModifiers.Shift) != 0 ? delta
                        : 0;
                if (pan != 0)
                {
                    _graphPanOffset = Math.Clamp(_graphPanOffset - pan * 40, 0, MaxPan);
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }
            }
        }

        _scrollOffset = Math.Clamp(_scrollOffset - delta * 40, 0, Math.Max(0, _scrollBar.Maximum));
        OnScrolled();
        e.Handled = true;
    }

    /// <summary>Pointer, thumb and track positions for the drag log, all translated into the
    /// vertical bar's own coordinates: <c>ptr</c> pointer Y, <c>thumb</c> thumb top..bottom,
    /// <c>trackArea</c> track top..bottom. <c>trackValue</c>/<c>trackMax</c> are the template
    /// Track's own copies, which draw the thumb; one that disagrees with the bar's (NaN, stale)
    /// explains a thumb drawn somewhere the bar's Value doesn't put it.</summary>
    private string ScrollBarGeometry()
    {
        var thumb = _scrollBar.GetVisualDescendants().OfType<Thumb>().FirstOrDefault();
        var track = _scrollBar.GetVisualDescendants().OfType<Track>().FirstOrDefault();
        if (thumb is null || track is null)
            return "geometry=none";
        var thumbTop = thumb.TranslatePoint(default, _scrollBar)?.Y ?? double.NaN;
        var trackTop = track.TranslatePoint(default, _scrollBar)?.Y ?? double.NaN;
        var ptr = _scrollBarPointer?.Y ?? double.NaN;
        return string.Create(CultureInfo.InvariantCulture,
            $"ptr={ptr:F1} thumb={thumbTop:F1}..{thumbTop + thumb.Bounds.Height:F1} trackArea={trackTop:F1}..{trackTop + track.Bounds.Height:F1} trackValue={track.Value:F1} trackMax={track.Maximum:F1}");
    }

    /// <summary>One <c>graph-scroll</c> line per vertical thumb move and one at drag end.</summary>
    private void LogScrollEvent(ScrollEventType type)
    {
        if (type == ScrollEventType.ThumbTrack)
            LogScroll(string.Create(CultureInfo.InvariantCulture,
                $"track value={_scrollBar.Value:F1} max={_scrollBar.Maximum:F1} viewport={_scrollBar.ViewportSize:F1} offset={_scrollOffset:F1} rows={_layout?.Layout.RowCount ?? 0} {ScrollBarGeometry()}"));
        else if (type == ScrollEventType.EndScroll)
            LogScroll("drag end");
    }

    /// <summary>Frame benchmark: scrolls to <paramref name="offset"/> the way a plain wheel step
    /// does — clamped to the scroll range, then <see cref="OnScrolled"/>.</summary>
    internal void SetVerticalOffsetForBench(double offset)
    {
        _scrollOffset = Math.Clamp(offset, 0, Math.Max(0, _scrollBar.Maximum));
        OnScrolled();
    }

    /// <summary>Frame benchmark: the largest vertical offset over the rows loaded so far.</summary>
    internal double MaxVerticalOffsetForBench => Math.Max(0, _scrollBar.Maximum);

    /// <summary>Frame benchmark: the selected commit's row, or -1 while it is not loaded.</summary>
    internal int SelectedRowForBench => _selectedRow;

    private void OnScrolled()
    {
        if (_revealTarget is not null && !_revealUserTookOver)
        {
            _revealUserTookOver = true; // manual scroll while a reveal is pending → user is driving
            InvalidateArrange();        // un-blank the graph body
        }
        _scrollBar.Value = _scrollOffset;
        CloseRefList();
        var lastVisible = (int)((_scrollOffset + Bounds.Height - RowsTop) / _rowHeight) + 1;
        _ = EnsureExtendedAsync(lastVisible);
        NoteViewportBottom();
        InvalidateVisual();
        if (IsPointerOver)
            UpdateHoverDwell(ResolveHoverTarget(_lastPointerPos));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        _lastPointerPos = pos;
        var leftDown = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;

        if (_resizing)
        {
            var delta = pos.X - _resizeStartX;
            var eff = _resizeInverted ? -delta : delta;
            var min = GraphColumnDefaults.MinWidthFor(_resizeKind);
            var max = GraphColumnDefaults.MaxWidthFor(_resizeKind);
            // Graph never drags wider than the lanes the layout uses (skip until a
            // layout has lanes). Applying the ceiling during the drag — not only on save —
            // also removes the snap-back when a non-Graph column is released past 2,000px.
            if (_resizeKind == GraphColumnKind.Graph && _layout is { } l && l.Layout.TotalLaneCount > 0)
                max = Math.Clamp(NaturalGraphWidth(l.Layout.TotalLaneCount), min, max);
            var w = Math.Clamp(_resizeStartWidth + eff, min, max);
            _columns = WithColumnWidth(_columns, _resizeKind, w);
            if (_resizeKind == GraphColumnKind.Graph)
            {
                _graphWidthPinned = true;
                _zoomOverridesPin = false;
            }
            InvalidateArrange();
            InvalidateVisual();
            return;
        }

        if (_reorderKind is not null)
        {
            _reorderGhostX = pos.X;
            _reorderCaret = CaretIndexAt(pos.X);
            InvalidateVisual();
            return;
        }
        if (_reorderCandidate is not null && leftDown && Math.Abs(pos.X - _dragStart.X) > 6)
        {
            _reorderKind = _reorderCandidate;
            _reorderCandidate = null;
            _capturedPointer = e.Pointer;
            e.Pointer.Capture(this);
            _reorderGhostX = pos.X;
            _reorderCaret = CaretIndexAt(pos.X);
            InvalidateVisual();
            return;
        }

        if (pos.Y < HeaderHeight)
        {
            var hoverIcon = HeaderIconAt(pos.X, pos.Y);
            if (hoverIcon != _hoverHeaderIcon)
            {
                _hoverHeaderIcon = hoverIcon;
                InvalidateVisual();
            }
            Cursor = new Cursor(
                hoverIcon is not null ? StandardCursorType.Hand
                : BoundaryAt(pos.X) >= 0 ? StandardCursorType.SizeWestEast
                : StandardCursorType.Arrow);
            return;
        }
        if (_hoverHeaderIcon is not null)
        {
            _hoverHeaderIcon = null;
            InvalidateVisual();
        }
        var overOverflow = _overflowChips.Any(c => c.Rect.Contains(pos));
        Cursor = new Cursor(_dragging || overOverflow ? StandardCursorType.Hand : StandardCursorType.Arrow);

        if (_dragRow >= 0 && !_dragging && leftDown
            && (Math.Abs(pos.X - _dragStart.X) > 6 || Math.Abs(pos.Y - _dragStart.Y) > 6))
        {
            _dragging = true;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        // While the pointer sits over the (auto-expanding) vertical scrollbar, skip the
        // row-hover tracking below: it was invalidating this whole custom-drawn surface on
        // every pixel of vertical movement, which raced against the ScrollBar's own hover-expand
        // transition and showed up as flicker wherever the expanded bar overlapped a text column.
        if (_scrollBar.IsVisible && _scrollBar.Bounds.Contains(pos))
        {
            if (_hoverRow != -1)
            {
                _hoverRow = -1;
                InvalidateVisual();
            }
            return;
        }

        var row = RowAt(pos.Y);
        if (row != _hoverRow)
        {
            _hoverRow = row;
            InvalidateVisual();
        }
        UpdateHoverTooltip(pos);
    }

    private int BoundaryAt(double x)
        => GraphColumnInteraction.BoundaryAt(_resolvedColumns, x, _hScrollOffset, BoundaryGrab);

    private (GraphColumnKind Kind, int Slot)? HeaderIconAt(double x, double y)
        => GraphColumnInteraction.HeaderIconAt(_resolvedColumns, x, y, _hScrollOffset, HeaderHeight, HeaderIconSize, HeaderIconInset, HeaderIconCandidates);

    /// <summary>Returns the graph to the left-most horizontal position and the default
    /// zoom, keeping the top visible row in place; column widths and order untouched; a
    /// no-op when already left-most at default zoom.</summary>
    private void ResetGraphView()
    {
        if (_hScrollOffset == 0 && _graphPanOffset == 0
#pragma warning disable S1244 // the reset assigns these exact constants; a near miss only resets again
            && _rowHeight == DefaultRowHeight && _laneWidth == DefaultLaneWidth)
#pragma warning restore S1244
        {
            return;
        }

#pragma warning disable S1244 // the reset assigns these exact constants; a near miss only resets again
        if (_rowHeight != DefaultRowHeight || _laneWidth != DefaultLaneWidth)
#pragma warning restore S1244
        {
            var topRow = _scrollOffset / _rowHeight;
            _rowHeight = DefaultRowHeight;
            _laneWidth = DefaultLaneWidth;
            _scrollOffset = topRow * DefaultRowHeight;
            _zoomOverridesPin = false;
            _elbowGeometryCache.Clear();
        }

        _hScrollOffset = 0;
        _graphPanOffset = 0;
        _hScrollBar.Value = 0;
        InvalidateArrange();
        InvalidateVisual();
    }

    private static IReadOnlyList<GraphColumn> WithColumnWidth(IReadOnlyList<GraphColumn> cols, GraphColumnKind kind, double width)
        => [.. cols.Select(c => c.Kind == kind ? c with { Width = width } : c)];

    private int CaretIndexAt(double x)
        => GraphColumnInteraction.CaretIndexAt(_resolvedColumns, x, _hScrollOffset);

    private GraphColumnKind? ColumnKindAt(double x)
        => GraphColumnInteraction.ColumnKindAt(_resolvedColumns, x, _hScrollOffset);

    private void ReorderColumn(GraphColumnKind moved, int caret)
    {
        var next = GraphColumnInteraction.Reorder(_columns, moved, caret);
        if (ReferenceEquals(next, _columns))
            return;

        _columns = next;
        PersistColumns();
        InvalidateArrange();
        InvalidateVisual();
    }

    private void ShowColumnMenu()
    {
        var menu = new ContextMenu();
        var items = new List<Control>();
        foreach (var col in _columns)
        {
            var kind = col.Kind;
            var mi = new MenuItem { Header = kind.ToString(), Icon = col.Visible ? new TextBlock { Text = "✓" } : null };
            mi.Click += (_, _) => ToggleColumn(kind);
            items.Add(mi);
        }
        items.Add(new Separator());
        items.Add(MenuLeaf("Reset columns", () =>
        {
            _columns = GraphColumnDefaults.All;
            _graphWidthPinned = false;
            _manuallySized.Clear();
            if (_context is { } ctx)
                _ = _repoLayout.UpdateAsync(ctx.RepositoryPath, l => l with { Columns = GraphColumnDefaults.All, GraphWidthPinned = false });
            InvalidateArrange();
            InvalidateVisual();
        }));
        items.Add(new Separator());
        // Hiding the Graph column would otherwise strand the cog with no entry point.
        items.Add(MenuLeaf("Repository settings", () =>
        {
            if (_workspace?.Focused is { } focused)
            {
                _workspace.Focus(focused);
                _nav.RequestNavigate("repo-settings");
            }
        }));
        menu.ItemsSource = items;
        OpenMenu(menu);
    }

    private void ToggleColumn(GraphColumnKind kind)
    {
        var next = _columns.Select(c => c.Kind == kind ? c with { Visible = !c.Visible } : c).ToList();
        if (next.Count(c => c.Visible) == 0 || next.Count(c => c.Visible && c.IsFlex) != 1)
            return;
        _columns = GraphColumnDefaults.Normalize(next);
        PersistColumns();
        InvalidateArrange();
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (_hoverRow != -1)
        {
            _hoverRow = -1;
            InvalidateVisual();
        }
        CancelHoverDwell();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (_resizing || _reorderKind is not null))
        {
            _resizing = false;
            _resizeInverted = false;
            _reorderKind = null;
            _reorderCandidate = null;
            _reorderCaret = -1;
            _capturedPointer?.Capture(null);
            if (_preResizeColumns is not null)
                _columns = _preResizeColumns;
            InvalidateArrange();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && (e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            ToggleQueryBar(true);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _queryBar.IsVisible)
        {
            ToggleQueryBar(false);
            e.Handled = true;
            return;
        }
        if (e.Key is Key.F3 && _matches.HasMatches)
        {
            StepMatch((e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
