namespace Historia.Views;

/// <summary>
/// File diff view powered by AvaloniaEdit presenters (Combined and Side-by-Side).
/// The toolbar and the placeholder text are markup bound to a <see cref="DiffViewModel"/>; the code
/// here hosts the presenters and the minimap, builds them for the view model's mode and model, and
/// forwards their hunk, selection, scroll and navigation events.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed partial class DiffView : UserControl, IDisposable
{
    private readonly IServiceProvider? _sp;

    private CombinedDiffPresenter? _combinedPresenter;
    private SideBySideDiffPresenter? _sideBySidePresenter;

    public DiffView(RepositoryContext ctx, DiffRequest request, ISettingsService? settings, IServiceProvider? sp)
    {
        _sp = sp;
        InitializeComponent();

        var ui = ViewServices.Dispatcher(sp);
        if (sp is null)
        {
            ViewModel = new DiffViewModel(ctx, request, ui, settings);
        }
        else
        {
            object[] args = settings is null ? [ctx, request, ui] : [ctx, request, ui, settings];
            ViewModel = ActivatorUtilities.CreateInstance<DiffViewModel>(sp, args);
        }

        Minimap.LineNavigationRequested += OnMinimapLineNavigationRequested;
        ViewModel.PresentationChanged += OnPresentationChanged;
        ViewModel.NavigationRequested += OnNavigationRequested;
        ViewModel.FocusLineRequested += OnFocusLineRequested;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = ViewModel;
        ApplyPresentation();
    }

    internal DiffViewModel ViewModel { get; }

    public Control? PresenterForTesting => ViewModel.IsDisposed
        ? null
        : PresenterHost.Content as Control ?? (ViewModel.HasMessage ? MessageText : null);

    public DiffMinimap MinimapForTesting => Minimap;
    public FileDiff? DiffForTesting => ViewModel.Diff;
    internal DiffDisplayModel? ModelForTesting => ViewModel.Model;
    internal Button CloseButtonForTesting => CloseButton;
    public void ToggleModeForTesting() => ViewModel.ToggleModeCommand.Execute(null);
    internal int LastModelCreationThreadId => ViewModel.LastModelCreationThreadId;

    /// <summary>Opens at the top instead of the request's line: a view rebuilt for a request whose
    /// line was already shown keeps the user where they scrolled.</summary>
    internal void SkipInitialFocusLine() => ViewModel.SkipInitialFocusLine();

    /// <summary>Scrolls to the request's line again, for a repeated request for this diff.</summary>
    internal void RefocusLine() => ViewModel.RefocusLine();

    public Task ApplyHunkAsync(int hunkIndex) => ViewModel.ApplyHunkAsync(hunkIndex);

    public Task ApplySelectionAsync(IReadOnlyList<DiffLineRef> refs) => ViewModel.ApplySelectionAsync(refs);

    private void OnPresentationChanged(object? sender, EventArgs e) => ApplyPresentation();

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiffViewModel.IsApplyingPatch))
            ApplyHunkActionsEnabled();
    }

    // The staging buttons and menu items are disabled while a patch applies.
    private void ApplyHunkActionsEnabled()
    {
        var enabled = !ViewModel.IsApplyingPatch;
        if (_sideBySidePresenter is { } sbs)
            sbs.HunkActionsEnabled = enabled;
        if (_combinedPresenter is { } combined)
            combined.HunkActionsEnabled = enabled;
    }

    // Builds the presenter for the view model's mode and model, or clears the body when the view
    // model shows a message instead.
    private void ApplyPresentation()
    {
        if (ViewModel.IsDisposed)
            return;

        DetachPresenters();
        if (!ViewModel.ShowsDiff || ViewModel.Model is not { } model)
        {
            Minimap.IsVisible = false;
            Minimap.Model = null;
            PresenterHost.Content = null;
            return;
        }

        Minimap.IsVisible = true;
        Minimap.Model = model;
        Minimap.IsSideBySide = ViewModel.IsSideBySide;

        if (ViewModel.IsSideBySide)
        {
            var sbs = new SideBySideDiffPresenter
            {
                ServicesProvider = _sp,
                FilePath = ViewModel.Path,
                Model = model,
                AllowStaging = ViewModel.CanStage,
                StageVerb = ViewModel.StageVerb,
                HunkActionsEnabled = !ViewModel.IsApplyingPatch,
            };
            sbs.LeftPresenter.WordWrap = false;
            sbs.RightPresenter.WordWrap = false;
            sbs.HunkActionRequested += OnHunkActionRequested;
            sbs.SelectionActionRequested += OnSelectionActionRequested;
            sbs.VisibleLineRangeChanged += OnPresenterVisibleLineRangeChanged;
            _sideBySidePresenter = sbs;
            Minimap.DisplayRange = sbs.VisibleLineRange;
            PresenterHost.Content = sbs;
        }
        else
        {
            var combined = new CombinedDiffPresenter
            {
                ServicesProvider = _sp,
                FilePath = ViewModel.Path,
                Model = model,
                AllowStaging = ViewModel.CanStage,
                StageVerb = ViewModel.StageVerb,
                WordWrap = ViewModel.WordWrap,
                HunkActionsEnabled = !ViewModel.IsApplyingPatch,
            };
            combined.HunkActionRequested += OnHunkActionRequested;
            combined.SelectionActionRequested += OnSelectionActionRequested;
            combined.VisibleLineRangeChanged += OnPresenterVisibleLineRangeChanged;
            _combinedPresenter = combined;
            Minimap.DisplayRange = combined.VisibleLineRange;
            PresenterHost.Content = combined;
        }
    }

    private void DetachPresenters()
    {
        if (_sideBySidePresenter != null)
        {
            _sideBySidePresenter.VisibleLineRangeChanged -= OnPresenterVisibleLineRangeChanged;
            _sideBySidePresenter.HunkActionRequested -= OnHunkActionRequested;
            _sideBySidePresenter.SelectionActionRequested -= OnSelectionActionRequested;
        }

        if (_combinedPresenter != null)
        {
            _combinedPresenter.VisibleLineRangeChanged -= OnPresenterVisibleLineRangeChanged;
            _combinedPresenter.HunkActionRequested -= OnHunkActionRequested;
            _combinedPresenter.SelectionActionRequested -= OnSelectionActionRequested;
        }

        _combinedPresenter = null;
        _sideBySidePresenter = null;
    }

    private void OnMinimapLineNavigationRequested(object? sender, int line) => ScrollPresenterToLine(line);

    private void OnPresenterVisibleLineRangeChanged(object? sender, (int Start, int End)? range)
    {
        Minimap.DisplayRange = range;
    }

    private void OnNavigationRequested(object? sender, DiffNavigation direction)
    {
        if (direction == DiffNavigation.PreviousChange)
        {
            _sideBySidePresenter?.GoToPreviousChange();
            _combinedPresenter?.GoToPreviousChange();
        }
        else
        {
            _sideBySidePresenter?.GoToNextChange();
            _combinedPresenter?.GoToNextChange();
        }
    }

    private void ScrollPresenterToLine(int line)
    {
        _sideBySidePresenter?.ScrollToLine(line);
        _combinedPresenter?.ScrollToLine(line);
    }

    private void OnFocusLineRequested(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(ApplyFocusLine, DispatcherPriority.Loaded);

    // Scrolls once the presenter has been laid out; a presenter that is not on screen yet is asked
    // again a few times, as the view model counts.
    private void ApplyFocusLine()
    {
        var presenter = PresenterHost.Content as Control;
        var ready = presenter is { IsEffectivelyVisible: true } && presenter.Bounds.Height > 0;
        switch (ViewModel.ResolveFocusLine(ready, out var line))
        {
            case FocusLineOutcome.Retry:
                Dispatcher.UIThread.Post(ApplyFocusLine, DispatcherPriority.Background);
                break;
            case FocusLineOutcome.Scroll:
                ScrollPresenterToLine(line);
                break;
        }
    }

    private void OnHunkActionRequested(object? sender, int hunkIndex)
    {
        _ = ViewModel.ApplyHunkAsync(hunkIndex);
    }

    private void OnSelectionActionRequested(object? sender, IReadOnlyList<DiffLineRef> refs)
    {
        _ = ViewModel.ApplySelectionAsync(refs);
    }

    public void Dispose()
    {
        if (ViewModel.IsDisposed)
            return;

        Minimap.LineNavigationRequested -= OnMinimapLineNavigationRequested;
        ViewModel.PresentationChanged -= OnPresentationChanged;
        ViewModel.NavigationRequested -= OnNavigationRequested;
        ViewModel.FocusLineRequested -= OnFocusLineRequested;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        DetachPresenters();
        PresenterHost.Content = null;
        ViewModel.Dispose();
    }
}
