namespace Historia.Controls;

/// <summary>
/// Side-by-side paired diff presenter with synchronized scrolling and aligned lines.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class SideBySideDiffPresenter : Control
{
    private readonly SingleSideDiffPresenter _leftPresenter;
    private readonly SingleSideDiffPresenter _rightPresenter;
    private readonly Grid _grid;
    private DiffDisplayModel? _model;
    private string? _filePath;
    private bool _isSyncingScroll;
    private bool _allowStaging = true;
    private string _stageVerb = "Stage";

    public SingleSideDiffPresenter LeftPresenter => _leftPresenter;
    public SingleSideDiffPresenter RightPresenter => _rightPresenter;

    public IServiceProvider? ServicesProvider
    {
        get => _leftPresenter.ServicesProvider;
        set
        {
            _leftPresenter.ServicesProvider = value;
            _rightPresenter.ServicesProvider = value;
        }
    }

    public DiffDisplayModel? Model
    {
        get => _model;
        set
        {
            _model = value;
            _leftPresenter.Model = value;
            _rightPresenter.Model = value;
        }
    }

    public string? FilePath
    {
        get => _filePath;
        set
        {
            _filePath = value;
            _leftPresenter.FilePath = value;
            _rightPresenter.FilePath = value;
        }
    }

    public bool AllowStaging
    {
        get => _allowStaging;
        set
        {
            _allowStaging = value;
            _leftPresenter.AllowStaging = value;
            _rightPresenter.AllowStaging = value;
        }
    }

    public string StageVerb
    {
        get => _stageVerb;
        set
        {
            _stageVerb = value;
            _leftPresenter.StageVerb = value;
            _rightPresenter.StageVerb = value;
        }
    }

    /// <summary>False while a patch is being applied; forwarded to both panes.</summary>
    public bool HunkActionsEnabled
    {
        get => _leftPresenter.HunkActionsEnabled;
        set
        {
            _leftPresenter.HunkActionsEnabled = value;
            _rightPresenter.HunkActionsEnabled = value;
        }
    }

    public event EventHandler<int>? HunkActionRequested;
    public event EventHandler<IReadOnlyList<DiffLineRef>>? SelectionActionRequested;

    public (int Start, int End)? VisibleLineRange => _leftPresenter.VisibleLineRange;
    public event EventHandler<(int Start, int End)?>? VisibleLineRangeChanged;

    public void ScrollToLine(int line)
    {
        _leftPresenter.ScrollToLine(line);
        _rightPresenter.ScrollToLine(line);
    }

    public SideBySideDiffPresenter()
    {
        _leftPresenter = new SingleSideDiffPresenter(isOld: true);
        _rightPresenter = new SingleSideDiffPresenter(isOld: false);

        _leftPresenter.VisibleLineRangeChanged += (_, r) => VisibleLineRangeChanged?.Invoke(this, r);

        _leftPresenter.ScrollOffsetChanged += OnLeftScrollChanged;
        _rightPresenter.ScrollOffsetChanged += OnRightScrollChanged;

        _leftPresenter.HunkActionRequested += (_, hi) => HunkActionRequested?.Invoke(this, hi);
        _rightPresenter.HunkActionRequested += (_, hi) => HunkActionRequested?.Invoke(this, hi);

        _leftPresenter.SelectionActionRequested += (_, refs) => SelectionActionRequested?.Invoke(this, refs);
        _rightPresenter.SelectionActionRequested += (_, refs) => SelectionActionRequested?.Invoke(this, refs);

        var separator = new Rectangle
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        separator[!Shape.FillProperty] = new DynamicResourceExtension("ColorBorder");

        _grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
        };

        Grid.SetColumn(_leftPresenter, 0);
        Grid.SetColumn(separator, 1);
        Grid.SetColumn(_rightPresenter, 2);

        _grid.Children.Add(_leftPresenter);
        _grid.Children.Add(separator);
        _grid.Children.Add(_rightPresenter);

        VisualChildren.Add(_grid);
        LogicalChildren.Add(_grid);
    }

    private void OnLeftScrollChanged(object? sender, Vector offset)
    {
        if (_isSyncingScroll)
        {
            return;
        }

        _isSyncingScroll = true;
        try
        {
            _rightPresenter.SetScrollOffset(offset);
        }
        finally
        {
            _isSyncingScroll = false;
        }
    }

    private void OnRightScrollChanged(object? sender, Vector offset)
    {
        if (_isSyncingScroll)
        {
            return;
        }

        _isSyncingScroll = true;
        try
        {
            _leftPresenter.SetScrollOffset(offset);
        }
        finally
        {
            _isSyncingScroll = false;
        }
    }

    public IReadOnlyList<DiffLineRef> GetSelectedLineRefs()
    {
        if (Model == null)
        {
            return [];
        }

        // Check right selection first
        var rightSel = _rightPresenter.TextArea.Selection;
        if (!rightSel.IsEmpty)
        {
            var (start, end) = DiffPresenterBase.SelectedLineSpan(rightSel, _rightPresenter.Document);
            return Model.GetSelectedLinesForSideBySide(start, end, oldSide: false);
        }

        // Check left selection
        var leftSel = _leftPresenter.TextArea.Selection;
        if (!leftSel.IsEmpty)
        {
            var (start, end) = DiffPresenterBase.SelectedLineSpan(leftSel, _leftPresenter.Document);
            return Model.GetSelectedLinesForSideBySide(start, end, oldSide: true);
        }

        return [];
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

    public DiffBlock? GoToNextChange()
    {
        var block = _leftPresenter.GoToNextChange();
        _rightPresenter.CurrentBlockIndex = _leftPresenter.CurrentBlockIndex;
        return block;
    }

    public DiffBlock? GoToPreviousChange()
    {
        var block = _leftPresenter.GoToPreviousChange();
        _rightPresenter.CurrentBlockIndex = _leftPresenter.CurrentBlockIndex;
        return block;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _grid.Measure(availableSize);
        return _grid.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _grid.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
