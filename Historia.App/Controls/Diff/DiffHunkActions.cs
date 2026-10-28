namespace Historia.Controls;

/// <summary>
/// Visual line element generator inserting inline stage/unstage hunk action buttons on diff indicator rows.
/// </summary>
public sealed class DiffHunkActionGenerator : VisualLineElementGenerator
{
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private readonly DiffPresenterBase _presenter;
    private bool _generatedForCurrentLine;

    public DiffHunkActionGenerator(DiffPresenterBase presenter)
    {
        _presenter = presenter;
    }

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        _generatedForCurrentLine = false;
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (_generatedForCurrentLine)
        {
            return -1;
        }

        var doc = CurrentContext.Document;
        if (doc == null || _presenter.Rows.Count == 0)
        {
            return -1;
        }

        var line = doc.GetLineByOffset(startOffset);
        if (line == null)
        {
            return -1;
        }

        if (startOffset > line.Offset)
        {
            return -1;
        }

        int rowIndex = line.LineNumber - 1;
        if (rowIndex >= 0 && rowIndex < _presenter.Rows.Count)
        {
            var row = _presenter.Rows[rowIndex];
            if (row.Type == DiffDisplayLineType.Indicator && row.HunkIndex >= 0)
            {
                return line.Offset;
            }
        }

        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        _generatedForCurrentLine = true;
        var doc = CurrentContext.Document;
        if (doc == null)
        {
            return null;
        }

        var line = doc.GetLineByOffset(offset);
        if (line == null || offset != line.Offset)
        {
            return null;
        }

        int rowIndex = line.LineNumber - 1;
        if (rowIndex < 0 || rowIndex >= _presenter.Rows.Count)
        {
            return null;
        }

        var row = _presenter.Rows[rowIndex];
        if (row.Type != DiffDisplayLineType.Indicator || row.HunkIndex < 0)
        {
            return null;
        }

        var button = new Button
        {
            Content = $"{_presenter.StageVerb} hunk",
            FontSize = 10,
            Padding = new Thickness(4, 1),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = HandCursor,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var hunkIndex = row.HunkIndex;
        button.Click += (_, _) => _presenter.RequestHunkAction(hunkIndex);

        return new InlineObjectElement(0, button);
    }
}
