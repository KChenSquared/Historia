namespace Historia.Controls;

/// <summary>
/// Line number margin for conflict panes, numbered by the pane's own lines. A row that is kept or
/// dropped tints its whole gutter row.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class ConflictLineNumberMargin : AbstractMargin
{
    private const double Pad = 8;

    private readonly TextEditor _editor;
    private readonly Func<IReadOnlyList<ConflictPaneLine>?>? _getLines;
    private readonly Func<int, ConflictRowTint>? _getTint;
    private readonly ShapedTextCache _textCache = new(DiffLineNumberMargin.TextCacheCapacity);
    private Typeface _typeface;
    private double _lastFontSize;

    internal ShapedTextCache TextCacheForTesting => _textCache;

    /// <summary>The tint the margin paints behind 0-based <paramref name="row"/>.</summary>
    internal ConflictRowTint TintForRowForTesting(int row) => _getTint?.Invoke(row) ?? ConflictRowTint.None;

    /// <summary>Gutter rows the last <see cref="Render"/> tinted.</summary>
    internal int TintedRowsDrawnForTesting { get; private set; }

    /// <param name="getTint">How a row (0-based document line) tints its gutter; null leaves the
    /// gutter untinted.</param>
    public ConflictLineNumberMargin(
        TextEditor editor,
        Func<IReadOnlyList<ConflictPaneLine>?>? getLines = null,
        Func<int, ConflictRowTint>? getTint = null)
    {
        _editor = editor;
        _getLines = getLines;
        _getTint = getTint;
        _typeface = new Typeface(_editor.FontFamily, _editor.FontStyle, _editor.FontWeight);
        _lastFontSize = _editor.FontSize;
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

        var typeface = new Typeface(_editor.FontFamily, _editor.FontStyle, _editor.FontWeight);
#pragma warning disable S1244 // exact compare against the stored copy
        if (_editor.FontSize != _lastFontSize || !typeface.Equals(_typeface))
#pragma warning restore S1244
        {
            _lastFontSize = _editor.FontSize;
            _typeface = typeface;
            _textCache.Clear();
        }

        TintedRowsDrawnForTesting = 0;
        var lines = _getLines?.Invoke();
        var foregroundBrush = PaletteBrushes.Get("ColorTextFaint");

        foreach (var line in view.VisualLines)
        {
            if (line.IsDisposed || line.FirstDocumentLine == null || line.FirstDocumentLine.IsDeleted)
            {
                continue;
            }

            int docLineNumber = line.FirstDocumentLine.LineNumber;
            string numText;

            if (lines != null)
            {
                if (docLineNumber > lines.Count)
                {
                    break;
                }
                var paneLine = lines[docLineNumber - 1];
                numText = paneLine.LineNumber is { } n ? n.ToString(CultureInfo.InvariantCulture) : string.Empty;
            }
            else
            {
                numText = docLineNumber.ToString(CultureInfo.InvariantCulture);
            }

            if (_getTint != null && ConflictRowTints.GutterBrush(_getTint(docLineNumber - 1)) is { } tint)
            {
                DiffGutterTint.FillRow(context, view, line, Bounds.Width, tint);
                TintedRowsDrawnForTesting++;
            }

            if (string.IsNullOrEmpty(numText))
            {
                continue;
            }

            var y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineMiddle) - view.VerticalOffset;
            var txt = _textCache.Get(numText, _typeface, _lastFontSize, foregroundBrush);
            txt.Draw(context, new Point(Bounds.Width - Pad - txt.Width, y - (txt.Height * 0.5)));
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var max = _editor.Document?.LineCount ?? 100;
        var test = new FormattedText(
            Math.Max(100, max).ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typeface,
            _editor.FontSize,
            Brushes.White);
        return new Size(test.Width + (2 * Pad), 0);
    }
}
