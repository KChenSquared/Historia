namespace Historia.Controls;

public sealed partial class CommitGraphControl
{
    // ── folded-badge ref list ──

    private bool RefListBadgeHovered()
    {
        if (_refListFor is not { } d || !IsPointerOver)
            return false;
        foreach (var p in _pillRects)
            if (p.Rect.Contains(_lastPointerPos) && p.Deco.Equals(d))
                return true;
        return false;
    }

    private bool RefListHovered =>
        _refList.IsPointerOver || _refList.Child?.IsPointerOver == true;

    private void CloseRefList()
    {
        _refListCloseTimer?.Stop();
        _refList.IsOpen = false;
        _refListFor = null;
        CloseRefSearch();
    }

    // ── header Refs icon: ranked ref jump popup ──

    private void OpenRefSearchPopup()
    {
        var rect = GraphColumnInteraction.HeaderIconRectFor(
            _resolvedColumns, GraphColumnKind.Refs, _hScrollOffset, HeaderHeight, HeaderIconSize, HeaderIconInset);
        if (rect is not { } r)
            return;

        _refSearchBox.Text = string.Empty;
        UpdateRefSearchMatches();
        _refSearch.PlacementRect = new Rect(r.X, r.Y, r.Width, r.Height);
        _refSearch.IsOpen = true;
        _refSearchBox.Focus();
    }

    private void CloseRefSearch()
    {
        _refSearch.IsOpen = false;
    }

    private void UpdateRefSearchMatches()
    {
        _refSearchItems.Children.Clear();
        _refSearchRows.Clear();
        _refSearchSelectedIndex = -1;

        var text = _refSearchBox.Text;
        if (string.IsNullOrWhiteSpace(text) || _state is null)
            return;

        var candidates = new List<RefCandidate>();
        foreach (var b in _state.Branches)
            candidates.Add(new RefCandidate(b.Name, b.IsRemote ? "cloud" : "hard_drive", b.TipCommitId));
        foreach (var t in _state.Tags)
            candidates.Add(new RefCandidate(t.Name, "sell", t.TargetCommitId));

        var query = FuzzyScorer.PrepareQuery(text);
        var ranked = RefSearch.Rank(candidates, c => c.Label, query, limit: 10);

        foreach (var hit in ranked)
        {
            var row = BuildRefSearchRow(hit.Item);
            _refSearchRows.Add((hit.Item, row));
            _refSearchItems.Children.Add(row);
        }

        if (_refSearchRows.Count > 0)
            HighlightRefSearchRow(0);
    }

    private Border BuildRefSearchRow(RefCandidate candidate)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(Historia.Views.ViewKit.Icon(candidate.Icon, 12, "ColorTextMuted"));
        var label = new TextBlock { Text = candidate.Label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Dyn(label, TextBlock.ForegroundProperty, "ColorText");
        content.Children.Add(label);

        var row = new Border
        {
            Child = content,
            Padding = new Thickness(6, 4),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        row.PointerEntered += (_, _) => HighlightRefSearchRow(_refSearchRows.FindIndex(r => ReferenceEquals(r.Row, row)));
        row.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            SelectRefSearchCandidate(candidate);
        };
        return row;
    }

    private void HighlightRefSearchRow(int index)
    {
        for (var i = 0; i < _refSearchRows.Count; i++)
            _refSearchRows[i].Row.Background = i == index ? _hoverFill : Brushes.Transparent;
        _refSearchSelectedIndex = index;
    }

    private void SelectRefSearchCandidate(RefCandidate candidate)
    {
        if (candidate.CommitId is not { } id)
        {
            CloseRefSearch();
            return;
        }
        _state?.SetSelectedCommit(id);
        _state?.RequestRevealCommit(id);
        CloseRefSearch();
        Focus();
    }

    private void OnRefSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseRefSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            if (_refSearchRows.Count > 0)
                HighlightRefSearchRow(Math.Min(_refSearchSelectedIndex + 1, _refSearchRows.Count - 1));
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            if (_refSearchRows.Count > 0)
                HighlightRefSearchRow(Math.Max(_refSearchSelectedIndex - 1, 0));
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (_refSearchSelectedIndex >= 0 && _refSearchSelectedIndex < _refSearchRows.Count)
                SelectRefSearchCandidate(_refSearchRows[_refSearchSelectedIndex].Candidate);
            e.Handled = true;
        }
    }

    private void OpenRefListFor(GraphDecoration head, Rect badgeRect)
    {
        if (!(_refList.IsOpen && _refListFor is { } cur && cur.Equals(head)))
        {
            _refListFor = head;
            _refList.Child = BuildRefListContent(head);
            _refList.PlacementTarget = this;
            _refList.PlacementRect = badgeRect;
            _refList.Placement = PlacementMode.BottomEdgeAlignedLeft;
            _refList.IsOpen = true;
        }
        if (_refListCloseTimer is { IsEnabled: false })
            _refListCloseTimer.Start();
    }

    private Control BuildRefListContent(GraphDecoration head)
    {
        var stack = new StackPanel { Margin = new Thickness(4) };
        stack.Children.Add(RefListRow(head.Type, head.Type == DecorationType.Head ? "HEAD" : head.Label,
            head.IsLocal, head.RemotesOrNull, head.ColorIndex, head.RowIndex));
        foreach (var s in head.Siblings)
            stack.Children.Add(RefListRow(s.Type, s.Label, s.IsLocal, s.Remotes, head.ColorIndex, head.RowIndex));

        var shell = new Border
        {
            Child = stack,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(2),
            BoxShadow = BoxShadows.Parse("0 2 10 0 #40000000"),
        };
        Dyn(shell, Border.BackgroundProperty, "ColorSurface");
        Dyn(shell, Border.BorderBrushProperty, "ColorBorder");
        return shell;
    }

    private Border RefListRow(DecorationType type, string label, bool isLocal, string[]? remotes,
        int colorIndex, int row)
    {
        var checkoutable = type is DecorationType.LocalBranch or DecorationType.RemoteBranch or DecorationType.Tag;

        var swatch = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = 4,
            Height = 14,
            RadiusX = 2,
            RadiusY = 2,
            Fill = new SolidColorBrush(ToColor(LaneRgb(colorIndex))),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var kind = new TextBlock
        {
            Text = type == DecorationType.Tag ? "tag" : type == DecorationType.Head ? "head" : isLocal ? "local" : "remote",
            FontSize = 10,
            Width = 44,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Dyn(kind, TextBlock.ForegroundProperty, "ColorTextMuted");
        var name = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Dyn(name, TextBlock.ForegroundProperty, "ColorText");
        if (remotes is { Length: > 0 })
            name.Text += $"  ({string.Join(", ", remotes)})";

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(swatch);
        left.Children.Add(new Border { Width = 6 });
        left.Children.Add(kind);
        left.Children.Add(name);

        // Same local/remote status glyphs as the badge itself, right-aligned here so
        // the row reads consistently with the badge that this popup expanded from.
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (type is DecorationType.LocalBranch or DecorationType.RemoteBranch)
        {
            if (isLocal)
                icons.Children.Add(Historia.Views.ViewKit.Icon("hard_drive", 12, "ColorTextMuted"));
            if (remotes is { Length: > 0 } && _remoteNumbering.IsActive)
            {
                foreach (var r in remotes.OrderBy(r => _remoteNumbering.NumberOf(r) is > 0 and var n ? n : int.MaxValue))
                {
                    var cloud = RemoteCloudGlyph.CreateIcon(_remoteNumbering.NumberOf(r), 14, "ColorTextMuted", "ColorSurface");
                    ToolTip.SetTip(cloud, _remoteNumbering.TooltipOf(r));
                    icons.Children.Add(cloud);
                }
            }
            else if (remotes is { Length: > 0 })
                icons.Children.Add(Historia.Views.ViewKit.Icon("cloud", 12, "ColorTextMuted"));
        }

        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(icons, 1);
        content.Children.Add(left);
        content.Children.Add(icons);

        var rowBorder = new Border
        {
            Child = content,
            Padding = new Thickness(6, 4, 10, 4),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = new Cursor(checkoutable ? StandardCursorType.Hand : StandardCursorType.Arrow),
        };
        rowBorder.PointerEntered += (_, _) => Dyn(rowBorder, Border.BackgroundProperty, "ColorSurface2");
        rowBorder.PointerExited += (_, _) => rowBorder.Background = Brushes.Transparent;
        rowBorder.AddHandler(PointerPressedEvent, (_, e) =>
        {
            var props = e.GetCurrentPoint(rowBorder).Properties;
            if (props.IsRightButtonPressed)
            {
                // Same context menu as right-clicking this ref's badge directly on the graph.
                e.Handled = true;
                ShowGraphContextMenu(row, new GraphDecoration
                {
                    RowIndex = row,
                    Type = type,
                    Label = label,
                    ColorIndex = colorIndex,
                    IsLocal = isLocal,
                    RemotesOrNull = remotes,
                });
                return;
            }

            e.Handled = true;
            if (e.ClickCount == 2 && checkoutable)
            {
                CheckoutRefEntry(type, label, isLocal, remotes);
                CloseRefList();
            }
            else if (_layout is { } l && row >= 0 && row < l.Layout.RowCount)
            {
                // Single click selects/reveals the tip commit but leaves the popup open, so a
                // double click on the same row can still land and check the ref out. The close
                // timer takes it down once the pointer leaves the popup and its badge.
                _state?.SetSelectedCommit(l.Layout.Commits[row].Id);
            }
        }, RoutingStrategies.Tunnel);
        return rowBorder;
    }
}
