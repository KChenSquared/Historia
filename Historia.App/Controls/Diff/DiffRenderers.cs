namespace Historia.Controls;

/// <summary>
/// Vertical separator margin between diff editor margins.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class DiffVerticalSeparatorMargin : AbstractMargin
{
    public DiffVerticalSeparatorMargin()
    {
        ClipToBounds = true;
    }

    public override void Render(DrawingContext context)
    {
        var pen = PaletteBrushes.Pen("ColorBorder", 1);
        context.DrawLine(pen, new Point(0.5, 0), new Point(0.5, Bounds.Height));
    }

    protected override Size MeasureOverride(Size availableSize) => new(1, 0);
}

/// <summary>The gutter tint a diff row gets behind its line number and +/- indicator.</summary>
internal static class DiffGutterTint
{
    /// <summary>The gutter brush for added and deleted rows; null for every other row.</summary>
    internal static IBrush? GutterBrush(DiffDisplayLineType type) => type switch
    {
        DiffDisplayLineType.Addition => PaletteBrushes.Get("ColorDiffAddGutter"),
        DiffDisplayLineType.Deletion => PaletteBrushes.Get("ColorDiffDelGutter"),
        _ => null,
    };

    /// <summary>Fills the full width of <paramref name="line"/>'s row in a margin with <paramref name="brush"/>.</summary>
    internal static void FillRow(DrawingContext context, TextView view, VisualLine line, double width, IBrush brush)
    {
        var top = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineTop) - view.VerticalOffset;
        var bottom = line.GetTextLineVisualYPosition(line.TextLines[^1], VisualYPosition.LineBottom) - view.VerticalOffset;
        context.DrawRectangle(brush, null, new Rect(0, top, width, bottom - top));
    }
}

/// <summary>
/// Line number margin displaying old or new line numbers for diff rows.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class DiffLineNumberMargin : AbstractMargin
{
    private const double Pad = 8;

    private readonly bool _isOld;
    private readonly DiffPresenterBase _presenter;
    private readonly ShapedTextCache _textCache = new(TextCacheCapacity);
    private Typeface _typeface;
    private double _lastFontSize;

    /// <summary>
    /// Line-number layouts kept per margin. A viewport shows well under 200 lines even on a tall
    /// display at a small font, so this holds several viewports: steady scrolling shapes only the
    /// numbers it reveals, and scrolling back finds the ones it passed.
    /// </summary>
    internal const int TextCacheCapacity = 1024;

    internal ShapedTextCache TextCacheForTesting => _textCache;

    /// <summary>The gutter brush the margin paints behind 0-based <paramref name="rowIndex"/>, or null.</summary>
    internal IBrush? TintForRowForTesting(int rowIndex)
        => rowIndex >= 0 && rowIndex < _presenter.Rows.Count ? DiffGutterTint.GutterBrush(_presenter.Rows[rowIndex].Type) : null;

    /// <summary>Gutter rows the last <see cref="Render"/> tinted.</summary>
    internal int TintedRowsDrawnForTesting { get; private set; }

    public DiffLineNumberMargin(DiffPresenterBase presenter, bool isOld)
    {
        _presenter = presenter;
        _isOld = isOld;
        _typeface = new Typeface(_presenter.FontFamily, _presenter.FontStyle, _presenter.FontWeight);
        _lastFontSize = _presenter.FontSize;
        ClipToBounds = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        PaletteBrushes.Changed += OnPaletteChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        PaletteBrushes.Changed -= OnPaletteChanged;
        _textCache.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPaletteChanged(object? sender, EventArgs e)
    {
        _textCache.Clear();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var view = TextView;
        if (view is not { VisualLinesValid: true })
        {
            return;
        }

        TintedRowsDrawnForTesting = 0;
        var rows = _presenter.Rows;
        var foregroundBrush = PaletteBrushes.Get("ColorTextFaint");

        var typeface = new Typeface(_presenter.FontFamily, _presenter.FontStyle, _presenter.FontWeight);
#pragma warning disable S1244 // exact compare against the stored copy
        if (_presenter.FontSize != _lastFontSize || !typeface.Equals(_typeface))
#pragma warning restore S1244
        {
            _lastFontSize = _presenter.FontSize;
            _typeface = typeface;
            _textCache.Clear();
        }

        foreach (var line in view.VisualLines)
        {
            if (line.IsDisposed || line.FirstDocumentLine == null || line.FirstDocumentLine.IsDeleted)
            {
                continue;
            }

            var index = line.FirstDocumentLine.LineNumber;
            if (index > rows.Count)
            {
                break;
            }

            var row = rows[index - 1];
            if (DiffGutterTint.GutterBrush(row.Type) is { } tint)
            {
                DiffGutterTint.FillRow(context, view, line, Bounds.Width, tint);
                TintedRowsDrawnForTesting++;
            }

            var lineNumber = _isOld ? row.OldLineText : row.NewLineText;
            if (string.IsNullOrEmpty(lineNumber))
            {
                continue;
            }

            var y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineMiddle) - view.VerticalOffset;
            var txt = _textCache.Get(lineNumber, _typeface, _lastFontSize, foregroundBrush);
            txt.Draw(context, new Point(Bounds.Width - Pad - txt.Width, y - (txt.Height * 0.5)));
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var maxLine = _presenter.Model?.MaxLineNumber ?? 0;
        if (maxLine <= 0)
        {
            return new Size(24 + (2 * Pad), 0);
        }

        var typeface = new Typeface(_presenter.FontFamily);
        var test = new FormattedText(
            maxLine.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            _presenter.FontSize,
            Brushes.White);

        return new Size(test.Width + (2 * Pad), 0);
    }
}

/// <summary>
/// Gutter margin showing '+' and '-' modification indicators.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class DiffLineModifyTypeMargin : AbstractMargin
{
    private const double Pad = 4;

    private readonly DiffPresenterBase _presenter;
    private readonly ShapedTextCache _textCache = new(capacity: 4);
    private Typeface _typeface;
    private double _lastFontSize;

    internal ShapedTextCache TextCacheForTesting => _textCache;

    /// <summary>The gutter brush the margin paints behind 0-based <paramref name="rowIndex"/>, or null.</summary>
    internal IBrush? TintForRowForTesting(int rowIndex)
        => rowIndex >= 0 && rowIndex < _presenter.Rows.Count ? DiffGutterTint.GutterBrush(_presenter.Rows[rowIndex].Type) : null;

    /// <summary>Gutter rows the last <see cref="Render"/> tinted.</summary>
    internal int TintedRowsDrawnForTesting { get; private set; }

    public DiffLineModifyTypeMargin(DiffPresenterBase presenter)
    {
        _presenter = presenter;
        _typeface = new Typeface(_presenter.FontFamily, _presenter.FontStyle, _presenter.FontWeight);
        _lastFontSize = _presenter.FontSize;
        ClipToBounds = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        PaletteBrushes.Changed += OnPaletteChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        PaletteBrushes.Changed -= OnPaletteChanged;
        _textCache.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPaletteChanged(object? sender, EventArgs e)
    {
        _textCache.Clear();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var view = TextView;
        if (view is not { VisualLinesValid: true })
        {
            return;
        }

        TintedRowsDrawnForTesting = 0;
        var rows = _presenter.Rows;

        var typeface = new Typeface(_presenter.FontFamily, _presenter.FontStyle, _presenter.FontWeight);
#pragma warning disable S1244 // exact compare against the stored copy
        if (_presenter.FontSize != _lastFontSize || !typeface.Equals(_typeface))
#pragma warning restore S1244
        {
            _lastFontSize = _presenter.FontSize;
            _typeface = typeface;
            _textCache.Clear();
        }

        var addBrush = PaletteBrushes.Get("ColorGitAdded");
        var delBrush = PaletteBrushes.Get("ColorGitDeleted");

        foreach (var line in view.VisualLines)
        {
            if (line.IsDisposed || line.FirstDocumentLine == null || line.FirstDocumentLine.IsDeleted)
            {
                continue;
            }

            var index = line.FirstDocumentLine.LineNumber;
            if (index > rows.Count)
            {
                break;
            }

            var row = rows[index - 1];
            if (DiffGutterTint.GutterBrush(row.Type) is { } tint)
            {
                DiffGutterTint.FillRow(context, view, line, Bounds.Width, tint);
                TintedRowsDrawnForTesting++;
            }

            TextLayout? indicator = row.Type switch
            {
                DiffDisplayLineType.Addition => _textCache.Get("+", _typeface, _lastFontSize, addBrush),
                DiffDisplayLineType.Deletion => _textCache.Get("-", _typeface, _lastFontSize, delBrush),
                _ => null,
            };

            if (indicator != null)
            {
                var y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineMiddle) - view.VerticalOffset;
                indicator.Draw(context, new Point(Pad, y - (indicator.Height * 0.5)));
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var typeface = new Typeface(_presenter.FontFamily);
        var test = new FormattedText(
            "-",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            _presenter.FontSize,
            Brushes.White);
        return new Size(test.Width + (2 * Pad), 0);
    }
}

/// <summary>
/// Safe background renderer for diff line washes, intra-line spans, and block borders.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class DiffLineBackgroundRenderer : SafeBackgroundRenderer
{
    private readonly DiffPresenterBase _presenter;

    public override KnownLayer Layer => KnownLayer.Background;

    public DiffLineBackgroundRenderer(DiffPresenterBase presenter)
    {
        _presenter = presenter;
    }

    protected override void DrawCore(TextView textView, DrawingContext drawingContext)
    {
        var rows = _presenter.Rows;
        if (rows.Count == 0)
        {
            return;
        }

        var width = textView.Bounds.Width;
        var changeBlock = _presenter.CurrentBlock;

        var addBg = PaletteBrushes.Get("ColorDiffAddBg");
        var delBg = PaletteBrushes.Get("ColorDiffDelBg");
        var emptyBg = PaletteBrushes.Get("ColorDiffEmptyBg");
        var addHl = PaletteBrushes.Get("ColorDiffAddHl");
        var delHl = PaletteBrushes.Get("ColorDiffDelHl");
        var blockBorderPen = PaletteBrushes.Pen("ColorAccent", 1);

        foreach (var line in textView.VisualLines)
        {
            if (line.IsDisposed || line.FirstDocumentLine == null || line.FirstDocumentLine.IsDeleted)
            {
                continue;
            }

            var index = line.FirstDocumentLine.LineNumber;
            if (index > rows.Count)
            {
                break;
            }

            var row = rows[index - 1];
            var lastTextLine = line.TextLines[^1];
            var startY = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineTop) - textView.VerticalOffset;
            var endY = line.GetTextLineVisualYPosition(lastTextLine, VisualYPosition.LineBottom) - textView.VerticalOffset;

            IBrush? bg = row.Type switch
            {
                DiffDisplayLineType.Addition => addBg,
                DiffDisplayLineType.Deletion => delBg,
                DiffDisplayLineType.None => emptyBg,
                _ => null,
            };

            if (bg != null)
            {
                drawingContext.DrawRectangle(bg, null, new Rect(0, startY, width, endY - startY));

                if (row.Highlights.Count > 0)
                {
                    var highlightBg = row.Type == DiffDisplayLineType.Addition ? addHl : delHl;
                    if (highlightBg != null)
                    {
                        int processingIdxStart = 0;
                        int processingIdxEnd = 0;
                        int nextHighlight = 0;

                        foreach (var tl in line.TextLines)
                        {
                            processingIdxEnd += tl.Length;
                            var y = line.GetTextLineVisualYPosition(tl, VisualYPosition.LineTop) - textView.VerticalOffset;
                            var h = line.GetTextLineVisualYPosition(tl, VisualYPosition.LineBottom) - textView.VerticalOffset - y;

                            while (nextHighlight < row.Highlights.Count)
                            {
                                var highlight = row.Highlights[nextHighlight];
                                int hlStart = highlight.Start;
                                int hlEnd = highlight.Start + highlight.Length - 1;

                                if (hlStart >= processingIdxEnd)
                                {
                                    break;
                                }

                                var startCol = line.GetVisualColumn(hlStart < processingIdxStart ? processingIdxStart : hlStart);
                                var endCol = line.GetVisualColumn(hlEnd >= processingIdxEnd ? processingIdxEnd : hlEnd + 1);

                                var x = line.GetTextLineVisualXPosition(tl, startCol) - textView.HorizontalOffset;
                                var w = line.GetTextLineVisualXPosition(tl, endCol) - textView.HorizontalOffset - x;

                                if (w > 0 && h > 0)
                                {
                                    drawingContext.DrawRectangle(highlightBg, null, new Rect(x, y, w, h));
                                }

                                if (hlEnd >= processingIdxEnd)
                                {
                                    break;
                                }

                                nextHighlight++;
                            }

                            processingIdxStart = processingIdxEnd;
                        }
                    }
                }
            }

            if (changeBlock != null)
            {
                if (index == changeBlock.Value.StartLine)
                {
                    drawingContext.DrawLine(blockBorderPen, new Point(0, startY), new Point(width, startY));
                }

                if (index == changeBlock.Value.EndLine)
                {
                    drawingContext.DrawLine(blockBorderPen, new Point(0, endY), new Point(width, endY));
                }
            }
        }
    }
}

/// <summary>
/// Transformer coloring @@ hunk indicator @@ header lines.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class DiffIndicatorTransformer : DocumentColorizingTransformer
{
    private readonly DiffPresenterBase _presenter;
    private readonly Typeface _indicatorTypeface;

    public DiffIndicatorTransformer(DiffPresenterBase presenter)
    {
        _presenter = presenter;
        _indicatorTypeface = new Typeface(_presenter.FontFamily, FontStyle.Italic);
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        var rows = _presenter.Rows;
        var idx = line.LineNumber;
        if (idx > rows.Count)
        {
            return;
        }

        var row = rows[idx - 1];
        if (row.Type == DiffDisplayLineType.Indicator)
        {
            var indicatorBrush = PaletteBrushes.Get("ColorAccent");
            ChangeLinePart(line.Offset, line.EndOffset, v =>
            {
                v.TextRunProperties.SetForegroundBrush(indicatorBrush);
                v.TextRunProperties.SetTypeface(_indicatorTypeface);
            });
        }
    }
}
