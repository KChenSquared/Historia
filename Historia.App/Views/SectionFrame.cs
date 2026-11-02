namespace Historia.Views;

/// <summary>
/// Shared frame and drag-resize machinery for section panels (Changes/Staged in staging,
/// Files in commit detail, and the staging commit-message box).
/// </summary>
internal static class SectionFrame
{
    public const double HeaderGap = 2;
    public const double HeaderRowHeight = 32;
    public const double DividerBottomGap = 6;
    public const double InnerSide = 8;
    public const double InnerBottom = 8;
    public const double HandleHeight = 7;
    public const double TopMinHeight = 150;
    public const double BottomMinHeight = 150;
    public const double SplitAreaMinHeight = TopMinHeight + HandleHeight + BottomMinHeight;
    public const double DefaultTopHeight = 260;

    /// <summary>The height of the repository top bar: one header row of content plus the header gap above and below.</summary>
    public const double TopBarHeight = 30 + 2 * HeaderGap;

    /// <summary>The margin of the branch label and ahead/behind count inside the top bar.</summary>
    public static readonly Thickness TopBarBranchMargin = new(12, HeaderGap);

    /// <summary>The inner margin of the commit detail panel's top section, which sits a little lower than a section frame's own.</summary>
    public static readonly Thickness DetailTopInnerMargin = new(InnerSide, HeaderGap + 4, InnerSide, InnerBottom);

    // UI-thread only.
    private static readonly Dictionary<string, double> SplitHeightByRepo = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, double> BoxHeightByRepo = new(StringComparer.Ordinal);

    public static double GetSplitHeight(string repoId)
    {
        return SplitHeightByRepo.TryGetValue(repoId, out var h) ? h : DefaultTopHeight;
    }

    public static void SetSplitHeight(string repoId, double h)
    {
        SplitHeightByRepo[repoId] = h;
    }

    /// <summary>The height the staging commit-message box was last given for a repository, or
    /// <paramref name="fallback"/> when it was never resized.</summary>
    public static double GetBoxHeight(string repoId, double fallback)
    {
        return BoxHeightByRepo.TryGetValue(repoId, out var h) ? h : fallback;
    }

    public static void SetBoxHeight(string repoId, double h)
    {
        BoxHeightByRepo[repoId] = h;
    }

    public static void ApplySplitHeight(Grid grid, RowDefinition topRow, string repoId)
    {
        var stored = GetSplitHeight(repoId);
        var clamped = ClampTopHeight(stored, grid.Bounds.Height, TopMinHeight, BottomMinHeight);
        topRow.Height = new GridLength(clamped, GridUnitType.Pixel);
    }

    public static double ClampTopHeight(double proposed, double available, double topMin, double bottomMin)
    {
        return Math.Clamp(proposed, topMin, Math.Max(topMin, available - HandleHeight - bottomMin));
    }

    public static double ClampGrowHeight(double proposed, double min, double current, double donorHeight, double donorMin)
    {
        return Math.Clamp(proposed, min, Math.Max(min, current + donorHeight - donorMin));
    }

    private static void AttachVerticalDrag(Control handle, Visual relativeTo, Action begin, Action<double> moved, Action ended)
    {
        handle.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);

        double startY = 0;
        bool isDragging = false;

        handle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
                return;

            e.Pointer.Capture(handle);
            startY = e.GetPosition(relativeTo).Y;
            isDragging = true;
            begin();
            e.Handled = true;
        };

        handle.PointerMoved += (_, e) =>
        {
            if (!isDragging)
                return;

            var currentY = e.GetPosition(relativeTo).Y;
            var delta = currentY - startY;
            moved(delta);
            e.Handled = true;
        };

        handle.PointerReleased += (_, e) =>
        {
            if (!isDragging)
                return;

            isDragging = false;
            e.Pointer.Capture(null);
            ended();
            e.Handled = true;
        };

        handle.PointerCaptureLost += (_, e) =>
        {
            if (!isDragging)
                return;

            isDragging = false;
            ended();
            e.Handled = true;
        };
    }

    public static (double Top, double Lender) SplitWithLender(double proposed, double available, double lenderStart, double lenderMin)
    {
        var maxTop = Math.Max(TopMinHeight, available - HandleHeight - BottomMinHeight);
        if (proposed <= maxTop)
            return (ClampTopHeight(proposed, available, TopMinHeight, BottomMinHeight), lenderStart);

        var lent = Math.Min(proposed - maxTop, Math.Max(0, lenderStart - lenderMin));
        return (maxTop + lent, lenderStart - lent);
    }

    public static void AttachRowResize(
        Control handle,
        Grid grid,
        RowDefinition topRow,
        Action<double> committed,
        (Func<(double Start, double Min)> Begin, Action<double> Apply)? lender = null)
    {
        double startHeight = 0;
        double startAvailable = 0;
        double lenderStart = 0;
        double lenderMin = 0;
        double lenderApplied = 0;

        AttachVerticalDrag(
            handle,
            grid,
            begin: () =>
            {
                startHeight = topRow.Height.IsAbsolute ? topRow.Height.Value : DefaultTopHeight;
                if (lender is { } l)
                {
                    startAvailable = grid.Bounds.Height;
                    (lenderStart, lenderMin) = l.Begin();
                    lenderApplied = lenderStart;
                }
            },
            moved: delta =>
            {
                if (lender is { } l)
                {
                    var lent = lenderStart - lenderApplied;
                    var (top, lenderHeight) = SplitWithLender(startHeight + delta, startAvailable + lent, lenderApplied, lenderMin);
                    // The lender only ever gives room up during a drag.
#pragma warning disable S1244 // exact compare against the stored copy
                    if (lenderHeight != lenderApplied)
#pragma warning restore S1244
                    {
                        l.Apply(lenderHeight);
                        lenderApplied = lenderHeight;
                    }
                    topRow.Height = new GridLength(top, GridUnitType.Pixel);
                    committed(top);
                }
                else
                {
                    var proposed = startHeight + delta;
                    var clamped = ClampTopHeight(proposed, grid.Bounds.Height, TopMinHeight, BottomMinHeight);
                    topRow.Height = new GridLength(clamped, GridUnitType.Pixel);
                }
            },
            ended: () => committed(topRow.Height.Value));
    }

    public static void AttachBottomResize(
        Control handle,
        Visual relativeTo,
        Func<(double Start, double Min, double Max)> begin,
        Action<double> apply,
        Action<double> committed)
    {
        double start = 0;
        double min = 0;
        double max = 0;
        double lastApplied = 0;

        AttachVerticalDrag(
            handle,
            relativeTo,
            begin: () =>
            {
                (start, min, max) = begin();
                lastApplied = start;
            },
            moved: delta =>
            {
                var proposed = start - delta;
                lastApplied = Math.Clamp(proposed, min, max);
                apply(lastApplied);
            },
            ended: () => committed(lastApplied));
    }

    public static Border Build(Control left, Control? center, Control? right, Control body,
        Thickness border, double minHeight, out Control dividerHandle, bool horizontalScroll = false, bool scrollBody = true)
    {
        var head = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            MinHeight = HeaderRowHeight,
            Margin = new Thickness(0, 0, 0, HeaderGap)
        };
        Grid.SetColumn(left, 0);
        left.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(left);

        if (center is not null)
        {
            Grid.SetColumn(center, 1);
            center.HorizontalAlignment = HorizontalAlignment.Center;
            center.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(center);
        }

        if (right is not null)
        {
            Grid.SetColumn(right, 2);
            right.HorizontalAlignment = HorizontalAlignment.Right;
            right.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(right);
        }

        var strip = new Border
        {
            Height = HandleHeight,
            Background = Brushes.Transparent,
            ZIndex = 1,
            Margin = new Thickness(0, -3, 0, DividerBottomGap - 3),
        };
        var line = new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Center,
        }.Dyn(Border.BackgroundProperty, "ColorBorder");
        strip.Child = line;
        dividerHandle = strip;

        // A body that scrolls and virtualizes itself (a list) goes in unwrapped; an outer scroller
        // would measure it at unbounded height and realize every item.
        Control scroll = scrollBody
            ? new ScrollViewer
            {
                Content = body,
                HorizontalScrollBarVisibility = horizontalScroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
            }
            : body;

        var inner = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(InnerSide, HeaderGap, InnerSide, InnerBottom)
        };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(strip, Dock.Top);
        inner.Children.Add(head);
        inner.Children.Add(strip);
        inner.Children.Add(scroll);

        return new Border
        {
            Child = inner,
            MinHeight = minHeight,
            BorderThickness = border,
        }.Dyn(Border.BorderBrushProperty, "ColorBorder");
    }
}
