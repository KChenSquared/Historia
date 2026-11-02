namespace Historia.Controls;

/// <summary>
/// Abstract base class for diff code editor presenters.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public abstract class DiffPresenterBase : ScrollSyncCodeEditor
{
    private DiffDisplayModel? _model;
    private IReadOnlyList<DiffDisplayRow> _rows = [];
    private int _currentBlockIndex = -1;
    private bool _allowStaging = true;
    private string _stageVerb = "Stage";
    private bool _enableRowButtons = true;
    private DiffHunkActionGenerator? _hunkActionGenerator;
    private int? _pointerLine;
    private (int Start, int End)? _visibleLineRange;
    private readonly DiffLineBackgroundRenderer _backgroundRenderer;
    private readonly DiffIndicatorTransformer _indicatorTransformer;

    public DiffDisplayModel? Model
    {
        get => _model;
        set
        {
            _model = value;
            _currentBlockIndex = -1;
            OnModelChanged();
            UpdateVisibleLineRange();
        }
    }

    public IReadOnlyList<DiffDisplayRow> Rows
    {
        get => _rows;
        protected set => _rows = value;
    }

    public bool EnableRowButtons
    {
        get => _enableRowButtons;
        set
        {
            if (_enableRowButtons != value)
            {
                _enableRowButtons = value;
                UpdateStagingControls();
            }
        }
    }

    public (int Start, int End)? VisibleLineRange
    {
        get => _visibleLineRange;
        private set
        {
            if (_visibleLineRange != value)
            {
                _visibleLineRange = value;
                VisibleLineRangeChanged?.Invoke(this, _visibleLineRange);
            }
        }
    }

    public event EventHandler<(int Start, int End)?>? VisibleLineRangeChanged;

    public bool AllowStaging
    {
        get => _allowStaging;
        set
        {
            if (_allowStaging != value)
            {
                _allowStaging = value;
                UpdateStagingControls();
            }
        }
    }

    public string StageVerb
    {
        get => _stageVerb;
        set
        {
            if (_stageVerb != value)
            {
                _stageVerb = value;
                UpdateStagingControls();
            }
        }
    }

    public virtual IReadOnlyList<DiffBlock> Blocks => Model?.CombinedBlocks ?? [];

    public DiffBlock? CurrentBlock =>
        Blocks.Count > 0 && _currentBlockIndex >= 0 && _currentBlockIndex < Blocks.Count
            ? Blocks[_currentBlockIndex]
            : null;

    public int CurrentBlockIndex
    {
        get => _currentBlockIndex;
        set
        {
            if (Blocks.Count == 0)
            {
                _currentBlockIndex = -1;
                return;
            }

            var clamped = Math.Clamp(value, 0, Blocks.Count - 1);
            if (_currentBlockIndex != clamped)
            {
                _currentBlockIndex = clamped;
                var block = Blocks[_currentBlockIndex];
                TextArea.Caret.Line = block.StartLine;
                ScrollToLine(block.StartLine);
                TextArea.TextView.Redraw();
            }
        }
    }

    public event EventHandler<int>? HunkActionRequested;
    public event EventHandler<IReadOnlyList<DiffLineRef>>? SelectionActionRequested;

    protected DiffPresenterBase()
    {
        _backgroundRenderer = new DiffLineBackgroundRenderer(this);
        _indicatorTransformer = new DiffIndicatorTransformer(this);

        TextArea.TextView.BackgroundRenderers.Add(_backgroundRenderer);
        TextArea.TextView.LineTransformers.Add(_indicatorTransformer);

        TextArea.TextView.VisualLinesChanged += OnTextViewVisualLinesChanged;
        TextArea.AddHandler(PointerPressedEvent, OnTextAreaPointerPressed, RoutingStrategies.Tunnel);
        TextArea.Caret.PositionChanged += OnCaretPositionChanged;
        TextArea.SelectionChanged += (_, _) => UpdateSelectionItem();
        TextArea.AddHandler(KeyDownEvent, OnPresenterKeyDown, RoutingStrategies.Tunnel);

        UpdateStagingControls();
    }

    protected abstract void OnModelChanged();

    protected override void OnScrollOffsetApplied() => UpdateVisibleLineRange();

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        var caretLine = TextArea.Caret.Line;
        for (int i = 0; i < Blocks.Count; i++)
        {
            if (Blocks[i].Contains(caretLine))
            {
                if (_currentBlockIndex != i)
                {
                    _currentBlockIndex = i;
                    TextArea.TextView.Redraw();
                }
                return;
            }
        }
    }

    public DiffBlock? GoToNextChange()
    {
        if (Blocks.Count == 0)
        {
            return null;
        }

        CurrentBlockIndex = Math.Min(Blocks.Count - 1, _currentBlockIndex + 1);
        return CurrentBlock;
    }

    public DiffBlock? GoToPreviousChange()
    {
        if (Blocks.Count == 0)
        {
            return null;
        }

        CurrentBlockIndex = Math.Max(0, _currentBlockIndex - 1);
        return CurrentBlock;
    }

    private async void OnPresenterKeyDown(object? sender, KeyEventArgs e)
    {
        var isCopy = e.Key == Key.C && e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
        if (isCopy)
        {
            e.Handled = true;
            await CopySelectionAsync();
        }
    }

    public async Task CopySelectionAsync()
    {
        var text = SelectedText;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var unescaped = DiffDisplayModel.UnescapeCarriageReturn(text);
        var clip = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clip != null)
        {
            await clip.SetTextAsync(unescaped);
        }
    }

    /// <summary>
    /// Computes the 1-based [firstLine, lastLine] document line range covered by a selection.
    /// When a multi-line selection ends at column 1 of a line, that end line is excluded because
    /// the selection only touches the line break before it. However, if the end line is the
    /// document's last line and is empty, it is kept because there is no following delimiter to end on.
    /// </summary>
    internal static (int First, int Last) SelectedLineSpan(Selection selection, TextDocument document)
    {
        var start = selection.StartPosition;
        var end = selection.EndPosition;

        int firstLine, lastLine, lastCol;
        if (start.Line < end.Line || (start.Line == end.Line && start.Column <= end.Column))
        {
            firstLine = start.Line;
            lastLine = end.Line;
            lastCol = end.Column;
        }
        else
        {
            firstLine = end.Line;
            lastLine = start.Line;
            lastCol = start.Column;
        }

        if (selection is SimpleSelection && lastLine > firstLine && lastCol == 1
            && !(lastLine == document.LineCount && document.GetLineByNumber(lastLine).Length == 0))
        {
            lastLine--;
        }

        return (firstLine, lastLine);
    }

    public virtual IReadOnlyList<DiffLineRef> GetSelectedLineRefs()
    {
        var sel = TextArea.Selection;
        if (sel.IsEmpty || Model == null)
        {
            return [];
        }

        var (start, end) = SelectedLineSpan(sel, Document);
        return Model.GetSelectedLinesForCombined(start, end);
    }

    public void RequestHunkAction(int hunkIndex)
    {
        HunkActionRequested?.Invoke(this, hunkIndex);
    }

    public void RequestSelectionAction()
    {
        var refs = GetSelectedLineRefs();
        if (refs.Count > 0)
        {
            SelectionActionRequested?.Invoke(this, refs);
        }
    }

    private void OnTextViewVisualLinesChanged(object? sender, EventArgs e)
    {
        UpdateVisibleLineRange();
    }

    public void UpdateVisibleLineRange()
    {
        var tv = TextArea.TextView;
        if (tv is { VisualLinesValid: true } && tv.VisualLines.Count > 0)
        {
            var first = tv.VisualLines[0].FirstDocumentLine.LineNumber;
            var last = tv.VisualLines[^1].LastDocumentLine.LineNumber;
            VisibleLineRange = (first, last);
        }
        else
        {
            VisibleLineRange = null;
        }
    }

    private void OnTextAreaPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        if (pt.Properties.IsRightButtonPressed)
        {
            var pos = e.GetPosition(TextArea.TextView);
            var visualY = pos.Y + TextArea.TextView.VerticalOffset;
            var vl = TextArea.TextView.GetVisualLineFromVisualTop(visualY);
            if (vl != null)
            {
                _pointerLine = vl.FirstDocumentLine.LineNumber;
            }
            else
            {
                _pointerLine = null;
            }
        }
    }

    protected void UpdateStagingControls()
    {
        UpdateHunkActionGenerator();
        UpdateContextMenu();
    }

    private void UpdateHunkActionGenerator()
    {
        var generators = TextArea.TextView.ElementGenerators;
        if (_allowStaging && _enableRowButtons)
        {
            if (_hunkActionGenerator == null)
            {
                _hunkActionGenerator = new DiffHunkActionGenerator(this);
                generators.Add(_hunkActionGenerator);
                TextArea.TextView.Redraw();
            }
        }
        else
        {
            if (_hunkActionGenerator != null)
            {
                generators.Remove(_hunkActionGenerator);
                _hunkActionGenerator = null;
                TextArea.TextView.Redraw();
            }
        }
    }

    private ContextMenu? _presenterMenu;
    private MenuItem? _stageLinesMenuItem;

    private void UpdateSelectionItem()
    {
        if (_stageLinesMenuItem != null)
        {
            var selectedRefs = GetSelectedLineRefs();
            _stageLinesMenuItem.IsEnabled = selectedRefs.Count > 0;
        }
    }

    private void UpdateContextMenu()
    {
        if (!_allowStaging)
        {
            _stageLinesMenuItem = null;
            _presenterMenu = null;
            TextArea.ContextMenu = null;
            return;
        }

        if (TextArea.ContextMenu == _presenterMenu && _presenterMenu != null)
        {
            return;
        }

        var menu = new ContextMenu();
        _presenterMenu = menu;
        void PopulateItems()
        {
            menu.Items.Clear();
            var selectedRefs = GetSelectedLineRefs();
            var stageLinesItem = new MenuItem
            {
                Header = $"{StageVerb} selected lines",
                IsEnabled = selectedRefs.Count > 0,
            };
            stageLinesItem.Click += (_, _) => RequestSelectionAction();
            _stageLinesMenuItem = stageLinesItem;
            menu.Items.Add(stageLinesItem);

            int hunkIndex = -1;
            if (_pointerLine is { } pl && pl - 1 >= 0 && pl - 1 < Rows.Count)
            {
                hunkIndex = Rows[pl - 1].HunkIndex;
            }
            if (hunkIndex < 0)
            {
                int caretLine = TextArea.Caret.Line;
                if (caretLine - 1 >= 0 && caretLine - 1 < Rows.Count)
                {
                    hunkIndex = Rows[caretLine - 1].HunkIndex;
                }
            }

            var stageHunkItem = new MenuItem
            {
                Header = $"{StageVerb} hunk",
                IsEnabled = hunkIndex >= 0,
            };
            var capturedHunk = hunkIndex;
            stageHunkItem.Click += (_, _) =>
            {
                if (capturedHunk >= 0)
                {
                    RequestHunkAction(capturedHunk);
                }
            };
            menu.Items.Add(stageHunkItem);
        }

        PopulateItems();
        menu.Opening += (s, e) => PopulateItems();
        TextArea.ContextMenu = menu;
    }
}

/// <summary>
/// Combined diff presenter showing additions and deletions in a single editor view.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class CombinedDiffPresenter : DiffPresenterBase
{
    public CombinedDiffPresenter()
    {
        TextArea.LeftMargins.Add(new DiffLineNumberMargin(this, isOld: true));
        TextArea.LeftMargins.Add(new DiffVerticalSeparatorMargin());
        TextArea.LeftMargins.Add(new DiffLineNumberMargin(this, isOld: false));
        TextArea.LeftMargins.Add(new DiffVerticalSeparatorMargin());
        TextArea.LeftMargins.Add(new DiffLineModifyTypeMargin(this));
    }

    protected override void OnModelChanged()
    {
        if (Model == null)
        {
            Rows = [];
            Text = string.Empty;
            return;
        }

        Rows = Model.CombinedRows;
        Text = string.Join('\n', Rows.Select(r => r.Content));
    }
}

/// <summary>
/// Single side editor presenter for side-by-side diff displays.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class SingleSideDiffPresenter : DiffPresenterBase
{
    private readonly bool _isOld;

    public override IReadOnlyList<DiffBlock> Blocks => Model?.SideBySideBlocks ?? [];

    public SingleSideDiffPresenter(bool isOld)
    {
        _isOld = isOld;
        WordWrap = false;
        EnableRowButtons = isOld;

        TextArea.LeftMargins.Add(new DiffLineNumberMargin(this, isOld: _isOld));
        TextArea.LeftMargins.Add(new DiffVerticalSeparatorMargin());
        TextArea.LeftMargins.Add(new DiffLineModifyTypeMargin(this));
    }

    public override IReadOnlyList<DiffLineRef> GetSelectedLineRefs()
    {
        var sel = TextArea.Selection;
        if (sel.IsEmpty || Model == null)
        {
            return [];
        }

        var (start, end) = SelectedLineSpan(sel, Document);
        return Model.GetSelectedLinesForSideBySide(start, end, oldSide: _isOld);
    }

    protected override void OnModelChanged()
    {
        if (Model == null)
        {
            Rows = [];
            Text = string.Empty;
            return;
        }

        Rows = _isOld ? Model.SideBySideOldRows : Model.SideBySideNewRows;
        Text = string.Join('\n', Rows.Select(r => r.Content));
    }
}
