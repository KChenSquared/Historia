namespace Historia.Controls;

/// <summary>What the toggle gutter draws on a line.</summary>
public enum ConflictToggleGlyph { None, Unchecked, Partial, Checked }

/// <summary>How the Result pane paints a line of a managed conflict.</summary>
[Flags]
public enum ConflictRowVisual
{
    None = 0,
    Ours = 1,
    Theirs = 2,
    Added = 4,
    Excluded = 8,
    RegionTop = 16,
    RegionBottom = 32,
    Deletion = 64,
}

/// <summary>
/// Check-box gutter for conflict panes: one box per line the owner says has one, clicked to
/// toggle. The box, check and dash are drawn from geometries built once per size and translated
/// per line, with palette pens and brushes resolved once per palette, so a repeat frame creates
/// nothing.
/// </summary>
public sealed class ConflictToggleMargin : AbstractMargin
{
    public const double MarginWidth = 16;

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private readonly Func<int, ConflictToggleGlyph> _getGlyph;
    private readonly Func<int, string?>? _getTip;

    private double _boxSize;
    private StreamGeometry? _checkGeometry;
    private StreamGeometry? _dashGeometry;
    private IBrush? _boxBrush;
    private IBrush? _accentBrush;
    private IPen? _outlinePen;
    private IPen? _accentPen;
    private IPen? _markPen;
    private int _hoverLine = -1;

    /// <summary>Raised with the 0-based document line whose box was clicked.</summary>
    public event EventHandler<int>? ToggleClicked;

    internal int PoolMissesForTesting { get; private set; }
    internal int RenderCountForTesting { get; private set; }
    internal IBrush? BoxBrushForTesting => _boxBrush;
    internal void ResetPoolCountersForTesting() => PoolMissesForTesting = 0;

    public ConflictToggleMargin(Func<int, ConflictToggleGlyph> getGlyph, Func<int, string?>? getTip = null)
    {
        _getGlyph = getGlyph;
        _getTip = getTip;
        ClipToBounds = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        PaletteBrushes.Changed += OnPaletteChanged;
        _boxBrush = null;
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        PaletteBrushes.Changed -= OnPaletteChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPaletteChanged(object? sender, EventArgs e)
    {
        _boxBrush = null;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(MarginWidth, 0);

    public override void Render(DrawingContext context)
    {
        RenderCountForTesting++;
        var view = TextView;
        if (view is not { VisualLinesValid: true })
        {
            return;
        }

        EnsureResources(Math.Clamp(Math.Floor(view.DefaultLineHeight) - 4, 8, 12));
        double s = _boxSize;
        double x = Math.Floor((Bounds.Width - s) / 2);

        foreach (var line in view.VisualLines)
        {
            if (line.IsDisposed || line.FirstDocumentLine == null || line.FirstDocumentLine.IsDeleted)
            {
                continue;
            }

            var glyph = _getGlyph(line.FirstDocumentLine.LineNumber - 1);
            if (glyph == ConflictToggleGlyph.None)
            {
                continue;
            }

            var tl = line.TextLines[0];
            double top = line.GetTextLineVisualYPosition(tl, VisualYPosition.LineTop) - view.VerticalOffset;
            double bottom = line.GetTextLineVisualYPosition(tl, VisualYPosition.LineBottom) - view.VerticalOffset;
            double y = Math.Floor(top + (bottom - top - s) / 2);
            var rect = new Rect(x + 0.5, y + 0.5, s - 1, s - 1);

            if (glyph == ConflictToggleGlyph.Unchecked)
            {
                context.DrawRectangle(_boxBrush, _outlinePen, rect, 2, 2);
                continue;
            }

            context.DrawRectangle(_accentBrush, _accentPen, rect, 2, 2);
            using (context.PushTransform(Matrix.CreateTranslation(x, y)))
            {
                context.DrawGeometry(null, _markPen, glyph == ConflictToggleGlyph.Checked ? _checkGeometry! : _dashGeometry!);
            }
        }
    }

    private void EnsureResources(double size)
    {
        if (_boxBrush == null)
        {
            PoolMissesForTesting++;
            _boxBrush = PaletteBrushes.Get("ColorBg");
            _accentBrush = PaletteBrushes.Get("ColorAccent");
            _outlinePen = PaletteBrushes.Pen("ColorTextMuted", 1);
            _accentPen = PaletteBrushes.Pen("ColorAccent", 1);
            _markPen = PaletteBrushes.Pen("ColorBg", 1.5);
        }

#pragma warning disable S1244 // exact compare against the stored copy
        if (_checkGeometry == null || size != _boxSize)
#pragma warning restore S1244
        {
            PoolMissesForTesting++;
            _boxSize = size;

            var check = new StreamGeometry();
            using (var ctx = check.Open())
            {
                ctx.BeginFigure(new Point(size * 0.24, size * 0.52), false);
                ctx.LineTo(new Point(size * 0.42, size * 0.70));
                ctx.LineTo(new Point(size * 0.76, size * 0.32));
                ctx.EndFigure(false);
            }
            _checkGeometry = check;

            var dash = new StreamGeometry();
            using (var ctx = dash.Open())
            {
                ctx.BeginFigure(new Point(size * 0.26, size * 0.5), false);
                ctx.LineTo(new Point(size * 0.74, size * 0.5));
                ctx.EndFigure(false);
            }
            _dashGeometry = dash;
        }
    }

    private int LineAt(double y)
    {
        var view = TextView;
        if (view is not { VisualLinesValid: true })
        {
            return -1;
        }

        var docLine = view.GetDocumentLineByVisualTop(y + view.VerticalOffset);
        return docLine == null ? -1 : docLine.LineNumber - 1;
    }

    /// <summary>Raises <see cref="ToggleClicked"/> for the box at <paramref name="y"/> (margin
    /// coordinates), if that line has one.</summary>
    internal bool ToggleAt(double y)
    {
        int line = LineAt(y);
        if (line < 0 || _getGlyph(line) == ConflictToggleGlyph.None)
        {
            return false;
        }

        ToggleClicked?.Invoke(this, line);
        return true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && ToggleAt(e.GetPosition(this).Y))
        {
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        int line = LineAt(e.GetPosition(this).Y);
        if (line == _hoverLine)
        {
            return;
        }

        _hoverLine = line;
        bool hasBox = line >= 0 && _getGlyph(line) != ConflictToggleGlyph.None;
        Cursor = hasBox ? HandCursor : null;
        ToolTip.SetTip(this, hasBox ? _getTip?.Invoke(line) : null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverLine = -1;
        Cursor = null;
        ToolTip.SetTip(this, null);
    }
}
