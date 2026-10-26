namespace Historia.Controls;

public sealed partial class CommitGraphControl
{
    // ── Graph query & find ──

    private void ToggleQueryBar(bool show)
    {
        _queryBar.IsVisible = show;
        if (show)
        {
            _searchBox.Focus();
            _searchBox.SelectAll();
        }
        else
        {
            Focus();
        }
        InvalidateArrange();
        InvalidateVisual();
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            StepMatch((e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ToggleQueryBar(false);
            e.Handled = true;
        }
    }

    // ── Find ──
    // The scan runs off the UI thread, after a pause in typing. Each request replaces the one
    // before it: the version number is bumped and the previous token cancelled, and a result is
    // applied only if its version is still the latest. _findTerm, _findStore and _findRows
    // describe the latest request, whether its result is applied yet or still on its way.

    private static readonly TimeSpan DefaultFindDebounce = TimeSpan.FromMilliseconds(200);
    private TimeSpan _findDebounce = DefaultFindDebounce;
    private CancellationTokenSource? _findCts;
    private int _findVersion;
    private string _findTerm = "";
    private ICommitStore? _findStore;
    private int _findRows;
    private Func<string, CancellationToken, IReadOnlyList<int>>? _findSearchForTesting;
    private Task? _findTask;

    internal TimeSpan FindDebounceForTesting
    {
        get => _findDebounce;
        set => _findDebounce = value;
    }

    /// <summary>Test-only: stands in for the store's search so a test can count, delay and
    /// cancel scans. Runs on a pool thread with the request's token.</summary>
    internal Func<string, CancellationToken, IReadOnlyList<int>>? FindSearchForTesting
    {
        get => _findSearchForTesting;
        set => _findSearchForTesting = value;
    }

    internal Task? FindTaskForTesting => _findTask;

    /// <summary>Starts a search for the box's text over the current layout. Typing waits out the
    /// debounce; a new layout searches at once. Asking again for what the latest request already
    /// covers (same text, same store, same row count) changes nothing, so a recompute that left
    /// the rows alone keeps its matches and the cursor on them.</summary>
    private void RecomputeMatches(bool debounce = true)
    {
        var term = _searchBox.Text?.Trim() ?? "";
        var layout = _layout?.Layout;
        var store = layout?.Commits;
        var rows = layout?.RowCount ?? 0;
        if (string.Equals(term, _findTerm, StringComparison.Ordinal)
            && ReferenceEquals(store, _findStore) && rows == _findRows)
            return;

        var sameTerm = string.Equals(term, _findTerm, StringComparison.Ordinal);
        CancelFind();
        _findTerm = term;
        _findStore = store;
        _findRows = rows;

        // A lease keeps the store's file open for the scan however soon a re-walk lets go of it.
        if (term.Length == 0 || store?.AddRef() is not { } lease)
        {
            ApplyMatches([], preserveCursor: false);
            return;
        }

        _matchLabel.Text = "";
        var cts = _findCts = new CancellationTokenSource();
        _findTask = RunFindAsync(term, lease, _findVersion, debounce ? _findDebounce : TimeSpan.Zero, sameTerm, cts.Token);
    }

    private async Task RunFindAsync(string term, ICommitStore lease, int version, TimeSpan delay,
        bool preserveCursor, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct).ConfigureAwait(true);

            // The store owns the scan: an in-memory array loops; a disk-backed store streams its
            // search sidecar rather than paging every record. The scan itself cannot be
            // interrupted, so a superseded one finishes and its result is dropped.
            var search = _findSearchForTesting;
            var hits = await Task.Run(() => search is null ? lease.Search(term) : search(term, ct), ct).ConfigureAwait(true);
            if (version == _findVersion)
                ApplyMatches(hits, preserveCursor);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "graph", "Find failed", ex);
            if (version == _findVersion)
                ApplyMatches([], preserveCursor: false);
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>Shows a search result. A result equal to the matches already shown, for the same
    /// text, leaves the cursor and the scroll position where they are.</summary>
    private void ApplyMatches(IReadOnlyList<int> hits, bool preserveCursor)
    {
        var shown = _matches.Rows;
        var same = preserveCursor && hits.Count == shown.Count;
        for (var i = 0; same && i < hits.Count; i++)
            same = hits[i] == shown[i];

        if (!same)
        {
            _matches.Reset(hits);
            if (_matches.HasMatches)
                ScrollRowIntoView(_matches.Rows[0]);
        }

        UpdateMatchLabel();
        InvalidateVisual();
    }

    /// <summary>Supersedes any search in flight: its result will be dropped and its token is
    /// cancelled.</summary>
    private void CancelFind()
    {
        _findVersion++;
        var cts = _findCts;
        _findCts = null;
        if (cts is null)
            return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
        cts.Dispose();
    }

    /// <summary>Forgets the search and its matches, for a control that is switching or releasing
    /// its repository.</summary>
    private void ResetFind()
    {
        CancelFind();
        _findTerm = "";
        _findStore = null;
        _findRows = 0;
        _matches.Clear();
        UpdateMatchLabel();
    }

    private void StepMatch(int direction)
    {
        var row = _matches.Step(direction);
        // Matches from a layout that has since been replaced can point past the new rows until
        // the new search lands.
        if (row < 0 || _layout is not { } stepLayout || row >= stepLayout.Layout.RowCount)
            return;
        ScrollRowIntoView(row);
        _state?.SetSelectedCommit(_layout!.Layout.Commits[row].Id);
        UpdateMatchLabel();
        InvalidateVisual();
    }

    private void UpdateMatchLabel() =>
        _matchLabel.Text = _matches.Label(!string.IsNullOrEmpty(_searchBox.Text));

    private void ScrollRowIntoView(int row)
    {
        var top = row * _rowHeight;
        var viewHeight = Math.Max(0, Bounds.Height - RowsTop);
        if (top < _scrollOffset)
            _scrollOffset = top;
        else if (top + _rowHeight > _scrollOffset + viewHeight)
            _scrollOffset = top + _rowHeight - viewHeight;
        _scrollOffset = Math.Clamp(_scrollOffset, 0, Math.Max(0, _scrollBar.Maximum));
        _scrollBar.Value = _scrollOffset;
        NoteViewportBottom();
    }

    private void ApplyQueryFromBar()
    {
        var scope = (_scopeBox.SelectedItem as ComboBoxItem)?.Tag is GraphRefScope s ? s : GraphRefScope.AllRefs;
        var path = string.IsNullOrWhiteSpace(_pathBox.Text) ? null : _pathBox.Text.Trim().Replace('\\', '/');
        var next = _query with
        {
            RefScope = scope,
            PathFilter = path,
        };
        if (next == _query)
            return;
        _query = next;
        if (_context is { } ctx)
            _ = ComputeLayoutAsync(ctx);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            OnRightButtonPressed(e);
            return;
        }
        if (!props.IsLeftButtonPressed)
            return;

        Focus();
        // When the Home panel is the mounted detail view, the first click on the graph should
        // just dismiss Home and show the working-copy ("changes") panel — it should not also
        // select whatever commit happens to sit under the pointer. The two exceptions
        // (branch/tag pill, commit node dot) are handled where the row selection is applied.
        var leavingHome = _context is not null
            && !string.Equals(
                (_nav.CurrentRoute ?? "").Split('?', 2)[0].TrimStart('/'), "repo", StringComparison.Ordinal);
        if (_context is not null)
            _nav.RequestNavigate("repo");

        var pos = e.GetPosition(this);

        if (pos.Y < HeaderHeight)
        {
            var icon = HeaderIconAt(pos.X, pos.Y);
            if (icon == (GraphColumnKind.Message, 0))
            {
                ToggleQueryBar(!_queryBar.IsVisible);
                e.Handled = true;
                return;
            }
            if (icon == (GraphColumnKind.Refs, 0))
            {
                OpenRefSearchPopup();
                e.Handled = true;
                return;
            }
            if (icon == (GraphColumnKind.Graph, 0))
            {
                if (_workspace?.Focused is { } focused)
                {
                    _workspace.Focus(focused);
                    _nav.RequestNavigate("repo-settings");
                }
                e.Handled = true;
                return;
            }
            if (icon == (GraphColumnKind.Graph, 1))
            {
                ResetGraphView();
                e.Handled = true;
                return;
            }

            var b = BoundaryAt(pos.X);
            if (b >= 0)
            {
                if (e.ClickCount == 2)
                {
                    AutoFitColumn(_resolvedColumns[b].Kind);
                }
                else
                {
                    var leftIsFlex = _columns.FirstOrDefault(c => c.Kind == _resolvedColumns[b].Kind)?.IsFlex == true;
                    var target = leftIsFlex && b + 1 < _resolvedColumns.Length ? b + 1 : b;
                    _resizeInverted = target != b;
                    _resizing = true;
                    _resizeBoundary = b;
                    _resizeKind = _resolvedColumns[target].Kind;
                    _resizeStartX = pos.X;
                    _resizeStartWidth = _resolvedColumns[target].Width;
                    _preResizeColumns = _columns;
                    _capturedPointer = e.Pointer;
                    e.Pointer.Capture(this);
                }
                e.Handled = true;
            }
            else
            {
                _reorderCandidate = ColumnKindAt(pos.X);
                _preResizeColumns = _columns;
                _dragStart = pos;
                e.Handled = true;
            }
            return;
        }

        _dragStart = pos;
        _dragRow = RowAt(pos.Y);
        _dragging = false;
        CloseRefList();

        // The +N chip opens the row's context menu, where every ref it hides has a submenu.
        foreach (var (rect, chipRow, _) in _overflowChips)
        {
            if (rect.Contains(pos))
            {
                ShowGraphContextMenu(chipRow, null);
                e.Handled = true;
                return;
            }
        }

        if (_layout is not { } l || _dragRow < 0)
            return;

        if (e.ClickCount == 2 && PillAt(pos) is { } dpill
            && dpill.Deco.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch)
        {
            CheckoutFromBadge(dpill.Deco);
            e.Handled = true;
            return;
        }

        var id = l.Layout.Commits[_dragRow].Id;
        var mods = e.KeyModifiers;

        if (leavingHome && (mods & (KeyModifiers.Control | KeyModifiers.Shift)) == 0)
        {
            // Dismiss-Home click: keep the selection only when the pointer is on a branch/tag
            // pill or the commit's node dot; otherwise clear it so RepositoryView shows the
            // working-copy staging ("changes") panel rather than a commit's details.
            var onPill = PillAt(pos) is not null;
            var onNode = HitTestNode(pos, _dragRow);
            if (!onPill && !onNode)
            {
                _multiSelect.Clear();
                _state?.SetSelectedCommit(null);
                e.Handled = true;
                return;
            }
        }

        if ((mods & KeyModifiers.Control) != 0)
        {
            if (!_multiSelect.Remove(_dragRow))
                _multiSelect.Add(_dragRow);
            InvalidateVisual();
        }
        else if ((mods & KeyModifiers.Shift) != 0 && _selectedRow >= 0)
        {
            _multiSelect.Clear();
            for (var r = Math.Min(_selectedRow, _dragRow); r <= Math.Max(_selectedRow, _dragRow); r++)
                _multiSelect.Add(r);
            InvalidateVisual();
        }
        else
        {
            _multiSelect.Clear();
            _state?.SetSelectedCommit(id);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        Cursor = new Cursor(StandardCursorType.Arrow);

        if (_resizing)
        {
            _resizing = false;
            _resizeBoundary = -1;
            _resizeInverted = false;
            _manuallySized.Add(_resizeKind);
            _capturedPointer?.Capture(null);
            PersistColumns();
            return;
        }

        if (_reorderKind is { } moved)
        {
            var caret = _reorderCaret;
            _reorderKind = null;
            _reorderCandidate = null;
            _reorderCaret = -1;
            _capturedPointer?.Capture(null);
            ReorderColumn(moved, caret);
            return;
        }
        _reorderCandidate = null;

        if (_dragging && _dragRow >= 0 && _layout is { } l)
        {
            var target = PillAt(e.GetPosition(this));
            if (target is { } pill && pill.Deco.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch)
                ShowDropIntentMenu(l.Layout.Commits[_dragRow].Id, l.Layout.Commits[_dragRow].Id.ToShortString(7), pill.Deco.Label);
        }

        _dragging = false;
        _dragRow = -1;
    }

    private IReadOnlyList<GraphColumn>? _preResizeColumns;

    private void PersistColumns()
    {
        if (_context is not { } ctx)
            return;
        var cols = _columns;
        var pinned = _graphWidthPinned;
        _ = _repoLayout.UpdateAsync(ctx.RepositoryPath, l => l with { Columns = cols, GraphWidthPinned = pinned });
    }

    /// <summary>the width that shows every lane the layout uses, plus one lane of margin
    /// for the right-edge tick.</summary>
    private double NaturalGraphWidth(int lanes) => (lanes + 1) * LaneWidth;

    private void AutoFitColumn(GraphColumnKind kind)
    {
        if (_layout is not { } result)
            return;
        var layout = result.Layout;

        // Graph auto-fit stretches to show the whole graph at once — every lane plus one
        // lane of tick margin — bounded by the floor and the hand-edit sanity ceiling. (Without
        // this it measured an empty string and landed on its 150px floor.)
        if (kind == GraphColumnKind.Graph)
        {
            var floor = GraphColumnDefaults.MinWidthFor(GraphColumnKind.Graph);
            var fitWidth = Math.Clamp(NaturalGraphWidth(layout.TotalLaneCount), floor,
                GraphColumnDefaults.MaxWidthFor(GraphColumnKind.Graph));

            _columns = WithColumnWidth(_columns, kind, fitWidth);
            _graphWidthPinned = true;
            _zoomOverridesPin = false;
            PersistColumns();
            InvalidateArrange();
            InvalidateVisual();
            return;
        }

        var rowArea = Bounds.Height - RowsTop;
        var first = Math.Max(0, (int)(_scrollOffset / _rowHeight));
        var last = Math.Min(layout.RowCount - 1, (int)((_scrollOffset + rowArea) / _rowHeight));

        double widest = GraphColumnDefaults.MinWidthFor(kind);
        for (var row = first; row <= last; row++)
        {
            var m = layout.Commits[row];
            var s = kind switch
            {
                GraphColumnKind.Message => m.MessageShort,
                GraphColumnKind.Author => m.AuthorName,
                GraphColumnKind.Date => DateCell(CommitDateDisplay.Pick(m.AuthorWhen, m.CommitWhen, _showAuthorDate)),
                GraphColumnKind.Sha => m.Id.ToShortString(7),
                _ => "",
            };
            if (s.Length == 0)
                continue;
            widest = Math.Max(widest, new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                _typeface, RowTextSize, _text).Width + 20);
        }

        _columns = WithColumnWidth(_columns, kind, widest);
        if (kind == GraphColumnKind.Graph)
            _graphWidthPinned = true;
        PersistColumns();
        InvalidateArrange();
        InvalidateVisual();
    }

    private void OnRightButtonPressed(PointerPressedEventArgs e)
    {
        // The message-column hover tooltip is a separate popup layer from the context menu and
        // doesn't auto-dismiss just because a menu opened on top of it — right-clicking a row
        // under an already-showing tooltip left the two overlapping. Close it unconditionally
        // before any of the menus below can open.
        ToolTip.SetIsOpen(this, false);
        _msgTipText = null;

        var pos = e.GetPosition(this);

        if (pos.Y < HeaderHeight)
        {
            ShowColumnMenu();
            e.Handled = true;
            return;
        }

        // A label, the row's +N overflow chip and the row itself all open the same menu for the
        // commit; a label only moves its own refs to the top.
        foreach (var (rect, chipRow, _) in _overflowChips)
        {
            if (rect.Contains(pos))
            {
                ShowGraphContextMenu(chipRow, null);
                e.Handled = true;
                return;
            }
        }

        var pill = PillAt(pos);
        if (pill is { } p)
        {
            ShowGraphContextMenu(p.Row, p.Deco);
            e.Handled = true;
            return;
        }

        var row = RowAt(pos.Y);
        if (row >= 0)
        {
            ShowGraphContextMenu(row, null);
            e.Handled = true;
        }
    }

    private int RowAt(double y)
    {
        if (_layout is not { } l)
            return -1;
        if (y < RowsTop)
            return -1;
        var row = (int)((y - RowsTop + _scrollOffset) / _rowHeight);
        return row >= 0 && row < l.Layout.RowCount ? row : -1;
    }

    private (Rect Rect, int Row, GraphDecoration Deco)? PillAt(Point p)
    {
        foreach (var pill in _pillRects)
            if (pill.Rect.Contains(p))
                return pill;
        return null;
    }

    /// <summary>True when <paramref name="p"/> lands on (or within a small grab margin of) the
    /// commit node dot drawn for <paramref name="row"/>.</summary>
    private bool HitTestNode(Point p, int row)
    {
        if (_layout is not { } l || row < 0 || row >= l.Layout.RowCount)
            return false;
        var cx = NodeX(l.Layout.Rows[row].LaneIndex, out _);
        var cy = RowY(row) + _rowHeight / 2;
        var dx = p.X - cx;
        var dy = p.Y - cy;
        const double grab = NodeRadius + 3;
        return dx * dx + dy * dy <= grab * grab;
    }

    private string? _lastTip;

    private void UpdateHoverTooltip(Point pos)
    {
        if (_openMenu is not null)
        {
            CancelHoverDwell();
            return;
        }

        var target = ResolveHoverTarget(pos);

        // The sibling-ref badge opens a popup, not a tooltip — the intent is to see the rest of
        // the refs on this commit as soon as the badge is under the pointer, so it stays
        // immediate rather than joining the 1s tooltip dwell below.
        if (target is { Kind: HoverTargetKind.RefListBadge })
        {
            _hoverDwell.Reset();
            _hoverDwellTimer.Stop();
            ShowHoverTarget(target.Value);
            return;
        }

        UpdateHoverDwell(target);
    }

    private void HandleHoverDwellElapsed()
    {
        _hoverDwellTimer.Stop();
        if (_hoverDwell.Elapse() is { } target)
        {
            if (IsPointerOver && ResolveHoverTarget(_lastPointerPos) == target)
            {
                ShowHoverTarget(target);
            }
        }
    }

    private void UpdateHoverDwell(HoverTarget? target)
    {
        var step = _hoverDwell.Move(target);
        switch (step)
        {
            case DwellStep.Keep:
                break;
            case DwellStep.Restart:
                ToolTip.SetIsOpen(this, false);
                _lastTip = null;
                _msgTipText = null;
                _hoverMsgId = null;
                CancelHoverFetch();
                _hoverDwellTimer.Stop();
                _hoverDwellTimer.Start();
                break;
            case DwellStep.Clear:
                ToolTip.SetIsOpen(this, false);
                _lastTip = null;
                _msgTipText = null;
                _hoverMsgId = null;
                CancelHoverFetch();
                _hoverDwellTimer.Stop();
                break;
        }
    }

    private void CancelHoverDwell()
    {
        _hoverDwell.Reset();
        _hoverDwellTimer.Stop();
        ToolTip.SetIsOpen(this, false);
        _lastTip = null;
        _msgTipText = null;
        _hoverMsgId = null;
        CancelHoverFetch();
    }

    private void CancelHoverFetch()
    {
        if (_hoverFetchCts is { } cts)
        {
            _hoverFetchCts = null;
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
    }

    private HoverTarget? ResolveHoverTarget(Point pos)
    {
        for (var i = 0; i < _iconRects.Count; i++)
        {
            if (_iconRects[i].Rect.Contains(pos))
                return new HoverTarget(HoverTargetKind.Icon, RowAt(pos.Y), i);
        }

        for (var i = 0; i < _pillRects.Count; i++)
        {
            var p = _pillRects[i];
            if (p.Rect.Contains(pos) && p.Deco.SiblingsOrNull is { Length: > 0 })
                return new HoverTarget(HoverTargetKind.RefListBadge, p.Row, i);
        }

        for (var i = 0; i < _pillRects.Count; i++)
        {
            var p = _pillRects[i];
            if (p.Rect.Contains(pos))
                return new HoverTarget(HoverTargetKind.Pill, p.Row, i);
        }

        for (var i = 0; i < _overflowChips.Count; i++)
        {
            if (_overflowChips[i].Rect.Contains(pos))
                return new HoverTarget(HoverTargetKind.Overflow, _overflowChips[i].Row, i);
        }

        if (_resolvedColumns is { Length: > 0 } cols)
        {
            for (var i = 0; i < cols.Length; i++)
            {
                var col = cols[i];
                if (col.Kind == GraphColumnKind.Message)
                {
                    var colX = col.Left - _hScrollOffset;
                    if (pos.X >= colX && pos.X < colX + col.Width && pos.Y >= RowsTop && pos.Y < Bounds.Height)
                    {
                        var row = (int)((pos.Y - RowsTop + _scrollOffset) / _rowHeight);
                        if (_layout is { } lay && row >= 0 && row < lay.Layout.Commits.Count)
                        {
                            return new HoverTarget(HoverTargetKind.Message, row, -1);
                        }
                    }
                    break;
                }
            }
        }

        return null;
    }

    private void ShowHoverTarget(HoverTarget target)
    {
        if (_openMenu is not null)
            return;

        switch (target.Kind)
        {
            case HoverTargetKind.Icon:
                if (target.Index >= 0 && target.Index < _iconRects.Count)
                    ShowSimpleTooltip(_iconRects[target.Index].Tip);
                break;

            case HoverTargetKind.RefListBadge:
                if (target.Index >= 0 && target.Index < _pillRects.Count)
                {
                    var p = _pillRects[target.Index];
                    OpenRefListFor(p.Deco, p.Rect);
                    ToolTip.SetIsOpen(this, false);
                    _lastTip = null;
                }
                break;

            case HoverTargetKind.Pill:
                if (target.Index >= 0 && target.Index < _pillRects.Count)
                {
                    var d = _pillRects[target.Index].Deco;
                    var tip = d.Type == DecorationType.Head ? "HEAD" : d.Label;
                    ShowSimpleTooltip(tip);
                }
                break;

            case HoverTargetKind.Overflow:
                if (target.Index >= 0 && target.Index < _overflowChips.Count)
                {
                    var hidden = _overflowChips[target.Index].Hidden;
                    var tip = $"{hidden.Count} more:\n" + string.Join("\n", hidden.Select(d => d.Type == DecorationType.Head ? "HEAD" : d.Label));
                    ShowSimpleTooltip(tip);
                }
                break;

            case HoverTargetKind.Message:
                if (_layout is { } lay && target.Row >= 0 && target.Row < lay.Layout.Commits.Count)
                {
                    var meta = lay.Layout.Commits[target.Row];
                    ShowMessageTooltip(meta.Id, meta.MessageShort);
                }
                break;
        }
    }

    private void ShowSimpleTooltip(string tip)
    {
        if (!string.Equals(_lastTip, tip, StringComparison.Ordinal))
        {
            _lastTip = tip;
            _msgTipText = null;
            _hoverMsgId = null;
            ToolTip.SetTip(this, tip);
            ToolTip.SetIsOpen(this, false);
            ToolTip.SetIsOpen(this, true);
        }
        else
        {
            ToolTip.SetIsOpen(this, true);
        }
    }

    // Graph tooltips open under the pointer, or flip above it, and never slide vertically: a
    // tip that ends up over the pointer makes the graph get PointerExited and closes at once.
    // The gaps live in the anchor rect rather than the offset because the positioner adds the
    // offset unflipped, so a flipped Pointer-placed tip would land back over the pointer.
    private const double TipGapAbove = 8;
    private const double TipGapBelow = 20;
    private const double TipScreenMargin = 8;

    internal const PopupPositionerConstraintAdjustment TipConstraintAdjustment =
        PopupPositionerConstraintAdjustment.FlipX | PopupPositionerConstraintAdjustment.FlipY
        | PopupPositionerConstraintAdjustment.SlideX | PopupPositionerConstraintAdjustment.ResizeY;

    // Message tooltip geometry. The chrome must match the ToolTip style in App.axaml (padding
    // 8,4) and Fluent's ToolTip theme (border 1, content max width 320, font size 12).
    internal const double MessageTipMaxWidth = 320;
    internal const double MessageTipMaxHeight = 500;
    private const double MessageTipFontSize = 12;
    private const double MessageTipHintFontSize = 11;
    private const double MessageTipHintGap = 4;
    private const double TipPadH = 8;
    private const double TipPadV = 4;
    private const double TipBorder = 1;
    internal const double MessageTipTextWidth = MessageTipMaxWidth - 2 * TipPadH - 2 * TipBorder;
    internal const double MessageTipChromeHeight = 2 * TipPadV + 2 * TipBorder;
    internal const string MessageTipHint = "Full message in the commit panel";

    private (string Text, double Budget, int MaxLines, bool Truncated)? _msgTipMeasure;
    private double _msgTipBudget = double.NaN;

    /// <summary>The tooltip anchor around a pointer at <paramref name="p"/> (TopLevel
    /// coordinates): from <see cref="TipGapAbove"/> above it to <see cref="TipGapBelow"/> below.</summary>
    internal static Rect TipAnchorRect(Point p) =>
        new(p.X, p.Y - TipGapAbove, 1, TipGapAbove + TipGapBelow);

    private void PlaceTip(CustomPopupPlacement placement)
    {
        if (TopLevel.GetTopLevel(this) is not { } top || this.TranslatePoint(_lastPointerPos, top) is not { } p)
            return;
        placement.AnchorRectangle = TipAnchorRect(p);
        placement.Anchor = PopupAnchor.BottomLeft;
        placement.Gravity = PopupGravity.BottomRight;
        placement.Offset = default;
        placement.ConstraintAdjustment = TipConstraintAdjustment;
    }

    /// <summary>The message tooltip's outer height: at most <see cref="MessageTipMaxHeight"/>,
    /// and no taller than the roomier side of the pointer's anchor less a screen margin. Room
    /// is infinite when the screen can't be resolved.</summary>
    internal static double MessageTipOuterHeight(double roomBelow, double roomAbove) =>
        Math.Min(MessageTipMaxHeight, Math.Max(roomBelow, roomAbove) - TipScreenMargin);

    private double MessageTipContentBudget()
    {
        double below = double.PositiveInfinity, above = double.PositiveInfinity;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Screens?.ScreenFromVisual(this) is { Scaling: > 0 } screen)
            {
                var pointerY = this.PointToScreen(_lastPointerPos).Y / screen.Scaling;
                var work = screen.WorkingArea;
                below = work.Bottom / screen.Scaling - (pointerY + TipGapBelow);
                above = pointerY - TipGapAbove - work.Y / screen.Scaling;
            }
        }
        catch (InvalidOperationException)
        {
            // Not attached to a visual root: fall back to the fixed cap.
        }
        return MessageTipOuterHeight(below, above) - MessageTipChromeHeight;
    }

    /// <summary>How many wrapped lines of <paramref name="text"/> fit in
    /// <paramref name="budget"/> with the hint line below them; 0 when the whole text fits.
    /// Shapes text, so it runs when a tip is shown and never from the render path.</summary>
    private (int MaxLines, bool Truncated) MeasureMessageTip(string text, double budget)
    {
        if (_msgTipMeasure is { } m && string.Equals(m.Text, text, StringComparison.Ordinal) && m.Budget == budget)
            return (m.MaxLines, m.Truncated);

        var typeface = new Typeface(FontFamily.Default);
        int maxLines = 0;
        var truncated = false;
        using (var layout = new TextLayout(text, typeface, MessageTipFontSize, null,
                   textWrapping: TextWrapping.Wrap, maxWidth: MessageTipTextWidth))
        {
            var total = 0.0;
            foreach (var line in layout.TextLines)
                total += line.Height;

            if (Math.Ceiling(total) > budget)
            {
                // Layout rounding sizes each TextBlock up to a whole pixel, so compare ceilings.
                double hintHeight;
                using (var hint = new TextLayout(MessageTipHint, typeface, MessageTipHintFontSize, null,
                           textWrapping: TextWrapping.Wrap, maxWidth: MessageTipTextWidth))
                    hintHeight = Math.Ceiling(hint.Height) + MessageTipHintGap;

                var avail = budget - hintHeight;
                var used = 0.0;
                foreach (var line in layout.TextLines)
                {
                    if (Math.Ceiling(used + line.Height) > avail)
                        break;
                    used += line.Height;
                    maxLines++;
                }
                maxLines = Math.Max(1, maxLines);
                truncated = true;
            }
        }

        _msgTipMeasure = (text, budget, maxLines, truncated);
        return (maxLines, truncated);
    }

    /// <summary>Show the full commit message for a Message-column cell, reflowed and capped in
    /// height. The message is fetched lazily via <see cref="IGitGateway.GetCommitDetailAsync"/>
    /// and cached; until it resolves the subject alone is shown.</summary>
    private void ShowMessageTooltip(GitObjectId id, string subject)
    {
        string text;
        if (TryGetCachedMessage(id, out var message))
        {
            text = CommitMessageReflow.Reflow(message);
        }
        else
        {
            text = CommitMessageReflow.Reflow(subject);
            CancelHoverFetch();
            if (_context is { } ctx)
            {
                _hoverFetchCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Lifetime);
                _ = FetchMessageAsync(id, subject, _hoverFetchCts.Token);
            }
        }

        _hoverMsgId = id;
        var budget = MessageTipContentBudget();
        if (string.Equals(_msgTipText, text, StringComparison.Ordinal) && _msgTipBudget == budget)
        {
            ToolTip.SetIsOpen(this, true);
            return;
        }

        _msgTipText = text;
        _msgTipBudget = budget;
        _lastTip = null;

        var (maxLines, truncated) = MeasureMessageTip(text, budget);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = FontFamily.Default,
            FontSize = MessageTipFontSize,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = MessageTipMaxWidth,
            TextTrimming = truncated ? TextTrimming.CharacterEllipsis : TextTrimming.None,
            MaxLines = maxLines,
        });
        if (truncated)
        {
            var hint = new TextBlock
            {
                Text = MessageTipHint,
                FontFamily = FontFamily.Default,
                FontSize = MessageTipHintFontSize,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = MessageTipMaxWidth,
                Margin = new Thickness(0, MessageTipHintGap, 0, 0),
            };
            Dyn(hint, TextBlock.ForegroundProperty, "ColorTextMuted");
            panel.Children.Add(hint);
        }

        ToolTip.SetTip(this, panel);
        ToolTip.SetIsOpen(this, false);
        ToolTip.SetIsOpen(this, true);
    }

    private async Task FetchMessageAsync(GitObjectId id, string subject, CancellationToken ct)
    {
        if (_context is not { } ctx || _msgCache.ContainsKey(id) || id.Equals(_msgFetchInFlight))
            return;
        _msgFetchInFlight = id;
        try
        {
            ct.ThrowIfCancellationRequested();
            var detail = await ctx.Gateway.GetCommitDetailAsync(id, ct).ConfigureAwait(true);
            CacheMessage(id, detail.Message);
            if (id.Equals(_hoverMsgId) && IsPointerOver && !ct.IsCancellationRequested)
                ShowMessageTooltip(id, subject);
        }
        catch { /* best-effort tooltip */ }
        finally
        {
            if (id.Equals(_msgFetchInFlight))
                _msgFetchInFlight = null;
        }
    }
}
