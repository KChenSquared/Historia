namespace Historia.ViewModels;

/// <summary>Where a change-navigation request should move in the shown diff.</summary>
public enum DiffNavigation
{
    PreviousChange,
    NextChange,
}

/// <summary>What the view should do with a pending focus line.</summary>
public enum FocusLineOutcome
{
    /// <summary>Nothing to scroll to, or the request is spent.</summary>
    Nothing,

    /// <summary>The presenter is not laid out yet; ask again shortly.</summary>
    Retry,

    /// <summary>Scroll the presenter to the display line that was returned.</summary>
    Scroll,
}

/// <summary>
/// The file diff shell: the diff for one request, the unified or side-by-side mode (saved through
/// settings), previous and next change requests, hunk and line staging, and the line the request
/// asks to open on. The presenters, the renderers and the minimap stay custom controls; they read
/// <see cref="Model"/> and raise hunk and selection actions back into <see cref="ApplyHunkAsync"/>
/// and <see cref="ApplySelectionAsync"/>. <see cref="PresentationChanged"/> says the presenters
/// must be rebuilt for the current <see cref="IsSideBySide"/> and <see cref="Model"/>; while
/// <see cref="Message"/> is set, no diff is shown.
/// </summary>
public sealed partial class DiffViewModel : ViewModelBase
{
    /// <summary>How long a load runs before "Loading diff..." appears.</summary>
    public static readonly TimeSpan LoadingDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>The message shown while a load outlasts <see cref="LoadingDelay"/>.</summary>
    public const string LoadingMessage = "Loading diff...";

    private const int MaxFocusLineAttempts = 20;

    private readonly RepositoryContext _ctx;
    private readonly ISettingsService? _settings;
    private readonly IPatchService? _patches;
    private readonly INotificationService? _notifications;
    private readonly IUiDispatcher _ui;
    private readonly CancellationTokenSource _loadingCts = new();
    private readonly CancellationTokenSource _readCts;
    private readonly CancellationToken _readToken;
    private bool _patchInFlight;
    private bool _focusLinePending;
    private int _focusLineAttempts;

    /// <param name="settings">Source of the initial mode and the word-wrap setting, and where a mode
    /// change is saved; without it the diff opens unified and nothing is saved.</param>
    /// <param name="patches">Staging backend; without it the diff offers no staging actions.</param>
    /// <param name="notifications">Where staging failures are reported.</param>
    public DiffViewModel(
        RepositoryContext ctx,
        DiffRequest request,
        IUiDispatcher ui,
        ISettingsService? settings = null,
        IPatchService? patches = null,
        INotificationService? notifications = null)
    {
        _ctx = ctx;
        Request = request;
        _ui = ui;
        _settings = settings;
        _patches = patches;
        _notifications = notifications;
        IsSideBySide = settings?.Current.DefaultDiffView == DiffViewMode.SideBySide;
        WordWrap = settings?.Current.DiffWordWrap ?? false;
        _focusLinePending = request.FocusLine is > 0;
        _readCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Lifetime);
        _readToken = _readCts.Token;

        _ = LoadAsync();
    }

    public DiffRequest Request { get; }

    /// <summary>The repository-relative path of the file being diffed.</summary>
    public string? Path => Request.Path;

    /// <summary>Whether long lines wrap in the unified presenter.</summary>
    public bool WordWrap { get; }

    /// <summary>True while the side-by-side presenter is the one shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    public partial bool IsSideBySide { get; private set; }

    /// <summary>The caption of the mode button: the mode a press switches to.</summary>
    public string ToggleLabel => IsSideBySide ? "Unified" : "Side-by-side";

    /// <summary>The loaded diff, or null while loading or after a failed load.</summary>
    [ObservableProperty]
    public partial FileDiff? Diff { get; private set; }

    /// <summary>The display rows built from <see cref="Diff"/>.</summary>
    [ObservableProperty]
    public partial DiffDisplayModel? Model { get; private set; }

    /// <summary>Text shown in place of the diff (loading, binary, too large, no changes, a failure),
    /// or null while the presenters are shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; private set; }

    /// <summary>True when <see cref="Message"/> reports a failure rather than a state.</summary>
    [ObservableProperty]
    public partial bool IsMessageError { get; private set; }

    public bool HasMessage => Message is not null;

    /// <summary>True when the presenters and the minimap are shown for <see cref="Model"/>.</summary>
    [ObservableProperty]
    public partial bool ShowsDiff { get; private set; }

    /// <summary>True when hunk and line staging actions are offered: the diff is of working-tree or
    /// index changes and a patch service exists.</summary>
    public bool CanStage => _patches is not null && Diff is { IsBinary: false }
        && Request.Source is DiffSource.UnstagedChanges or DiffSource.StagedChanges;

    /// <summary>"Stage" for a working-tree diff, "Unstage" for an index diff.</summary>
    public string StageVerb => Request.Source == DiffSource.UnstagedChanges ? "Stage" : "Unstage";

    /// <summary>The managed thread that built the last display model; it is never the UI thread.</summary>
    public int LastModelCreationThreadId { get; private set; }

    /// <summary>Raised after <see cref="ShowsDiff"/>, <see cref="Message"/> and <see cref="Model"/>
    /// describe a new state of the body, and after a mode change.</summary>
    public event EventHandler? PresentationChanged;

    /// <summary>Raised when the shown presenter should move to the previous or next change.</summary>
    public event EventHandler<DiffNavigation>? NavigationRequested;

    /// <summary>Raised when the view should try to scroll to the request's line.</summary>
    public event EventHandler? FocusLineRequested;

    [RelayCommand]
    private void Close() => _ctx.State.SetOpenDiff(null);

    [RelayCommand]
    private void PreviousChange() => NavigationRequested?.Invoke(this, DiffNavigation.PreviousChange);

    [RelayCommand]
    private void NextChange() => NavigationRequested?.Invoke(this, DiffNavigation.NextChange);

    [RelayCommand]
    private void ToggleMode()
    {
        IsSideBySide = !IsSideBySide;
        _ = SaveModeAsync(IsSideBySide ? DiffViewMode.SideBySide : DiffViewMode.Unified);
        Publish();
    }

    private async Task SaveModeAsync(DiffViewMode mode)
    {
        if (_settings is null)
            return;

        try
        {
            await _settings.UpdateAsync(s => s with { DefaultDiffView = mode });
        }
        catch (Exception ex)
        {
            _notifications?.Publish(NotificationSeverity.Warning, "Failed to save diff view preference.", ex.NotificationDetails());
        }
    }

    // Reads the diff, builds the display model off the UI thread, and publishes. "Loading diff..."
    // appears only when the read outlasts LoadingDelay. The read stops when the view model is
    // disposed; the staging steps below keep running on the repository's lifetime so a git write is
    // never cut short.
    private async Task LoadAsync()
    {
        var token = _loadingCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(LoadingDelay, token);
                _ui.Post(() =>
                {
                    if (!IsDisposed && Diff is null && Message is null)
                        Message = LoadingMessage;
                });
            }
            catch (OperationCanceledException) { }
        }, token);

        try
        {
            var diff = await _ctx.Gateway.GetFileDiffAsync(Request, _readToken);
            if (IsDisposed)
                return;

            Diff = diff;
            _loadingCts.Cancel();
            var model = await BuildModelAsync(diff);
            if (IsDisposed)
                return;

            Model = model;
            Publish();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed)
                ShowMessage($"Could not load diff: {ex.Message}", isError: true);
        }
    }

    private Task<DiffDisplayModel> BuildModelAsync(FileDiff diff) => Task.Run(() =>
    {
        LastModelCreationThreadId = Environment.CurrentManagedThreadId;
        return DiffDisplayModel.Create(diff);
    });

    // Moves the body to the state the loaded diff calls for: a message for a diff that cannot be
    // shown, otherwise the presenters.
    private void Publish()
    {
        if (IsDisposed || Diff is not { } diff)
            return;

        if (diff.IsBinary)
        {
            ShowMessage("Binary file — no textual diff.");
            return;
        }

        if (diff.IsTruncated)
        {
            ShowMessage("File too large to diff inline");
            return;
        }

        if (diff.Hunks.Count == 0)
        {
            ShowMessage("No changes.");
            return;
        }

        Model ??= DiffDisplayModel.Create(diff);
        Message = null;
        IsMessageError = false;
        ShowsDiff = true;
        PresentationChanged?.Invoke(this, EventArgs.Empty);

        if (_focusLinePending)
            FocusLineRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ShowMessage(string text, bool isError = false)
    {
        _focusLinePending = false;
        ShowsDiff = false;
        IsMessageError = isError;
        Message = text;
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Opens at the top instead of the request's line: a view rebuilt for a request whose
    /// line was already shown keeps the user where they scrolled. A later
    /// <see cref="RefocusLine"/> still scrolls to it.</summary>
    public void SkipInitialFocusLine() => _focusLinePending = false;

    /// <summary>Asks to scroll to the request's line again, for a repeated request for this diff.
    /// Before the diff has loaded, the load applies it.</summary>
    public void RefocusLine()
    {
        if (IsDisposed || Request.FocusLine is not > 0)
            return;

        _focusLinePending = true;
        _focusLineAttempts = 0;
        if (Model is not null)
            FocusLineRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Settles a pending focus line. While the presenter is not <paramref name="presenterReady"/>
    /// (not laid out or not on screen) the answer is <see cref="FocusLineOutcome.Retry"/> a limited
    /// number of times; after that, or once it is ready, the request is spent either way so later
    /// mode switches never pull the user back, and <paramref name="displayLine"/> is the line to
    /// scroll to when the answer is <see cref="FocusLineOutcome.Scroll"/>.
    /// </summary>
    public FocusLineOutcome ResolveFocusLine(bool presenterReady, out int displayLine)
    {
        displayLine = 0;
        if (IsDisposed || !_focusLinePending || Model is not { } model || Request.FocusLine is not { } focus)
            return FocusLineOutcome.Nothing;

        if (!presenterReady && ++_focusLineAttempts < MaxFocusLineAttempts)
            return FocusLineOutcome.Retry;

        _focusLinePending = false;
        if (model.FindDisplayLineForNewLine(focus, IsSideBySide) is not { } line)
            return FocusLineOutcome.Nothing;

        displayLine = line;
        return FocusLineOutcome.Scroll;
    }

    public Task ApplyHunkAsync(int hunkIndex)
        => ApplyPatchAsync(PatchScope.SingleHunk, new HashSet<DiffLineRef> { new(hunkIndex, 0) }, "this hunk");

    public Task ApplySelectionAsync(IReadOnlyList<DiffLineRef> refs)
        => ApplyPatchAsync(PatchScope.SelectedLines, refs.ToHashSet(), "selected lines");

    // Stages (or, for an index diff, unstages) the selected part of the diff: build the patch from
    // the diff as it was on entry, check it, apply it, then reload the diff so the presenters show
    // what is left. A second request while one runs is ignored. A failure is reported, never thrown.
    private async Task ApplyPatchAsync(PatchScope scope, IReadOnlySet<DiffLineRef> selected, string what)
    {
        if (_patches is null || Diff is null || IsDisposed || _patchInFlight)
            return;

        _patchInFlight = true;
        var diffAtEntry = Diff;
        var stage = Request.Source == DiffSource.UnstagedChanges;
        var reverse = !stage;
        try
        {
            var patch = _patches.GeneratePatch(diffAtEntry, scope, selected, reverse: reverse);
            if (string.IsNullOrEmpty(patch) || IsDisposed)
                return;

            var check = await _patches.CheckAsync(_ctx, patch, PatchTarget.StagingArea, reverse, _ctx.Lifetime);
            if (IsDisposed)
                return;

            if (!check.CanApply)
            {
                _notifications?.Publish(NotificationSeverity.Warning,
                    $"Cannot {(stage ? "stage" : "unstage")} {what}: {check.FailureReason}");
                return;
            }

            await _patches.ApplyPatchAsync(_ctx, patch, PatchTarget.StagingArea, reverse, _ctx.Lifetime);
            if (IsDisposed)
                return;

            await _ctx.State.RefreshStatusAsync(_ctx.Lifetime);
            if (IsDisposed)
                return;

            var diff = await _ctx.Gateway.GetFileDiffAsync(Request, _readToken);
            if (IsDisposed)
                return;

            Diff = diff;
            var model = await BuildModelAsync(diff);
            if (IsDisposed)
                return;

            Model = model;
            Publish();
        }
        catch (HistoriaGitException ex)
        {
            if (!IsDisposed)
                _notifications?.Publish(NotificationSeverity.Warning, ex.Message);
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                _notifications?.Publish(NotificationSeverity.Error, ex.Message, ex.NotificationDetails());
        }
        finally
        {
            _patchInFlight = false;
        }
    }

    protected override void OnDisposed()
    {
        _loadingCts.Cancel();
        _loadingCts.Dispose();
        _readCts.Cancel();
        _readCts.Dispose();
    }
}
